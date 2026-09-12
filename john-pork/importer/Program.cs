using System.Numerics;
using System.Text.Json;
using HSDRaw;
using HSDRaw.Common;
using HSDRaw.GX;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "export-rig")
            {
                Rig.Load(args[1]).Export(args[2]);
                return 0;
            }
            if (args.Length == 2 && args[0] == "hawking-export")
            {
                HawkingAnimations.Export(args[1]);
                return 0;
            }
            if (args.Length == 2 && args[0] == "hawking-import")
            {
                HawkingAnimations.Import(args[1]);
                return 0;
            }
            if (args.Length == 4 && args[0] == "import-mesh")
            {
                MeshImporter.Import(args[1], args[2], args[3]);
                return 0;
            }
            if (args.Length == 3 && args[0] == "export-portraits")
            {
                MenuPortraits.Export(args[1], args[2]);
                return 0;
            }
            if (args.Length == 6 && args[0] == "import-portrait")
            {
                MenuPortraits.Import(args[1], int.Parse(args[2]), int.Parse(args[3]), args[4], args[5]);
                return 0;
            }
            if (args.Length == 3 && args[0] == "export-ui")
            {
                UiTextures.Export(args[1], args[2]);
                return 0;
            }
            if (args.Length == 4 && args[0] == "import-ui")
            {
                UiTextures.Import(args[1], args[2], args[3]);
                return 0;
            }
            Console.Error.WriteLine("Usage: export-rig <costume.dat> <rig.json> | import-mesh <original.dat> <mesh.json> <output.dat> | hawking-export <project-root> | hawking-import <project-root> | export-portraits <menu.dat> <directory> | import-portrait <menu.dat> <bank> <frame> <image.png> <output.dat> | export-ui <archive.dat> <directory> | import-ui <source.dat> <textures.json> <output.dat>");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }
}

internal sealed class Joint
{
    public required HSD_JOBJ Node { get; init; }
    public int Parent { get; init; }
    public Matrix4x4 World { get; init; }
    public Matrix4x4 Inverse { get; init; }
}

internal sealed class Rig
{
    public required HSDRawFile File { get; init; }
    public required string RootName { get; init; }
    public required HSD_JOBJ Root { get; init; }
    public List<Joint> Joints { get; } = new();
    public Dictionary<HSDStruct, int> Indices { get; } = new();

    public static Rig Load(string path)
    {
        var file = new HSDRawFile(path);
        var roots = file.Roots.Where(r => r.Data is HSD_JOBJ).ToArray();
        if (roots.Length != 1)
            throw new InvalidDataException($"Expected one costume JOBJ root, found {roots.Length}.");
        var rig = new Rig { File = file, RootName = roots[0].Name, Root = (HSD_JOBJ)roots[0].Data };
        rig.Visit(rig.Root, -1, Matrix4x4.Identity);
        return rig;
    }

    private void Visit(HSD_JOBJ node, int parent, Matrix4x4 parentWorld)
    {
        for (HSD_JOBJ? j = node; j != null; j = j.Next)
        {
            if (Indices.ContainsKey(j._s))
                throw new InvalidDataException("Joint hierarchy is cyclic or instanced; costume needs an unambiguous depth-first rig.");
            if (j.Flags.HasFlag(JOBJ_FLAG.USE_QUATERNION))
                throw new InvalidDataException("Quaternion JOBJ transforms are not supported by this Euler costume converter.");
            var local = Matrix4x4.CreateScale(j.SX, j.SY, j.SZ)
                * Matrix4x4.CreateRotationX(j.RX)
                * Matrix4x4.CreateRotationY(j.RY)
                * Matrix4x4.CreateRotationZ(j.RZ)
                * Matrix4x4.CreateTranslation(j.TX, j.TY, j.TZ);
            var world = local * parentWorld;
            Matrix4x4 inverse;
            // Stored inverse binds are authoritative for geometry, including joints with scale compensation.
            if (j.InverseWorldTransform != null)
            {
                inverse = FromHsd(j.InverseWorldTransform);
                if (!Matrix4x4.Invert(inverse, out world))
                    throw new InvalidDataException("Singular stored inverse bind.");
            }
            else if (!Matrix4x4.Invert(world, out inverse))
                throw new InvalidDataException("Singular joint transform.");
            int index = Joints.Count;
            Indices.Add(j._s, index);
            Joints.Add(new Joint { Node = j, Parent = parent, World = world, Inverse = inverse });
            if (j.Child != null)
                Visit(j.Child, index, world);
        }
    }

    public static Matrix4x4 FromHsd(HSD_Matrix4x3 m) => new(
        m.M11, m.M21, m.M31, 0,
        m.M12, m.M22, m.M32, 0,
        m.M13, m.M23, m.M33, 0,
        m.M14, m.M24, m.M34, 1);

    public static HSD_Matrix4x3 ToHsd(Matrix4x4 m) => new()
    {
        M11 = m.M11, M12 = m.M21, M13 = m.M31, M14 = m.M41,
        M21 = m.M12, M22 = m.M22, M23 = m.M32, M24 = m.M42,
        M31 = m.M13, M32 = m.M23, M33 = m.M33, M34 = m.M43
    };

    private static float[] MatrixArray(Matrix4x4 m) => new[] {
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 };

    public void Export(string path)
    {
        var drawables = new List<object>();
        var geometry = new List<object>();
        int drawableIndex = 0;
        for (int ji = 0; ji < Joints.Count; ji++)
        {
            var joint = Joints[ji];
            int localIndex = 0;
            for (var d = joint.Node.Dobj; d != null; d = d.Next, localIndex++, drawableIndex++)
            {
                var polygons = new List<object>();
                int pi = 0;
                for (var p = d.Pobj; p != null; p = p.Next, pi++)
                {
                    var dl = p.ToDisplayList();
                    var samples = new List<MeshVertex>();
                    foreach (var v in dl.Vertices)
                    {
                        var (bones, weights, transform) = DecodeBinding(ji, p, v);
                        var pos = Vector3.Transform(new Vector3(v.POS.X, v.POS.Y, v.POS.Z), transform);
                        Matrix4x4.Invert(transform, out var inv);
                        var normal = Vector3.TransformNormal(new Vector3(v.NRM.X, v.NRM.Y, v.NRM.Z), Matrix4x4.Transpose(inv));
                        if (normal.LengthSquared() > 0) normal = Vector3.Normalize(normal);
                        samples.Add(new MeshVertex {
                            Position = new[] { pos.X, pos.Y, pos.Z },
                            Normal = new[] { normal.X, normal.Y, normal.Z },
                            Uv = new[] { v.TEX0.X, v.TEX0.Y }, Joints = bones, Weights = weights
                        });
                    }
                    polygons.Add(new { index = pi, flags = (ushort)p.Flags, vertices = dl.Vertices.Count,
                        envelopes = p.EnvelopeWeights?.Select(e => new {
                            joints = e.JOBJs.Select(j => Indices[j._s]).ToArray(), weights = e.Weights }).ToArray() });
                    geometry.Add(new { drawable = drawableIndex, polygon = pi, owner = ji,
                        vertices = samples, primitives = dl.Primitives.Select(g => new { type = g.PrimitiveType.ToString(), count = g.Count }).ToArray() });
                }
                var material = d.Mobj?.Material;
                drawables.Add(new { index = drawableIndex, owner = ji, localIndex,
                    name = d.ClassName, renderFlags = d.Mobj == null ? 0u : (uint)d.Mobj.RenderFlags,
                    diffuse = material == null ? null : new[] { material.DIF_R / 255f, material.DIF_G / 255f, material.DIF_B / 255f, material.Alpha },
                    texture = d.Mobj?.Textures?.ImageData == null ? null : new {
                        width = d.Mobj.Textures.ImageData.Width, height = d.Mobj.Textures.ImageData.Height,
                        format = d.Mobj.Textures.ImageData.Format.ToString() }, polygons });
            }
        }
        var result = new {
            rootName = RootName,
            matrixConvention = "row-major, row-vector, translation indices 12/13/14; worldMatrix is bind/rest pose",
            uvConvention = "GX UV, v=0 at top; no automatic flip",
            joints = Joints.Select((j, i) => new {
                index = i, parent = j.Parent, flags = (uint)j.Node.Flags,
                rotation = new[] { j.Node.RX, j.Node.RY, j.Node.RZ },
                scale = new[] { j.Node.SX, j.Node.SY, j.Node.SZ },
                translation = new[] { j.Node.TX, j.Node.TY, j.Node.TZ },
                worldMatrix = MatrixArray(j.World),
                inverseBindMatrix = j.Node.InverseWorldTransform == null ? null : MatrixArray(j.Inverse)
            }).ToArray(), drawables, geometry
        };
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(result, Program.Json));
        Console.WriteLine($"Exported {Joints.Count} joints, {drawableIndex} drawables, {geometry.Count} polygons to {path}");
    }

    private (int[], float[], Matrix4x4) DecodeBinding(int owner, HSD_POBJ p, GX_Vertex v)
    {
        if (p.Flags.HasFlag(POBJ_FLAG.ENVELOPE))
        {
            var env = p.EnvelopeWeights[v.PNMTXIDX / 3];
            var bones = env.JOBJs.Select(j => Indices[j._s]).ToArray();
            var weights = env.Weights;
            if (weights.Length == 1 && weights[0] >= 1 - 1e-6f)
                return (bones, weights, Joints[bones[0]].World);
            return (bones, weights, Joints[owner].World);
        }
        if (p.Flags.HasFlag(POBJ_FLAG.UNKNOWN2))
        {
            int index = v.PNMTXIDX == 0 ? owner : Joints[owner].Parent;
            return (new[] { index }, new[] { 1f }, Joints[index].World);
        }
        int single = p.SingleBoundJOBJ == null ? owner : Indices[p.SingleBoundJOBJ._s];
        return (new[] { single }, new[] { 1f }, Joints[single].World);
    }
}

internal sealed class MeshDocument
{
    public MaterialInput[] Materials { get; set; } = [];
    public MeshInput[] Meshes { get; set; } = [];
    public PreservedDrawableInput[] PreservedDrawables { get; set; } = [];
}
internal sealed class PreservedDrawableInput
{
    public int Index { get; set; }
    public int Material { get; set; }
}
internal sealed class MaterialInput
{
    public string Name { get; set; } = "";
    public float[] Diffuse { get; set; } = [];
    public string? Texture { get; set; }
}
internal sealed class MeshInput
{
    public string Name { get; set; } = "";
    public int Material { get; set; }
    public MeshVertex[] Vertices { get; set; } = [];
    public int[][] Triangles { get; set; } = [];
}
internal sealed class MeshVertex
{
    public float[] Position { get; set; } = [];
    public float[] Normal { get; set; } = [];
    public float[] Uv { get; set; } = [];
    public int[] Joints { get; set; } = [];
    public float[] Weights { get; set; } = [];
}
