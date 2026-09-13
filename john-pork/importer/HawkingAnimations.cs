using System.Numerics;
using System.Text.Json;
using HSDRaw;
using HSDRaw.Common;
using HSDRaw.Common.Animation;
using HSDRaw.Melee.Pl;
using HSDRaw.Tools;
using HSDRaw.Tools.Melee;

// No added joints: FigaTree node n, costume JOBJ n and all fighter bone IDs remain identical.
internal static class HawkingAnimations
{
    private static readonly byte[] PoseTypes = { 1, 2, 3, 5, 6, 7, 8, 9, 10 };
    private sealed class BindDocument
    {
        public BindJoint[] Joints { get; set; } = Array.Empty<BindJoint>();
    }
    private sealed class BindJoint
    {
        public int Index { get; set; }
        public int Parent { get; set; }
        public float[] Rotation { get; set; } = Array.Empty<float>();
        public float[] Translation { get; set; } = Array.Empty<float>();
        public float[] Scale { get; set; } = Array.Empty<float>();
        public float[] WorldMatrix { get; set; } = Array.Empty<float>();
        public float[] InverseBindMatrix { get; set; } = Array.Empty<float>();
    }
    private sealed record Archive(string Kind, string Output, string? Wrapper, FighterAJManager Manager);

    private static string Rename(string symbol) => symbol.Replace("PlyZelda", "PlyHawking", StringComparison.Ordinal)
        .Replace("PlyTaro", "PlyHwTaro", StringComparison.Ordinal);
    private static bool IsVictimTemplate(string symbol, HSD_FigaTree tree) =>
        tree.NodeCount == 52 && symbol.StartsWith("PlyTaro_Share_ACTION_TZeldaThrow", StringComparison.Ordinal);
    private static SBM_FighterData Fighter(HSDRawFile file) => file.Roots.Select(r => r.Data).OfType<SBM_FighterData>().Single();
    private static HSD_FigaTree Tree(FighterAJManager manager, string symbol) =>
        new HSDRawFile(manager.GetAnimationData(symbol)).Roots.Select(r => r.Data).OfType<HSD_FigaTree>().Single();
    private static float Rest(HSD_JOBJ j, byte type) => type switch {
        1 => j.RX, 2 => j.RY, 3 => j.RZ, 5 => j.TX, 6 => j.TY, 7 => j.TZ,
        8 => j.SX, 9 => j.SY, 10 => j.SZ, _ => throw new ArgumentOutOfRangeException(nameof(type)) };
    private static float Target(BindJoint j, byte type) => type switch {
        >= 1 and <= 3 => j.Rotation[type - 1], >= 5 and <= 7 => j.Translation[type - 5],
        >= 8 and <= 10 => j.Scale[type - 8], _ => throw new ArgumentOutOfRangeException(nameof(type)) };
    private static Dictionary<byte, FOBJ_Player>[] Players(HSD_FigaTree tree) => tree.Nodes.Select(n =>
        n.Tracks.Where(t => PoseTypes.Contains(t.TrackType)).ToDictionary(t => t.TrackType,
            t => new FOBJ_Player(t.TrackType, t.GetKeys()))).ToArray();
    private static float Sample(Dictionary<byte, FOBJ_Player>[] players, int joint, byte type, float frame, Rig rig) =>
        joint < players.Length && players[joint].TryGetValue(type, out var player)
            ? player.GetValue(frame) : Rest(rig.Joints[joint].Node, type);
    private static float[][] Reference(Rig rig, FighterAJManager aj)
    {
        var symbol = aj.GetAnimationSymbols().Single(s => s.EndsWith("_ACTION_Wait1_figatree", StringComparison.Ordinal));
        var players = Players(Tree(aj, symbol));
        return rig.Joints.Select((_, i) => PoseTypes.Select(t => Sample(players, i, t, 0, rig)).ToArray()).ToArray();
    }

    private static List<Archive> Archives(string root, HSDRawFile fighter)
    {
        var archives = new List<Archive> {
            new("main", "PlHwAJ.dat", null, new FighterAJManager(File.ReadAllBytes(Path.Combine(root, "original/PlZdAJ.dat"))))
        };
        var wanted = new Dictionary<string, (string Kind, string Output, string Wrapper)> {
            ["ftDemoResultMotionFileZelda"] = ("result", "HwDemoResult.dat", "HwDemoResult"),
            ["ftDemoIntroMotionFileZelda"] = ("intro", "HwDemoIntro.dat", "HwDemoIntro"),
            ["ftDemoEndingMotionFileZelda"] = ("ending", "HwDemoEnding.dat", "HwDemoEnding"),
            ["ftDemoViWaitMotionFileZelda"] = ("wait", "PlHwDViWaitAJ.dat", "HwDemoViWait")
        };
        // Original intro/ending/result containers are shared disc files, not PlZdAJ.
        var files = new List<HSDRawFile> { fighter };
        foreach (var name in new[] { "PlZdDViWaitAJ.dat", "GmRstMZd.dat", "IrAls.dat", "GmRegEnd.dat" })
        {
            var path = Path.Combine(root, "original", name);
            if (File.Exists(path)) files.Add(new HSDRawFile(path));
        }
        foreach (var file in files)
            foreach (var node in file.Roots)
                if (wanted.Remove(node.Name, out var description))
                    archives.Add(new Archive(description.Kind, description.Output, description.Wrapper,
                        new FighterAJManager(node.Data._s.GetData())));
        if (wanted.Count != 0)
            throw new InvalidDataException("Missing original demo archives: " + string.Join(", ", wanted.Keys));
        return archives;
    }

    private static Dictionary<string, float> BombDurations(string root)
    {
        var data = File.ReadAllBytes(Path.Combine(root, "original/PlSsAJ.dat"));
        var actions = Fighter(new HSDRawFile(Path.Combine(root, "original/PlSs.dat"))).FighterActionTable.Commands;
        var mapping = new Dictionary<string, int> {
            ["SpecialLw"] = 309, ["SpecialAirLw"] = 310,
            ["SpecialLw2"] = 295, ["SpecialAirLw2"] = 296
        };
        // Samus reuses names for bomb-jump and bomb-drop; offsets, not symbols, distinguish them.
        return mapping.ToDictionary(p => p.Key, p => {
            var action = actions[p.Value];
            var file = new HSDRawFile(data.AsSpan(action.AnimationOffset, action.AnimationSize).ToArray());
            return file.Roots.Select(r => r.Data).OfType<HSD_FigaTree>().Single().FrameCount;
        });
    }
    private static string Action(string symbol)
    {
        int start = symbol.IndexOf("_ACTION_", StringComparison.Ordinal);
        return start < 0 ? symbol : symbol[(start + 8)..].Replace("_figatree", "", StringComparison.Ordinal);
    }
    private static bool UsesSeatedPose(string action) => action is
        "Wait1" or "Wait2" or "Wait3" or "Wait4" or "WaitItem" or
        "WalkSlow" or "WalkMiddle" or "WalkFast" or "WalkBrake" or
        "Dash" or "Run" or "RunBrake" or "Turn" or "TurnRun" or
        "HeavyWalk1" or "HeavyWalk2";


    public static void Export(string root)
    {
        root = Path.GetFullPath(root);
        string output = Path.Combine(root, "character");
        Directory.CreateDirectory(output);
        var rig = Rig.Load(Path.Combine(root, "original/PlZdNr.dat"));
        if (rig.Joints.Count != 118) throw new InvalidDataException("Expected Zelda v1.02's 118-joint costume.");
        rig.Export(Path.Combine(output, "hawking-source-rig.json"));
        var file = new HSDRawFile(Path.Combine(root, "original/PlZd.dat"));
        var archives = Archives(root, file);
        var reference = Reference(rig, archives[0].Manager);
        var durations = BombDurations(root);
        var shooting = new HawkingSamusAnimations(root);
        var replaced = Fighter(file).FighterActionTable.Commands.Where((_, i) => HawkingSamusAnimations.IsReplacedAction(i))
            .Select(a => a.Name).ToHashSet();
        var previewNames = new HashSet<string> { "Wait1", "WalkMiddle", "Run", "JumpF", "Landing", "Attack11", "AttackAirF", "AttackS4S", "DamageN1", "ThrowF", "SpecialLw", "SpecialAirLw" };
        var clips = new List<object>();
        var manifest = new List<object>();
        foreach (var archive in archives)
            foreach (string symbol in archive.Manager.GetAnimationSymbols().Distinct())
            {
                if (archive.Kind == "main" && replaced.Contains(symbol)) continue;
                var tree = Tree(archive.Manager, symbol);
                if (tree.NodeCount != rig.Joints.Count && !IsVictimTemplate(symbol, tree))
                    throw new InvalidDataException($"{symbol}: {tree.NodeCount} nodes do not match Zelda's 118-joint mapping.");
                string action = Action(symbol);
                bool lockSeatedPose = UsesSeatedPose(action);
                float duration = durations.GetValueOrDefault(action, tree.FrameCount);
                manifest.Add(new { symbol, outputSymbol = Rename(symbol), archive = archive.Kind, sourceFrames = tree.FrameCount, frames = duration, nodes = tree.NodeCount, lockSeatedPose });
                if (archive.Kind == "main" && !previewNames.Contains(action)) continue;
                var players = Players(tree);
                var frames = Enumerable.Range(0, (int)Math.Ceiling(duration) + 1).Select(f =>
                    rig.Joints.Select((_, i) => PoseTypes.Select(t => Sample(players, i, t,
                        duration == 0 ? 0 : Math.Min(tree.FrameCount, f * tree.FrameCount / duration), rig)).ToArray()).ToArray()).ToArray();
                clips.Add(new { name = Rename(symbol), frames = duration, lockSeatedPose, samples = frames });
            }
        // Shooting poses require the seated bind produced by Blender. Do not bootstrap
        // previews from yesterday's bind or change the normal/demo sample schema.
        foreach (var clip in shooting.Describe())
            manifest.Add(new { symbol = clip.SourceSymbol, outputSymbol = clip.Symbol, archive = "main",
                sourceFrames = clip.Frames, frames = clip.Frames, nodes = clip.Nodes, lockSeatedPose = false,
                sourceAction = clip.SourceAction, action = clip.Action, state = clip.State });
        File.WriteAllText(Path.Combine(output, "hawking-animation-input.json"), JsonSerializer.Serialize(new {
            poseTypes = PoseTypes.Select(t => (int)t).ToArray(), reference, bombDurations = durations, clips, manifest
        }, Program.Json));
        Console.WriteLine($"Exported {manifest.Count} native clips and {clips.Count} editable preview actions; Samus bomb timings {JsonSerializer.Serialize(durations)}.");
    }

    private static Matrix4x4 Matrix(float[] m)
    {
        if (m.Length != 16 || m.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Invalid bind matrix.");
        return new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
    }
    private static float TranslationRatio(Rig rig, BindJoint target, int joint)
    {
        // Root translations carry gameplay motion; only anatomical segment stretch is rescaled.
        if (joint <= 4) return 1;
        var source = rig.Joints[joint].Node;
        float length = new Vector3(source.TX, source.TY, source.TZ).Length();
        return length < 0.01f ? 1 : Math.Clamp(new Vector3(target.Translation[0], target.Translation[1], target.Translation[2]).Length() / length, 0.1f, 3f);
    }
    private static float ConvertValue(float value, int joint, byte type, Rig rig, BindJoint target, float[][] reference)
    {
        if (joint <= 3 || joint == 116 || joint == 117) return value;
        int component = Array.IndexOf(PoseTypes, type);
        float ratio = type is >= 5 and <= 7 ? TranslationRatio(rig, target, joint) : 1;
        if (type >= 8) return value * Target(target, type) / Rest(rig.Joints[joint].Node, type);
        return Target(target, type) + (value - reference[joint][component]) * ratio;
    }
    private static void Retarget(HSD_FigaTree tree, string symbol, Rig rig, BindDocument bind,
        float[][] reference, Dictionary<string, float> bombDurations)
    {
        var nodes = tree.Nodes;
        // These 52-node clips animate the OTHER fighter through the common victim
        // hierarchy. Seating them would corrupt throws; keep their poses native.
        if (IsVictimTemplate(symbol, tree)) return;
        if (nodes.Count != rig.Joints.Count) throw new InvalidDataException($"Unexpected node mapping in {symbol}.");
        string action = Action(symbol);
        bool bomb = bombDurations.TryGetValue(action, out float duration);
        bool lockSeatedPose = UsesSeatedPose(action);
        float sourceDuration = tree.FrameCount;
        for (int joint = 0; joint < nodes.Count; joint++)
        {
            if (!bomb && (joint <= 3 || joint == 116 || joint == 117)) continue;
            // The seated pelvis and chair are one rigid base. Zelda's hip
            // sways/turns would tip its wheels and pull the occupant off the seat.
            // At rest and during locomotion the whole occupant stays seated.
            // Missing pose tracks restore the costume's bind pose on state changes.
            // Root travel/facing and attack/reaction tracks stay native.
            if (joint == 4 || lockSeatedPose)
            {
                nodes[joint].Tracks.RemoveAll(t => PoseTypes.Contains(t.TrackType));
                continue;
            }
            foreach (byte type in PoseTypes)
            {
                var old = nodes[joint].Tracks.SingleOrDefault(t => t.TrackType == type);
                var keys = old?.GetKeys() ?? new List<FOBJKey> { new() { Value = Rest(rig.Joints[joint].Node, type), InterpolationType = GXInterpolationType.HSD_A_OP_KEY } };
                if (bomb)
                {
                    var player = new FOBJ_Player(type, keys);
                    keys = Enumerable.Range(0, (int)Math.Ceiling(duration) + 1).Select(f => new FOBJKey {
                        Frame = f, Value = player.GetValue(duration == 0 ? 0 : Math.Min(sourceDuration, f * sourceDuration / duration)),
                        InterpolationType = GXInterpolationType.HSD_A_OP_LIN }).ToList();
                }
                float ratio = type is >= 5 and <= 7 ? TranslationRatio(rig, bind.Joints[joint], joint) :
                    type >= 8 ? Target(bind.Joints[joint], type) / Rest(rig.Joints[joint].Node, type) : 1;
                if (joint <= 3 || joint == 116 || joint == 117) ratio = 1;
                foreach (var key in keys)
                {
                    key.Value = ConvertValue(key.Value, joint, type, rig, bind.Joints[joint], reference);
                    key.Tan *= ratio;
                }
                // Keep exact constants compact, including their final key and
                // delayed start. The library's sampled IsConstant omits the last frame.
                if (keys.All(k => Math.Abs(k.Value - keys[0].Value) < 1e-6f && Math.Abs(k.Tan) < 1e-6f))
                {
                    if (Math.Abs(keys[0].Value - Target(bind.Joints[joint], type)) < 1e-6f)
                    {
                        if (old != null) nodes[joint].Tracks.Remove(old);
                        continue;
                    }
                    keys = new List<FOBJKey> { new() {
                        Frame = keys[0].Frame, Value = keys[0].Value,
                        InterpolationType = GXInterpolationType.HSD_A_OP_KEY
                    } };
                }
                else if (bomb)
                {
                    // Use HSDRaw's established bounded-error curve compressor
                    // only for the resampled clips, retaining the 54-frame move.
                    var player = new FOBJ_Player(type, keys);
                    AnimationKeyCompressor.CompressTrack(player);
                    keys = player.Keys;
                }
                // GetKeys includes StartFrame. Preserve the original origin when encoding sparse spline keys.
                float start = keys[0].Frame;
                foreach (var key in keys) key.Frame -= start;
                var track = new HSD_Track(FOBJFrameEncoder.EncodeFrames(keys, type)) { StartFrame = checked((short)start) };
                if (old != null) nodes[joint].Tracks.Remove(old);
                nodes[joint].Tracks.Add(track);
            }
        }
        tree.Nodes = nodes;
        if (bomb) tree.FrameCount = duration;
    }

    private static byte[] EncodeAnimation(HSDRawFile animation, HSD_FigaTree tree, int limit)
    {
        using var memory = new MemoryStream();
        animation.Save(memory);
        // HSDRaw closes the supplied stream; ToArray remains valid.
        byte[] encoded = memory.ToArray();
        if (encoded.Length <= limit) return encoded;

        // Long result-screen clips can exceed the demo allocator after
        // retargeting. Compress only an oversized clip, never its root motion.
        var nodes = tree.Nodes;
        for (int joint = 4; joint < Math.Min(116, nodes.Count); joint++)
            for (int i = 0; i < nodes[joint].Tracks.Count; i++)
            {
                var track = nodes[joint].Tracks[i];
                if (!PoseTypes.Contains(track.TrackType)) continue;
                var keys = track.GetKeys();
                if (keys.Count <= 1) continue;
                float start = keys[0].Frame;
                foreach (var key in keys) key.Frame -= start;
                var player = new FOBJ_Player(track.TrackType, keys);
                AnimationKeyCompressor.CompressTrack(player);
                nodes[joint].Tracks[i] = new HSD_Track(FOBJFrameEncoder.EncodeFrames(player.Keys, track.TrackType))
                    { StartFrame = checked((short)start) };
            }
        tree.Nodes = nodes;
        using var compacted = new MemoryStream();
        animation.Save(compacted);
        return compacted.ToArray();
    }

    public static void Import(string root)
    {
        root = Path.GetFullPath(root);
        string output = Path.Combine(root, "character");
        var originalRig = Rig.Load(Path.Combine(root, "original/PlZdNr.dat"));
        var bind = JsonSerializer.Deserialize<BindDocument>(File.ReadAllText(Path.Combine(output, "hawking-bind.json")), Program.Json)
            ?? throw new InvalidDataException("Missing seated bind document.");
        if (bind.Joints.Length != originalRig.Joints.Count) throw new InvalidDataException("Seated rig must retain every original joint.");
        var costume = Rig.Load(Path.Combine(root, "original/PlZdNr.dat"));
        for (int i = 0; i < bind.Joints.Length; i++)
        {
            var b = bind.Joints[i];
            if (b.Index != i || b.Parent != originalRig.Joints[i].Parent || b.Rotation.Length != 3 || b.Translation.Length != 3 || b.Scale.Length != 3)
                throw new InvalidDataException($"Invalid anatomical mapping at joint {i}.");
            var j = costume.Joints[i].Node;
            j.RX = b.Rotation[0]; j.RY = b.Rotation[1]; j.RZ = b.Rotation[2];
            j.TX = b.Translation[0]; j.TY = b.Translation[1]; j.TZ = b.Translation[2];
            j.SX = b.Scale[0]; j.SY = b.Scale[1]; j.SZ = b.Scale[2];
            if (i != 0)
            {
                j.InverseWorldTransform = Rig.ToHsd(Matrix(b.InverseBindMatrix));
                j.Flags |= JOBJ_FLAG.SKELETON;
            }
        }
        costume.File.Roots[0].Name = Rename(costume.RootName);
        string seatedSource = Path.Combine(output, "PlHw-bind.dat");
        costume.File.Save(seatedSource, bufferAlign: true, optimize: false, trim: false);
        MeshImporter.Import(seatedSource, Path.Combine(output, "hawking-mesh.json"), Path.Combine(output, "PlHwNr.dat"));

        var fighterFile = new HSDRawFile(Path.Combine(root, "original/PlZd.dat"));
        var fighter = Fighter(fighterFile);
        var archives = Archives(root, fighterFile);
        var reference = Reference(originalRig, archives[0].Manager);
        var durations = BombDurations(root);
        var shooting = new HawkingSamusAnimations(root);
        var replaced = fighter.FighterActionTable.Commands.Where((_, i) => HawkingSamusAnimations.IsReplacedAction(i))
            .Select(a => a.Name).ToHashSet();
        foreach (string symbol in replaced) archives[0].Manager.RemoveAnimation(symbol);
        var shootingClips = Array.Empty<HawkingSamusAnimations.Clip>();
        var files = new List<object>();
        foreach (var archive in archives)
        {
            var symbols = archive.Manager.GetAnimationSymbols().Distinct().ToArray();
            int maxClipBytes = 0;
            foreach (string symbol in symbols)
            {
                var animation = new HSDRawFile(archive.Manager.GetAnimationData(symbol));
                var tree = animation.Roots.Select(r => r.Data).OfType<HSD_FigaTree>().Single();
                Retarget(tree, symbol, originalRig, bind, reference, durations);
                animation.Roots[0].Name = Rename(symbol);
                int limit = archive.Kind == "main" ? 0x8000 : 0xB000;
                byte[] encoded = EncodeAnimation(animation, tree, limit);
                if (encoded.Length > limit)
                    throw new InvalidDataException($"{symbol}: {encoded.Length} bytes exceed Melee's {limit}-byte {archive.Kind} animation allocation.");
                maxClipBytes = Math.Max(maxClipBytes, encoded.Length);
                archive.Manager.RemoveAnimation(symbol);
                archive.Manager.SetAnimation(Rename(symbol), encoded);
            }
            var outputSymbols = symbols.Select(Rename).ToList();
            if (archive.Kind == "main")
            {
                shootingClips = shooting.Import(fighter, costume, archive.Manager);
                outputSymbols.AddRange(shootingClips.Select(c => c.Symbol));
                maxClipBytes = Math.Max(maxClipBytes, shootingClips.Max(c => c.Bytes));
            }
            byte[] data = archive.Manager.RebuildAJFile(outputSymbols.ToArray(), true);
            if (archive.Wrapper == null) File.WriteAllBytes(Path.Combine(output, archive.Output), data);
            else
            {
                var wrapper = new HSDRawFile();
                var payload = new HSDAccessor();
                payload._s.SetData(data);
                wrapper.Roots.Add(new HSDRootNode { Name = archive.Wrapper, Data = payload });
                wrapper.Save(Path.Combine(output, archive.Output));
            }
            files.Add(new { kind = archive.Kind, file = archive.Output, symbol = archive.Wrapper, clips = outputSymbols.Count, maxClipBytes });
        }
        void Remap(SBM_FighterAction action, Archive archive)
        {
            if (string.IsNullOrEmpty(action.Name)) return;
            string name = Rename(action.Name);
            if (!archive.Manager.GetAnimationSymbols().Contains(name))
            {
                if (action.AnimationSize == 0) return;
                throw new InvalidDataException($"{archive.Kind} archive lacks action {name}.");
            }
            var location = archive.Manager.GetOffsetSize(name);
            action.Name = name;
            action.AnimationOffset = location.Item1;
            action.AnimationSize = location.Item2;
        }
        var actions = fighter.FighterActionTable.Commands;
        foreach (var action in actions) Remap(action, archives[0]);
        fighter.FighterActionTable.Commands = actions;
        var demos = fighter.DemoActionTable.Commands;
        for (int i = 0; i < demos.Length; i++)
        {
            string kind = i < 10 ? "result" : i < 12 ? "intro" : i == 12 ? "ending" : "wait";
            Remap(demos[i], archives.Single(a => a.Kind == kind));
        }
        fighter.DemoActionTable.Commands = demos;
        // ftDataZelda remains the fighter root: the roster clone owns its symbol metadata.
        fighterFile.Save(Path.Combine(output, "PlHw-base.dat"), bufferAlign: true, optimize: false, trim: false);
        File.WriteAllText(Path.Combine(output, "hawking-animation-manifest.json"), JsonSerializer.Serialize(new {
            jointCount = bind.Joints.Length, costume = "PlHwNr.dat", costumeSymbol = Rename(costume.RootName),
            fighter = "PlHw-base.dat", fighterSymbol = "ftDataZelda", actionCount = actions.Length,
            files, bombDurations = durations, shootingClips,
            shootingBones = new { chargeHand = new { samus = 50, hawking = 104 },
                throwN = new { samus = 51, hawking = 113 }, missile = new { samus = 56, hawking = 103 } },
            rootMotion = "Native nodes 0..3,116,117 unchanged except bomb-duration resampling and bind-relative Samus shooting motion",
            retarget = "Fixed seated pelvis/chair; Zelda Wait1-relative normals and demos; Samus upper-body global rotations with seated arm-segment alignment and hand-relative emitters; bounded-error compression for shooting, bombs and oversized demo clips"
        }, Program.Json));
        var resultArchive = archives.Single(a => a.Kind == "result");
        var waitArchive = archives.Single(a => a.Kind == "wait");
        File.WriteAllText(Path.Combine(output, "hawking-assets.json"), JsonSerializer.Serialize(new {
            animFile = archives[0].Output, animCount = actions.Length,
            rstAnimFile = resultArchive.Output, rstAnimCount = Math.Min(10, demos.Length),
            demoFile = waitArchive.Output, demoWait = waitArchive.Wrapper,
            demoResult = resultArchive.Wrapper,
            demoIntro = archives.Single(a => a.Kind == "intro").Wrapper,
            demoEnding = archives.Single(a => a.Kind == "ending").Wrapper,
            files = archives.Select(a => a.Output).ToArray()
        }, Program.Json));
        Console.WriteLine($"Imported Hawking costume, {actions.Length} action slots and {demos.Length} demo slots; no native joint indices changed.");
    }
}
