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
using mexTool.Tools;
using mexTool.Core;

namespace CustomSmash
{
    internal static class ResultNames
    {
        // Result-name frames use external IDs, unlike stock-icon frames.
        // Put transformed Sheik immediately after all selectable fighter IDs.
        private static int SheikFrame => MEX.FighterCount - MEXFighterIDConverter.ExternalSpecialCharCount;

        internal static void ConfigureRuntime(Codes runtime)
        {
            byte[] code = runtime.GetCompiled();
            using (var stream = new MemoryStream(code))
            using (var reader = new BinaryReaderExt(stream) { BigEndian = true })
            {
                int patched = 0;
                while (stream.Position < stream.Length)
                {
                    uint command = reader.ReadUInt32();
                    uint value = reader.ReadUInt32();
                    if ((command >> 24) == 4) continue;
                    if ((command >> 24) != 0xC2)
                        throw new InvalidDataException("Unexpected pinned m-ex code type.");
                    long body = stream.Position, end = body + value * 8L;
                    uint address = 0x80000000 | (command & 0xFFFFFF);
                    if (address == 0x80178EDC || address == 0x80178FD8)
                    {
                        // Both winner and player-card hooks shipped with an
                        // off-by-one Zelda threshold and a fixed Sheik frame 37.
                        uint[] expected = { 0x2C030019, 0x41810020, 0x2C030012,
                            0x41800018, 0x2C040007, 0x4182000C, 0x3863FFFF,
                            0x48000008, 0x38600025 };
                        foreach (uint instruction in expected)
                            if (reader.ReadUInt32() != instruction)
                                throw new InvalidDataException("The pinned result-name mapper has changed.");
                        // Check current fighter kind before shifting external
                        // IDs: Zelda's external ID stays 18 after transforming.
                        uint[] replacement = { 0x2C030019, 0x41810020, 0x2C040007,
                            0x41820014, 0x2C030013, 0x41800010, 0x3863FFFF,
                            0x48000008, 0x38600000u | checked((ushort)SheikFrame) };
                        for (int i = 0; i < replacement.Length; i++)
                        {
                            int offset = checked((int)body + i * 4);
                            uint instruction = replacement[i];
                            code[offset] = (byte)(instruction >> 24);
                            code[offset + 1] = (byte)(instruction >> 16);
                            code[offset + 2] = (byte)(instruction >> 8);
                            code[offset + 3] = (byte)instruction;
                        }
                        patched++;
                    }
                    stream.Position = end;
                }
                if (patched != 2) throw new InvalidDataException("Both native result-name hooks are required.");
            }
            runtime.SetCompiled(code);
        }

        internal static void Add(params (string Root, string Prefix, int ExternalId)[] identities)
        {
            foreach (string name in new[] { "GmRst.dat", "GmRst.usd" })
            {
                // Serialize each archive once: the deferred-file overlay must
                // never receive successive intermediate versions of one file.
                var file = new HSDRawFile(MEX.ImageResource.GetFileData(name));
                var scene = (HSD_SOBJ)file["pnlsce"].Data;
                var joints = scene.JOBJDescs[0].MaterialAnimations[0].TreeList;
                var sheikBanks = new HashSet<HSDStruct>();
                MapSheik(joints[10].MaterialAnimation.Next.TextureAnimation, sheikBanks);
                foreach (int joint in new[] { 33, 41, 49, 57 })
                    MapSheik(joints[joint].MaterialAnimation.TextureAnimation, sheikBanks);
                foreach (var identity in identities)
                {
                    using (var winner = new Bitmap(Path.Combine(identity.Root, "interface", identity.Prefix + "-winner.png")))
                    using (var card = new Bitmap(Path.Combine(identity.Root, "interface", identity.Prefix + "-result-name.png")))
                    {
                        var seen = new HashSet<HSDStruct>();
                        Append(joints[10].MaterialAnimation.Next.TextureAnimation, winner, identity.ExternalId, seen);
                        foreach (int joint in new[] { 33, 41, 49, 57 })
                            Append(joints[joint].MaterialAnimation.TextureAnimation, card, identity.ExternalId, seen);
                    }
                    Console.WriteLine($"Added result-name frame {identity.ExternalId} to {name}; original fighter frames retained.");
                }
                using (var stream = new MemoryStream())
                {
                    file.Save(stream, optimize: true, trim: true);
                    MEX.ImageResource.AddFile(name, stream.ToArray());
                }
            }
        }

        private static void MapSheik(HSD_TexAnim bank, HashSet<HSDStruct> seen)
        {
            if (!seen.Add(bank._s)) return;
            // Native frame 25 is Sheik; retain its actual image/palette indices.
            foreach (var track in bank.AnimationObject.FObjDesc.List.ToArray())
            {
                if (track.TexTrackType != TexTrackType.HSD_A_T_TIMG &&
                    track.TexTrackType != TexTrackType.HSD_A_T_TCLT) continue;
                var player = new FOBJ_Player(track.TrackType, track.GetDecodedKeys());
                SetFrame(bank.AnimationObject, track.TexTrackType, SheikFrame, checked((int)player.GetValue(25)));
            }
            bank.AnimationObject.EndFrame = Math.Max(bank.AnimationObject.EndFrame, SheikFrame + 1);
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
