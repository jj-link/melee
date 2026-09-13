using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HSDRaw;
using HSDRaw.Melee.Cmd;
using HSDRaw.Melee.Pl;
using HSDRaw.MEX;
using mexTool.Core;

namespace CustomSmash
{
    internal static class HawkingGameplay
    {
        // GALE01 executable ranges are pinned by config/GALE01/symbols.txt.
        // Transplant Samus's neutral/side/bomb modules and their private items;
        // Zelda's live attributes, normal attacks and teleport stay untouched.
        private const uint SpecialFirst = 0x80128944, SpecialEnd = 0x8012A674;
        private const uint BombFirst = 0x8012ADF0, BombEnd = 0x8012B6E8;
        private const uint ItemFirst = 0x802B4AC8, ItemEnd = 0x802B7150;
        private const uint ZeldaStates = 0x803CFA58, SamusStates = 0x803CE2D0;
        private const int SamusBombItem = 93; // Consecutive bomb, charge-shot and missile items.
        private static readonly int[] SpecialStates = { 341, 342, 343, 344, 345, 346, 347, 348, 359, 360 };
        private static readonly int[] SpecialAnimations = { 295, 296, 297, 298, 299, 300, 301, 302, 311, 312 };

        internal static void Configure(MEXFighter zelda, MEXFighter hawking, HSDRawFile fighterFile, string root)
        {
            int fighterId = MEX.Fighters.IndexOf(hawking);
            if (fighterId < 0 || ReferenceEquals(zelda, hawking))
                throw new InvalidDataException("Insert an isolated Hawking clone before configuring gameplay.");
            var dataRoot = fighterFile.Roots.Single(r => r.Name == hawking.FighterDataSymbol);
            var data = new SBM_FighterData { _s = dataRoot.Data._s };
            var samusFile = new HSDRawFile(Path.Combine(root, "original", "PlSs.dat"));
            var samus = new SBM_FighterData { _s = samusFile.Roots.Single(r => r.Name == "ftDataSamus").Data._s };
            var samusFighter = MEX.Fighters.Single(f => f.FighterDataPath == "PlSs.dat");

            if (data.FighterActionTable.Commands.Length != 313)
                throw new InvalidDataException("Run hawking-import before installing the Samus shooting actions.");

            // Din's Fire is gone. These three articles and the immutable donor
            // attribute block belong only to PlHw.dat, never to Zelda or Samus.
            const int attrsArticle = 3;
            data.Articles = new SBM_ArticlePointer { _s = new HSDStruct(16) };
            int customFirst = hawking.Items.Count;
            var customItems = new MEX_Item[3];
            for (int index = 0; index < customItems.Length; index++)
            {
                data.Articles._s.SetReference(index * 4,
                    HSDAccessor.DeepClone<SBM_Article>(samus.Articles.Articles[index]));
                customItems[index] = HSDAccessor.DeepClone<MEX_Item>(MEX.FighterItems[SamusBombItem + index - 43]);
                hawking.Items.Add(customItems[index]);
            }
            data.Articles._s.SetReference(attrsArticle * 4,
                HSDAccessor.DeepClone<HSDAccessor>(samus.Attributes2));
            // Each private item's owner callbacks must use these relocated
            // moves/attributes, not the original Samus callbacks on Zelda data.
            int customBomb = customFirst, customShot = customFirst + 1, customMissile = customFirst + 2;
            hawking.SubCharacter = null;
            hawking.SubCharacterBehavior = SubCharacterBehavior.SpawnSeparately;
            // Preserve Zelda's effects and preload Samus's firing/bomb sounds.
            hawking.SSMBitfield1 |= samusFighter.SSMBitfield1;
            hawking.SSMBitfield2 |= samusFighter.SSMBitfield2;

            var native = new HawkingNativeCode(Path.Combine(root, "original", "main.dol"));
            native.Import(SpecialFirst, SpecialEnd);
            native.Import(BombFirst, BombEnd);
            native.Import(ItemFirst, ItemEnd);
            native.Import(0x80128428, 0x80128464); // charge/effect damage and destruction cleanup
            native.Import(0x80128628, 0x80128684); // restore full-charge flash on state changes
            native.Import(0x80139334, 0x801393AC); // Zelda's exact PUSH_ATTRS implementation
            native.LinkOriginalCode();
            native.AdaptAttributeLoads(SpecialFirst, SpecialEnd, attrsArticle * 4);
            native.AdaptAttributeLoads(BombFirst, BombEnd, attrsArticle * 4);
            native.AdaptAttributeLoads(0x80128628, 0x80128684, attrsArticle * 4);
            native.ScaleAttributeLoad(0x80128B64, 0xC01E0008, 29); // bombjump horizontal impulse
            native.ScaleAttributeLoad(0x80128B78, 0xC01E0008, 29); // bombjump vertical impulse
            native.ScaleAttributeLoad(0x8012AE74, 0xC01E0078, 31); // bomb spawn height
            native.ScaleAttributeLoad(0x8012B0D4, 0xC0080058, 7); // aerial entry vertical velocity
            native.ScaleAttributeLoad(0x8012B614, 0xC0040054, 31); // grounded hop

            // Neutral's six states remain contiguous, including the exclusive
            // upper bound in its held-item lifetime checks. The two air missile
            // states extend the table rather than consuming teleport/bomb slots.
            int statePatches = 0;
            for (uint address = 0x80129158; address < SpecialEnd; address += 4)
            {
                uint word = native.Read(address), instruction = word & 0xFFFF0000, state = word & 0xFFFF;
                if ((instruction == 0x38800000 || instruction == 0x2C000000) && state >= 343 && state <= 352)
                {
                    native.Patch(address, word, instruction | (uint)SpecialStates[state - 343]);
                    statePatches++;
                }
            }
            if (statePatches != 26)
                throw new InvalidDataException("Unexpected GALE01 Samus neutral/side state instruction layout.");
            // HawkingSamusAnimations keeps the existing rig and animates the
            // left hand104, release helper113 and missile emitter103.
            native.Patch(0x80129344, 0x80630320, 0x80630680); // raw Samus hand50 -> Hawking104
            native.Patch(0x8012935C, 0x38A00032, 0x38A00068); // charge-item attachment uses the same hand
            native.Patch(0x80129410, 0x80630330, 0x80630710); // raw ThrowN51 ->113
            native.Patch(0x8012A0DC, 0x80630380, 0x80630670); // raw missile emitter56 ->103
            native.Patch(0x8012844C, 0x4BFB184D, 0x60000000); // no unrelated Samus grapple cleanup
            native.Patch(0x8013935C, 0x83E40048, 0x60000000); // no obsolete Din's Fire article load
            native.Redirect(0x80139380, 0x80139398); // no obsolete Din's Fire item registration

            // The private charge-shot module has only its Samus-style variant.
            // Select that implementation directly instead of testing vanilla
            // item94/151 IDs; never change the custom item's actual kind.
            native.Redirect(0x802B591C, 0x802B5938); // held-item destruction clears its Hawking owner
            native.Redirect(0x802B5A24, 0x802B5A40); // charging effect
            native.Redirect(0x802B5B00, 0x802B5B1C); // lifetime and charge/max-charge reads
            native.Redirect(0x802B5F1C, 0x802B5F38); // full-power projectile effects

            // Source Samus states: 341/342 = bombjump; 355/356 = bomb drop.
            // Hawking retains Zelda's four animation/motion slots: 355/357 =
            // bomb drop; 356/358 = bombjump. No path can reach transformation.
            RemapState(native, 0x80128BCC, 342, 358);
            RemapState(native, 0x80129078, 342, 358);
            RemapState(native, 0x801290D4, 341, 356);
            RemapState(native, 0x8012B0A0, 356, 357);
            RemapState(native, 0x8012B5AC, 356, 357);
            RemapState(native, 0x8012B638, 356, 357);

            // The chair remains seated rather than becoming a three-unit
            // morph-ball hurt capsule. Skip the two inlined morph-hurtbox
            // blocks; standalone bombjump morph calls use the normal hurtbox
            // enable helper instead. No intangible capsule or mutated capsule
            // geometry survives a hit, grab, death, landing, or state exit.
            native.Redirect(0x8012B190, 0x8012B230);
            native.Redirect(0x8012B284, 0x8012B324);
            native.Redirect(0x8012AEBC, 0x8012AF38);
            // Likewise retain the actual chair's normal ECB, not Samus's
            // morph-ball collision box. Both branches still use the original
            // frame-preserving takeoff/landing transition helpers.
            native.Redirect(0x80128F84, 0x80128FA8);
            native.Redirect(0x80128FF8, 0x8012901C);
            native.Redirect(0x8012B4AC, 0x8012B4D0);
            native.Redirect(0x8012B520, 0x8012B544);

            // Samus's self-bombjump skips its startup when model part0 == 2
            // (morphed). The chair never changes model parts: use the retained
            // script's movement/morph flag, guarded by our four down-B states,
            // so another Zelda move's command variable cannot trigger this.
            uint morphState = native.Position;
            native.Emit(0x801F0010, 0x28000163, 0x41800020,
                0x28000166, 0x41810018, 0x801F2200, 0x2C000000,
                0x4182000C, 0x38000002);
            native.Branch(native.Location(0x801289C8));
            native.Emit(0x38000000);
            native.Branch(native.Location(0x801289C8));
            native.BranchAt(native.Location(0x801289C4), morphState);

            // Replace the spawn request's kind store with runtime ID lookup.
            // At 802B4AEC the original frame has saved r30/r31 and f31;
            // r30=owner and r4=position. r31 is not live yet. GetFtItemID has
            // (owner GOBJ, custom item index), NOT (kind, article pointer).
            uint spawnKind = native.Position;
            native.Emit(0x7C9F2378, 0x38800000u | checked((uint)customBomb)); // mr r31,r4; li r4,index
            native.Branch(0x803D7088, true); // m-ex GetFtItemID
            native.Emit(0x9061001C, 0x7FC3F378, 0x7FE4FB78); // request.kind=r3; restore owner/position
            native.Branch(native.Location(0x802B4AF0));
            native.BranchAt(native.Location(0x802B4AEC), spawnKind);

            // The charge constructor accepts its kind as r6. The caller has
            // already saved r29=owner and r30=fighter; restore all live arguments
            // after the runtime lookup, before the original facing-dir load.
            uint shotKind = native.Position;
            native.Emit(0x38800000u | checked((uint)customShot));
            native.Branch(0x803D7088, true);
            native.Emit(0x7C661B78, 0x7FA3EB78, 0x38810024, 0x38A00068);
            native.Branch(native.Location(0x80129364));
            native.BranchAt(native.Location(0x80129360), shotKind);

            // Missile constructor: r28=owner, r29=smash flag, f31=facing.
            // r30 is saved but not live until the original spawn has returned.
            uint missileKind = native.Position;
            native.Emit(0x7C9E2378, 0x38800000u | checked((uint)customMissile));
            native.Branch(0x803D7088, true);
            native.Emit(0x90610024, 0x7F83E378, 0x7FC4F378);
            native.Branch(native.Location(0x802B6304));
            native.BranchAt(native.Location(0x802B6300), missileKind);

            uint onLoad = native.Position;
            Prologue(native);
            native.Branch(native.Location(0x80139334), true);
            ClearCharge(native);
            for (int index = 0; index < customItems.Length; index++)
            {
                native.Emit(0x807F002C, 0x8083010C, 0x80840048,
                    0x80840000u | (uint)(index * 4), 0x80630004,
                    0x38A00000u | checked((uint)(customFirst + index)));
                native.Branch(0x803D7058, true); // Index Fighter Item(kind, article, custom index)
            }
            // Native firing/projectile effects use Samus bank2. LoadSync is
            // cached and does not replace Zelda's bank or require Samus present.
            native.Emit(0x38600002);
            native.Branch(0x8006737C, true);
            Epilogue(native);

            uint onDeath = native.Position;
            Prologue(native);
            native.Branch(native.Location(0x80129258), true); // destroy held shot before Zelda clears222C
            native.Emit(0x7FE3FB78);
            native.Branch(zelda.Functions.OnRespawn, true);
            ClearCharge(native);
            Epilogue(native);
            uint onStateChange = native.Location(0x80128628);
            if (zelda.Functions.OnActionStateChange != 0)
            {
                onStateChange = native.Position;
                Prologue(native);
                native.Branch(zelda.Functions.OnActionStateChange, true);
                native.Emit(0x7FE3FB78);
                native.Branch(native.Location(0x80128628), true);
                Epilogue(native);
            }

            // Only the six teleport entries retain Zelda's native state data.
            // Shooting scripts/clips are already imported at the ten contracted
            // action IDs; down-B keeps its existing chair-safe scripts and clips.
            uint moves = native.Position;
            int[] bombStates = { 355, 341, 356, 342 };
            var hawkingActions = data.FighterActionTable.Commands;
            var samusActions = samus.FighterActionTable.Commands;
            for (int state = 341; state <= 360; state++)
            {
                int index = Array.IndexOf(SpecialStates, state);
                bool bomb = state >= 355 && state <= 358;
                uint original = ZeldaStates + (uint)((state - 341) * 32);
                uint source = index >= 0 ? SamusStates + (uint)((343 + index - 341) * 32)
                    : bomb ? SamusStates + (uint)((bombStates[state - 355] - 341) * 32) : original;
                int targetAnimation = index >= 0 ? SpecialAnimations[index] : checked((int)native.Read(original));
                native.Emit((uint)targetAnimation, native.Read(source + 4), native.Read(source + 8));
                for (uint offset = 12; offset < 32; offset += 4) native.Pointer(native.Read(source + offset));
                if (bomb)
                    hawkingActions[targetAnimation].SubAction =
                        ChairBombScript(samusActions[checked((int)native.Read(source))].SubAction);
            }
            data.FighterActionTable.Commands = hawkingActions;

            // Retain every native item state and collision/reflection/lifetime
            // callback. Offset-relative row boundaries matter: the charge-shot
            // table starts at803F7288, not a sixteen-byte-aligned address.
            uint[] tables = { 0x803F7220, 0x803F7288, 0x803F7340 };
            int[] counts = { 4, 9, 4 };
            var itemOverrides = new List<uint>[customItems.Length];
            for (int index = 0; index < customItems.Length; index++)
            {
                uint itemStates = native.Position;
                for (uint offset = 0; offset < counts[index] * 16; offset += 4)
                    if ((offset & 15) == 0) native.Emit(native.Read(tables[index] + offset));
                    else native.Pointer(native.Read(tables[index] + offset));
                var overrides = new List<uint> { 0, itemStates };
                for (int offset = 4; offset < 0x3C; offset += 4)
                {
                    uint callback = unchecked((uint)customItems[index]._s.GetInt32(offset));
                    if (callback >= ItemFirst && callback < ItemEnd)
                    { overrides.Add((uint)(offset / 4)); overrides.Add(native.Location(callback)); }
                }
                itemOverrides[index] = overrides;
            }
            var code = native.Code();
            var relocations = native.Relocations();
            AddRoot(fighterFile, "ftFunction", HawkingNativeCode.Header(code, relocations, native.RelocationCount,
                0, onLoad, 1, onDeath, 2, native.Location(0x80128428), 3, moves,
                4, native.Location(0x8012954C), 5, native.Location(0x801295F0),
                6, native.Location(0x8012A1D8), 7, native.Location(0x8012A2AC),
                10, native.Location(0x8012AF5C), 11, native.Location(0x8012B09C), 24, onStateChange));
            // All four headers share one code base. The existing type1/4/6
            // stores and type10 OR relocations are idempotent for that base.
            var items = new HSDStruct(4 + (customFirst + customItems.Length) * 4);
            items.SetInt32(0, customFirst + customItems.Length);
            for (int index = 0; index < customItems.Length; index++)
                items.SetReferenceStruct(4 + (customFirst + index) * 4,
                    HawkingNativeCode.Header(code, relocations, native.RelocationCount, itemOverrides[index].ToArray()));
            AddRoot(fighterFile, "itFunction", items);
            HawkingKirbyCopy.Configure(samusFighter, hawking, root);
        }

        private static void Prologue(HawkingNativeCode native)
        { native.Emit(0x9421FFE0, 0x7C0802A6, 0x90010024, 0x93E1001C, 0x7C7F1B78); }
        private static void Epilogue(HawkingNativeCode native)
        { native.Emit(0x83E1001C, 0x80010024, 0x7C0803A6, 0x38210020, 0x4E800020); }
        private static void ClearCharge(HawkingNativeCode native)
        {
            // With Din's Fire removed, Zelda's222C is exclusively the held shot.
            // 2230/2234/2238/2244 are otherwise unused in retained Zelda moves.
            native.Emit(0x807F002C, 0x38000000,
                0x9003222C, 0x90032230, 0x90032234, 0x90032238, 0x90032244);
        }

        private static void RemapState(HawkingNativeCode native, uint address, uint oldState, uint newState)
        { native.Patch(address, 0x38800000 | oldState, 0x38800000 | newState); }

        private static SBM_FighterSubactionData ChairBombScript(SBM_FighterSubactionData source)
        {
            // Original source events (PlSs.dat action309/310): frame3 starts
            // grounded hop; frame10 sets projectile flag, rumble and sound;
            // frame10..43 movement flag0; frame46 grounded crouch interrupt;
            // frame49 unmorph. Bombjump actions295/296 have no projectile flag.
            // Only model changes/body invisibility/morph GFX are omitted. The
            // event data itself, not hand-entered timing constants, is retained.
            var output = new List<byte>();
            AppendScript(source, output, new HashSet<HSDStruct>());
            output.AddRange(new byte[4]);
            return new SBM_FighterSubactionData { _s = new HSDStruct(output.ToArray()) };
        }

        private static void AppendScript(SBM_FighterSubactionData script, List<byte> output, HashSet<HSDStruct> active)
        {
            if (script == null || !active.Add(script._s)) throw new InvalidDataException("Invalid Samus bomb subroutine.");
            byte[] bytes = script._s.GetData();
            for (int offset = 0; offset < bytes.Length;)
            {
                int op = bytes[offset] >> 2;
                if (op == 0 || op == 6) { active.Remove(script._s); return; }
                int size;
                switch (op)
                {
                    case 5: size = 8; break; // call
                    case 10: size = 20; break; // fighter morph GFX (not item explosion)
                    case 17: size = 12; break; // sound
                    case 1: case 2: case 19: case 24: case 31: case 36: case 43: size = 4; break;
                    default: throw new InvalidDataException($"Unexpected Samus bomb event {op}; inspect before porting.");
                }
                if (offset + size > bytes.Length) throw new InvalidDataException("Truncated Samus bomb event.");
                if (op == 5)
                    AppendScript(script._s.GetReference<SBM_FighterSubactionData>(offset + 4), output, active);
                else if (op != 10 && op != 31 && op != 36)
                    for (int i = 0; i < size; i++) output.Add(bytes[offset + i]);
                offset += size;
            }
            throw new InvalidDataException("Samus bomb script has no terminator.");
        }


        private static void AddRoot(HSDRawFile file, string name, HSDStruct data)
        {
            if (file.Roots.Any(r => r.Name == name))
                throw new InvalidDataException($"Refusing to overwrite existing {name} runtime hooks.");
            file.Roots.Add(new HSDRootNode { Name = name, Data = new HSDAccessor { _s = data } });
        }
    }
}
