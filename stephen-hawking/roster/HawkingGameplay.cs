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
        // Source of every DOL address below: config/GALE01/symbols.txt and
        // src/melee/ft/kinds/ftSamus/{ftsamusspeciallw0,ftsamusspeciallw1}.c,
        // src/melee/it/kinds/itsamusbomb.c. This is executable native code, not
        // a source-only decomp modification and not calls into unadapted Samus.
        private const uint JumpFirst = 0x80128944, JumpEnd = 0x80129100;
        private const uint BombFirst = 0x8012ADF0, BombEnd = 0x8012B6E8;
        private const uint ItemFirst = 0x802B4AC8, ItemEnd = 0x802B5518;
        private const uint ZeldaStates = 0x803CFA58, SamusStates = 0x803CE2D0;
        private const int SamusBombItem = 93; // It_Kind_Samus_Bomb, fighter-item table entry 50.

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

            // The original Zelda article pointers remain first and untouched.
            // Additional article references belong only to PlHw.dat. The last
            // reference is a private Samus attribute block, NOT an item entry.
            int bombArticle = data.Articles._s.Length / 4;
            int attrsArticle = bombArticle + 1;
            var articlePointers = data.Articles._s;
            articlePointers.Resize((attrsArticle + 1) * 4);
            articlePointers.SetReference(bombArticle * 4,
                HSDAccessor.DeepClone<SBM_Article>(samus.Articles.Articles[0]));
            articlePointers.SetReference(attrsArticle * 4,
                HSDAccessor.DeepClone<HSDAccessor>(samus.Attributes2));

            // A private CUSTOM item is essential: vanilla bomb accessory code
            // calls Samus's bombjump code on its owner without a fighter-kind
            // guard. Merely registering shared item 93 would read Zelda attrs
            // and enter Zelda's neutral-special states when its own bomb hits.
            // Keep any preexisting custom items; do not confuse this lookup
            // index with bombArticle or with the runtime external item ID.
            int customBomb = hawking.Items.Count;
            var item = HSDAccessor.DeepClone<MEX_Item>(MEX.FighterItems[SamusBombItem - 43]);
            hawking.Items.Add(item);
            hawking.SubCharacter = null;
            hawking.SubCharacterBehavior = SubCharacterBehavior.SpawnSeparately;
            // Retain Zelda voice/SFX mapping, additionally preload Samus's bank
            // for the unmodified bomb article and deployment sound commands.
            hawking.SSMBitfield1 |= samusFighter.SSMBitfield1;
            hawking.SSMBitfield2 |= samusFighter.SSMBitfield2;

            var native = new HawkingNativeCode(Path.Combine(root, "original", "main.dol"));
            native.Import(JumpFirst, JumpEnd);
            native.Import(BombFirst, BombEnd);
            native.Import(ItemFirst, ItemEnd);
            native.LinkOriginalCode();
            native.AdaptAttributeLoads(JumpFirst, JumpEnd, attrsArticle * 4);
            native.AdaptAttributeLoads(BombFirst, BombEnd, attrsArticle * 4);
            native.ScaleAttributeLoad(0x80128B64, 0xC01E0008, 29); // bombjump horizontal impulse
            native.ScaleAttributeLoad(0x80128B78, 0xC01E0008, 29); // bombjump vertical impulse
            native.ScaleAttributeLoad(0x8012AE74, 0xC01E0078, 31); // bomb spawn height
            native.ScaleAttributeLoad(0x8012B0D4, 0xC0080058, 7); // aerial entry vertical velocity
            native.ScaleAttributeLoad(0x8012B614, 0xC0040054, 31); // grounded hop

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

            uint onLoad = native.Position;
            native.Emit(0x9421FFE0, 0x7C0802A6, 0x90010024, 0x93E1001C, 0x7C7F1B78);
            native.Branch(zelda.Functions.OnLoad, true); // exact original Zelda attribute/item setup
            native.Emit(0x807F002C, 0x8083010C, 0x80840048,
                0x80840000u | checked((uint)(bombArticle * 4)),
                0x80630004, 0x38A00000u | checked((uint)customBomb));
            native.Branch(0x803D7058, true); // m-ex Index Fighter Item(kind, article, custom index)
            native.Emit(0x83E1001C, 0x80010024, 0x7C0803A6, 0x38210020, 0x4E800020);

            // ftFunction overload index 3 supplies a complete private state
            // table. All fourteen non-down-B Zelda entries remain byte-for-byte
            // identical; all callbacks in the four replacement entries link to
            // our copied native code. Animation IDs remain Zelda's original IDs.
            uint moves = native.Position;
            int[] sourceStates = { 355, 341, 356, 342 };
            var zeldaActions = data.FighterActionTable.Commands;
            var samusActions = samus.FighterActionTable.Commands;
            for (int state = 341; state <= 358; state++)
            {
                uint original = ZeldaStates + (uint)((state - 341) * 32);
                if (state < 355)
                {
                    for (uint off = 0; off < 32; off += 4) native.Emit(native.Read(original + off));
                    continue;
                }
                uint source = SamusStates + (uint)((sourceStates[state - 355] - 341) * 32);
                int targetAnimation = checked((int)native.Read(original));
                int sourceAnimation = checked((int)native.Read(source));
                native.Emit((uint)targetAnimation, native.Read(source + 4), native.Read(source + 8));
                for (uint off = 12; off < 32; off += 4) native.Pointer(native.Read(source + off));
                zeldaActions[targetAnimation].SubAction = ChairBombScript(samusActions[sourceAnimation].SubAction);
                // Keep the generated animation offsets/sizes/names. Only the
                // script changes; Samus's flags contain his model bone IDs.
            }
            data.FighterActionTable.Commands = zeldaActions;

            // Copy the native bomb's four-state table, including explosion
            // lifetime/hitboxes/reflect/clank/shield/owner cleanup. Its article
            // supplies original model, animations, damage, fuse and velocity.
            // Every call inside the copied bomb module resolves internally,
            // including its explosion accessory -> adapted self-bombjump.
            uint itemStates = native.Position;
            for (uint p = 0x803F7220; p < 0x803F7260; p += 4)
                if ((p & 15) == 0) native.Emit(native.Read(p)); else native.Pointer(native.Read(p));
            var itemOverrides = new List<uint> { 0, itemStates };
            for (int off = 4; off < 0x3C; off += 4)
            {
                uint callback = unchecked((uint)item._s.GetInt32(off));
                if (callback >= ItemFirst && callback < ItemEnd)
                { itemOverrides.Add((uint)(off / 4)); itemOverrides.Add(native.Location(callback)); }
            }
            var code = native.Code();
            var relocations = native.Relocations();
            AddRoot(fighterFile, "ftFunction", HawkingNativeCode.Header(code, relocations, native.RelocationCount,
                0, onLoad, 3, moves, 10, native.Location(0x8012AF5C), 11, native.Location(0x8012B09C)));
            var itemHeader = HawkingNativeCode.Header(code, relocations, native.RelocationCount, itemOverrides.ToArray());
            // itFunction is a count followed by CUSTOM-item-indexed xFunction
            // pointers. Sharing code/relocations is intentional: Reloc's type
            // 1/4/6 stores and type10 OR are idempotent for the same code base.
            // Separate headers carry different fighter/item overload tables.
            var items = new HSDStruct(4 + (customBomb + 1) * 4);
            items.SetInt32(0, customBomb + 1);
            items.SetReferenceStruct(4 + customBomb * 4, itemHeader);
            AddRoot(fighterFile, "itFunction", items);
            AddKirbyCopy(zelda, hawking);
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

        private static void AddKirbyCopy(MEXFighter zelda, MEXFighter hawking)
        {
            var cap = new HSDRawFile(MEX.ImageResource.GetFileData(zelda.KirbyCapFileName));
            // ftKb_SpecialN_800F0A54 indexes vanilla Zelda slot19 in the cap
            // table. m-ex Header.s rtoc+0x124 owns the expanded table and Kirby
            // Fighter+0x2238 is the copied INTERNAL ID. Bind slot19 only during
            // this synchronous gain callback, exactly as KirbyClone does for
            // Luigi. Nayru's Love uses no Kirby article, so no item-init wrapper
            // or bomb copying is appropriate. All other Zelda copy callbacks,
            // copy effects and cap metadata remain the original ones.
            var code = HawkingNativeCode.Words(
                0x9421FFE0, 0x7C0802A6, 0x90010024, 0x93C10018, 0x93E1001C,
                0x83E20124, 0x83DF004C, 0x8083002C, 0x80842238, 0x5484103A,
                0x7C9F202E, 0x909F004C, 0x48000001, 0x93DF004C,
                0x83C10018, 0x83E1001C, 0x80010024, 0x7C0803A6, 0x38210020, 0x4E800020);
            AddRoot(cap, "kbFunction", HawkingNativeCode.Header(code,
                HawkingNativeCode.Words(0x0A000030, zelda.Functions.KirbyOnSwallow), 1, 0, 0));
            hawking.KirbyCapFileName = "PlKbHw.dat";
            using (var stream = new MemoryStream())
            {
                cap.Save(stream, trim: true);
                MEX.ImageResource.AddFile(hawking.KirbyCapFileName, stream.ToArray());
            }
        }

        private static void AddRoot(HSDRawFile file, string name, HSDStruct data)
        {
            if (file.Roots.Any(r => r.Name == name))
                throw new InvalidDataException($"Refusing to overwrite existing {name} runtime hooks.");
            file.Roots.Add(new HSDRootNode { Name = name, Data = new HSDAccessor { _s = data } });
        }
    }
}
