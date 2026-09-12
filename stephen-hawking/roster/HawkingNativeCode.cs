using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HSDRaw;

namespace CustomSmash
{
    // A deliberately small GALE01 PPC transplant/linker, not a second compiler.
    // It retains the original instructions/ABI and emits the same relocation
    // format used by KirbyClone and m-ex 1.1 Standalone Functions/Reloc.asm.
    internal sealed class HawkingNativeCode
    {
        private readonly byte[] dol;
        private readonly List<uint> words = new List<uint>();
        private readonly Dictionary<uint, uint> locations = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, KeyValuePair<byte, uint>> relocations =
            new Dictionary<uint, KeyValuePair<byte, uint>>();

        internal HawkingNativeCode(string path) { dol = File.ReadAllBytes(path); }
        internal uint Position => checked((uint)words.Count * 4);
        internal uint Location(uint address) => locations[address];
        internal uint Read(uint address)
        {
            for (int i = 0; i < 18; i++)
            {
                uint start = Big(dol, 0x48 + i * 4), size = Big(dol, 0x90 + i * 4);
                if (address >= start && address - start + 4 <= size)
                    return Big(dol, checked((int)(Big(dol, i * 4) + address - start)));
            }
            throw new InvalidDataException($"GALE01 DOL does not contain 0x{address:X8}.");
        }

        internal void Import(uint first, uint end)
        {
            for (uint p = first; p < end; p += 4)
            {
                locations.Add(p, Position);
                Emit(Read(p));
            }
        }

        internal void LinkOriginalCode()
        {
            foreach (var entry in locations)
            {
                uint address = entry.Key, at = entry.Value, word = Read(address);
                if ((word >> 26) == 18)
                {
                    if ((word & 2) != 0) throw new InvalidDataException("Unexpected absolute PPC branch.");
                    int displacement = unchecked((int)(word << 6)) >> 6;
                    uint target = unchecked(address + (uint)(displacement & ~3));
                    BranchAt(at, Resolve(target), (word & 1) != 0);
                }
                else if ((word >> 26) == 16)
                {
                    uint target = unchecked(address + (uint)(short)(word & 0xFFFC));
                    if (!locations.ContainsKey(target) ||
                        unchecked((int)(Location(target) - at)) != (short)(word & 0xFFFC))
                        throw new InvalidDataException("Conditional branch escapes its transplanted block.");
                }
                // Only address-forming pairs resolving into our copied text need
                // relocation. r2/r13-relative vanilla constants remain valid.
                else if ((word >> 26) == 14)
                {
                    uint ra = (word >> 16) & 31;
                    if (ra == 0) continue;
                    for (uint prior = address - 4; prior + 64 >= address && locations.ContainsKey(prior); prior -= 4)
                    {
                        uint high = Read(prior);
                        if ((high & 0xFFFF0000) != (0x3C000000 | (ra << 21))) continue;
                        uint target = unchecked(((high & 0xFFFF) << 16) + (uint)(short)word);
                        if (locations.ContainsKey(target))
                        {
                            Relocate(Location(prior) + 2, 6, Location(target));
                            Relocate(at + 2, 4, Location(target));
                        }
                        break;
                    }
                }
            }
        }

        internal void AdaptAttributeLoads(uint first, uint end, int attributeArticleOffset)
        {
            // Fighter+0x2D4 is Zelda's live special attributes: NEVER overwrite it.
            // Replace each lwz rD,0x2D4(rA) by a non-linking branch to a thunk
            // loading Fighter+0x10C -> ftData+0x48 -> our private attribute slot.
            // Only rD changes, exactly as for the replaced instruction; LR/CR and
            // every other register are untouched, including when rD == rA.
            for (uint address = first; address < end; address += 4)
            {
                uint word = Read(address);
                if ((word & 0xFC00FFFF) != 0x800002D4) continue;
                uint rd = (word >> 21) & 31, ra = (word >> 16) & 31;
                if (rd == 0) throw new InvalidDataException("Attribute thunk cannot use r0 as a pointer base.");
                uint thunk = Position;
                Emit(0x8000010C | (rd << 21) | (ra << 16),
                     0x80000048 | (rd << 21) | (rd << 16),
                     0x80000000 | (rd << 21) | (rd << 16) | (uint)attributeArticleOffset);
                Branch(Location(address + 4));
                BranchAt(Location(address), thunk);
            }
        }

        internal void ScaleAttributeLoad(uint address, uint expected, uint fighterRegister)
        {
            if (Read(address) != expected || (expected & 0xFFE00000) != 0xC0000000)
                throw new InvalidDataException("Expected a Samus scaled-attribute load into f0.");
            // Samus LoadSpecialAttrs multiplies these values by Fighter+0x38.
            // Our immutable per-DAT block cannot be scaled globally (two copies
            // can have different Giant/Tiny scales), so multiply at each use.
            // f13 is ABI-volatile and unused throughout these copied routines.
            uint thunk = Position;
            Emit(expected, 0xC1A00038 | (fighterRegister << 16), 0xEC000372); // lfs f13,scale; fmuls f0,f0,f13
            Branch(Location(address + 4));
            BranchAt(Location(address), thunk);
        }

        internal void Patch(uint address, uint expected, uint replacement)
        {
            if (Read(address) != expected)
                throw new InvalidDataException($"Expected clean GALE01 instruction {expected:X8} at {address:X8}.");
            uint at = Location(address);
            relocations.Remove(at);
            words[(int)(at / 4)] = replacement;
        }
        internal void Redirect(uint address, uint target, bool link = false)
        { BranchAt(Location(address), Resolve(target), link); }
        internal void Pointer(uint value)
        {
            uint at = Position;
            Emit(value);
            if (locations.ContainsKey(value)) Relocate(at, 1, Location(value));
        }
        internal void Emit(params uint[] values) { words.AddRange(values); }
        internal void Branch(uint target, bool link = false) { uint at = Position; Emit(0); BranchAt(at, target, link); }
        internal void BranchAt(uint at, uint target, bool link = false)
        {
            words[(int)(at / 4)] = link ? 0x48000001u : 0x48000000u;
            Relocate(at, 10, target);
        }
        internal void Relocate(uint at, byte type, uint target)
        { relocations[at] = new KeyValuePair<byte, uint>(type, target); }
        private uint Resolve(uint target) => locations.TryGetValue(target, out uint local) ? local : target;
        internal HSDStruct Code() => Words(words.ToArray());
        internal HSDStruct Relocations() => Words(relocations.OrderBy(x => x.Key)
            .SelectMany(x => new[] { ((uint)x.Value.Key << 24) | x.Key, x.Value.Value }).ToArray());
        internal int RelocationCount => relocations.Count;

        internal static HSDStruct Header(HSDStruct code, HSDStruct relocations, int count, params uint[] overrides)
        {
            // xFunction: code, instruction relocations/count, overloads/count,
            // code size, debug symbol table/count. No overload uses an absolute
            // DOL address: only this fighter's / this custom item's table changes.
            var header = new HSDStruct(0x20);
            header.SetReferenceStruct(0, code);
            header.SetReferenceStruct(4, relocations);
            header.SetInt32(8, count);
            header.SetReferenceStruct(12, Words(overrides));
            header.SetInt32(16, overrides.Length / 2);
            header.SetInt32(20, code.Length);
            return header;
        }
        internal static HSDStruct Words(params uint[] values)
        {
            var data = new HSDStruct(values.Length * 4);
            for (int i = 0; i < values.Length; i++) data.SetInt32(i * 4, unchecked((int)values[i]));
            return data;
        }
        private static uint Big(byte[] bytes, int offset) =>
            ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) |
            ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
    }
}
