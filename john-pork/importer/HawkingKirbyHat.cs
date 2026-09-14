using HSDRaw;
using HSDRaw.Common;

internal static class HawkingKirbyHat
{
    public static void Export(string root)
    {
        root = Path.GetFullPath(root);
        string output = Path.Combine(root, "character");
        Directory.CreateDirectory(output);

        var cap = new HSDRawFile(Path.Combine(root, "original/PlKbCpSs.dat"));
        var capRoot = cap.Roots.Single();
        if (capRoot.Name != "ftDataKirbyCopySamus")
            throw new InvalidDataException("Expected the clean Samus Kirby copy cap.");
        var hat = capRoot.Data._s.GetReference<HSD_JOBJ>(0x00)
            ?? throw new InvalidDataException("Samus's Kirby cap has no hat joint.");
        // The appended geometry uses root-weighted envelopes. Keep the native
        // head transform while satisfying the existing costume import contract.
        hat.Flags |= JOBJ_FLAG.SKELETON_ROOT | JOBJ_FLAG.ENVELOPE_MODEL;
        // Reuse the loaded archive so its external-reference metadata survives.
        // Only the hat is public in this donor; the original cap stays untouched.
        capRoot.Name = "KirbyHawking_joint";
        capRoot.Data = hat;
        string donor = Path.Combine(output, "kirby-hawking-donor.dat");
        cap.Save(donor, bufferAlign: true, optimize: true, trim: false);
        Rig.Load(donor).Export(Path.Combine(output, "kirby-hawking-source-rig.json"));
        Rig.Load(Path.Combine(root, "original/PlKbNr.dat"))
            .Export(Path.Combine(output, "kirby-source-rig.json"));
    }
}
