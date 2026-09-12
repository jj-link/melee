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
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CustomSmash
{
    internal static class HawkingFighter
    {
        internal static int Add(string root)
        {
            var zelda = MEX.Fighters[19];
            if (zelda.FighterDataPath != "PlZd.dat")
                throw new InvalidDataException("The expected vanilla Zelda definition is missing.");
            string character = Path.Combine(root, "character");
            var assets = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build().Deserialize<AnimationAssets>(File.ReadAllText(Path.Combine(character, "hawking-assets.json")));
            // Retain registered shared resources, as in mexTool's supported clone operation.
            var hawking = System.ObjectExtensions.Copy(zelda);
            hawking.NameText = "Stephen Hawking";
            hawking.TargetTestStage = zelda.TargetTestStage;
            hawking.SoundBank = zelda.SoundBank;
            hawking.VictoryTheme = zelda.VictoryTheme;
            hawking.FighterSongID1 = zelda.FighterSongID1;
            hawking.FighterSongID2 = zelda.FighterSongID2;
            hawking.SubCharacter = null;
            hawking.SubCharacterBehavior = HSDRaw.MEX.SubCharacterBehavior.SpawnNormally;
            hawking.FighterDataPath = "PlHw.dat";
            hawking.AnimFile = assets.AnimFile;
            hawking.AnimCount = assets.AnimCount;
            hawking.RstAnimFile = assets.RstAnimFile;
            hawking.RstAnimCount = assets.RstAnimCount;
            hawking.DemoFile = assets.DemoFile;
            hawking.DemoWait = assets.DemoWait;
            hawking.DemoResult = assets.DemoResult;
            hawking.DemoIntro = assets.DemoIntro;
            hawking.DemoEnding = assets.DemoEnding;
            foreach (string name in assets.Files)
            {
                if (Path.GetFileName(name) != name || MEX.ImageResource.FileExists(name))
                    throw new InvalidDataException($"Hawking animation must have a new, isolated filename: {name}");
                MEX.ImageResource.AddFile(name, Path.Combine(character, name));
            }

            var model = new HSDRawFile(Path.Combine(character, "PlHwNr.dat"));
            var costume = new MEXCostume {
                FileName = "PlHwNr.dat",
                ModelSymbol = model.Roots.Single(r => r.Data is HSD_JOBJ).Name,
                MaterialSymbol = model.Roots.SingleOrDefault(r => r.Name.EndsWith("_matanim_joint", StringComparison.Ordinal))?.Name,
                VisibilityIndex = zelda.Costumes[0].VisibilityIndex
            };
            using (var portrait = new Bitmap(Path.Combine(character, "hawking-portrait.png")))
                costume.CSP = portrait.ToTOBJ(GXTexFmt.CI8, GXTlutFmt.RGB5A3);
            using (var stock = new Bitmap(Path.Combine(root, "interface", "hawking-stock.png")))
                costume.Icon = stock.ToTOBJ(GXTexFmt.CI4, GXTlutFmt.RGB5A3);
            MEX.ImageResource.AddFile(costume.FileName, Path.Combine(character, costume.FileName));
            hawking.Costumes.Clear();
            // Melee's four-player CSS assigns distinct costume indices without
            // consulting the costume count. Share the approved appearance across
            // all four slots so mirror matches never index another fighter's CSP.
            for (int slot = 0; slot < 4; slot++)
                hawking.Costumes.Add(costume);
            hawking.RedCostumeIndex = hawking.BlueCostumeIndex = hawking.GreenCostumeIndex = 0;

            MEX.Fighters.Insert(MEX.FighterCount - MEXFighterIDConverter.InternalSpecialCharCount, hawking);
            var fighter = new HSDRawFile(Path.Combine(character, "PlHw-base.dat"));
            HawkingGameplay.Configure(zelda, hawking, fighter, root);
            using (var stream = new MemoryStream())
            {
                fighter.Save(stream, trim: true);
                MEX.ImageResource.AddFile(hawking.FighterDataPath, stream.ToArray());
            }
            AddRosterIcon(root, zelda, hawking);
            int internalId = MEX.Fighters.IndexOf(hawking);
            int externalId = MEXFighterIDConverter.ToExternalID(internalId, MEX.FighterCount);
            Console.WriteLine($"Added {hawking.NameText}: internal {internalId}, external {externalId}; Zelda retained, down-B is Samus bomb drop.");
            return externalId;
        }

        private static void AddRosterIcon(string root, MEXFighter zelda, MEXFighter hawking)
        {
            var template = MEX.FighterIcons.Single(i => i.Fighter == MEX.Fighters[20]);
            var zeldaIcon = MEX.FighterIcons.Single(i => i.Fighter == zelda);
            float row = template.Y - 7.0f;
            // Native rows are not exactly seven units apart. Test whole tile
            // bounds, not matching origins, so Roy and the other originals remain visible.
            var column = MEX.FighterIcons.Where(i => Math.Abs(i.Y - template.Y) < .1f)
                .OrderBy(i => Math.Abs(i.X - template.X))
                .First(i => !MEX.FighterIcons.Any(other =>
                    other.X < i.X + template.Width - .01f && other.X + other.Width > i.X + .01f &&
                    other.Y < row + template.Height - .01f && other.Y + other.Height > row + .01f));
            var icon = new MEXFighterIcon {
                Fighter = hawking,
                Icon = HSDAccessor.DeepClone<MEX_CSSIcon>(template.Icon),
                _joint = HSDAccessor.DeepClone<HSD_JOBJ>(template._joint),
                MaterialAnimation = HSDAccessor.DeepClone<HSD_MatAnimJoint>(template.MaterialAnimation),
                IconModel = template.IconModel,
                Frame = -1
            };
            icon._joint.Next = null;
            icon.MaterialAnimation.Next = null;
            icon.X = column.X;
            icon.Y = row;
            icon.FromAnimJoint(null);
            icon.Icon.StatusID = Status.UnlockedAndVisible;
            icon.Icon.IsAnimated = 0;
            icon.SoundEffectID = zeldaIcon.SoundEffectID;
            using (var roster = new Bitmap(Path.Combine(root, "interface", "hawking-roster.png")))
                icon.Image = roster.ToTOBJ(GXTexFmt.CI8, GXTlutFmt.RGB5A3);
            MEX.FighterIcons.Add(icon);
        }

        public sealed class AnimationAssets
        {
            public string AnimFile { get; set; }
            public int AnimCount { get; set; }
            public string RstAnimFile { get; set; }
            public int RstAnimCount { get; set; }
            public string DemoFile { get; set; }
            public string DemoWait { get; set; }
            public string DemoResult { get; set; }
            public string DemoIntro { get; set; }
            public string DemoEnding { get; set; }
            public string[] Files { get; set; }
        }
    }
}
