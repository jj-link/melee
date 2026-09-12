using System.Numerics;
using HSDRaw;
using HSDRaw.Common;
using HSDRaw.Common.Animation;
using HSDRaw.Melee.Cmd;
using HSDRaw.Melee.Pl;
using HSDRaw.Tools;
using HSDRaw.Tools.Melee;

// The target costume is never edited here. These are animation correspondences,
// not a new bind skeleton: all 61 Luigi JOBJs retain their indices and transforms.
internal static class JohnPorkAnimations
{
    private static readonly byte[] PoseTypes = { 1, 2, 3, 5, 6, 7, 8, 9, 10 };
    // Anatomical correspondence from the two original costume hierarchies.
    // -1 leaves Luigi-only facial/accessory joints at their own local bind pose.
    private static readonly int[] SourceJoint = {
        0, 1, 2, 3, 4, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, -1,
        43, 44, -1, -1, -1, -1, -1, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64,
        -1, 25, 5, 6, 7, 8, 9, 10, 15, 16, 17, 18, 19, 20, 73, 74
    };
    private static readonly int[] TargetActions = { 295, 312, 313, 314, 315, 296, 316, 317, 318, 319 };
    private static SBM_FighterData Fighter(HSDRawFile file) => file.Roots.Select(r => r.Data).OfType<SBM_FighterData>().Single();
    private static string Rename(string name) => name.Replace("PlyDonkey5K", "PlyJohnPork5K", StringComparison.Ordinal);
    private static float Rest(HSD_JOBJ joint, byte type) => type switch {
        1 => joint.RX, 2 => joint.RY, 3 => joint.RZ, 5 => joint.TX, 6 => joint.TY, 7 => joint.TZ,
        8 => joint.SX, 9 => joint.SY, 10 => joint.SZ, _ => throw new ArgumentOutOfRangeException(nameof(type)) };
    private static Matrix4x4 Rotation(float x, float y, float z) =>
        Matrix4x4.CreateRotationX(x) * Matrix4x4.CreateRotationY(y) * Matrix4x4.CreateRotationZ(z);
    private static Matrix4x4[] BindRotations(Rig rig)
    {
        var result = new Matrix4x4[rig.Joints.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var j = rig.Joints[i];
            result[i] = Rotation(j.Node.RX, j.Node.RY, j.Node.RZ);
            if (j.Parent >= 0) result[i] *= result[j.Parent];
        }
        return result;
    }
    private static Vector3 Euler(Matrix4x4 m)
    {
        float y = MathF.Asin(Math.Clamp(-m.M13, -1, 1));
        return MathF.Abs(MathF.Cos(y)) > 1e-5f
            ? new Vector3(MathF.Atan2(m.M23, m.M33), y, MathF.Atan2(m.M12, m.M11))
            : new Vector3(MathF.Atan2(-m.M32, m.M22), y, 0);
    }

    private static void Retarget(HSD_FigaTree tree, Rig source, Rig target)
    {
        if (tree.NodeCount != source.Joints.Count || target.Joints.Count != SourceJoint.Length)
            throw new InvalidDataException("Unexpected DK/Luigi animation joint mapping.");
        var players = tree.Nodes.Select(n => n.Tracks.Where(t => PoseTypes.Contains(t.TrackType))
            .ToDictionary(t => t.TrackType, t => new FOBJ_Player(t.TrackType, t.GetKeys()))).ToArray();
        float Sample(int joint, byte type, float frame) => players[joint].TryGetValue(type, out var player)
            ? player.GetValue(frame) : Rest(source.Joints[joint].Node, type);
        var sourceBind = BindRotations(source);
        var targetBind = BindRotations(target);
        var inverseSourceBind = sourceBind.Select(Matrix4x4.Transpose).ToArray();
        var sourceWorld = new Matrix4x4[source.Joints.Count];
        var targetWorld = new Matrix4x4[target.Joints.Count];
        var keys = Enumerable.Range(0, target.Joints.Count).Select(_ => PoseTypes.Select(_ => new List<FOBJKey>()).ToArray()).ToArray();
        float ratio = target.Joints[2].Node.TY / source.Joints[2].Node.TY;
        int frames = checked((int)Math.Ceiling(tree.FrameCount));
        for (int frame = 0; frame <= frames; frame++)
        {
            float time = Math.Min(frame, tree.FrameCount);
            for (int i = 0; i < sourceWorld.Length; i++)
            {
                sourceWorld[i] = Rotation(Sample(i, 1, time), Sample(i, 2, time), Sample(i, 3, time));
                int parent = source.Joints[i].Parent;
                if (parent >= 0) sourceWorld[i] *= sourceWorld[parent];
            }
            for (int i = 0; i < targetWorld.Length; i++)
            {
                int donor = SourceJoint[i], parent = target.Joints[i].Parent;
                var joint = target.Joints[i].Node;
                Matrix4x4 local;
                if (donor < 0)
                {
                    local = Rotation(joint.RX, joint.RY, joint.RZ);
                    targetWorld[i] = parent < 0 ? local : local * targetWorld[parent];
                }
                else
                {
                    // Global bind-relative rotations correctly collapse DK's
                    // extra spine joint without imposing gorilla bind lengths.
                    targetWorld[i] = targetBind[i] * inverseSourceBind[donor] * sourceWorld[donor];
                    local = parent < 0 ? targetWorld[i] : targetWorld[i] * Matrix4x4.Transpose(targetWorld[parent]);
                }
                Vector3 rotation = Euler(local);
                for (int channel = 0; channel < PoseTypes.Length; channel++)
                {
                    byte type = PoseTypes[channel];
                    float value = type <= 3 ? (type == 1 ? rotation.X : type == 2 ? rotation.Y : rotation.Z) : Rest(joint, type);
                    // Root, pelvis and motion-extraction nodes keep DK motion
                    // deltas in Luigi units; all limb lengths stay target-bind.
                    if (donor >= 0 && type >= 5 && type <= 7 && (i <= 4 || i >= 59))
                        value += (Sample(donor, type, time) - Rest(source.Joints[donor].Node, type)) * ratio;
                    if (type <= 3 && frame > 0)
                    {
                        float previous = keys[i][channel][frame - 1].Value;
                        while (value - previous > MathF.PI) value -= MathF.Tau;
                        while (value - previous < -MathF.PI) value += MathF.Tau;
                    }
                    keys[i][channel].Add(new FOBJKey { Frame = time, Value = value, InterpolationType = GXInterpolationType.HSD_A_OP_LIN });
                }
            }
        }
        var nodes = new List<FigaTreeNode>();
        for (int i = 0; i < target.Joints.Count; i++)
        {
            var node = new FigaTreeNode();
            for (int channel = 0; channel < PoseTypes.Length; channel++)
            {
                byte type = PoseTypes[channel];
                var values = keys[i][channel];
                if (values.All(k => Math.Abs(k.Value - values[0].Value) < 1e-6f))
                {
                    if (Math.Abs(values[0].Value - Rest(target.Joints[i].Node, type)) < 1e-6f) continue;
                    values = new List<FOBJKey> { new() { Value = values[0].Value, InterpolationType = GXInterpolationType.HSD_A_OP_KEY } };
                }
                else
                {
                    var player = new FOBJ_Player(type, values);
                    AnimationKeyCompressor.CompressTrack(player);
                    values = player.Keys;
                }
                node.Tracks.Add(new HSD_Track(FOBJFrameEncoder.EncodeFrames(values, type)));
            }
            nodes.Add(node);
        }
        tree.Nodes = nodes;
    }

    private static void RemapScript(SBM_FighterSubactionData script, HashSet<HSDStruct> visited)
    {
        if (!visited.Add(script._s)) return; // Native charge script loops back to itself.
        byte[] bytes = script._s.GetData();
        for (int offset = 0; offset < bytes.Length;)
        {
            int op = bytes[offset] >> 2;
            if (op >= ActionCommon.SubActions.Count) throw new InvalidDataException("Unknown Giant Punch command.");
            int size = ActionCommon.SubActions[op].ByteSize;
            if (offset + size > bytes.Length) throw new InvalidDataException("Truncated Giant Punch command.");
            // The native GFX command's first eight bits after its opcode are
            // the attachment bone (ActionCommon's legacy label calls it Unk1).
            int shift = op == 10 ? 18 : op == 11 ? 11 : op == 28 ? 18 : -1;
            if (shift >= 0)
            {
                uint word = unchecked((uint)script._s.GetInt32(offset));
                uint mask = op == 11 ? 0x7Fu : 0xFFu;
                int bone = (int)((word >> shift) & mask);
                int mapped = Array.IndexOf(SourceJoint, bone);
                if (mapped < 0) throw new InvalidDataException($"Unmapped Giant Punch event bone {bone}.");
                script._s.SetInt32(offset, unchecked((int)((word & ~(mask << shift)) | (uint)mapped << shift)));
            }
            if (op == 5 || op == 7)
                RemapScript(script._s.GetReference<SBM_FighterSubactionData>(offset + 4)
                    ?? throw new InvalidDataException("Missing Giant Punch script branch."), visited);
            if (op == 0 || op == 6 || op == 7) return;
            offset += size;
        }
        throw new InvalidDataException("Giant Punch script has no terminator.");
    }

    public static void Import(string root)
    {
        root = Path.GetFullPath(root);
        string original = Path.Combine(root, "original"), output = Path.Combine(root, "character");
        var source = Rig.Load(Path.Combine(original, "PlDkNr.dat"));
        var target = Rig.Load(Path.Combine(original, "PlLgNr.dat"));
        var file = new HSDRawFile(Path.Combine(original, "PlLg.dat"));
        var fighter = Fighter(file);
        var donkey = Fighter(new HSDRawFile(Path.Combine(original, "PlDk.dat")));
        var actions = fighter.FighterActionTable.Commands.ToList();
        if (actions.Count != 312 || source.Joints.Count != 75 || target.Joints.Count != 61)
            throw new InvalidDataException("Expected clean GALE01 Luigi and Donkey inputs.");
        var aj = new FighterAJManager();
        aj.ScanAJData(File.ReadAllBytes(Path.Combine(original, "PlLgAJ.dat")));
        var donorAJ = new FighterAJManager();
        donorAJ.ScanAJData(File.ReadAllBytes(Path.Combine(original, "PlDkAJ.dat")));
        foreach (int index in new[] { 295, 296 }) aj.RemoveAnimation(actions[index].Name);
        var donorActions = donkey.FighterActionTable.Commands;
        var generated = new HashSet<string>();
        var visitedScripts = new HashSet<HSDStruct>();
        uint targetBoneFlags = actions[295].Flags & 0xFF;
        for (int i = 0; i < 10; i++)
        {
            var action = HSDAccessor.DeepClone<SBM_FighterAction>(donorActions[319 + i]);
            string sourceName = action.Name, name = Rename(sourceName);
            if (generated.Add(name))
            {
                var animation = new HSDRawFile(donorAJ.GetAnimationData(sourceName));
                var tree = animation.Roots.Select(r => r.Data).OfType<HSD_FigaTree>().Single();
                Retarget(tree, source, target);
                animation.Roots[0].Name = name;
                using var memory = new MemoryStream();
                animation.Save(memory);
                byte[] encoded = memory.ToArray();
                if (encoded.Length > 0x8000)
                    throw new InvalidDataException($"{name} exceeds Melee's 0x8000-byte animation allocation.");
                aj.SetAnimation(name, encoded);
            }
            action.Name = name;
            action.Flags = (action.Flags & ~0xFFu) | targetBoneFlags;
            RemapScript(action.SubAction, visitedScripts);
            int index = TargetActions[i];
            if (index == actions.Count) actions.Add(action);
            else if (index < actions.Count) actions[index] = action;
            else throw new InvalidDataException("Noncontiguous John action mapping.");
        }
        // Rebuild only the private main archive. Demo/results and every other
        // Luigi clip retain their original encoded payloads and symbols.
        var symbols = actions.Where(a => !string.IsNullOrEmpty(a.Name) && a.AnimationSize != 0).Select(a => a.Name).Distinct().ToArray();
        File.WriteAllBytes(Path.Combine(output, "PlJpAJ.dat"), aj.RebuildAJFile(symbols, true));
        foreach (var action in actions)
        {
            if (string.IsNullOrEmpty(action.Name) || action.AnimationSize == 0) continue;
            var location = aj.GetOffsetSize(action.Name);
            action.AnimationOffset = location.Item1;
            action.AnimationSize = location.Item2;
        }
        fighter.FighterActionTable.Commands = actions.ToArray();
        // Fireball was Luigi's only article. Its obsolete resource is removed;
        // slot0 now holds immutable DK special attrs for native access thunks.
        var articles = new HSDStruct(4);
        articles.SetReference(0, HSDAccessor.DeepClone<HSDAccessor>(donkey.Attributes2));
        fighter.Articles = new SBM_ArticlePointer { _s = articles };
        file.Save(Path.Combine(output, "PlJp.dat"), bufferAlign: true, optimize: false, trim: false);
        Console.WriteLine($"Imported John Giant Punch: {generated.Count} retargeted clips, 10 states, {actions.Count} action slots; Luigi's 61-joint bind is unchanged.");
    }
}
