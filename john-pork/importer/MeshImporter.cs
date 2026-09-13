using System.Drawing;
using System.Numerics;
using System.Text.Json;
using HSDRaw.Common;
using HSDRaw.GX;
using HSDRaw.Tools;

internal static class MeshImporter
{
    public static void Import(string original, string meshPath, string output)
    {
        if (Path.GetFullPath(original).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must not overwrite the original DAT.");
        var rig = Rig.Load(original);
        var document = JsonSerializer.Deserialize<MeshDocument>(File.ReadAllText(meshPath), Program.Json)
            ?? throw new InvalidDataException("Mesh document is null.");
        if (document.Materials.Length == 0 || document.Meshes.Length == 0)
            throw new InvalidDataException("Materials and meshes must be nonempty.");
        if (!rig.Root.Flags.HasFlag(JOBJ_FLAG.SKELETON_ROOT))
            throw new InvalidDataException("Costume drawable owner must be the skeleton root for global envelope coordinates.");
        if (rig.Joints.Skip(1).Any(j => j.Node.Dobj != null))
            throw new InvalidDataException("This costume has non-root drawables: appending would shift existing fighter visibility indices.");
        if (rig.Root.Dobj == null)
            throw new InvalidDataException("Original costume has no drawable inventory.");

        string meshDirectory = Path.GetDirectoryName(Path.GetFullPath(meshPath))!;
        var materials = document.Materials.Select(m => MakeMaterial(m, meshDirectory)).ToArray();
        var newDrawables = new List<HSD_DOBJ>();
        int triangleCount = 0;
        foreach (var mesh in document.Meshes)
        {
            if ((uint)mesh.Material >= materials.Length)
                throw new InvalidDataException($"Material index out of range in {mesh.Name}.");
            if (mesh.Vertices.Length == 0 || mesh.Triangles.Length == 0)
                throw new InvalidDataException($"Empty mesh {mesh.Name}.");
            var converted = new GX_Vertex[mesh.Vertices.Length];
            var bones = new HSD_JOBJ[mesh.Vertices.Length][];
            var weights = new float[mesh.Vertices.Length][];
            for (int i = 0; i < mesh.Vertices.Length; i++)
            {
                var vertex = mesh.Vertices[i];
                CheckVector(vertex.Position, 3, "position");
                CheckVector(vertex.Normal, 3, "normal");
                CheckVector(vertex.Uv, 2, "uv");
                if (vertex.Joints.Length != vertex.Weights.Length || vertex.Joints.Length == 0)
                    throw new InvalidDataException($"Missing or mismatched weights at {mesh.Name}:{i}.");
                var influence = new Dictionary<int, float>();
                for (int w = 0; w < vertex.Joints.Length; w++)
                {
                    int joint = vertex.Joints[w];
                    float weight = vertex.Weights[w];
                    if ((uint)joint >= rig.Joints.Count || !float.IsFinite(weight) || weight < 0)
                        throw new InvalidDataException($"Invalid influence at {mesh.Name}:{i}.");
                    if (weight > 0) influence[joint] = influence.GetValueOrDefault(joint) + weight;
                }
                // Six influences is HSDRaw's established Melee import limit. Reject rather than silently discard deformation.
                if (influence.Count is 0 or > 6)
                    throw new InvalidDataException($"Expected 1..6 positive influences at {mesh.Name}:{i}.");
                float sum = influence.Values.Sum();
                if (!float.IsFinite(sum) || sum <= 0)
                    throw new InvalidDataException("Invalid weight sum.");
                // Runtime treats first weight >= 1-FLT_EPSILON as rigid; remove numerical dust before choosing coordinate space.
                var sorted = influence.Where(p => p.Value / sum > 1e-6f).OrderBy(p => p.Key).ToArray();
                sum = sorted.Sum(p => p.Value);
                bones[i] = sorted.Select(p => rig.Joints[p.Key].Node).ToArray();
                weights[i] = sorted.Select(p => p.Value / sum).ToArray();
                var position = new Vector3(vertex.Position[0], vertex.Position[1], vertex.Position[2]);
                var normal = new Vector3(vertex.Normal[0], vertex.Normal[1], vertex.Normal[2]);
                if (normal.LengthSquared() < 1e-12f)
                    throw new InvalidDataException($"Zero normal at {mesh.Name}:{i}.");
                if (sorted.Length == 1)
                {
                    // SetupEnvelopeModelMtx omits inverse bind for a unit-weight root-owned envelope.
                    var joint = rig.Joints[sorted[0].Key];
                    position = Vector3.Transform(position, joint.Inverse);
                    normal = Vector3.TransformNormal(normal, Matrix4x4.Transpose(joint.World));
                }
                else
                {
                    foreach (var bone in sorted)
                    {
                        var joint = rig.Joints[bone.Key];
                        if (joint.Node.InverseWorldTransform == null)
                        {
                            joint.Node.InverseWorldTransform = Rig.ToHsd(joint.Inverse);
                            joint.Node.Flags |= JOBJ_FLAG.SKELETON;
                        }
                    }
                }
                normal = Vector3.Normalize(normal);
                converted[i] = new GX_Vertex {
                    POS = new GXVector3(position.X, position.Y, position.Z),
                    NRM = new GXVector3(normal.X, normal.Y, normal.Z),
                    TEX0 = new GXVector2(vertex.Uv[0], vertex.Uv[1])
                };
            }

            HSD_POBJ? rootPolygon = null;
            HSD_POBJ? lastPolygon = null;
            // Bound each generator's ushort envelope and attribute indices; each GX palette is split to <=10 by HSDRaw.
            const int trianglesPerChunk = 4000;
            for (int start = 0; start < mesh.Triangles.Length; start += trianglesPerChunk)
            {
                int end = Math.Min(start + trianglesPerChunk, mesh.Triangles.Length);
                var triangleVertices = new List<GX_Vertex>((end - start) * 3);
                var triangleBones = new List<HSD_JOBJ[]>((end - start) * 3);
                var triangleWeights = new List<float[]>((end - start) * 3);
                for (int t = start; t < end; t++)
                {
                    var triangle = mesh.Triangles[t];
                    if (triangle.Length != 3)
                        throw new InvalidDataException($"Non-triangle at {mesh.Name}:{t}.");
                    foreach (int index in triangle)
                    {
                        if ((uint)index >= converted.Length)
                            throw new InvalidDataException($"Vertex index out of range at {mesh.Name}:{t}.");
                        triangleVertices.Add(converted[index]);
                        triangleBones.Add(bones[index]);
                        triangleWeights.Add(weights[index]);
                    }
                }
                var generator = new POBJ_Generator { UseTriangleStrips = true, CullMode = GenCullMode.None };
                var attributes = materials[mesh.Material].Textures == null
                    ? new[] { GXAttribName.GX_VA_PNMTXIDX, GXAttribName.GX_VA_POS, GXAttribName.GX_VA_NRM }
                    : new[] { GXAttribName.GX_VA_PNMTXIDX, GXAttribName.GX_VA_POS, GXAttribName.GX_VA_NRM, GXAttribName.GX_VA_TEX0 };
                var polygons = generator.CreatePOBJsFromTriangleList(triangleVertices, attributes, triangleBones, triangleWeights);
                generator.SaveChanges();
                if (polygons == null)
                    throw new InvalidDataException($"Generator emitted no geometry for {mesh.Name}.");
                if (rootPolygon == null) rootPolygon = polygons;
                else lastPolygon!.Next = polygons;
                lastPolygon = polygons;
                while (lastPolygon.Next != null) lastPolygon = lastPolygon.Next;
            }
            // ClassName is a runtime class lookup, not a user-facing mesh label: leave it null.
            newDrawables.Add(new HSD_DOBJ { Mobj = materials[mesh.Material], Pobj = rootPolygon });
            triangleCount += mesh.Triangles.Length;
        }

        // Keep all original DOBJ shells, material/texture animation targets, and absolute indices intact.
        // Preserve requested native geometry and its high/low-detail visibility switches.
        HSD_DOBJ? last = null;
        int originalDrawables = 0;
        var preserved = document.PreservedDrawables.ToDictionary(p => p.Index);
        for (var d = rig.Root.Dobj; d != null; d = d.Next)
        {
            if (preserved.TryGetValue(originalDrawables, out var keep))
            {
                var appearance = materials[keep.Material];
                d.Mobj.Material = appearance.Material;
                d.Mobj.Textures = appearance.Textures;
                d.Mobj.RenderFlags = appearance.RenderFlags;
            }
            else
            {
                d.Pobj = null;
            }
            last = d;
            originalDrawables++;
        }
        if (preserved.Keys.Any(index => index < 0 || index >= originalDrawables))
            throw new InvalidDataException("Preserved drawable index is outside the original costume.");
        foreach (var d in newDrawables)
        {
            last!.Next = d;
            last = d;
        }
        // ftParts_80075240 addresses TOBJs by flat ordinal, including empty
        // drawables. Keep every slot, but share existing same-format image storage
        // behind slots that cannot render. Never alter a visible texture object.
        var textures = new List<HSD_TOBJ>();
        var visibleTextures = new HashSet<HSDRaw.HSDStruct>();
        for (var d = rig.Root.Dobj; d != null; d = d.Next)
            for (var texture = d.Mobj?.Textures; texture != null; texture = texture.Next)
            {
                if (texture.ImageData == null) continue;
                textures.Add(texture);
                if (d.Pobj != null) visibleTextures.Add(texture._s);
            }
        var smallest = textures.GroupBy(t => t.ImageData.Format).ToDictionary(
            g => g.Key, g => g.MinBy(t => t.ImageData.Width * t.ImageData.Height)!);
        foreach (var texture in textures)
        {
            if (visibleTextures.Contains(texture._s)) continue;
            var shared = smallest[texture.ImageData.Format];
            texture.ImageData = shared.ImageData;
            texture.TlutData = shared.TlutData;
        }
        rig.Root.Flags |= JOBJ_FLAG.OPA | JOBJ_FLAG.ROOT_OPA | JOBJ_FLAG.LIGHTING | JOBJ_FLAG.TEXGEN;
        // Discard unreachable buffers and deduplicate identical data, but do not trim
        // live structures: fighter material-animation targets and skeleton indices stay intact.
        rig.File.Save(output, bufferAlign: true, optimize: true, trim: false);
        Console.WriteLine($"Saved {output}: {rig.Joints.Count} original joints, {originalDrawables} preserved slots, {preserved.Count} native hand drawables, {newDrawables.Count} appended meshes, {triangleCount} new triangles.");
    }

    private static void CheckVector(float[] values, int length, string label)
    {
        if (values.Length != length || values.Any(v => !float.IsFinite(v)))
            throw new InvalidDataException($"Expected {length} finite {label} components.");
    }

    private static HSD_MOBJ MakeMaterial(MaterialInput input, string directory)
    {
        CheckVector(input.Diffuse, 4, "diffuse");
        if (input.Diffuse.Any(v => v < 0 || v > 1) || input.Diffuse[3] != 1)
            throw new InvalidDataException($"Material {input.Name} must have RGB in 0..1 and opaque alpha=1.");
        static byte ColorByte(float v) => (byte)Math.Round(v * 255);
        var material = new HSD_MOBJ {
            RenderFlags = RENDER_MODE.DIFFUSE,
            Material = new HSD_Material {
                AMB_R = 255, AMB_G = 255, AMB_B = 255, AMB_A = 255,
                DIF_R = ColorByte(input.Diffuse[0]), DIF_G = ColorByte(input.Diffuse[1]),
                DIF_B = ColorByte(input.Diffuse[2]), DIF_A = 255,
                SPC_R = 0, SPC_G = 0, SPC_B = 0, SPC_A = 255,
                Alpha = 1, Shininess = 0
            }
        };
        if (!string.IsNullOrWhiteSpace(input.Texture))
        {
            string texturePath = Path.IsPathRooted(input.Texture) ? input.Texture : Path.Combine(directory, input.Texture);
            using var source = new Bitmap(texturePath);
            if (source.Width > 512 || source.Height > 512 || source.Width < 4 || source.Height < 4
                || !IsPowerOfTwo(source.Width) || !IsPowerOfTwo(source.Height))
                throw new InvalidDataException($"Texture {texturePath} dimensions must be powers of two, 4..512.");
            // GXImageConverter historically calls this RGBA but consumes native Windows BGRA bytes.
            var pixels = new byte[source.Width * source.Height * 4];
            var colors = new HashSet<int>();
            for (int y = 0; y < source.Height; y++)
                for (int x = 0; x < source.Width; x++)
                {
                    var color = source.GetPixel(x, y);
                    if (color.A != 255)
                        throw new InvalidDataException($"Texture {texturePath} is not opaque at ({x},{y}).");
                    int i = (y * source.Width + x) * 4;
                    // Keep the existing RGB565 precision before lossless palette encoding.
                    pixels[i] = (byte)(color.B & 0xF8);
                    pixels[i + 1] = (byte)(color.G & 0xFC);
                    pixels[i + 2] = (byte)(color.R & 0xF8);
                    pixels[i + 3] = 255;
                    if (colors.Count <= 256)
                        colors.Add(pixels[i] | pixels[i + 1] << 8 | pixels[i + 2] << 16);
                }
            var texture = new HSD_TOBJ {
                TexMapID = GXTexMapID.GX_TEXMAP0, GXTexGenSrc = GXTexGenSrc.GX_TG_TEX0,
                SX = 1, SY = 1, SZ = 1, RepeatS = 1, RepeatT = 1,
                WrapS = GXWrapMode.CLAMP, WrapT = GXWrapMode.CLAMP,
                Flags = TOBJ_FLAGS.LIGHTMAP_DIFFUSE | TOBJ_FLAGS.COLORMAP_MODULATE,
                Blending = 1, MagFilter = GXTexFilter.GX_LINEAR,
                LOD = new HSD_TOBJ_LOD { MinFilter = GXTexFilter.GX_LINEAR }
            };
            // CI4 uses 8x8 tiles and a 32-byte palette; CI8 uses 8x4 tiles
            // and a 512-byte palette. Both preserve the existing RGB565 colors.
            int ci4Bytes = ((source.Width + 7) & ~7) * ((source.Height + 7) & ~7) / 2 + 32;
            int ci8Bytes = ((source.Width + 7) & ~7) * source.Height + 512;
            var format = colors.Count <= 16 && ci4Bytes < pixels.Length / 2 ? GXTexFmt.CI4
                : colors.Count <= 256 && ci8Bytes < pixels.Length / 2 ? GXTexFmt.CI8
                : GXTexFmt.RGB565;
            texture.EncodeImageData(pixels, source.Width, source.Height, format, GXTlutFmt.RGB565);
            material.Textures = texture;
            material.RenderFlags |= RENDER_MODE.TEX0;
        }
        return material;
    }

    private static bool IsPowerOfTwo(int n) => (n & (n - 1)) == 0;
}
