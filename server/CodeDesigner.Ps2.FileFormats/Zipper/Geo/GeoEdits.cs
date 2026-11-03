using System.Text;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Geo
{
    /// <summary>
    /// Edits to a GEO archive's scene nodes in place (see docs/formats/geo.md): a node's <c>nparams</c> (transform,
    /// bounds, kind, flags) and its <c>model_name</c> reference. Each changes one record and splices it back, so every
    /// other byte of the archive stays as it was.
    /// </summary>
    public static class GeoEdits
    {
        /// <summary>
        /// Sets parts of the <c>nparams</c> record at <paramref name="index"/>: the local transform (16 floats,
        /// column-major), bounds min / max, kind (word 22) and flags (word 23). Null parts keep their value.
        /// </summary>
        public static byte[] WithNodeParams(byte[] geoZed, int index, float[]? matrix, float[]? min, float[]? max, int? kind, uint? flags)
        {
            var nodes = ZedContainer.FromBytes(geoZed).Nodes;
            if (index < 0 || index >= nodes.Count || nodes[index].Name != "nparams") throw new ArgumentException($"Record #{index} is not an nparams");
            byte[] d = nodes[index].Data;
            if (d.Length < 64) throw new InvalidDataException($"nparams #{index} is {d.Length} bytes: too short for a transform");
            // A short record (no kind / flags words) grows to the full 96 bytes when a later word is set.
            int need = flags != null ? 96 : kind != null ? 92 : min != null || max != null ? 88 : 64;
            d = d.Length >= need ? (byte[])d.Clone() : d.Concat(new byte[96 - d.Length]).ToArray();
            void Floats(float[]? v, int count, int word, string what)
            {
                if (v == null) return;
                if (v.Length != count || !v.All(float.IsFinite)) throw new ArgumentException($"{what} is {count} finite numbers");
                for (int i = 0; i < count; i++) BitConverter.GetBytes(v[i]).CopyTo(d, (word + i) * 4);
            }
            Floats(matrix, 16, 0, "The transform");
            Floats(min, 3, 16, "Bounds min");
            Floats(max, 3, 19, "Bounds max");
            if (kind is int k) BitConverter.GetBytes(k).CopyTo(d, 22 * 4);
            if (flags is uint f) BitConverter.GetBytes(f).CopyTo(d, 23 * 4);
            return ZedSplice.Replace(geoZed, index, d);
        }

        /// <summary>
        /// Sets the <c>model_name</c> record at <paramref name="index"/> to <paramref name="name"/> (printable ASCII,
        /// zero-terminated). A name that fits keeps the record's size, zero-padded, so the archive does not move.
        /// </summary>
        public static byte[] WithModelName(byte[] geoZed, int index, string name)
        {
            var nodes = ZedContainer.FromBytes(geoZed).Nodes;
            if (index < 0 || index >= nodes.Count || nodes[index].Name != "model_name") throw new ArgumentException($"Record #{index} is not a model_name");
            if (name.Length == 0 || name.Any(c => c < 0x20 || c > 0x7E)) throw new ArgumentException("A model name is printable ASCII");
            byte[] text = Encoding.ASCII.GetBytes(name);
            int size = Math.Max(nodes[index].Data.Length, (text.Length + 1 + 3) / 4 * 4);
            var d = new byte[size];
            text.CopyTo(d, 0);
            return ZedSplice.Replace(geoZed, index, d);
        }
    }
}
