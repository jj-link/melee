using System.IO;
using HSDRaw;
using mexTool.Core;

namespace CustomSmash
{
    internal static class KirbyClone
    {
        internal static void Add(MEXFighter luigi, MEXFighter john)
        {
            var cap = new HSDRawFile(MEX.ImageResource.GetFileData(luigi.KirbyCapFileName));
            // m-ex 1.1 Header.s: rtoc+0x124 is Kirby's cap-data pointer table.
            // Vanilla Luigi's gain callback indexes slot 17. Bind that one slot
            // to the current copy's loaded cap for the call, then restore it.
            // This works even when no actual Luigi is present in the match.
            var code = Words(
                0x9421FFE0, // stwu r1,-0x20(r1)
                0x7C0802A6, // mflr r0
                0x90010024, // stw r0,0x24(r1)
                0x93C10018, // stw r30,0x18(r1)
                0x93E1001C, // stw r31,0x1C(r1)
                0x83E20124, // lwz r31,0x124(r2): cap pointer table
                0x83DF0044, // lwz r30,17*4(r31): preserve Luigi's entry
                0x8083002C, // lwz r4,0x2C(r3): GOBJ user data
                0x80842238, // lwz r4,0x2238(r4): copied internal fighter kind
                0x5484103A, // slwi r4,r4,2
                0x7C9F202E, // lwzx r4,r31,r4
                0x909F0044, // stw r4,17*4(r31)
                0x48000001, // bl original Luigi gain callback (relocated)
                0x93DF0044, // stw r30,17*4(r31)
                0x83C10018, // lwz r30,0x18(r1)
                0x83E1001C, // lwz r31,0x1C(r1)
                0x80010024, // lwz r0,0x24(r1)
                0x7C0803A6, // mtlr r0
                0x38210020, // addi r1,r1,0x20
                0x4E800020, // blr
                // KirbyIndexItems passes (copied kind, loaded cap data), not GOBJ.
                // IDs >=27 do not enter the vanilla item-initialization switch.
                0x8064000C, // lwz r3,0x0C(r4): Luigi fireball article
                0x38800084, // li r4,132: It_Kind_Kirby_LuigiFire
                0x48000000  // b ItemRegister (relocated tail call)
            );
            var relocations = Words(
                0x0A000030, luigi.Functions.KirbyOnSwallow,
                0x0A000058, 0x8026B3F8
            );
            // xFunction overrides table index 0 (gain) and 5 (item initialization).
            var overrides = Words(0, 0, 5, 0x50);
            var header = new HSDStruct(0x20);
            header.SetReferenceStruct(0, code);
            header.SetReferenceStruct(4, relocations);
            header.SetInt32(8, 2);
            header.SetReferenceStruct(0x0C, overrides);
            header.SetInt32(0x10, 2);
            header.SetInt32(0x14, 0x5C);
            cap.Roots.Add(new HSDRootNode { Name = "kbFunction", Data = new HSDAccessor { _s = header } });
            john.KirbyCapFileName = "PlKbJp.dat";
            using (var stream = new MemoryStream())
            {
                cap.Save(stream, trim: true);
                MEX.ImageResource.AddFile(john.KirbyCapFileName, stream.ToArray());
            }
        }

        private static HSDStruct Words(params uint[] values)
        {
            var data = new HSDStruct(values.Length * 4);
            for (int i = 0; i < values.Length; i++)
                data.SetInt32(i * 4, unchecked((int)values[i]));
            return data;
        }
    }
}
