using HSDRaw;
using HSDRaw.Common.Animation;
using HSDRaw.Melee.Mn;

internal static class MenuPortraits
{
    private static IEnumerable<HSD_TexAnim> Banks(HSD_MatAnimJoint? node)
    {
        for (var joint = node; joint != null; joint = joint.Next)
        {
            for (var material = joint.MaterialAnimation; material != null; material = material.Next)
                for (var texture = material.TextureAnimation; texture != null; texture = texture.Next)
                    yield return texture;
            foreach (var texture in Banks(joint.Child)) yield return texture;
        }
    }

    private static HSD_TexAnim[] PortraitBanks(HSDRawFile file)
    {
        var menu = file.Roots.Select(r => r.Data).OfType<SBM_SelectChrDataTable>().Single();
        return Banks(menu.PortraitMaterialAnimation).ToArray();
    }

    public static void Export(string input, string output)
    {
        var banks = PortraitBanks(new HSDRawFile(input));
        Directory.CreateDirectory(output);
        for (int b = 0; b < banks.Length; b++)
        {
            var images = banks[b].ToTOBJs();
            Console.WriteLine($"Portrait bank {b}: {images.Length} images");
            for (int frame = 0; frame < images.Length; frame++)
            {
                var texture = images[frame];
                UiTextures.SaveImage(texture, Path.Combine(output, $"bank-{b:D2}-frame-{frame:D3}.png"));
            }
        }
    }

    public static void Import(string input, int bankIndex, int frame, string imagePath, string output)
    {
        if (Path.GetFullPath(input).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must not overwrite the original menu DAT.");
        var file = new HSDRawFile(input);
        var bank = PortraitBanks(file)[bankIndex];
        var sourceTexture = bank.ToTOBJs()[frame];
        var original = sourceTexture.ImageData;
        var texture = UiTextures.LoadImage(imagePath);
        if (texture.ImageData.Width != original.Width || texture.ImageData.Height != original.Height)
            throw new InvalidDataException($"Portrait must retain {original.Width}x{original.Height} dimensions.");
        // Versus and single-player menus have separate animation banks referencing
        // copies of the same portrait. Replace every identical image/palette pair.
        var originalPixels = original.ImageData;
        var originalPalette = sourceTexture.TlutData?.TlutData ?? [];
        var menu = file.Roots.Select(r => r.Data).OfType<SBM_SelectChrDataTable>().Single();
        var sharedBanks = new[] { menu.MenuMaterialAnimation, menu.SingleMenuMaterialAnimation,
            menu.PortraitMaterialAnimation }.SelectMany(Banks).DistinctBy(b => b._s);
        int replaced = 0;
        foreach (var shared in sharedBanks)
        {
            var images = shared.ToTOBJs();
            for (int i = 0; i < images.Length; i++)
            {
                var candidate = images[i];
                if (candidate.ImageData.Width != original.Width || candidate.ImageData.Height != original.Height
                    || candidate.ImageData.Format != original.Format
                    || candidate.TlutData?.Format != sourceTexture.TlutData?.Format
                    || !candidate.ImageData.ImageData.AsSpan().SequenceEqual(originalPixels)
                    || !(candidate.TlutData?.TlutData ?? []).AsSpan().SequenceEqual(originalPalette))
                    continue;
                shared.ImageBuffers[i] = new HSD_TexBuffer { Data = texture.ImageData };
                replaced++;
            }
        }
        file.Save(output, bufferAlign: true, optimize: false, trim: false);
        Console.WriteLine($"Saved {output}: replaced {replaced} equivalent portrait references.");
    }
}
