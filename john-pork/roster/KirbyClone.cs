using System.Collections.Generic;
using System.IO;
using HSDRaw;
using mexTool.Core;

namespace CustomSmash
{
    internal static class KirbyClone
    {
        internal static void Add(MEXFighter donkey, MEXFighter john)
        {
            var cap = new HSDRawFile(MEX.ImageResource.GetFileData(donkey.KirbyCapFileName));
            john.KirbyCapSymbol = donkey.KirbyCapSymbol;
            john.KirbyEffectFile = donkey.KirbyEffectFile;
            john.KirbyEffectSymbol = donkey.KirbyEffectSymbol;
            john.KirbyCostumes = System.ObjectExtensions.Copy(donkey.KirbyCostumes);
            john.Functions.KirbySpecialN = donkey.Functions.KirbySpecialN;
            john.Functions.KirbySpecialNAir = donkey.Functions.KirbySpecialNAir;
            john.Functions.KirbyOnSwallow = donkey.Functions.KirbyOnSwallow;
            john.Functions.KirbyOnLoseAbility = donkey.Functions.KirbyOnLoseAbility;
            // The vanilla Kirby cleanup switch recognizes copied kind3, not
            // John's kind27. Its DK branch is exactly this BC-charge/effect
            // cleanup; use the expanded m-ex copy callbacks instead of lying
            // about the copied fighter identity throughout the match.
            john.Functions.KirbyOnHit = 0x80100DE0;
            john.Functions.KirbyOnDeath = 0x80100DE0;
            john.Functions.KirbyOnItemInit = 0;
            john.Functions.KirbyOnFrame = 0;
            uint donorSlot = (uint)MEX.Fighters.IndexOf(donkey) * 4;
            uint johnSlot = (uint)MEX.Fighters.IndexOf(john) * 4;
            // DK's full-body copy needs both rtoc+124 cap data and rtoc+120
            // costume data. Bind the donor entries only during gain/loss;
            // keep John's identity and restore any real DK's cached entries.
            var bind = new uint[] {
                0x9421FFE0, 0x7C0802A6, 0x90010024, 0xBF810010,
                0x83E20124, 0x83DF0000 | donorSlot, 0x83A20120, 0x839D0000 | donorSlot,
                0x809F0000 | johnSlot, 0x909F0000 | donorSlot,
                0x809D0000 | johnSlot, 0x909D0000 | donorSlot,
                0x48000001, 0x93DF0000 | donorSlot, 0x939D0000 | donorSlot,
                0xBB810010, 0x80010024, 0x7C0803A6, 0x38210020, 0x4E800020 };
            var instructions = new List<uint>(bind);
            uint lose = (uint)instructions.Count * 4;
            instructions.AddRange(bind);
            uint restoreFlash = (uint)instructions.Count * 4;
            instructions.AddRange(new uint[] {
                // Restore DK's full-charge flash without its kind3 test.
                // Kirby's private persistent xBC is Fighter+22E8, not John's2238.
                0x8063002C, 0x808302D4, 0x80040190, 0x808322E8, 0x7C040000,
                0x4C820020, 0x3880003A, 0x38A00000, 0x48000000 });
            var code = HawkingNativeCode.Words(instructions.ToArray());
            var relocations = HawkingNativeCode.Words(
                0x0A000030, donkey.Functions.KirbyOnSwallow,
                0x0A000000 | (lose + 0x30), donkey.Functions.KirbyOnLoseAbility,
                0x0A000000 | (restoreFlash + 0x20), 0x800BFFD0);
            cap.Roots.Add(new HSDRootNode { Name = "kbFunction", Data = new HSDAccessor {
                _s = HawkingNativeCode.Header(code, relocations, 3, 0, 0, 1, lose, 7, restoreFlash) } });
            john.KirbyCapFileName = "PlKbJp.dat";
            using (var stream = new MemoryStream())
            {
                cap.Save(stream, trim: true);
                MEX.ImageResource.AddFile(john.KirbyCapFileName, stream.ToArray());
            }
        }
    }
}
