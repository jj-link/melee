using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HSDRaw;
using HSDRaw.Melee.Pl;
using mexTool.Core;

namespace CustomSmash
{
    internal static class JohnPorkGameplay
    {
        // GALE01 symbols.txt: complete ftdonkeyspecialn.c, plus its damage
        // callback and Luigi's attribute loader. No original executable is patched.
        private const uint First = 0x8010E574, End = 0x8010FB1C;
        private const uint DonkeyStates = 0x803CB838, LuigiStates = 0x803D0628;
        private static readonly int[] States = { 341, 359, 360, 361, 362, 342, 363, 364, 365, 366 };
        private static readonly int[] Animations = { 295, 312, 313, 314, 315, 296, 316, 317, 318, 319 };

        internal static void Configure(MEXFighter luigi, MEXFighter john, HSDRawFile file, string root)
        {
            if (MEX.Fighters.IndexOf(john) != 27 || ReferenceEquals(john, luigi))
                throw new InvalidDataException("Giant Punch requires the private John Pork fighter at internal ID 27.");
            var data = file.Roots.Select(r => r.Data).OfType<SBM_FighterData>().Single();
            if (data.FighterActionTable.Commands.Length != 320 || data.Articles._s.Length != 4)
                throw new InvalidDataException("Run john-pork-import before installing John's Giant Punch.");
            var donkey = MEX.Fighters.Single(f => f.FighterDataPath == "PlDk.dat");
            // Keep Luigi's voice, movement sounds and effects. The original DK
            // sound bank is additionally resident for the transplanted scripts.
            john.SSMBitfield1 |= donkey.SSMBitfield1;
            john.SSMBitfield2 |= donkey.SSMBitfield2;

            var native = new HawkingNativeCode(Path.Combine(root, "original", "main.dol"));
            native.Import(First, End);
            native.Import(0x8010D774, 0x8010D7A8);
            native.Import(0x8010D96C, 0x8010D9AC);
            native.Import(0x80142324, 0x80142388);
            native.LinkOriginalCode();
            native.AdaptAttributeLoads(First, End, 0);
            native.AdaptAttributeLoads(0x8010D96C, 0x8010D9AC, 0);
            native.Patch(0x8010D980, 0x80A3222C, 0x80A32238);
            // Luigi's only persistent fields are 222C (Cyclone), 2230 and
            // 2234. 2238 is unused padding inside his allocated fighter union.
            // Native motion scratch at 2340..2354 remains motion-local; only
            // the charge that survives cancellation moves to private 2238.
            int chargeAccesses = 0, stateChanges = 0;
            for (uint address = First; address < End; address += 4)
            {
                uint word = native.Read(address);
                if ((word & 0xFFFF) == 0x222C && (word >> 26 == 32 || word >> 26 == 36))
                {
                    native.Patch(address, word, (word & 0xFFFF0000) | 0x2238);
                    chargeAccesses++;
                }
                if ((word & 0xFFFF0000) == 0x38800000 && (word & 0xFFFF) >= 369 && (word & 0xFFFF) <= 378)
                {
                    native.Patch(address, word, 0x38800000u | (uint)States[(int)(word & 0xFFFF) - 369]);
                    stateChanges++;
                }
            }
            if (chargeAccesses != 23 || stateChanges != 20)
                throw new InvalidDataException("Unexpected GALE01 Giant Punch charge/state instruction layout.");
            // D774 normally also cleans up Spinning Kong. Only its punch half
            // belongs to John; never run a donor's unrelated move cleanup.
            native.Patch(0x8010D790, 0x480028E5, 0x60000000);
            // Keep Luigi's exact PUSH_ATTRS implementation, delete its obsolete
            // fireball item registration and even its unused article load.
            native.Patch(0x8014236C, 0x38800069, 0x60000000);
            native.Patch(0x80142370, 0x80680000, 0x60000000);
            native.Patch(0x80142374, 0x48129085, 0x60000000);
            Guard(native, First);
            Guard(native, 0x8010E69C);
            Guard(native, 0x8010D774);
            Guard(native, 0x8010FAD0);
            Guard(native, 0x8010FAF0);
            Guard(native, 0x8010D96C);

            uint onLoad = native.Position;
            Prologue(native);
            native.Branch(native.Location(0x80142324), true);
            ClearCharge(native);
            // efSync 1224/1225 use immutable DK bank8 effects 8002/8003.
            // LoadSync is cached and leaves Luigi's effect bank selected; this
            // works without Donkey present and does not alter either archive.
            native.Emit(0x38600008);
            native.Branch(0x8006737C, true);
            Epilogue(native);
            uint onDeath = native.Position;
            Prologue(native);
            native.Branch(luigi.Functions.OnRespawn, true);
            ClearCharge(native);
            Epilogue(native);

            uint moves = native.Position;
            for (int state = 341; state <= 366; state++)
            {
                int index = Array.IndexOf(States, state);
                uint source = index < 0 ? LuigiStates + (uint)((state - 341) * 32)
                    : DonkeyStates + (uint)((369 + index - 341) * 32);
                native.Emit(index < 0 ? native.Read(source) : (uint)Animations[index],
                    native.Read(source + 4), native.Read(source + 8));
                for (uint offset = 12; offset < 32; offset += 4)
                    native.Pointer(native.Read(source + offset));
            }
            if (file.Roots.Any(r => r.Name == "ftFunction"))
                throw new InvalidDataException("Refusing to overwrite existing John runtime hooks.");
            file.Roots.Add(new HSDRootNode { Name = "ftFunction", Data = new HSDAccessor {
                _s = HawkingNativeCode.Header(native.Code(), native.Relocations(), native.RelocationCount,
                    0, onLoad, 1, onDeath, 3, moves, 4, native.Location(First), 5, native.Location(0x8010E69C),
                    24, native.Location(0x8010D96C)) } });
            KirbyClone.Add(donkey, john);
        }

        private static void Guard(HawkingNativeCode native, uint address)
        {
            uint instruction = native.Read(address);
            if (instruction != 0x7C0802A6)
                throw new InvalidDataException("Expected a native callback prologue before adding the John kind guard.");
            uint guard = native.Position;
            native.Emit(0x8183002C, 0x818C0004, 0x2C0C001B, 0x4082000C, instruction);
            native.Branch(native.Location(address + 4));
            native.Emit(0x4E800020);
            native.BranchAt(native.Location(address), guard);
        }
        private static void Prologue(HawkingNativeCode native)
        { native.Emit(0x9421FFE0, 0x7C0802A6, 0x90010024, 0x93E1001C, 0x7C7F1B78); }
        private static void ClearCharge(HawkingNativeCode native)
        { native.Emit(0x807F002C, 0x38000000, 0x90032238); }
        private static void Epilogue(HawkingNativeCode native)
        { native.Emit(0x83E1001C, 0x80010024, 0x7C0803A6, 0x38210020, 0x4E800020); }
    }
}
