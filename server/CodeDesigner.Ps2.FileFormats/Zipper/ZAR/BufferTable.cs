using System;
using System.Collections.Generic;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.ZAR
{
    /// <summary>
    /// <c>DBUFFER.ZAR</c>: one top-level record <c>file buffersize table</c> whose children are named after archives
    /// (<c>M51_GEO.ZED</c>) and hold one u32 each. The value is the archive's body size (ZED header +0x54): it matches
    /// for 1430 of the 1488 archive entries found in the 34 retail map packages; the other 58 differ by a few KB either
    /// way (stale build values: WEAP_MDL.ZED's body is larger than its entry in several retail packages). A map package
    /// carries its own table (<c>RUN\SP\M51\DBUFFER.ZAR</c>) and the shared one (<c>RUN\COMMON\DBUFFER.ZAR</c>), whose
    /// table also lists vehicle and turret archives the package does not hold. Whether the game sizes a buffer from it
    /// has not been tested. Layout: docs/formats/disc-survey.md.
    /// </summary>
    public static class BufferTable
    {
        public const string RootName = "file buffersize table";

        /// <summary>Archive leaf name → listed size, in file order.</summary>
        public static IReadOnlyList<(string Archive, uint Size)> Parse(byte[] zar)
        {
            var c = ZedContainer.FromBytes(zar);
            var rows = new List<(string, uint)>();
            if (c.NodeCount == 0 || c.Nodes[0].Name != RootName) return rows;
            int children = (int)c.Nodes[0].Type;
            for (int i = 1; i <= children && i < c.NodeCount; i++)
                if (c.Nodes[i].Data.Length >= 4) rows.Add((c.Nodes[i].Name, BitConverter.ToUInt32(c.Nodes[i].Data, 0)));
            return rows;
        }

        /// <summary>The body size of a ZED / ZAR (the value a buffer table lists for it).</summary>
        public static uint BodySize(byte[] archive) => archive.Length >= 0x58 ? BitConverter.ToUInt32(archive, 0x54) : 0;
    }
}
