using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using HSDRaw;
using HSDRaw.Common;
using HSDRaw.Common.Animation;
using HSDRaw.GX;
using HSDRaw.Tools;
using mexTool.Core;

namespace CustomSmash
{
    internal static class ResultNames
    {
        internal static void Add(string root, int externalId)
        {
            using (var winner = new Bitmap(Path.Combine(root, "interface", "john-pork-winner.png")))
            using (var card = new Bitmap(Path.Combine(root, "interface", "john-pork-result-name.png")))
            {
                foreach (string name in new[] { "GmRst.dat", "GmRst.usd" })
                {
                    // Load pristine results, never the older Luigi-replacement archive.
                    var file = new HSDRawFile(MEX.ImageResource.GetFileData(name));
                    var scene = (HSD_SOBJ)file["pnlsce"].Data;
                    var joints = scene.JOBJDescs[0].MaterialAnimations[0].TreeList;
                    var seen = new HashSet<HSDStruct>();
                    Append(joints[10].MaterialAnimation.Next.TextureAnimation, winner, externalId, seen);
                    foreach (int joint in new[] { 33, 41, 49, 57 })
                        Append(joints[joint].MaterialAnimation.TextureAnimation, card, externalId, seen);
                    using (var stream = new MemoryStream())
                    {
                        file.Save(stream, optimize: true, trim: true);
                        MEX.ImageResource.AddFile(name, stream.ToArray());
                    }
                    Console.WriteLine($"Added result-name frame {externalId} to {name}; original fighter frames retained.");
                }
            }
        }

        private static void Append(HSD_TexAnim bank, Bitmap image, int frame, HashSet<HSDStruct> seen)
        {
            // The four player cards can share the same backing animation.
            if (!seen.Add(bank._s)) return;
            var original = bank.ImageBuffers[7].Data;
            if (image.Width != original.Width || image.Height != original.Height)
                throw new InvalidDataException("Result-name image dimensions do not match the original material.");
            var texture = image.ToTOBJ(original.Format, GXTlutFmt.RGB5A3);
            if (!bank.AddImage(texture, out int imageIndex, out int paletteIndex))
                throw new InvalidDataException("Unable to append the result-name texture.");
            SetFrame(bank.AnimationObject, TexTrackType.HSD_A_T_TIMG, frame, imageIndex);
            if (texture.TlutData != null)
                SetFrame(bank.AnimationObject, TexTrackType.HSD_A_T_TCLT, frame, paletteIndex);
            bank.AnimationObject.EndFrame = Math.Max(bank.AnimationObject.EndFrame, frame + 1);
        }

        private static void SetFrame(HSD_AOBJ animation, TexTrackType type, int frame, int value)
        {
            var track = animation.FObjDesc?.List.SingleOrDefault(f => f.TexTrackType == type);
            var keys = track == null ? new List<FOBJKey>() : track.GetDecodedKeys();
            if (track == null)
            {
                track = new HSD_FOBJDesc { Next = animation.FObjDesc };
                animation.FObjDesc = track;
            }
            keys.RemoveAll(key => key.Frame == frame);
            keys.Add(new FOBJKey { Frame = frame, Value = value, InterpolationType = GXInterpolationType.HSD_A_OP_CON });
            keys.Sort((left, right) => left.Frame.CompareTo(right.Frame));
            // Do not call FromTOBJs: it would replace every original key with a
            // dense frame map and could destroy special/team animation mappings.
            track.SetKeys(keys, (byte)type);
        }
    }
}
