using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using HSDRaw;
using HSDRaw.Common;
using HSDRaw.Common.Animation;
using HSDRaw.GX;
using HSDRaw.Melee.Mn;
using HSDRaw.Tools;

internal static class UiTextures
{
    private sealed record Slot(string Path, HSD_TOBJ Texture, HSDStruct Owner, int Offset);

    private static IEnumerable<Slot> Model(HSD_JOBJ? root, string path)
    {
        int joint = 0;
        IEnumerable<Slot> Visit(HSD_JOBJ? node)
        {
            for (; node != null; node = node.Next)
            {
                int current = joint++, material = 0;
                for (var drawable = node.Dobj; drawable != null; drawable = drawable.Next, material++)
                {
                    int texture = 0;
                    for (var tex = drawable.Mobj?.Textures; tex != null; tex = tex.Next, texture++)
                        if (tex.ImageData != null)
                            yield return new($"{path}/joint{current}/material{material}/texture{texture}", tex, tex._s, 0x4C);
                }
                foreach (var slot in Visit(node.Child)) yield return slot;
            }
        }
        return Visit(root);
    }

    private static IEnumerable<Slot> Animation(HSD_MatAnimJoint? root, string path)
    {
        int joint = 0;
        IEnumerable<Slot> Visit(HSD_MatAnimJoint? node)
        {
            for (; node != null; node = node.Next)
            {
                int current = joint++, material = 0;
                for (var mat = node.MaterialAnimation; mat != null; mat = mat.Next, material++)
                {
                    int texture = 0;
                    for (var tex = mat.TextureAnimation; tex != null; tex = tex.Next, texture++)
                    {
                        var images = tex.ToTOBJs();
                        for (int frame = 0; frame < images.Length; frame++)
                        {
                            if (GXImageConverter.IsPalettedFormat(images[frame].ImageData.Format) && images[frame].TlutData == null)
                                throw new InvalidDataException($"Unpaired palette at {path}, joint {current}, frame {frame}.");
                            yield return new($"{path}/joint{current}/material{material}/texture{texture}/frame{frame}",
                                images[frame], tex.ImageBuffers._s, frame * 4);
                        }
                    }
                }
                foreach (var slot in Visit(node.Child)) yield return slot;
            }
        }
        return Visit(root);
    }

    private static IEnumerable<Slot> Scene(HSDNullPointerArrayAccessor<HSD_JOBJDesc>? models, string path)
    {
        if (models == null) yield break;
        for (int i = 0; i < models.Length; i++)
        {
            var model = models[i];
            if (model == null) continue;
            foreach (var slot in Model(model.RootJoint, $"{path}/model{i}")) yield return slot;
            var animations = model.MaterialAnimations;
            if (animations == null) continue;
            for (int j = 0; j < animations.Length; j++)
                foreach (var slot in Animation(animations[j], $"{path}/animation{i}.{j}")) yield return slot;
        }
    }

    private static IEnumerable<Slot> Slots(HSDRawFile file)
    {
        foreach (var root in file.Roots)
        {
            if (root.Data is SBM_SelectChrDataTable menu)
            {
                var sections = new (string Name, HSD_JOBJ Model, HSD_MatAnimJoint Animation)[] {
                    ("background", menu.BackgroundModel, menu.BackgroundMaterialAnimation),
                    ("hand", menu.HandModel, menu.HandMaterialAnimation),
                    ("token", menu.TokenModel, menu.TokenMaterialAnimation),
                    ("versus", menu.MenuModel, menu.MenuMaterialAnimation),
                    ("start", menu.PressStartModel, menu.PressStartMaterialAnimation),
                    ("debug", menu.DebugCameraModel, menu.DebugCameraMaterialAnimation),
                    ("single", menu.SingleMenuModel, menu.SingleMenuMaterialAnimation),
                    ("options", menu.SingleOptionsModel, menu.SingleOptionsMaterialAnimation),
                    ("portrait", menu.PortraitModel, menu.PortraitMaterialAnimation),
                };
                foreach (var section in sections)
                {
                    foreach (var slot in Model(section.Model, $"{root.Name}/{section.Name}/model")) yield return slot;
                    foreach (var slot in Animation(section.Animation, $"{root.Name}/{section.Name}/animation")) yield return slot;
                }
            }
            else if (root.Data is HSD_SOBJ scene)
            {
                foreach (var slot in Scene(scene.JOBJDescs, root.Name)) yield return slot;
            }
            else if (root.Data is HSDNullPointerArrayAccessor<HSD_JOBJDesc> models)
            {
                foreach (var slot in Scene(models, root.Name)) yield return slot;
            }
        }
    }

    private static string Fingerprint(HSD_TOBJ texture)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var image = texture.ImageData;
        hash.AppendData(System.Text.Encoding.ASCII.GetBytes($"{image.Width},{image.Height},{image.Format},{texture.TlutData?.Format};"));
        hash.AppendData(image.ImageData);
        if (texture.TlutData != null) hash.AppendData(texture.TlutData.TlutData);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static void SaveImage(HSD_TOBJ texture, string output)
    {
        int width = texture.ImageData.Width, height = texture.ImageData.Height;
        var pixels = texture.GetDecodedImageData();
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
        finally { bitmap.UnlockBits(data); }
        bitmap.Save(output, ImageFormat.Png);
    }

    internal static HSD_TOBJ LoadImage(string path, GXTexFmt format = GXTexFmt.RGB5A3)
    {
        using var image = new Bitmap(path);
        var pixels = new byte[image.Width * image.Height * 4];
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                var color = image.GetPixel(x, y);
                int i = (y * image.Width + x) * 4;
                pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = color.A;
            }
        var texture = new HSD_TOBJ();
        texture.EncodeImageData(pixels, image.Width, image.Height, format, GXTlutFmt.RGB5A3);
        return texture;
    }

    public static void Export(string input, string output)
    {
        var file = new HSDRawFile(input);
        Directory.CreateDirectory(output);
        var groups = Slots(file).GroupBy(slot => Fingerprint(slot.Texture)).ToArray();
        var manifest = new List<object>();
        for (int i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            var texture = group.First().Texture;
            string filename = $"{i:D4}-{texture.ImageData.Width}x{texture.ImageData.Height}.png";
            SaveImage(texture, Path.Combine(output, filename));
            manifest.Add(new { id = group.Key, image = filename, width = texture.ImageData.Width,
                height = texture.ImageData.Height, format = texture.ImageData.Format.ToString(),
                paths = group.Select(slot => slot.Path).ToArray() });
        }
        File.WriteAllText(Path.Combine(output, "textures.json"), JsonSerializer.Serialize(manifest, Program.Json));
        Console.WriteLine($"{input}: exported {groups.Length} unique interface textures to {output}.");
    }

    public static void Import(string input, string manifestPath, string output)
    {
        if (Path.GetFullPath(input).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must not overwrite the source DAT.");
        var replacements = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath), Program.Json)
            ?? throw new InvalidDataException("Expected a texture fingerprint to PNG mapping.");
        var file = new HSDRawFile(input);
        var groups = Slots(file).GroupBy(slot => Fingerprint(slot.Texture)).ToDictionary(group => group.Key);
        int replaced = 0;
        foreach (var (id, filename) in replacements)
        {
            if (!groups.TryGetValue(id, out var group)) throw new InvalidDataException($"Texture {id} not found in {input}.");
            var original = group.First().Texture.ImageData;
            var format = GXImageConverter.IsPalettedFormat(original.Format) ? GXTexFmt.RGB5A3 : original.Format;
            var replacement = LoadImage(Path.Combine(Path.GetDirectoryName(manifestPath)!, filename), format);
            if (replacement.ImageData.Width != original.Width || replacement.ImageData.Height != original.Height)
                throw new InvalidDataException($"{filename} must retain {original.Width}x{original.Height} dimensions.");
            foreach (var slot in group)
            {
                // Repoint each owner, never mutate an image shared with a different palette.
                slot.Owner.SetReference(slot.Offset, replacement.ImageData);
                replaced++;
            }
        }
        // Discard superseded textures and share identical icon buffers; Melee's preload heaps are fixed-size.
        file.Save(output, bufferAlign: true, optimize: true, trim: false);
        Console.WriteLine($"Saved {output}: replaced {replaced} texture references for {replacements.Count} interface assets.");
    }
}
