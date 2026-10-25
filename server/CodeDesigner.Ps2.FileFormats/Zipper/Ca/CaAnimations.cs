namespace CodeDesigner.Ps2.FileFormats.Zipper.Ca
{
    /// <summary>
    /// CA animation banks (<c>BONE</c> resources, e.g. <c>RUN/AUX_GZAN.ZDB › bone_loader</c>: 460 clips). A bank is a
    /// serialized object graph holding 0x60-byte clip records: +0x00 tracks vector {begin, end, capacity}, +0x20 flags
    /// (0x2017.... / 0x2019....), +0x24 name CRC32, +0x2C duration (s). A track is 16 bytes {bone hash, position
    /// keys, rotation keys, counts (low 16 = 3 x positions, high 16 = 4 x rotations)}; keys are int16 (x, y, z) in
    /// 1/256 units and (x, y, z, w) / 32767 quaternions, bone-local (relative to the parent bone), spread evenly over
    /// the clip. Port of Mods/SOCOMCA/ca_anim.ca_clips / ca_clip (CA clips converted this way play in SOCOM II).
    /// </summary>
    public static class CaAnimations
    {
        public const float PositionScale = 1f / 256f;
        public const float FramesPerSecond = 30f;

        public sealed record Track(uint Bone, short[][] Positions, short[][] Rotations);

        public sealed record Clip(uint Hash, string Name, string Bank, float Duration, List<Track> Tracks)
        {
            /// <summary>Frame intervals at 30 fps (samples = intervals + 1).</summary>
            public int Intervals => Math.Max(1, (int)MathF.Round(Duration * FramesPerSecond));
        }

        public static List<Clip> Parse(byte[] d, string bank, Func<uint, string?>? names = null)
        {
            uint U(int o) => BitConverter.ToUInt32(d, o);
            var clips = new List<Clip>();
            var seen = new HashSet<uint>();
            for (int o = 0; o + 0x60 <= d.Length; o += 4)
            {
                uint flags = U(o + 0x20) >> 16;
                if (flags is not (0x2017 or 0x2019)) continue;
                uint a = U(o), b = U(o + 4), c = U(o + 8);
                if (!(0 < a && a < b && b == c && b <= d.Length && (b - a) % 16 == 0 && (b - a) / 16 <= 40)) continue;
                uint hash = U(o + 0x24);
                if (!seen.Add(hash)) continue;
                float duration = BitConverter.ToSingle(d, o + 0x2C);
                var tracks = new List<Track>();
                for (int t = (int)a; t < b; t += 16)
                {
                    uint bone = U(t), pp = U(t + 4), rp = U(t + 8), counts = U(t + 12);
                    int np = (int)(counts & 0xFFFF) / 3, nr = (int)(counts >> 16) / 4;
                    if (pp + np * 6 > d.Length || rp + nr * 8 > d.Length) { tracks.Clear(); break; }
                    var pos = new short[np][];
                    for (int k = 0; k < np; k++) pos[k] = new[] { BitConverter.ToInt16(d, (int)pp + 6 * k), BitConverter.ToInt16(d, (int)pp + 6 * k + 2), BitConverter.ToInt16(d, (int)pp + 6 * k + 4) };
                    var rot = new short[nr][];
                    for (int k = 0; k < nr; k++) rot[k] = new[] { BitConverter.ToInt16(d, (int)rp + 8 * k), BitConverter.ToInt16(d, (int)rp + 8 * k + 2), BitConverter.ToInt16(d, (int)rp + 8 * k + 4), BitConverter.ToInt16(d, (int)rp + 8 * k + 6) };
                    tracks.Add(new Track(bone, pos, rot));
                }
                if (tracks.Count == 0 || !float.IsFinite(duration) || duration <= 0) continue;
                clips.Add(new Clip(hash, names?.Invoke(hash) ?? CaClipNames.Name(hash) ?? $"clip_{hash:x8}", bank, duration, tracks));
            }
            return clips;
        }

        /// <summary>A track's positions (units) at each of <paramref name="samples"/> evenly spaced times; one entry
        /// when it has one key.</summary>
        public static List<float[]> SamplePositions(Track t, int samples)
        {
            var keys = t.Positions.Select(p => new[] { p[0] * PositionScale, p[1] * PositionScale, p[2] * PositionScale }).ToList();
            if (keys.Count <= 1) return keys;
            return Enumerable.Range(0, samples).Select(i =>
            {
                var (k, f) = At(keys.Count, i, samples);
                return new[] { Lerp(keys[k][0], keys[k + 1][0], f), Lerp(keys[k][1], keys[k + 1][1], f), Lerp(keys[k][2], keys[k + 1][2], f) };
            }).ToList();
        }

        /// <summary>A track's rotations (unit quaternions x, y, z, w) per sample (nlerp: keys are 1/30 s apart).</summary>
        public static List<float[]> SampleRotations(Track t, int samples)
        {
            var keys = t.Rotations.Select(Normalize).ToList();
            if (keys.Count <= 1) return keys;
            return Enumerable.Range(0, samples).Select(i =>
            {
                var (k, f) = At(keys.Count, i, samples);
                float[] a = keys[k], b = keys[k + 1];
                float dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3], s = dot < 0 ? -1 : 1;
                var q = new[] { Lerp(a[0], s * b[0], f), Lerp(a[1], s * b[1], f), Lerp(a[2], s * b[2], f), Lerp(a[3], s * b[3], f) };
                return Normalize(q);
            }).ToList();
        }

        private static (int Key, float Frac) At(int keys, int sample, int samples)
        {
            float x = samples <= 1 ? 0 : (float)sample / (samples - 1) * (keys - 1);
            int k = Math.Min((int)x, keys - 2);
            return (k, x - k);
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static float[] Normalize(short[] q) => Normalize(new[] { q[0] / 32767f, q[1] / 32767f, q[2] / 32767f, q[3] / 32767f });

        private static float[] Normalize(float[] q)
        {
            float n = MathF.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            return n > 1e-6f ? new[] { q[0] / n, q[1] / n, q[2] / n, q[3] / n } : new[] { 0f, 0f, 0f, 1f };
        }
    }
}
