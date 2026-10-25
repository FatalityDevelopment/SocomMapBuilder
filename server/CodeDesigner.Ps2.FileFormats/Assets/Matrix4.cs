namespace CodeDesigner.Ps2.FileFormats.Assets
{
    /// <summary>Column-major 4x4 matrices (translation in 12-14): the convention every game's mesh code here uses.</summary>
    public static class Matrix4
    {
        public static float[] Identity() => new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        public static float[] Multiply(float[] a, float[] b)
        {
            var o = new float[16];
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 4; col++)
                    o[col * 4 + row] = a[row] * b[col * 4] + a[4 + row] * b[col * 4 + 1] + a[8 + row] * b[col * 4 + 2] + a[12 + row] * b[col * 4 + 3];
            return o;
        }

        public static (float X, float Y, float Z) Apply(float[] m, float x, float y, float z) =>
            (x * m[0] + y * m[4] + z * m[8] + m[12],
             x * m[1] + y * m[5] + z * m[9] + m[13],
             x * m[2] + y * m[6] + z * m[10] + m[14]);

        /// <summary>Uniform scale × rotation about Y (degrees, 0 = identity) + translation.</summary>
        public static float[] Compose(float x, float y, float z, float yawDegrees = 0, float scale = 1)
        {
            float r = yawDegrees * MathF.PI / 180f, c = MathF.Cos(r) * scale, s = MathF.Sin(r) * scale;
            return new[] { c, 0, -s, 0, 0, scale, 0, 0, s, 0, c, 0, x, y, z, 1 };
        }

        /// <summary>Uniform scale of a transform (length of its X axis).</summary>
        public static float ScaleOf(float[] m) => MathF.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]);

        /// <summary>Rotation about Y in degrees, read from the X axis.</summary>
        public static float YawOf(float[] m) => MathF.Atan2(-m[2], m[0]) * 180f / MathF.PI;

        /// <summary>Inverse of an affine transform (bottom row 0 0 0 1).</summary>
        public static float[] InverseAffine(float[] m)
        {
            float a = m[0], b = m[4], c = m[8], d = m[1], e = m[5], f = m[9], g = m[2], h = m[6], i = m[10];
            float det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
            if (MathF.Abs(det) < 1e-12f) throw new ArgumentException("Transform is not invertible");
            float inv = 1 / det;
            float[] r =
            {
                (e * i - f * h) * inv, -(d * i - f * g) * inv, (d * h - e * g) * inv, 0,
                -(b * i - c * h) * inv, (a * i - c * g) * inv, -(a * h - b * g) * inv, 0,
                (b * f - c * e) * inv, -(a * f - c * d) * inv, (a * e - b * d) * inv, 0,
                0, 0, 0, 1,
            };
            var (tx, ty, tz) = Apply(r, m[12], m[13], m[14]);
            r[12] = -tx; r[13] = -ty; r[14] = -tz;
            return r;
        }

        public static bool LooksLikeTransform(float[] m)
        {
            if (!m.All(float.IsFinite)) return false;
            float sx = MathF.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]);
            float sy = MathF.Sqrt(m[4] * m[4] + m[5] * m[5] + m[6] * m[6]);
            float sz = MathF.Sqrt(m[8] * m[8] + m[9] * m[9] + m[10] * m[10]);
            bool saneScale = sx > 0.001f && sy > 0.001f && sz > 0.001f && sx < 500 && sy < 500 && sz < 500;
            bool saneBottom = MathF.Abs(m[3]) < 0.01f && MathF.Abs(m[7]) < 0.01f && MathF.Abs(m[11]) < 0.01f && MathF.Abs(MathF.Abs(m[15]) - 1) < 0.25f;
            bool saneTranslate = MathF.Abs(m[12]) < 100000 && MathF.Abs(m[13]) < 100000 && MathF.Abs(m[14]) < 100000;
            return saneScale && saneBottom && saneTranslate;
        }
    }

}
