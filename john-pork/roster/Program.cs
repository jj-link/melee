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
            if (args.Length is < 1 or > 2)
            {
                Console.Error.WriteLine("Usage: CustomSmashBuilder <john-pork project directory> [<hawking project directory>]");
                return 2;
            }
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            string johnRoot = Path.GetFullPath(args[0]);
            string root = Path.GetFullPath(args[args.Length - 1]);
            string gameRoot = args.Length == 2 ? Path.GetDirectoryName(root) : root;
            BuildPaths.Initialize(gameRoot);
            string disc = Path.Combine(gameRoot, "output", "custom-smash", "disc");
            string output = args.Length == 2
                ? Path.Combine(gameRoot, "Melee - Custom Smash.iso")
                : Path.Combine(gameRoot, "playable", "Melee - Custom Smash.iso");
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

                int externalId = JohnPorkFighter.Add(johnRoot);
                var identities = new List<(string Root, string Prefix, int ExternalId)> {
                    (johnRoot, "john-pork", externalId)
                };
                if (args.Length == 2)
                    identities.Add((root, "hawking", HawkingFighter.Add(root)));
                ResultNames.Add(identities.ToArray());

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
                HawkingAudio.FinalizeSoundBank();

                Directory.CreateDirectory(Path.GetDirectoryName(output));
                Console.WriteLine("Rebuilding the separate Custom Smash ISO.");
                using (var iso = new GCILib.GCISO(image.GetBoot(), image.GetBin2(), image.GetAppLoader(), image.GetDOL()))
                {
                    foreach (string file in image.GetAllFiles())
                        iso.AddFile(file, image.GetRealFilePath(file));
                    iso.Rebuild(output, progress);
                }
                Console.WriteLine($"Built {output}");
                Console.WriteLine($"John Pork: external ID {externalId}; all original fighters retained.");
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
            ResultNames.ConfigureRuntime(baseCode);
            HawkingAudio.ConfigureRuntime(baseCode);
            baseCode.SetCheckState(true);
            var defaults = CodeLoader.FromINI(File.ReadAllBytes(Path.Combine(library, "codes.ini"))).ToList();
            // Match the supported GUI defaults, except that this build must show
            // its real winner and player-card names rather than skip results.
            defaults.Single(c => c.Name == "QOL | Skip Result Screen []").SetCheckState(false);
            var accepted = new List<Codes>();
            var addresses = new HashSet<uint>();
            foreach (var code in new[] { baseCode, MatchLoadingCode(image) }.Concat(defaults).Where(c => c.IsChecked()))
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

        private static Codes MatchLoadingCode(ImageResource image)
        {
            // Ground_801C06B8: leave stage-specific scratch-buffer setup intact,
            // but do not preload the stage DAT into the fighter archive cache.
            // grDatFiles_801C6038 then uses lbArchive_800171CC's native scene-heap
            // loader after the selection screen is freed, including particles.
            var dol = new HawkingNativeCode(image.GetDOL());
            if (dol.Read(0x801C06F0) != 0x4182002C)
                throw new InvalidDataException("The GALE01 stage archive preload branch has changed.");
            // Increase the actual synth bank before both its capacity sum and
            // HSD_SynthSFXAllocateBank. The matching animation-cache reduction
            // in ConfigureRuntime keeps the end of persistent ARAM unchanged.
            if (dol.Read(0x800284C4) != 0x90EDADA4)
                throw new InvalidDataException("The GALE01 fighter audio ARAM allocation has changed.");
            var code = new Codes { Name = "FIX | Match archive and audio allocation" };
            code.SetCompiled(HawkingNativeCode.Words(
                0x041C06F0, 0x4800002C,
                0xC20284C4, 2,
                0x3CE70000 | (HawkingAudio.AdditionalAramBytes >> 16), 0x90EDADA4,
                0x60000000, 0).GetData());
            code.SetCheckState(true);
            return code;
        }
    }
}
