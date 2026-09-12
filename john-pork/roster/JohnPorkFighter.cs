using System;
using System.Drawing;
using System.IO;
using System.Linq;
using HSDRaw;
using HSDRaw.Common;
using HSDRaw.Common.Animation;
using HSDRaw.GX;
using HSDRaw.MEX.Menus;
using mexTool.Core;

namespace CustomSmash
{
    internal static class JohnPorkFighter
    {
        internal static int Add(string root)
        {
            if (MEX.FighterCount != 33)
                throw new InvalidDataException("John Pork must be added to a freshly installed vanilla roster.");
            var luigi = MEX.Fighters[17];
            if (luigi.FighterDataPath != "PlLg.dat")
                throw new InvalidDataException("The expected vanilla Luigi definition is missing.");

            // Match mexTool's supported clone operation, retaining registered
            // shared sound/music/stage references rather than cloned orphans.
            var john = System.ObjectExtensions.Copy(luigi);
            john.NameText = "John Pork";
            john.TargetTestStage = luigi.TargetTestStage;
            john.SoundBank = luigi.SoundBank;
            john.VictoryTheme = luigi.VictoryTheme;
            john.FighterSongID1 = luigi.FighterSongID1;
            john.FighterSongID2 = luigi.FighterSongID2;
            john.SubCharacter = luigi.SubCharacter;

            string modelPath = Path.Combine(root, "character", "PlLgNr.dat");
            var model = new HSDRawFile(modelPath);
            var costume = new MEXCostume
            {
                FileName = "PlJpNr.dat",
                ModelSymbol = model.Roots.Single(r => r.Data is HSD_JOBJ).Name,
                MaterialSymbol = model.Roots.SingleOrDefault(r => r.Name.EndsWith("_matanim_joint", StringComparison.Ordinal))?.Name,
                VisibilityIndex = luigi.Costumes[0].VisibilityIndex
            };
            using (var csp = new Bitmap(Path.Combine(root, "interface", "john-pork-menu-portrait.png")))
                costume.CSP = csp.ToTOBJ(GXTexFmt.CI8, GXTlutFmt.RGB5A3);
            using (var stock = new Bitmap(Path.Combine(root, "interface", "john-pork-stock.png")))
                costume.Icon = stock.ToTOBJ(GXTexFmt.CI4, GXTlutFmt.RGB5A3);
            MEX.ImageResource.AddFile(costume.FileName, modelPath);
            john.Costumes.Clear();
            // The CSS assigns distinct costume indices to duplicate fighters.
            // Share the same model and menu portrait across all four player slots.
            for (int slot = 0; slot < 4; slot++)
                john.Costumes.Add(costume);
            john.RedCostumeIndex = john.BlueCostumeIndex = john.GreenCostumeIndex = 0;

            // Six special/non-roster fighters must remain at the end. Inserting
            // here gives John internal ID 27 and external ID 26, leaving Luigi 17/7.
            MEX.Fighters.Insert(MEX.FighterCount - MEXFighterIDConverter.InternalSpecialCharCount, john);
            int internalId = MEX.Fighters.IndexOf(john);
            int externalId = MEXFighterIDConverter.ToExternalID(internalId, MEX.FighterCount);
            var fighterFile = new HSDRawFile(Path.Combine(root, "character", "PlJp.dat"));
            JohnPorkGameplay.Configure(luigi, john, fighterFile, root);
            john.FighterDataPath = "PlJp.dat";
            john.AnimFile = "PlJpAJ.dat";
            john.AnimCount = 320;
            using (var stream = new MemoryStream())
            {
                fighterFile.Save(stream, bufferAlign: true, optimize: false, trim: false);
                MEX.ImageResource.AddFile(john.FighterDataPath, stream.ToArray());
            }
            MEX.ImageResource.AddFile(john.AnimFile, Path.Combine(root, "character", john.AnimFile));
            AddRosterIcon(root, luigi, john);
            Console.WriteLine($"Added {john.NameText}: internal {internalId}, external {externalId}, costume {costume.FileName}.");
            return externalId;
        }

        private static void AddRosterIcon(string root, MEXFighter luigi, MEXFighter john)
        {
            // The cell beneath Young Link is empty on the clean Melee roster.
            // Deep-copy both linked graphics structures so no original icon is moved.
            var template = MEX.FighterIcons.Single(i => i.Fighter == MEX.Fighters[20]);
            var luigiIcon = MEX.FighterIcons.Single(i => i.Fighter == luigi);
            var icon = new MEXFighterIcon
            {
                Fighter = john,
                Icon = HSDAccessor.DeepClone<MEX_CSSIcon>(template.Icon),
                _joint = HSDAccessor.DeepClone<HSD_JOBJ>(template._joint),
                MaterialAnimation = HSDAccessor.DeepClone<HSD_MatAnimJoint>(template.MaterialAnimation),
                IconModel = template.IconModel,
                Frame = -1
            };
            icon._joint.Next = null;
            icon.MaterialAnimation.Next = null;
            icon.Y = template.Y - 7.0f;
            icon.FromAnimJoint(null);
            icon.Icon.StatusID = Status.UnlockedAndVisible;
            icon.Icon.IsAnimated = 0;
            icon.SoundEffectID = luigiIcon.SoundEffectID;
            using (var roster = new Bitmap(Path.Combine(root, "interface", "john-pork-roster-MnSlChr.usd.png")))
                icon.Image = roster.ToTOBJ(GXTexFmt.CI8, GXTlutFmt.RGB5A3);
            MEX.FighterIcons.Add(icon);
        }
    }
}
