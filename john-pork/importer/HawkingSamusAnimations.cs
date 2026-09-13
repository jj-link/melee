using System.Numerics;
using HSDRaw;
using HSDRaw.Common;
using HSDRaw.Common.Animation;
using HSDRaw.Melee.Cmd;
using HSDRaw.Melee.Pl;
using HSDRaw.Tools;
using HSDRaw.Tools.Melee;

// Animation-only correspondences: the approved 118-joint seated costume is never changed.
internal sealed class HawkingSamusAnimations
{
    private static readonly byte[] PoseTypes = { 1, 2, 3, 5, 6, 7, 8, 9, 10 };
    private static readonly int[] TargetActions = { 295, 296, 297, 298, 299, 300, 301, 302, 311, 312 };
    private static readonly int[] TargetStates = { 341, 342, 343, 344, 345, 346, 347, 348, 359, 360 };
    private static readonly int[] Emitters = { 103, 113 };
    private static readonly string[] Names = {
        "SpecialNStart", "SpecialNHold", "SpecialNCancel", "SpecialN", "SpecialAirNStart", "SpecialAirN",
        "SpecialS", "SpecialSSmash", "SpecialAirS", "SpecialAirSSmash"
    };
    private static readonly int[] SourceJoint = Correspondence();
    private readonly Rig source;
    private readonly SBM_FighterAction[] actions;
    private readonly byte[] archive;

    internal sealed record Clip(int SourceAction, int Action, int State, string SourceSymbol, string Symbol,
        float Frames, int Nodes, int Bytes);

    public HawkingSamusAnimations(string root)
    {
        source = Rig.Load(Path.Combine(root, "original/PlSsNr.dat"));
        actions = new HSDRawFile(Path.Combine(root, "original/PlSs.dat")).Roots.Select(r => r.Data)
            .OfType<SBM_FighterData>().Single().FighterActionTable.Commands;
        archive = File.ReadAllBytes(Path.Combine(root, "original/PlSsAJ.dat"));
        if (source.Joints.Count != 60 || actions.Length != 313)
            throw new InvalidDataException("Expected GALE01 Samus's 60-joint costume and 313 actions.");
    }

    public static bool IsReplacedAction(int action) => action is >= 295 and <= 302;
    private static string Symbol(int index) => $"PlyHawking5K_Share_ACTION_{Names[index]}_figatree";
    private HSDRawFile Animation(int index)
    {
        var action = actions[297 + index];
        // Read the actual action slice: Samus AJ contains duplicate symbols elsewhere.
        return new HSDRawFile(archive.AsSpan(action.AnimationOffset, action.AnimationSize).ToArray());
    }
    public IEnumerable<Clip> Describe()
    {
        for (int i = 0; i < TargetActions.Length; i++)
        {
            var tree = Animation(i).Roots.Select(r => r.Data).OfType<HSD_FigaTree>().Single();
            yield return new Clip(297 + i, TargetActions[i], TargetStates[i], actions[297 + i].Name,
                Symbol(i), tree.FrameCount, 118, 0);
        }
    }

    private static int[] Correspondence()
    {
        var map = Enumerable.Repeat(-1, 118).ToArray();
        foreach (var (target, donor) in new (int, int)[] {
            (0, 0), (1, 1), (2, 2), (3, 3), (66, 19), (67, 20),
            (68, 21), (69, 22), (70, 23), (71, 25), (72, 26), (73, 27), (76, 28),
            (77, 29), (78, 30), (79, 31), (80, 32), (81, 33), (82, 34), (83, 35), (84, 36),
            (85, 37), (86, 38), (87, 39), (88, 41), (89, 42),
            (96, 43), (97, 44), (98, 45), (99, 47), (100, 48), (101, 49), (104, 50),
            (113, 51), (103, 56), (116, 58), (117, 59)
        }) map[target] = donor;
        return map;
    }

    private static float Rest(HSD_JOBJ j, byte type) => type switch {
        1 => j.RX, 2 => j.RY, 3 => j.RZ, 5 => j.TX, 6 => j.TY, 7 => j.TZ,
        8 => j.SX, 9 => j.SY, 10 => j.SZ, _ => throw new ArgumentOutOfRangeException(nameof(type)) };
    private static Matrix4x4 Rotation(float x, float y, float z) =>
        Matrix4x4.CreateRotationX(x) * Matrix4x4.CreateRotationY(y) * Matrix4x4.CreateRotationZ(z);
    private static Vector3 Euler(Matrix4x4 m)
    {
        float y = MathF.Asin(Math.Clamp(-m.M13, -1, 1));
        return MathF.Abs(MathF.Cos(y)) > 1e-5f
            ? new Vector3(MathF.Atan2(m.M23, m.M33), y, MathF.Atan2(m.M12, m.M11))
            : new Vector3(MathF.Atan2(-m.M32, m.M22), y, 0);
    }
    private static Matrix4x4[] BindWorld(Rig rig, bool rotationOnly)
    {
        var world = new Matrix4x4[rig.Joints.Count];
        for (int i = 0; i < world.Length; i++)
        {
            var j = rig.Joints[i].Node;
            world[i] = Rotation(j.RX, j.RY, j.RZ);
            if (!rotationOnly) world[i] = Matrix4x4.CreateScale(j.SX, j.SY, j.SZ) * world[i]
                * Matrix4x4.CreateTranslation(j.TX, j.TY, j.TZ);
            int parent = rig.Joints[i].Parent;
            if (parent >= 0) world[i] *= world[parent];
        }
        return world;
    }
    private static Matrix4x4 Align(Vector3 from, Vector3 to)
    {
        from = Vector3.Normalize(from);
        to = Vector3.Normalize(to);
        float dot = Math.Clamp(Vector3.Dot(from, to), -1, 1);
        if (dot > 1 - 1e-6f) return Matrix4x4.Identity;
        Vector3 axis = Vector3.Cross(from, to);
        if (dot < -1 + 1e-6f)
            axis = Vector3.Cross(from, MathF.Abs(from.X) < .9f ? Vector3.UnitX : Vector3.UnitY);
        return Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(dot));
    }

    private void Retarget(HSD_FigaTree tree, Rig target)
    {
        if (tree.NodeCount != source.Joints.Count || target.Joints.Count != SourceJoint.Length)
            throw new InvalidDataException("Unexpected Samus/Hawking shooting rig.");
        var players = tree.Nodes.Select(n => n.Tracks.Where(t => PoseTypes.Contains(t.TrackType))
            .ToDictionary(t => t.TrackType, t => new FOBJ_Player(t.TrackType, t.GetKeys()))).ToArray();
        float Sample(int joint, byte type, float frame) => players[joint].TryGetValue(type, out var player)
            ? player.GetValue(frame) : Rest(source.Joints[joint].Node, type);
        var sourceBind = BindWorld(source, true);
        var targetBind = BindWorld(target, true);
        var sourcePositions = BindWorld(source, false);
        var targetPositions = BindWorld(target, false);
        var inverseSourceBind = sourceBind.Select(Matrix4x4.Transpose).ToArray();
        // The seated bind moves elbow/wrist pivots but keeps Zelda's original axes.
        // Align each actual segment, not just its Euler basis, to the donor limb.
        // This lifts the shooting arm instead of rotating a still-folded arm into the chair.
        foreach (var (joint, child, donor, donorChild) in new (int, int, int, int)[] {
            (72, 73, 26, 27), (73, 76, 27, 28), (100, 101, 48, 49), (101, 104, 49, 50)
        }) targetBind[joint] *= Align(targetPositions[child].Translation - targetPositions[joint].Translation,
            sourcePositions[donorChild].Translation - sourcePositions[donor].Translation);
        float rootRatio = target.Joints[2].Node.TY / source.Joints[2].Node.TY;
        float handRatio = Vector3.Distance(targetPositions[101].Translation, targetPositions[104].Translation)
            / Vector3.Distance(sourcePositions[49].Translation, sourcePositions[50].Translation);
        var sourceWorld = new Matrix4x4[source.Joints.Count];
        var sourceFull = new Matrix4x4[source.Joints.Count];
        var targetWorld = new Matrix4x4[target.Joints.Count];
        var targetFull = new Matrix4x4[target.Joints.Count];
        var localRotation = new Vector3[target.Joints.Count];
        var localPosition = new Vector3[target.Joints.Count];
        var keys = SourceJoint.Select(donor => donor < 0 ? Array.Empty<List<FOBJKey>>()
            : PoseTypes.Select(_ => new List<FOBJKey>()).ToArray()).ToArray();
        int frames = checked((int)Math.Ceiling(tree.FrameCount));
        for (int frame = 0; frame <= frames; frame++)
        {
            float time = Math.Min(frame, tree.FrameCount);
            for (int i = 0; i < sourceWorld.Length; i++)
            {
                sourceWorld[i] = Rotation(Sample(i, 1, time), Sample(i, 2, time), Sample(i, 3, time));
                sourceFull[i] = Matrix4x4.CreateScale(Sample(i, 8, time), Sample(i, 9, time), Sample(i, 10, time))
                    * sourceWorld[i] * Matrix4x4.CreateTranslation(Sample(i, 5, time), Sample(i, 6, time), Sample(i, 7, time));
                int parent = source.Joints[i].Parent;
                if (parent >= 0) { sourceWorld[i] *= sourceWorld[parent]; sourceFull[i] *= sourceFull[parent]; }
            }
            for (int i = 0; i < targetWorld.Length; i++)
            {
                int donor = SourceJoint[i], parent = target.Joints[i].Parent;
                var j = target.Joints[i].Node;
                var local = Rotation(j.RX, j.RY, j.RZ);
                // Emitters are solved after the hand (103 precedes 104 in the unchanged hierarchy).
                if (donor >= 0 && i != 103 && i != 113)
                {
                    targetWorld[i] = targetBind[i] * inverseSourceBind[donor] * sourceWorld[donor];
                    local = parent < 0 ? targetWorld[i] : targetWorld[i] * Matrix4x4.Transpose(targetWorld[parent]);
                }
                else targetWorld[i] = parent < 0 ? local : local * targetWorld[parent];
                localRotation[i] = Euler(local);
                localPosition[i] = new Vector3(j.TX, j.TY, j.TZ);
                if (donor >= 0 && (i <= 3 || i >= 116))
                    localPosition[i] += new Vector3(Sample(donor, 5, time) - Rest(source.Joints[donor].Node, 5),
                        Sample(donor, 6, time) - Rest(source.Joints[donor].Node, 6),
                        Sample(donor, 7, time) - Rest(source.Joints[donor].Node, 7)) * rootRatio;
                targetFull[i] = Matrix4x4.CreateScale(j.SX, j.SY, j.SZ) * local * Matrix4x4.CreateTranslation(localPosition[i]);
                if (parent >= 0) targetFull[i] *= targetFull[parent];
            }
            if (!Matrix4x4.Invert(sourceFull[50], out var inverseHand))
                throw new InvalidDataException("Singular Samus cannon animation.");
            foreach (int i in Emitters)
            {
                // Native raw50 is the charging hand, raw51 the release ThrowN, raw56 the missile emitter.
                // Transfer their complete hand-relative motion; do not substitute canonical FtPart labels.
                int donor = SourceJoint[i], parent = target.Joints[i].Parent;
                var relative = sourceFull[donor] * inverseHand;
                relative.Translation *= handRatio;
                var desired = relative * targetFull[104];
                if (!Matrix4x4.Invert(targetFull[parent], out var inverseParent))
                    throw new InvalidDataException("Singular Hawking emitter parent.");
                var local = desired * inverseParent;
                if (!Matrix4x4.Decompose(local, out _, out var rotation, out var position))
                    throw new InvalidDataException("Invalid Hawking emitter transform.");
                localRotation[i] = Euler(Matrix4x4.CreateFromQuaternion(rotation));
                localPosition[i] = position;
            }
            for (int i = 0; i < target.Joints.Count; i++)
                for (int channel = 0; channel < keys[i].Length; channel++)
                {
                    byte type = PoseTypes[channel];
                    float value = type <= 3 ? localRotation[i][type - 1] : type <= 7
                        ? localPosition[i][type - 5] : Rest(target.Joints[i].Node, type);
                    if (type <= 3 && frame > 0)
                    {
                        float previous = keys[i][channel][frame - 1].Value;
                        while (value - previous > MathF.PI) value -= MathF.Tau;
                        while (value - previous < -MathF.PI) value += MathF.Tau;
                    }
                    keys[i][channel].Add(new FOBJKey { Frame = time, Value = value, InterpolationType = GXInterpolationType.HSD_A_OP_LIN });
                }
        }
        var nodes = new List<FigaTreeNode>();
        for (int i = 0; i < target.Joints.Count; i++)
        {
            var node = new FigaTreeNode();
            // Unmapped pelvis, legs and chair attachments restore their exact seated bind.
            if (SourceJoint[i] >= 0)
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

    private static SBM_FighterSubactionData RemapScript(SBM_FighterSubactionData source,
        Dictionary<HSDStruct, SBM_FighterSubactionData> visited)
    {
        if (visited.TryGetValue(source._s, out var existing)) return existing;
        var result = new SBM_FighterSubactionData();
        visited.Add(source._s, result);
        var branches = new List<(int Offset, SBM_FighterSubactionData Script)>();
        using var bytes = new MemoryStream();
        byte[] input = source._s.GetData();
        bool ended = false;
        for (int offset = 0; offset < input.Length;)
        {
            int op = input[offset] >> 2;
            if (op >= ActionCommon.SubActions.Count) throw new InvalidDataException("Unknown Samus shooting event.");
            int size = ActionCommon.SubActions[op].ByteSize;
            if (size < 4 || offset + size > input.Length) throw new InvalidDataException("Truncated Samus shooting event.");
            // Model/material/cannon visibility commands refer to Samus's costume, never the chair.
            // 0x34 is NOT model switching: it sets fighter x221C_u16_y and must survive.
            if (op is not (0x1F or 0x20 or 0x21 or 0x24 or 0x25 or 0x28 or 0x29))
            {
                int outputOffset = checked((int)bytes.Position);
                byte[] command = input.AsSpan(offset, size).ToArray();
                int shift = op == 0x0A || op == 0x1C ? 18 : op == 0x0B ? 11 : -1;
                if (shift >= 0)
                {
                    uint word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(command);
                    // Common-ID GFX already resolve through Hawking's own canonical bone table.
                    if (op != 0x0A || (word & (1u << 17)) == 0)
                    {
                        if (op == 0x0A && (word & (1u << 15)) != 0)
                            throw new InvalidDataException("Samus shooting GFX uses an unsupported model-lookup bone.");
                        uint mask = op == 0x0B ? 0x7Fu : 0xFFu;
                        int bone = (int)((word >> shift) & mask);
                        int mapped = Array.IndexOf(SourceJoint, bone);
                        if (mapped < 0) throw new InvalidDataException($"Unmapped Samus shooting event bone {bone}.");
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(command,
                            (word & ~(mask << shift)) | (uint)mapped << shift);
                    }
                }
                if (op is 5 or 7)
                    branches.Add((outputOffset + 4, RemapScript(source._s.GetReference<SBM_FighterSubactionData>(offset + 4)
                        ?? throw new InvalidDataException("Missing Samus shooting script branch."), visited)));
                bytes.Write(command);
            }
            offset += size;
            if (op is 0 or 6 or 7) { ended = true; break; }
        }
        if (!ended) throw new InvalidDataException("Samus shooting script has no terminator.");
        result._s.SetData(bytes.ToArray());
        foreach (var branch in branches) result._s.SetReference(branch.Offset, branch.Script);
        return result;
    }

    public Clip[] Import(SBM_FighterData fighter, Rig target, FighterAJManager manager)
    {
        var targetActions = fighter.FighterActionTable.Commands.ToList();
        if (targetActions.Count != 311) throw new InvalidDataException("Expected Zelda's original 311 action slots.");
        uint boneFlags = targetActions[295].Flags & 0xFF;
        var clips = new List<Clip>();
        var scripts = new Dictionary<HSDStruct, SBM_FighterSubactionData>();
        for (int i = 0; i < TargetActions.Length; i++)
        {
            var action = HSDAccessor.DeepClone<SBM_FighterAction>(actions[297 + i]);
            var animation = Animation(i);
            var tree = animation.Roots.Select(r => r.Data).OfType<HSD_FigaTree>().Single();
            Retarget(tree, target);
            string symbol = Symbol(i);
            animation.Roots[0].Name = symbol;
            using var memory = new MemoryStream();
            animation.Save(memory);
            byte[] encoded = memory.ToArray();
            if (encoded.Length > 0x8000)
                throw new InvalidDataException($"{symbol}: {encoded.Length} bytes exceed Melee's 0x8000-byte animation allocation.");
            manager.SetAnimation(symbol, encoded);
            action.Name = symbol;
            action.Flags = (action.Flags & ~0xFFu) | boneFlags;
            action.SubAction = RemapScript(action.SubAction, scripts);
            if (TargetActions[i] == targetActions.Count) targetActions.Add(action);
            else targetActions[TargetActions[i]] = action;
            clips.Add(new Clip(297 + i, TargetActions[i], TargetStates[i], actions[297 + i].Name,
                symbol, tree.FrameCount, tree.NodeCount, encoded.Length));
        }
        fighter.FighterActionTable.Commands = targetActions.ToArray();
        return clips.ToArray();
    }
}
