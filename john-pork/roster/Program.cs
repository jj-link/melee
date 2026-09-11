using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using mexTool.Core;
using mexTool.Core.FileSystem;
using mexTool.Core.Installer;
using mexTool.Tools;

namespace CustomSmash
{
    internal static class BuildPaths
    {
        // The pinned upstream TempFileManager getter is redirected here at build time.
        internal static string Workspace { get; private set; }

        internal static void Initialize(string root)
        {
            Workspace = Path.Combine(root, "output", "custom-smash", "working-data") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(Workspace);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("Usage: CustomSmashBuilder <john-pork project directory>");
                return 2;
            }
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            string root = Path.GetFullPath(args[0]);
            BuildPaths.Initialize(root);
            string disc = Path.Combine(root, "output", "custom-smash", "disc");
            string output = Path.Combine(root, "playable", "Melee - Custom Smash.iso");
            int previousPercentage = -1;
            ProgressChangedEventHandler progress = (sender, e) =>
            {
                if (e.ProgressPercentage == previousPercentage) return;
                previousPercentage = e.ProgressPercentage;
                Console.WriteLine($"{e.ProgressPercentage,3}% {e.UserState}");
            };
            try
            {
                // This host only opens the freshly extracted working filesystem. It
                // never opens the original ISO or the preserved replacement build.
                MEX.Close();
                var image = AttachExtractedImage(disc);
                Console.WriteLine("Installing the pinned m-ex 1.1 runtime into the working filesystem.");
                if (!MEXInstaller.InstallMEX(image))
                    throw new InvalidDataException("m-ex installation failed.");
                var initialize = typeof(MEX).GetMethod("Init", BindingFlags.Static | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null) ?? throw new MissingMethodException("Pinned MEX.Init was not found.");
                if (!(bool)initialize.Invoke(null, null))
                    throw new InvalidDataException("m-ex could not initialize the installed working filesystem.");

                int externalId = JohnPorkFighter.Add(root);
                ResultNames.Add(root, externalId);

                var boot = image.GetBoot();
                Array.Clear(boot, 0x20, 0x60);
                byte[] title = Encoding.ASCII.GetBytes("Super Smash Bros. Melee - Custom Smash");
                Buffer.BlockCopy(title, 0, boot, 0x20, title.Length);
                image.SetBoot(boot);

                // One serialization pass: the upstream deferred-file overlay must
                // not receive repeated intermediate versions of the same file.
                Console.WriteLine("Serializing the expanded roster and interface tables.");
                MEX.PrepareSave(progress);
                ComposeCodes(image);
                image.Save(progress, null, false);

                Directory.CreateDirectory(Path.GetDirectoryName(output));
                Console.WriteLine("Rebuilding the separate Custom Smash ISO.");
                using (var iso = new GCILib.GCISO(image.GetBoot(), image.GetBin2(), image.GetAppLoader(), image.GetDOL()))
                {
                    foreach (string file in image.GetAllFiles())
                        iso.AddFile(file, image.GetRealFilePath(file));
                    iso.Rebuild(output, progress);
                }
                Console.WriteLine($"Built {output}");
                Console.WriteLine($"John Pork: external ID {externalId}; original Luigi remains external ID 7.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
            finally
            {
                MEX.Close();
            }
        }

        private static ImageResource AttachExtractedImage(string directory)
        {
            foreach (string file in new[] { "boot.bin", "bi2.bin", "apploader.img", "main.dol" })
                if (!File.Exists(Path.Combine(directory, "sys", file)))
                    throw new FileNotFoundException("Run build_custom_smash.py to extract a clean source disc first.", file);
            var filesystem = new FS_Extracted();
            if (!filesystem.TryOpen(directory))
                throw new InvalidDataException("Unable to open the extracted working filesystem.");
            var image = new ImageResource();
            var backend = typeof(ImageResource).GetField("_fileSystem", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException("Pinned ImageResource._fileSystem was not found.");
            var current = typeof(MEX).GetField("_imageResource", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingFieldException("Pinned MEX._imageResource was not found.");
            backend.SetValue(image, filesystem);
            current.SetValue(null, image);
            return image;
        }

        private static void ComposeCodes(ImageResource image)
        {
            string library = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib");
            var baseCode = CodeLoader.FromGCT(File.ReadAllBytes(Path.Combine(library, "codes.gct")));
            baseCode.SetCheckState(true);
            var defaults = CodeLoader.FromINI(File.ReadAllBytes(Path.Combine(library, "codes.ini"))).ToList();
            // Match the supported GUI defaults, except that this build must show
            // its real winner and player-card names rather than skip results.
            defaults.Single(c => c.Name == "QOL | Skip Result Screen []").SetCheckState(false);
            var accepted = new List<Codes>();
            var addresses = new HashSet<uint>();
            foreach (var code in new[] { baseCode }.Concat(defaults).Where(c => c.IsChecked()))
            {
                if (code.GetCompiled() == null)
                    throw new InvalidDataException($"Required enabled code did not compile: {code.Name}");
                var used = new HashSet<uint>(code.UsedAddresses());
                if (addresses.Overlaps(used))
                    throw new InvalidDataException($"Enabled code conflicts with an earlier code: {code.Name}");
                addresses.UnionWith(used);
                accepted.Add(code);
            }
            image.AddFile("codes.gct", CodeLoader.ToGCT(accepted));
            image.AddFile("codes.ini", CodeLoader.ToINI(Array.Empty<Codes>()));
            Console.WriteLine($"Compiled {accepted.Count} enabled runtime/default code groups; results remain enabled.");
        }
    }
}
