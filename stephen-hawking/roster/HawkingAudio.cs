using System;
using System.IO;
using System.Linq;
using CSCore;
using CSCore.Codecs.MP3;
using HSDRaw;
using HSDRaw.Melee.Pl;
using HSDRaw.Tools;
using MeleeMedia.Audio;
using mexTool.Core;
using mexTool.Tools;

namespace CustomSmash
{
    internal static class HawkingAudio
    {
        internal const uint AdditionalAramBytes = 512 * 1024;
        private static int voiceBank = -1;
        private static int zeldaBank, samusBank;

        internal static void Configure(MEXFighter zelda, MEXFighter hawking,
            HSDRawFile fighterFile, string root)
        {
            string[] clips = {
                "take-that", "oh-no", "ahhh", "eat-my-shit",
                "predicted-in-88", "a-brief-history"
            };
            var samples = new DSP[clips.Length];
            var scripts = new SEMBankScript[clips.Length * 3];
            for (int clip = 0; clip < clips.Length; clip++)
            {
                samples[clip] = ImportMP3(Path.Combine(root, "voice", $"hawking-{clips[clip]}.mp3"));
                // Melee selects the adjacent normal/big/small vocal scripts.
                // Keep Zelda's native pitch commands, but use our local sample.
                for (int variant = 0; variant < 3; variant++)
                {
                    var script = System.ObjectExtensions.Copy(zelda.SoundBank.ScriptBank.Scripts[2 + variant]);
                    script.Name = $"SFXv_hawking_{clips[clip]}_{variant}";
                    script.SFXID = clip;
                    scripts[clip * 3 + variant] = script;
                }
            }
            var bank = new MEXSoundBank(new SEMBank { Scripts = scripts },
                new SSM { Name = "hawking.ssm", Sounds = samples }) {
                Flags = zelda.SoundBank.Flags,
                GroupFlags = zelda.SoundBank.GroupFlags
            };
            MEX.SoundBanks.Add(bank);
            voiceBank = MEX.SoundBanks.IndexOf(bank);
            zeldaBank = MEX.SoundBanks.IndexOf(zelda.SoundBank);
            samusBank = MEX.SoundBanks.IndexOf(MEX.Fighters.Single(f => f.FighterDataPath == "PlSs.dat").SoundBank);
            int voiceBase = voiceBank * 10000;
            int Voice(int clip) => voiceBase + clip * 3;

            var data = new SBM_FighterData {
                _s = fighterFile.Roots.Single(r => r.Name == hawking.FighterDataSymbol).Data._s
            };
            var sounds = data.CommonSoundEffectTable;
            var replacements = FighterAudio.DonorVoiceMap(zelda.SoundBank);
            foreach (int id in sounds.RandomSmashSFX.Entries) ReplaceVariants(id, Voice(0));
            foreach (int id in sounds.LightHitSFX.Entries.Concat(sounds.HeavyHitSFX.Entries))
                ReplaceVariants(id, Voice(1));
            ReplaceVariants(sounds._s.GetInt32(0x04), Voice(2));
            ReplaceVariants(sounds._s.GetInt32(0x0C), Voice(2));
            FighterAudio.ReplaceDonorVoices(hawking, data, replacements);

            var actions = data.FighterActionTable.Commands;
            FighterAudio.AddBombVoice(actions[307].SubAction, Voice(3));
            FighterAudio.AddBombVoice(actions[309].SubAction, Voice(3));
            var demos = data.DemoActionTable.Commands;
            foreach (int index in new[] { 0, 2, 5 })
                demos[index].SubAction = FighterAudio.VictoryVoice(demos[index].SubAction, Voice(4), Voice(5));
            data.DemoActionTable.Commands = demos;

            // Absolute Zelda/Samus effect IDs retain their original banks.
            // ConfigureRuntime queues these dependencies whenever this SSM loads.
            hawking.SoundBank = bank;
            Console.WriteLine($"Hawking audio: {clips.Length} recordings, private bank {voiceBase / 10000}; inherited vocals muted.");

            void ReplaceVariants(int id, int replacement)
            {
                for (int variant = 0; variant < 3; variant++)
                    if (replacements.ContainsKey(id + variant))
                        replacements[id + variant] = replacement + variant;
            }
        }

        internal static void ConfigureRuntime(Codes runtime)
        {
            if (voiceBank < 0) return; // John-Pork-only builds add no bank.
            // The pinned m-ex loader queues only the assigned bank and ignores
            // legacy fighter preload masks here. Extend its existing request
            // hook, not playback: every scene loads Hawking's effects with him.
            byte[] expected = HawkingNativeCode.Words(
                0x2C030037, 0x41820018, 0x81820060, 0x818C0004, 0x38800001,
                0x1C630004, 0x7C83612E, 0x4E800020, 0x60000000, 0).GetData();
            byte[] replacement = HawkingNativeCode.Words(
                0x2C030037, 0x41820028, 0x81820060, 0x818C0004, 0x38800001,
                0x1C630004, 0x7C83612E, 0x2C030000u | (uint)(voiceBank * 4),
                0x4082000C, 0x908C0000u | (uint)(samusBank * 4),
                0x908C0000u | (uint)(zeldaBank * 4),
                0x4E800020, 0x60000000, 0).GetData();
            byte[] code = runtime.GetCompiled();
            using (var input = new MemoryStream(code))
            using (var reader = new BinaryReaderExt(input) { BigEndian = true })
            using (var output = new MemoryStream())
            {
                int patched = 0, heapsPatched = 0;
                while (input.Position < input.Length)
                {
                    int start = checked((int)input.Position);
                    uint command = reader.ReadUInt32(), value = reader.ReadUInt32();
                    if ((command >> 24) == 4)
                    {
                        output.Write(code, start, 8);
                        continue;
                    }
                    if ((command >> 24) != 0xC2)
                        throw new InvalidDataException("Unexpected pinned m-ex audio code type.");
                    int size = checked((int)value * 8);
                    long end = input.Position + size;
                    if ((command & 0xFFFFFF) == 0x56A8)
                    {
                        if (!reader.ReadBytes(size).SequenceEqual(expected))
                            throw new InvalidDataException("The pinned m-ex sound-bank request hook has changed.");
                        byte[] header = HawkingNativeCode.Words(command, (uint)(replacement.Length / 8)).GetData();
                        output.Write(header, 0, header.Length);
                        output.Write(replacement, 0, replacement.Length);
                        patched++;
                    }
                    else if ((command & 0xFFFFFF) == 0x15F88)
                    {
                        // Full-rate voices plus donor effects exceed the original audio
                        // reservation on large-bank stages. Transfer 512 KiB from the
                        // ARAM animation cache; do not shrink any RAM/menu heap.
                        if (size != 168)
                            throw new InvalidDataException("The pinned m-ex heap-definition hook has changed.");
                        // Shared donor image buffers also recover archive space. Return
                        // 256 KiB to the live scene heap for stage/copy animation peaks.
                        input.Position += 112;
                        if (reader.ReadUInt32() != 4 || reader.ReadUInt32() != 2 ||
                            reader.ReadUInt32() != 6 || reader.ReadUInt32() != 0x0064B400)
                            throw new InvalidDataException("The pinned fighter archive heap descriptor has changed.");
                        const uint fighterHeapSize = 0x0060B400;
                        for (int i = 0; i < 4; i++)
                            code[start + 8 + 124 + i] = (byte)(fighterHeapSize >> (24 - i * 8));
                        if (reader.ReadUInt32() != 5 || reader.ReadUInt32() != 4 ||
                            reader.ReadUInt32() != 6 || reader.ReadUInt32() != 0x0096C800)
                            throw new InvalidDataException("The pinned animation ARAM heap descriptor has changed.");
                        const uint animationHeapSize = 0x0096C800 - AdditionalAramBytes;
                        for (int i = 0; i < 4; i++)
                            code[start + 8 + 140 + i] = (byte)(animationHeapSize >> (24 - i * 8));
                        output.Write(code, start, size + 8);
                        heapsPatched++;
                    }
                    else output.Write(code, start, size + 8);
                    input.Position = end;
                }
                if (patched != 1)
                    throw new InvalidDataException("The Hawking sound-bank dependency hook is required.");
                if (heapsPatched != 1)
                    throw new InvalidDataException("The expanded Hawking audio ARAM reservation is required.");
                runtime.SetCompiled(output.ToArray());
            }
        }

        private static DSP ImportMP3(string path)
        {
            using (var input = new MemoryStream(File.ReadAllBytes(path)))
            using (IWaveSource source = new DmoMp3Decoder(input))
            using (var wave = new MemoryStream())
            {
                source.WriteToWaveStream(wave);
                var sample = new DSP();
                sample.FromWAVE(wave.ToArray());
                return sample;
            }
        }
    }
}
