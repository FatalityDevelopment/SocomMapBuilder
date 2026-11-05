using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Geo
{
    /// <summary>
    /// Scrolling textures: the <c>TextureScroll_Object</c> record of a map's own <c>.ZED</c> (<c>M51.ZED</c>), found on
    /// 25 of the 34 retail maps (rivers, sky fog bands, rain). Rows of 520 bytes: the model (char[256], e.g.
    /// <c>m51_terrain</c>), the visual inside it (char[256], a GEO visual name such as <c>riv1</c>) and the u / v
    /// scroll speeds (2 × f32, observed 0.2 / 0 for M51's river). The bytes after each name's NUL are uninitialised
    /// tool memory in retail files. Layout: docs/formats/disc-survey.md.
    /// </summary>
    public static class TextureScroll
    {
        public const int RowSize = 0x208;
        public const int NameSize = 0x100;

        public sealed record Row(string Model, string Visual, float U, float V);

        public static IReadOnlyList<Row> Parse(byte[] data)
        {
            if (data.Length % RowSize != 0)
                throw new InvalidDataException($"TextureScroll_Object is {data.Length} bytes, not a multiple of {RowSize}.");
            var rows = new List<Row>(data.Length / RowSize);
            for (int o = 0; o < data.Length; o += RowSize)
                rows.Add(new Row(Name(data, o), Name(data, o + NameSize),
                    BitConverter.ToSingle(data, o + 2 * NameSize), BitConverter.ToSingle(data, o + 2 * NameSize + 4)));
            return rows;
        }

        /// <summary>Rows written with zeroed name padding.</summary>
        public static byte[] Build(IEnumerable<Row> rows)
        {
            using var ms = new MemoryStream();
            foreach (var r in rows)
            {
                var row = new byte[RowSize];
                Put(row, 0, r.Model);
                Put(row, NameSize, r.Visual);
                BitConverter.GetBytes(r.U).CopyTo(row, 2 * NameSize);
                BitConverter.GetBytes(r.V).CopyTo(row, 2 * NameSize + 4);
                ms.Write(row, 0, row.Length);
            }
            return ms.ToArray();
        }

        private static string Name(byte[] data, int at)
        {
            int end = Array.IndexOf(data, (byte)0, at, NameSize);
            return Encoding.ASCII.GetString(data, at, (end < 0 ? at + NameSize : end) - at);
        }

        private static void Put(byte[] row, int at, string name)
        {
            byte[] b = Encoding.ASCII.GetBytes(name);
            if (b.Length >= NameSize) throw new ArgumentException($"Name too long: {name}");
            b.CopyTo(row, at);
        }
    }
}
