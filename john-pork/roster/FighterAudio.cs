using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HSDRaw;
using HSDRaw.Melee.Cmd;
using HSDRaw.Melee.Pl;
using mexTool.Core;

namespace CustomSmash
{
    internal static class FighterAudio
    {
        internal const int Silent = 540000;

        // GALE01 lbCommand + ftAction_803C0870 (src/melee/ft/ftaction.c).
        // The bundled HSDRaw editor's lengths for opcodes 53..58 are outdated.
        private static readonly byte[] CommandWords = {
            1, 1, 1, 1, 1, 2, 1, 2, 1, 1,
            5, 5, 1, 1, 1, 1, 1, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            1, 1, 1, 1, 3, 1, 1, 1, 7, 4, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            1, 1, 1, 1, 3, 3, 2, 1, 4
        };

        internal static Dictionary<int, int> DonorVoiceMap(MEXSoundBank donor)
        {
            int bank = MEX.SoundBanks.IndexOf(donor);
            if (bank < 0) throw new InvalidDataException("The donor sound bank is not registered.");
            var replacements = new Dictionary<int, int>();
            for (int index = 0; index < donor.ScriptBank.Scripts.Length; index++)
                if (donor.ScriptBank.Scripts[index].Name?.StartsWith("SFXv_", StringComparison.Ordinal) == true)
                    replacements.Add(bank * 10000 + index, Silent);
            if (replacements.Count == 0)
                throw new InvalidDataException($"No fighter vocal metadata found in {donor}.");
            return replacements;
        }

        internal static void ReplaceDonorVoices(MEXFighter fighter, SBM_FighterData data,
            Dictionary<int, int> replacements)
        {
            int Map(int id) => replacements.TryGetValue(id, out int replacement) ? replacement : id;
            var sounds = data.CommonSoundEffectTable;
            foreach (var pool in new[] { sounds.RandomSmashSFX, sounds.LightHitSFX, sounds.HeavyHitSFX })
                if (pool != null) pool.Entries = pool.Entries.Select(Map).Distinct().ToArray();
            for (int offset = 4; offset < 0x38; offset += 4)
                if (offset != 0x1C && offset != 0x20)
                    sounds._s.SetInt32(offset, Map(sounds._s.GetInt32(offset)));
            sounds.SFX_Cheer = Silent;
            fighter.AnnouncerCall = Silent;

            var visited = new HashSet<HSDStruct>();
            foreach (var action in data.FighterActionTable.Commands.Concat(data.DemoActionTable.Commands))
                RewriteScript(action.SubAction, replacements, visited);
        }

        private static void RewriteScript(SBM_FighterSubactionData script,
            Dictionary<int, int> replacements, HashSet<HSDStruct> visited)
        {
            if (script == null || !visited.Add(script._s)) return;
            for (int offset = 0; offset < script._s.Length;)
            {
                int op = script._s.GetByte(offset) >> 2;
                int size = CommandSize(script, offset, op);
                if (op == 17 || op == 39 || op == 54 || op == 55)
                    Replace(offset + 4);
                else if (op == 38)
                    for (int index = 1; index <= 6; index++) Replace(offset + index * 4);
                else if (op == 5 || op == 7)
                    RewriteScript(script._s.GetReference<SBM_FighterSubactionData>(offset + 4)
                        ?? throw new InvalidDataException("Missing fighter sound-script branch."), replacements, visited);
                if (op == 0 || op == 6 || op == 7) return;
                offset += size;
            }
            throw new InvalidDataException("Fighter sound script has no terminator.");

            void Replace(int offset)
            {
                if (replacements.TryGetValue(script._s.GetInt32(offset), out int replacement))
                    script._s.SetInt32(offset, replacement);
            }
        }

        private static int CommandSize(SBM_FighterSubactionData script, int offset, int op)
        {
            if (op >= CommandWords.Length || offset + CommandWords[op] * 4 > script._s.Length)
                throw new InvalidDataException("Unknown or truncated GALE01 fighter sound command.");
            return CommandWords[op] * 4;
        }

        internal static void AddBombVoice(SBM_FighterSubactionData script, int voice)
        {
            // HawkingGameplay.ChairBombScript flattens the native bomb script.
            if (script._s.References.Count != 0)
                throw new InvalidDataException("Expected a flattened chair-bomb script.");
            for (int offset = 0; offset < script._s.Length;)
            {
                int op = script._s.GetByte(offset) >> 2;
                int size = CommandSize(script, offset, op);
                if (op == 17 && script._s.GetInt32(offset + 4) == 260027)
                {
                    byte[] original = script._s.GetData();
                    byte[] added = HawkingNativeCode.Words(0x44080000, (uint)voice, 0x00007F40).GetData();
                    byte[] output = new byte[original.Length + added.Length];
                    int insertion = offset + size;
                    Buffer.BlockCopy(original, 0, output, 0, insertion);
                    Buffer.BlockCopy(added, 0, output, insertion, added.Length);
                    Buffer.BlockCopy(original, insertion, output, insertion + added.Length, original.Length - insertion);
                    script._s.SetData(output);
                    return;
                }
                if (op == 0 || op == 6 || op == 7) break;
                offset += size;
            }
            throw new InvalidDataException("The chair-bomb deployment sound was not found.");
        }

        internal static SBM_FighterSubactionData VictoryVoice(SBM_FighterSubactionData original,
            int first, int second)
        {
            // Native opcode38 picks one of two clips, on the fighter voice track.
            // A subroutine preserves the entire original winning-pose script.
            uint randomVoice = (38u << 26) | (127u << 18) | (64u << 10) | (2u << 6) | 2u;
            var script = new SBM_FighterSubactionData { _s = HawkingNativeCode.Words(
                randomVoice, (uint)first, (uint)second, Silent, Silent, Silent, Silent,
                0x14000000, 0, 0) };
            script._s.SetReference(32, original);
            return script;
        }
    }
}
