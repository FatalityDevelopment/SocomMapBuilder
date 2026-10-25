using CodeDesigner.Ps2.FileFormats.Assets;

namespace CodeDesigner.Ps2.FileFormats.Gta
{
    /// <summary>
    /// GTA: San Andreas animation packages (IFP, "ANP3"): header {"ANP3", size, name[24], clip count}; per clip
    /// {name[24], object count, frame data size, flags}; per object {name[24], frame type, key count, bone id} then its
    /// keys. Frame type 3 = rotation keys {qx, qy, qz, qw /4096, time}; 4 = with translation {.., tx, ty, tz /1024}.
    /// Times are in 1/60 s. Objects name bones by HAnim id (the ped skeleton's ids; the root object is "Normal" or
    /// "Root", id 0). The quaternion is the bone's local rotation in the column-major convention the frames use.
    /// </summary>
    public static class GtaAnimations
    {
        public const float TimeUnit = 1f / 60f;
        public const int SampleRate = 30;

        public sealed record Key(float Time, float[] Rotation, float[]? Translation);

        public sealed record Track(string Name, int BoneId, IReadOnlyList<Key> Keys);

        public sealed record Clip(string Name, string Source, IReadOnlyList<Track> Tracks)
        {
            public float Duration => Tracks.SelectMany(t => t.Keys).Select(k => k.Time).DefaultIfEmpty(0).Max();
            public int Samples => Math.Max(1, (int)MathF.Round(Duration * SampleRate) + 1);
        }

        /// <summary>The clips of an IFP; empty when it is not ANP3.</summary>
        public static List<Clip> Parse(byte[] d, string source)
        {
            var clips = new List<Clip>();
            if (d.Length < 36 || d[0] != 'A' || d[1] != 'N' || d[2] != 'P' || d[3] != '3') return clips;
            int count = BitConverter.ToInt32(d, 32), o = 36;
            for (int a = 0; a < count && o + 36 <= d.Length; a++)
            {
                string name = Name(d, o);
                int objects = BitConverter.ToInt32(d, o + 24);
                if (objects < 0 || objects > 256) return clips;
                o += 36;
                var tracks = new List<Track>();
                for (int j = 0; j < objects && o + 36 <= d.Length; j++)
                {
                    string bone = Name(d, o).Trim();
                    int type = BitConverter.ToInt32(d, o + 24), keys = BitConverter.ToInt32(d, o + 28), id = BitConverter.ToInt32(d, o + 32);
                    o += 36;
                    int stride = type == 4 ? 16 : 10;
                    if ((type != 3 && type != 4) || keys < 0 || (long)keys * stride > d.Length - o) return clips;   // not this layout
                    var list = new List<Key>(keys);
                    for (int k = 0; k < keys && o + stride <= d.Length; k++, o += stride)
                    {
                        float[] q = { S(d, o) / 4096f, S(d, o + 2) / 4096f, S(d, o + 4) / 4096f, S(d, o + 6) / 4096f };
                        float t = S(d, o + 8) * TimeUnit;
                        float[]? p = type == 4 ? new[] { S(d, o + 10) / 1024f, S(d, o + 12) / 1024f, S(d, o + 14) / 1024f } : null;
                        list.Add(new Key(t, q, p));
                    }
                    tracks.Add(new Track(bone, id, list));
                }
                clips.Add(new Clip(name, source, tracks));
            }
            return clips;
        }

        /// <summary>Unit quaternions (x, y, z, w) at <paramref name="samples"/> even steps over the clip (slerp-free nlerp).</summary>
        public static List<float[]> SampleRotations(Track t, Clip c, int samples) =>
            Sample(t, c, samples, k => k.Rotation, quaternion: true);

        /// <summary>Translations at even steps; empty when the track has none (the bone keeps its bind translation).</summary>
        public static List<float[]> SampleTranslations(Track t, Clip c, int samples) =>
            t.Keys.Count > 0 && t.Keys[0].Translation != null ? Sample(t, c, samples, k => k.Translation!, quaternion: false) : new List<float[]>();

        private static List<float[]> Sample(Track t, Clip c, int samples, Func<Key, float[]> value, bool quaternion)
        {
            var keys = t.Keys;
            if (keys.Count <= 1) return keys.Count == 0 ? new List<float[]>() : new List<float[]> { Normalize(value(keys[0]), quaternion) };
            var output = new List<float[]>(samples);
            int i = 0;
            for (int s = 0; s < samples; s++)
            {
                float time = samples > 1 ? c.Duration * s / (samples - 1) : 0;
                while (i + 1 < keys.Count - 1 && keys[i + 1].Time <= time) i++;
                var a = keys[i]; var b = keys[Math.Min(i + 1, keys.Count - 1)];
                float span = b.Time - a.Time, u = span > 1e-6f ? Math.Clamp((time - a.Time) / span, 0, 1) : (time >= b.Time ? 1 : 0);
                float[] va = value(a), vb = value(b);
                float sign = quaternion && va[0] * vb[0] + va[1] * vb[1] + va[2] * vb[2] + va[3] * vb[3] < 0 ? -1 : 1;
                var v = new float[va.Length];
                for (int k = 0; k < v.Length; k++) v[k] = va[k] + (sign * vb[k] - va[k]) * u;
                output.Add(Normalize(v, quaternion));
            }
            return output;
        }

        /// <summary>
        /// The clip posed on a ped skeleton: per sample (0..<paramref name="frames"/>, evenly over the clip) every bone's
        /// world matrix (column-major, the skeleton's model space). A track replaces its bone's local rotation; the
        /// root's translation too, held in place (its game-space x, y travel fixed at the first key).
        /// </summary>
        public static List<List<float[]>> Pose(CharacterSkeleton skeleton, Clip c, int frames, Func<int, string, string> boneName)
        {
            int samples = frames + 1;
            var byBone = new Dictionary<int, (List<float[]> Rot, List<float[]> Pos)>();
            foreach (var t in c.Tracks)
            {
                string name = boneName(t.BoneId, t.Name);
                // Names as the ped skeleton spells them; CJ's differ ("Spine 1", root "Ripped"): compare without spaces,
                // the root (id 0) is the clump frame's child.
                static string Key(string n) => n.Replace(" ", "").ToLowerInvariant();
                int i = skeleton.Bones.FindIndex(b => Key(b.Name) == Key(name));
                if (i < 0 && t.BoneId == 0) i = skeleton.Bones.FindIndex(b => b.Parent >= 0 && skeleton.Bones[b.Parent].Parent < 0);
                if (i < 0 || byBone.ContainsKey(i)) continue;
                var pos = SampleTranslations(t, c, samples);
                if (t.BoneId == 0 && pos.Count > 0) { float x = pos[0][0], y = pos[0][1]; pos = pos.Select(p => new[] { x, y, p[2] }).ToList(); }
                byBone[i] = (SampleRotations(t, c, samples), pos);
            }
            var output = new List<List<float[]>>(samples);
            for (int s = 0; s < samples; s++)
            {
                var worlds = new List<float[]>(skeleton.Bones.Count);
                for (int i = 0; i < skeleton.Bones.Count; i++)
                {
                    var bone = skeleton.Bones[i];
                    float[] local = bone.Local;
                    if (byBone.TryGetValue(i, out var tr) && tr.Rot.Count > 0)
                    {
                        var q = tr.Rot[Math.Min(s, tr.Rot.Count - 1)];
                        float[] p = tr.Pos.Count > 0 ? tr.Pos[Math.Min(s, tr.Pos.Count - 1)] : new[] { local[12], local[13], local[14] };
                        local = Compose(q, p);
                    }
                    worlds.Add(bone.Parent >= 0 && bone.Parent < i ? Matrix4.Multiply(worlds[bone.Parent], local) : local);
                }
                output.Add(worlds);
            }
            return output;
        }

        /// <summary>Column-major matrix from a unit quaternion (x, y, z, w) and a translation.</summary>
        private static float[] Compose(float[] q, float[] p)
        {
            float x = q[0], y = q[1], z = q[2], w = q[3];
            return new[]
            {
                1 - 2 * (y * y + z * z), 2 * (x * y + z * w), 2 * (x * z - y * w), 0,
                2 * (x * y - z * w), 1 - 2 * (x * x + z * z), 2 * (y * z + x * w), 0,
                2 * (x * z + y * w), 2 * (y * z - x * w), 1 - 2 * (x * x + y * y), 0,
                p[0], p[1], p[2], 1,
            };
        }

        private static float[] Normalize(float[] v, bool quaternion)
        {
            if (!quaternion) return v;
            float n = MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2] + v[3] * v[3]);
            return n > 1e-6f ? new[] { v[0] / n, v[1] / n, v[2] / n, v[3] / n } : new float[] { 0, 0, 0, 1 };
        }

        private static short S(byte[] d, int o) => BitConverter.ToInt16(d, o);

        private static string Name(byte[] d, int o)
        {
            int end = Array.IndexOf(d, (byte)0, o, 24);
            return System.Text.Encoding.ASCII.GetString(d, o, (end < 0 ? o + 24 : end) - o);
        }
    }
}
