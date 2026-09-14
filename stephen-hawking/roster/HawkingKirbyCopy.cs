using System.IO;
using System.Linq;
using HSDRaw;
using HSDRaw.Common;
using mexTool.Core;

namespace CustomSmash
{
    internal static class HawkingKirbyCopy
    {
        internal static void Configure(MEXFighter samus, MEXFighter hawking, string root)
        {
            int donor = MEX.Fighters.IndexOf(samus), target = MEX.Fighters.IndexOf(hawking);
            if (donor != 13 || target != 28 || samus.KirbyCapSymbol != "ftDataKirbyCopySamus")
                throw new InvalidDataException("Kirby Charge Shot requires the pinned Samus13/Hawking28 roster.");
            // The hat names in ftkirby.c are shifted: these are the actual Samus
            // entries in the installed m-ex table, not Yoshi's 800F0604/800F06B4.
            if (samus.Functions.KirbyOnSwallow != 0x800F04F8 ||
                samus.Functions.KirbyOnLoseAbility != 0x800F05A8 ||
                samus.Functions.KirbySpecialN != 0x800FCF74 ||
                samus.Functions.KirbySpecialNAir != 0x800FD020)
                throw new InvalidDataException("Unexpected native Samus copy callbacks.");

            var cap = new HSDRawFile(MEX.ImageResource.GetFileData(samus.KirbyCapFileName));
            var capRoot = cap.Roots.Single(r => r.Name == samus.KirbyCapSymbol).Data._s;
            if (capRoot.GetReference<HSDAccessor>(0x00) == null || capRoot.GetReference<HSDAccessor>(0x0C) == null)
                throw new InvalidDataException("Samus's cap must contain its hat joint and Charge Shot article.");
            if (cap.Roots.Any(r => r.Name == "kbFunction" || r.Name == "itFunction"))
                throw new InvalidDataException("Refusing to overwrite donor Kirby runtime hooks.");

            var headpiece = new HSDRawFile(Path.Combine(root, "character", "kirby-hawking-hat.dat"));
            var hat = headpiece.Roots.Single(r => r.Name == "KirbyHawking_joint").Data as HSD_JOBJ;
            if (hat == null)
                throw new InvalidDataException("Hawking's Kirby headpiece must contain its generated hat joint.");
            // Replace only this private cap's model. Keep its native animation
            // descriptors at +4/+8 and Charge Shot article at +0x0C unchanged.
            capRoot.SetReference(0x00, hat);

            // Replace every inherited Zelda copy field, including null costumes.
            // The private glasses/hair cap retains Samus's native article and
            // has no full-body costume. Still bind both expanded runtime tables
            // around native gain/loss, using KirbyClone's save/restore ABI.
            hawking.KirbyCapSymbol = samus.KirbyCapSymbol;
            hawking.KirbyEffectFile = samus.KirbyEffectFile;
            hawking.KirbyEffectSymbol = samus.KirbyEffectSymbol;
            hawking.KirbyCostumes = System.ObjectExtensions.Copy(samus.KirbyCostumes);
            hawking.Functions.KirbySpecialN = samus.Functions.KirbySpecialN;
            hawking.Functions.KirbySpecialNAir = samus.Functions.KirbySpecialNAir;
            hawking.Functions.KirbyOnSwallow = samus.Functions.KirbyOnSwallow;
            hawking.Functions.KirbyOnLoseAbility = samus.Functions.KirbyOnLoseAbility;
            hawking.Functions.KirbyOnHit = 0x800FCD60;
            hawking.Functions.KirbyOnDeath = 0x800FCD60;
            hawking.Functions.KirbyOnItemInit = 0;
            hawking.Functions.KirbyOnFrame = 0;

            var native = new HawkingNativeCode(Path.Combine(root, "original", "main.dol"));
            uint cleanup = AddCleanup(native);
            uint gain = AddBoundCallback(native, samus.Functions.KirbyOnSwallow, (uint)donor * 4, (uint)target * 4);
            uint removeHat = AddBoundCallback(native, samus.Functions.KirbyOnLoseAbility, (uint)donor * 4, (uint)target * 4);
            uint lose = native.Position;
            native.Emit(0x9421FFF0, 0x7C0802A6, 0x90010014);
            native.Branch(cleanup, true); // returns the unchanged Kirby GObj in r3
            native.Emit(0x80010014, 0x7C0803A6, 0x38210010);
            native.Branch(removeHat);

            uint itemInit = native.Position;
            // m-ex KirbyIndexItems passes (r3=copied kind, r4=cap data), NOT
            // a Fighter GObj. Register this loaded cap's article directly, so
            // neither a real Samus nor her cached cap/article is required.
            // Vanilla item0x97 already selects Kirby's +22D0/+22D4 callbacks,
            // Kirby effects and Kirby states407..412 by ITEM kind. The original
            // 800F185C registration and 800FD140 spawn both use 0x97.
            native.Emit(0x8064000C, 0x38800097);
            native.Branch(0x8026B3F8);

            uint frame = AddFlashRestore(native);
            cap.Roots.Add(new HSDRootNode { Name = "kbFunction", Data = new HSDAccessor {
                _s = HawkingNativeCode.Header(native.Code(), native.Relocations(), native.RelocationCount,
                    0, gain, 1, lose, 4, cleanup, 5, itemInit, 7, frame, 8, cleanup) } });
            hawking.KirbyCapFileName = "PlKbHw.dat";
            using (var stream = new MemoryStream())
            {
                cap.Save(stream, trim: true);
                MEX.ImageResource.AddFile(hawking.KirbyCapFileName, stream.ToArray());
            }
        }

        private static uint AddBoundCallback(HawkingNativeCode native, uint callback, uint donorSlot, uint targetSlot)
        {
            uint start = native.Position;
            native.Emit(
                0x9421FFE0, 0x7C0802A6, 0x90010024, 0xBF810010,
                0x83E20124, 0x83DF0000 | donorSlot, 0x83A20120, 0x839D0000 | donorSlot,
                0x809F0000 | targetSlot, 0x909F0000 | donorSlot,
                0x809D0000 | targetSlot, 0x909D0000 | donorSlot);
            native.Branch(callback, true);
            native.Emit(0x93DF0000 | donorSlot, 0x939D0000 | donorSlot,
                0xBB810010, 0x80010024, 0x7C0803A6, 0x38210020, 0x4E800020);
            return start;
        }

        private static uint AddCleanup(HawkingNativeCode native)
        {
            uint start = native.Position;
            native.Emit(0x9421FFE0, 0x7C0802A6, 0x90010024, 0xBFA10014,
                0x7C7F1B78, 0x808DC18C, 0x83C40024); // owner; GObj entities->items
            uint loop = native.Position;
            // Ability discard/death calls 800F190C BEFORE OnLoseAbility, wiping
            // the held-item pointer. Do not cache a possibly freed GObj or rely
            // on 800F19AC's vanilla-only switch (the pinned 1.1 runtime does not
            // dispatch KirbyOnDeath there). Recover only this owner's unfired
            // Kirby Charge Shot from the live item list, saving next before free.
            native.Emit(0x2C1E0000, 0x41820040, 0x83BE0008, 0x809E002C,
                0x80040010, 0x2C000097, 0x40820024,
                0x80040518, 0x7C00F800, 0x40820018,
                0x80040DE8, 0x2C000000, 0x4082000C, 0x7FC3F378);
            native.Branch(0x802B5974, true);
            native.Emit(0x7FBEEB78); // mr r30,r29
            native.Branch(loop);
            native.Emit(0x7FE3FB78);
            native.Branch(0x800FCD60, true); // destroy charge effects; clear +22D0/+22D4/+22D8
            // Remove only our charge flash, never damage/invincibility colors.
            native.Emit(0x807F002C, 0x800304B0, 0x2C000036, 0x4082000C, 0x38800036);
            native.Branch(0x800C0200, true);
            native.Emit(0x7FE3FB78, 0xBBA10014, 0x80010024, 0x7C0803A6, 0x38210020, 0x4E800020);
            return start;
        }

        private static uint AddFlashRestore(HawkingNativeCode native)
        {
            uint start = native.Position;
            native.Emit(0x9421FFE0, 0x7C0802A6, 0x90010024,
                0x8063002C, 0x800304B0, 0x2C000036, 0x41820040, 0x80A302D4);
            // Original signed-integer -> float comparison with Kirby's actual
            // float charge-time attribute (+0x168). Keep its r2 constant and
            // stack scratch at +18/+1C; no integer reinterpretation of the float.
            native.Import(0x800EE874, 0x800EE89C);
            native.Emit(0x40820010, 0x38800036, 0x38A00000);
            native.Branch(0x800BFFD0, true);
            native.Emit(0x80010024, 0x7C0803A6, 0x38210020, 0x4E800020);
            // OnFrame restores a flash lost during other actions. The +4B0
            // guard is essential: 800BFFD0 restarts even an identical color
            // script, so calling it every frame would freeze the charge flash.
            return start;
        }
    }
}
