namespace CodeDesigner.Ps2.FileFormats.Zipper.Anim
{
    /// <summary>
    /// A SOCOM II character animation clip: one record of <c>MOTION_S.ZAR</c> (per map package) or
    /// <c>RUN/MOTION_P.ZAR</c> (shared), the version-5 <c>.rdr</c> layout of <c>Mods/SOCOMCA/ca_anim.py</c> and the
    /// workbench's <c>rdr-animation-parser.ts</c>:
    /// <list type="bullet">
    /// <item>header: 5, duration (s), frame intervals, bone count, norm min / max (-1, 1), names offset, tracks offset;</item>
    /// <item>16-byte bone records (runtime scratch, kept as stored), NUL-terminated bone names (leading dashes are part
    /// of the stored name), padding to 4;</item>
    /// <item>per bone a track: 0x0C animated position + rotation, 0x1C animated position, 0x2C animated rotation,
    /// 0x3C static. Animated positions are (x, y, z) int16 per sample, static ones (x, y, z, w) int16; rotations are
    /// int16 quaternions (x, y, z, w) / 32767. Samples = intervals + 1, spread evenly over the duration.</item>
    /// </list>
    /// Positions are the bone's local translation in 1/256 units (a seal's calf is 1108 = 4.33, its GEO bind length),
    /// rotations its local rotation under the parent bone of the character skeleton (bones match by name).
    /// <see cref="Encode"/> writes the parsed bytes back unchanged.
    /// </summary>
    public sealed class MotionClip
    {
        public const int Version = 5;
        public const float PositionScale = 1f / 256f;
        /// <summary>The fourth word stored with static positions in retail clips (no known effect).</summary>
        public const short StaticPositionW = 612;

        public const int KindAnimated = 0x0C, KindAnimatedPosition = 0x1C, KindAnimatedRotation = 0x2C, KindStatic = 0x3C;

        public sealed class Track
        {
            /// <summary>The name as stored, dashes included (<c>--hips</c>).</summary>
            public string RawName { get; set; } = string.Empty;
            public string Name => RawName.TrimStart('-');
            public byte[] Record { get; set; } = new byte[16];
            /// <summary>One (static, 4 values) or <see cref="Samples"/> (3 values) positions.</summary>
            public List<short[]> Positions { get; set; } = new();
            /// <summary>One (static) or <see cref="Samples"/> quaternions (x, y, z, w) int16.</summary>
            public List<short[]> Rotations { get; set; } = new();
            /// <summary>The alignment bytes between animated positions and rotations as stored (not always zero).</summary>
            public byte[] AlignPad { get; set; } = Array.Empty<byte>();
            public int Kind => (Positions.Count > 1, Rotations.Count > 1) switch
            {
                (true, true) => KindAnimated,
                (true, false) => KindAnimatedPosition,
                (false, true) => KindAnimatedRotation,
                _ => KindStatic,
            };
        }

        public float Duration { get; set; }
        public int Intervals { get; set; }
        public float NormMin { get; set; } = -1;
        public float NormMax { get; set; } = 1;
        public List<Track> Tracks { get; } = new();
        /// <summary>Bytes between the names and the tracks as stored (alignment leftovers).</summary>
        public byte[] Pad { get; set; } = Array.Empty<byte>();
        /// <summary>Bytes after the last track as stored.</summary>
        public byte[] Tail { get; set; } = Array.Empty<byte>();

        public int Samples => Intervals + 1;
        public float FramesPerSecond => Duration > 0 && Intervals > 0 ? Intervals / Duration : 30f;

        public Track? Find(string bone) => Tracks.FirstOrDefault(t => t.Name.Equals(bone, StringComparison.OrdinalIgnoreCase));

        public static bool LooksLikeClip(byte[] d) => d.Length >= 0x20 && BitConverter.ToInt32(d, 0) == Version;

        public static MotionClip Parse(byte[] d)
        {
            if (!LooksLikeClip(d)) throw new InvalidDataException("Not a version-5 motion clip");
            var c = new MotionClip
            {
                Duration = BitConverter.ToSingle(d, 4),
                Intervals = BitConverter.ToInt32(d, 8),
                NormMin = BitConverter.ToSingle(d, 0x10),
                NormMax = BitConverter.ToSingle(d, 0x14),
            };
            int bones = BitConverter.ToInt32(d, 0xC);
            int namesAt = BitConverter.ToInt32(d, 0x18), tracksAt = BitConverter.ToInt32(d, 0x1C);
            if (bones < 0 || bones > 512 || namesAt > d.Length || tracksAt > d.Length) throw new InvalidDataException("Bad clip header");
            int p = namesAt;
            for (int i = 0; i < bones; i++)
            {
                int e = Array.IndexOf(d, (byte)0, p);
                if (e < 0) throw new InvalidDataException("Unterminated bone name");
                c.Tracks.Add(new Track { Record = d[(0x20 + 16 * i)..(0x30 + 16 * i)], RawName = System.Text.Encoding.Latin1.GetString(d, p, e - p) });
                p = e + 1;
            }
            c.Pad = d[p..tracksAt];
            int n = c.Samples, o = tracksAt;
            short S(int at) => BitConverter.ToInt16(d, at);
            short[] V(int at, int count) { var v = new short[count]; for (int k = 0; k < count; k++) v[k] = S(at + 2 * k); return v; }
            foreach (var t in c.Tracks)
            {
                int kind = BitConverter.ToInt32(d, o);
                switch (kind)
                {
                    case KindStatic:
                        t.Positions.Add(V(o + 4, 4)); t.Rotations.Add(V(o + 12, 4)); o += 20; break;
                    case KindAnimatedRotation:
                        t.Positions.Add(V(o + 4, 4));
                        for (int k = 0; k < n; k++) t.Rotations.Add(V(o + 12 + 8 * k, 4));
                        o += 12 + 8 * n; break;
                    case KindAnimatedPosition:
                    case KindAnimated:
                    {
                        for (int k = 0; k < n; k++) t.Positions.Add(V(o + 4 + 6 * k, 3));
                        int q = (o + 4 + 6 * n + 3) & ~3;
                        t.AlignPad = d[(o + 4 + 6 * n)..q];
                        int rots = kind == KindAnimated ? n : 1;
                        for (int k = 0; k < rots; k++) t.Rotations.Add(V(q + 8 * k, 4));
                        o = q + 8 * rots; break;
                    }
                    default: throw new InvalidDataException($"Track kind 0x{kind:X} at 0x{o:X} ({t.RawName})");
                }
                if (o > d.Length) throw new InvalidDataException("Track runs past the clip");
            }
            c.Tail = d[o..];
            return c;
        }

        public byte[] Encode()
        {
            Validate();
            var names = Tracks.SelectMany(t => System.Text.Encoding.Latin1.GetBytes(t.RawName).Append((byte)0)).ToArray();
            int namesAt = 0x20 + 16 * Tracks.Count;
            int tracksAt = (namesAt + names.Length + 3) & ~3;
            bool keepPad = Pad.Length == tracksAt - namesAt - names.Length;
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(Version); w.Write(Duration); w.Write(Intervals); w.Write(Tracks.Count);
            w.Write(NormMin); w.Write(NormMax); w.Write(namesAt); w.Write(tracksAt);
            foreach (var t in Tracks) w.Write(t.Record.Length == 16 ? t.Record : new byte[16]);
            w.Write(names);
            w.Write(keepPad ? Pad : new byte[tracksAt - namesAt - names.Length]);
            void Vec(short[] v, int count) { for (int k = 0; k < count; k++) w.Write(k < v.Length ? v[k] : (short)0); }
            foreach (var t in Tracks)
            {
                int kind = t.Kind;
                w.Write(kind);
                if (kind == KindStatic) { Vec(StaticPosition(t.Positions[0]), 4); Vec(t.Rotations[0], 4); }
                else if (kind == KindAnimatedRotation) { Vec(StaticPosition(t.Positions[0]), 4); foreach (var q in t.Rotations) Vec(q, 4); }
                else
                {
                    foreach (var p in t.Positions) Vec(p, 3);
                    int pad = (int)(-ms.Position & 3);
                    w.Write(t.AlignPad.Length == pad ? t.AlignPad : new byte[pad]);
                    foreach (var q in kind == KindAnimated ? t.Rotations : t.Rotations.Take(1)) Vec(q, 4);
                }
            }
            w.Write(Tail);
            return ms.ToArray();
        }

        private static short[] StaticPosition(short[] p) => p.Length >= 4 ? p : new[] { p[0], p[1], p[2], StaticPositionW };

        /// <summary>Every animated track has <see cref="Samples"/> keys; static tracks one.</summary>
        public void Validate()
        {
            if (Intervals < 1) throw new InvalidDataException("A clip needs at least 1 frame interval");
            if (!(Duration > 0) || !float.IsFinite(Duration)) throw new InvalidDataException("Duration must be > 0");
            foreach (var t in Tracks)
            {
                if (t.Positions.Count != 1 && t.Positions.Count != Samples) throw new InvalidDataException($"{t.Name}: {t.Positions.Count} positions, expected 1 or {Samples}");
                if (t.Rotations.Count != 1 && t.Rotations.Count != Samples) throw new InvalidDataException($"{t.Name}: {t.Rotations.Count} rotations, expected 1 or {Samples}");
                if (t.Positions.Any(p => p.Length < 3) || t.Rotations.Any(q => q.Length < 4)) throw new InvalidDataException($"{t.Name}: short key");
            }
        }

        // ------------------------------------------------------------------ values

        public static float[] PositionUnits(short[] p) => new[] { p[0] * PositionScale, p[1] * PositionScale, p[2] * PositionScale };

        public static short[] PositionKey(float x, float y, float z) => new[] { Q(x / PositionScale), Q(y / PositionScale), Q(z / PositionScale) };

        public static float[] Quaternion(short[] q)
        {
            float x = q[0] / 32767f, y = q[1] / 32767f, z = q[2] / 32767f, w = q[3] / 32767f;
            float n = MathF.Sqrt(x * x + y * y + z * z + w * w);
            return n > 1e-6f ? new[] { x / n, y / n, z / n, w / n } : new[] { 0f, 0f, 0f, 1f };
        }

        public static short[] QuaternionKey(float x, float y, float z, float w)
        {
            float n = MathF.Sqrt(x * x + y * y + z * z + w * w);
            if (!(n > 1e-6f)) return new short[] { 0, 0, 0, 32767 };
            return new[] { Q(x / n * 32767), Q(y / n * 32767), Q(z / n * 32767), Q(w / n * 32767) };
        }

        private static short Q(float v)
        {
            if (!float.IsFinite(v)) throw new ArgumentException("Non-finite key value");
            float r = MathF.Round(v);
            if (r < short.MinValue || r > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(v), $"Key value {v / 256f:0.##} units is outside the int16 range (±128 units)");
            return (short)r;
        }

        /// <summary>A deep copy.</summary>
        public MotionClip Clone()
        {
            var c = new MotionClip { Duration = Duration, Intervals = Intervals, NormMin = NormMin, NormMax = NormMax, Pad = (byte[])Pad.Clone(), Tail = (byte[])Tail.Clone() };
            foreach (var t in Tracks)
                c.Tracks.Add(new Track
                {
                    RawName = t.RawName, Record = (byte[])t.Record.Clone(), AlignPad = (byte[])t.AlignPad.Clone(),
                    Positions = t.Positions.Select(p => (short[])p.Clone()).ToList(),
                    Rotations = t.Rotations.Select(q => (short[])q.Clone()).ToList(),
                });
            return c;
        }

        /// <summary>
        /// Resamples to <paramref name="intervals"/> frame intervals (linear positions, normalised-lerp rotations),
        /// keeping the duration. Static tracks stay static.
        /// </summary>
        public void Resample(int intervals)
        {
            if (intervals < 1) throw new ArgumentOutOfRangeException(nameof(intervals));
            if (intervals == Intervals) return;
            int from = Samples, to = intervals + 1;
            foreach (var t in Tracks)
            {
                if (t.Positions.Count > 1) t.Positions = Enumerable.Range(0, to).Select(i => Lerp(t.Positions, i * (from - 1) / (float)(to - 1), false)).ToList();
                if (t.Rotations.Count > 1) t.Rotations = Enumerable.Range(0, to).Select(i => Lerp(t.Rotations, i * (from - 1) / (float)(to - 1), true)).ToList();
            }
            Intervals = intervals;
        }

        private static short[] Lerp(List<short[]> keys, float f, bool quaternion)
        {
            int i = Math.Min((int)f, keys.Count - 2);
            float u = f - i;
            short[] a = keys[i], b = keys[i + 1];
            if (!quaternion) return new[] { (short)MathF.Round(a[0] + (b[0] - a[0]) * u), (short)MathF.Round(a[1] + (b[1] - a[1]) * u), (short)MathF.Round(a[2] + (b[2] - a[2]) * u) };
            float dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
            float s = dot < 0 ? -1 : 1;
            return QuaternionKey(a[0] + (s * b[0] - a[0]) * u, a[1] + (s * b[1] - a[1]) * u, a[2] + (s * b[2] - a[2]) * u, a[3] + (s * b[3] - a[3]) * u);
        }
    }
}
