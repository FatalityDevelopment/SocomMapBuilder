using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Geo
{
    /// <summary>
    /// CLUTTER.ZAR: the trees, bushes, grass, rocks and small props a map places outside its GEO scene.
    /// Tree: <c>Clutter</c> > <c>&lt;model&gt;</c> > one <c>a_clutter</c> per placement > <c>params</c> (96 bytes)
    /// + <c>scale_inverse</c> (f32). <c>params</c> is either a 4x4 matrix laid out like nparams (translation in
    /// floats 12-14) or a quaternion (floats 0-3, x y z w, not always unit length), floats 4-7 = 0 0 0 1, the
    /// position in 8-10 and the uniform scale in 11 (= 1 / scale_inverse). Floats 12-23 are kept verbatim.
    /// Port of parseClutterPlacements in zed-scene-compose.ts.
    /// </summary>
    public static class ClutterFile
    {
        public enum Layout { Matrix, Quaternion }

        public sealed class Placement
        {
            public string ModelName { get; set; } = string.Empty;
            public Layout Layout { get; set; }
            /// <summary>World transform, column-major.</summary>
            public float[] Matrix { get; set; } = Matrix4.Identity();
            /// <summary>Index of the <c>params</c> record in the container (for in-place edits).</summary>
            public int ParamsIndex { get; set; } = -1;
            public int ScaleInverseIndex { get; set; } = -1;
        }

        public static List<Placement> Parse(ZedContainer container)
        {
            var nodes = container.Nodes;
            var parents = ZedTree.ParentIndices(nodes);
            var outList = new List<Placement>();
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].Name != "params") continue;
                int clutter = parents[i];
                int model = clutter >= 0 ? parents[clutter] : -1;
                byte[] d = nodes[i].Data;
                if (model < 0 || d.Length < 64) continue;
                float F(int k) => BitConverter.ToSingle(d, k * 4);

                var p = new Placement { ModelName = nodes[model].Name, ParamsIndex = i };
                for (int s = 0; s < nodes.Count; s++)
                    if (parents[s] == clutter && nodes[s].Name == "scale_inverse") { p.ScaleInverseIndex = s; break; }

                if (F(3) == 0 && F(7) == 0 && F(11) == 0 && F(15) == 1)
                {
                    p.Layout = Layout.Matrix;
                    p.Matrix = Enumerable.Range(0, 16).Select(F).ToArray();
                }
                else if (F(4) == 0 && F(5) == 0 && F(6) == 0 && F(7) == 1 && F(11) > 0)
                {
                    p.Layout = Layout.Quaternion;
                    p.Matrix = QuaternionMatrix(F(0), F(1), F(2), F(3), F(11), F(8), F(9), F(10));
                }
                else continue;
                if (p.Matrix.All(float.IsFinite)) outList.Add(p);
            }
            return outList;
        }

        public static List<Placement> Parse(byte[] clutterZar) => Parse(ZedContainer.FromBytes(clutterZar));

        /// <summary>
        /// Writes <paramref name="matrix"/> (column-major, rotation + uniform scale + translation) into the
        /// placement's <c>params</c> record in its own layout, and its <c>scale_inverse</c>.
        /// </summary>
        public static void WritePlacement(ZedContainer container, Placement placement, float[] matrix)
        {
            byte[] d = (byte[])container.Nodes[placement.ParamsIndex].Data.Clone();
            float scale = MathF.Sqrt(matrix[0] * matrix[0] + matrix[1] * matrix[1] + matrix[2] * matrix[2]);
            if (placement.Layout == Layout.Matrix)
            {
                for (int k = 0; k < 16; k++) WriteF(d, k, matrix[k]);
            }
            else
            {
                var (x, y, z, w) = MatrixToQuaternion(matrix, scale);
                WriteF(d, 0, x); WriteF(d, 1, y); WriteF(d, 2, z); WriteF(d, 3, w);
                WriteF(d, 8, matrix[12]); WriteF(d, 9, matrix[13]); WriteF(d, 10, matrix[14]); WriteF(d, 11, scale);
            }
            container.ReplaceNodeData(placement.ParamsIndex, d);
            if (placement.ScaleInverseIndex >= 0 && scale > 0)
                container.ReplaceNodeData(placement.ScaleInverseIndex, BitConverter.GetBytes(1f / scale));
            placement.Matrix = (float[])matrix.Clone();
        }

        /// <summary>
        /// Adds a placement of <paramref name="modelName"/>. The new <c>a_clutter</c> record copies an existing
        /// placement of the same model (any model when it has none, adding the model group) so the floats past the
        /// transform keep plausible values, then takes <paramref name="matrix"/>.
        /// </summary>
        public static Placement Add(ZedContainer container, string modelName, float[] matrix)
        {
            var nodes = container.Nodes;
            int root = -1;
            for (int i = 0; i < nodes.Count; i++) if (nodes[i].Name == "Clutter") { root = i; break; }
            if (root < 0) throw new InvalidDataException("CLUTTER.ZAR has no Clutter record");

            var existing = Parse(container);
            Placement? template = existing.FirstOrDefault(p => p.ModelName == modelName) ?? existing.FirstOrDefault();
            if (template == null) throw new InvalidDataException("CLUTTER.ZAR has no placement to use as a template; add one in another map first");
            var parents = ZedTree.ParentIndices(nodes);
            ZedTree.Record record = ZedTree.Copy(nodes, parents[template.ParamsIndex]);

            int model = ZedTree.Children(nodes, root).FirstOrDefault(c => nodes[c].Name == modelName, -1);
            if (model < 0) model = ZedTree.AppendChild(container, root, new ZedTree.Record(modelName, Array.Empty<byte>()));
            int placed = ZedTree.AppendChild(container, model, record);

            var added = Parse(container).Single(p => ZedTree.ParentIndices(container.Nodes)[p.ParamsIndex] == placed);
            WritePlacement(container, added, matrix);
            return added;
        }

        /// <summary>Removes a placement (its <c>a_clutter</c> record); an emptied model group is removed too.</summary>
        public static void Delete(ZedContainer container, Placement placement)
        {
            var parents = ZedTree.ParentIndices(container.Nodes);
            int clutter = parents[placement.ParamsIndex];
            int model = parents[clutter];
            ZedTree.Remove(container, clutter);
            if (model >= 0 && container.Nodes[model].Type == 0) ZedTree.Remove(container, model);
        }

        private static void WriteF(byte[] d, int k, float v) => BitConverter.GetBytes(v).CopyTo(d, k * 4);

        public static float[] QuaternionMatrix(float qx, float qy, float qz, float qw, float s, float tx, float ty, float tz)
        {
            float len = MathF.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (len <= 0) len = 1;
            float x = qx / len, y = qy / len, z = qz / len, w = qw / len;
            return new[]
            {
                (1 - 2 * (y * y + z * z)) * s, 2 * (x * y + w * z) * s, 2 * (x * z - w * y) * s, 0,
                2 * (x * y - w * z) * s, (1 - 2 * (x * x + z * z)) * s, 2 * (y * z + w * x) * s, 0,
                2 * (x * z + w * y) * s, 2 * (y * z - w * x) * s, (1 - 2 * (x * x + y * y)) * s, 0,
                tx, ty, tz, 1,
            };
        }

        private static (float X, float Y, float Z, float W) MatrixToQuaternion(float[] m, float scale)
        {
            float s = scale > 0 ? scale : 1;
            float m00 = m[0] / s, m01 = m[4] / s, m02 = m[8] / s;
            float m10 = m[1] / s, m11 = m[5] / s, m12 = m[9] / s;
            float m20 = m[2] / s, m21 = m[6] / s, m22 = m[10] / s;
            float trace = m00 + m11 + m22;
            if (trace > 0)
            {
                float k = MathF.Sqrt(trace + 1) * 2;
                return ((m21 - m12) / k, (m02 - m20) / k, (m10 - m01) / k, k / 4);
            }
            if (m00 > m11 && m00 > m22)
            {
                float k = MathF.Sqrt(1 + m00 - m11 - m22) * 2;
                return (k / 4, (m01 + m10) / k, (m02 + m20) / k, (m21 - m12) / k);
            }
            if (m11 > m22)
            {
                float k = MathF.Sqrt(1 + m11 - m00 - m22) * 2;
                return ((m01 + m10) / k, k / 4, (m12 + m21) / k, (m02 - m20) / k);
            }
            float kk = MathF.Sqrt(1 + m22 - m00 - m11) * 2;
            return ((m02 + m20) / kk, (m12 + m21) / kk, kk / 4, (m10 - m01) / kk);
        }
    }
}
