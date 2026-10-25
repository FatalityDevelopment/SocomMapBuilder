using System.Buffers.Binary;
using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Zipper.Anim;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;
using CodeDesigner.Ps2.FileFormats.Zipper.Texture;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Conversion
{
    /// <summary>
    /// Any game's model (a game-neutral <see cref="ModelMesh"/>) -> SOCOM II pack (<c>&lt;PACK&gt;.CDB</c> + its ZEDs),
    /// encoding SOCOM II geometry from triangles (unlike <see cref="CaToSocom2"/>, which copies CA's native blocks):
    /// <list type="bullet">
    /// <item>characters (<c>CLIB</c>): each source bone maps onto a bone of the retail alg_UN01 skeleton; every vertex
    /// influence is re-expressed in that bone (relative to the bone's anchor joint, limbs turned onto SOCOM II's limb
    /// directions) and packed into skinned groups (vertex batches per bone + a draw block, &lt;= 40 vertices / 45
    /// triangles, the retail limits);</item>
    /// <item>weapons (<c>WEAP</c>, the retail held layout) and props (map-prop library, 1/16 units): chunks from
    /// s2static.encode_mesh;</item>
    /// <item>textures: RGBA median-cut to 8-bit with a 32-bit CSM1 palette in the pack's own block of palette ids;</item>
    /// <item>clips: retargeted per frame so each SOCOM II bone follows its anchor's world rotation (the same alignment
    /// the mesh used, so mesh and motion agree); the hips carry the motion.</item>
    /// </list>
    /// </summary>
    public static class ModelToSocom2
    {
        /// <summary>What to convert. <paramref name="Scale"/>: SOCOM II units per source unit. <paramref name="BoneMap"/>:
        /// source bone -> SOCOM II bone (characters); <paramref name="Anchors"/>: SOCOM II bone -> the source bone whose
        /// joint it sits on. <paramref name="Points"/>: weapon attach points (firepoint ...) in source model space.</summary>
        public sealed record Source(
            ModelMesh Mesh,
            string Kind,
            float Scale,
            Func<string, (byte[] Rgba, int Width, int Height)?> Texture,
            IReadOnlyDictionary<string, string>? BoneMap = null,
            IReadOnlyDictionary<string, string>? Anchors = null,
            IReadOnlyDictionary<string, float[]>? Points = null);

        /// <summary>A clip as source-skeleton world matrices (column-major, model space like the mesh) per frame
        /// 0..<paramref name="Frames"/> at 30 fps.</summary>
        public sealed record SourceClip(string Name, float Duration, int Frames, Func<int, IReadOnlyList<float[]>> Worlds);

        private const int GroupVerts = 40, GroupTris = 45, ChunkVerts = 72, ChunkTris = 48;

        public static CaToSocom2.Result Export(Source src, IReadOnlyList<SourceClip> clips, Socom2Templates templates, CaToSocom2.Options o)
        {
            PackageSave.CheckPackName(o.PackName);
            var warnings = new List<string>();
            var files = new List<(string Leaf, byte[] Data)>();
            string library;
            int triangles, textures, palettes;
            Rig? rig = null;
            if (src.Kind == "character")
            {
                rig = new Rig(src, templates.CharacterTemplate(), warnings);
                (library, triangles, textures, palettes) = Character(src, rig, templates, o, files, warnings);
            }
            else
                (library, triangles, textures, palettes) = Static(src, templates, o, files, warnings);

            var names = new List<string>();
            if (clips.Count > 0)
            {
                if (rig == null) throw new NotSupportedException("Clips export with a character");
                var (header, stand) = templates.ClipTemplate();
                var records = new List<ZedTree.Record>();
                foreach (var clip in clips)
                {
                    records.Add(new ZedTree.Record(clip.Name.Length > 63 ? clip.Name[..63] : clip.Name, Retarget(clip, rig, stand).Encode()));
                    names.Add(clip.Name);
                }
                files.Add((PackageAnimations.PackageLeaf, Socom2Templates.WriteZed(header, records)));
            }

            string pack = o.PackName.ToUpperInvariant();
            string dir = Path.Combine(o.OutputDir, o.PackName);
            Directory.CreateDirectory(dir);
            foreach (var (leaf, data) in files) File.WriteAllBytes(Path.Combine(dir, leaf), data);
            byte[] cdb = PackageSave.BuildCdb(files.Select(f => ($"RUN\\MP\\{pack}\\{f.Leaf}", f.Data)).ToList());
            string path = Path.Combine(dir, pack + ".CDB");
            File.WriteAllBytes(path, cdb);
            return new CaToSocom2.Result(path, o.PackName, o.ModelName, src.Kind, library, triangles, textures, palettes, names, warnings);
        }

        // ================================================================== rig: source bones on SOCOM II's skeleton

        /// <summary>
        /// The bone alignment shared by mesh and clips. SOCOM II bone b sits on source joint a (its anchor); limbs are
        /// turned by Rmin_b (the shortest rotation from the source limb direction onto SOCOM II's; hands / feet / toes
        /// take their limb's). A source vector u (model space, bind) becomes K_b u in b's bind frame with
        /// K_b = R_S2b^T Rmin_b, so a pose G_a(t) of the source joint drives b as W_b(t) = G_a(t) R_a^T Rmin_b^T R_S2b.
        /// </summary>
        private sealed class Rig
        {
            public readonly CharacterSkeleton Source, Target;
            public readonly float Scale;
            public readonly Dictionary<string, CharacterSkeleton.Bone> Mids;
            public readonly Dictionary<string, int> Anchor = new();          // S2 bone -> source bone index
            // S2 bone -> source bind model space to SOCOM II's: v' = Map v + Move (Map row-major 3x3: scale, limb turn and
            // stretch); Rot is the pure turn (normals, clips).
            public readonly Dictionary<string, (double[] Map, double[] Move, double[] Rot)> Place = new(StringComparer.OrdinalIgnoreCase);

            // Thickness across a segment: alg_UN01's shoulders sit lower and closer than a scaled ped's, which leaves
            // the shoulders looking narrow; a little more girth there reads right.
            private static readonly Dictionary<string, double> Girth = new() { ["rscap"] = 1.15, ["lscap"] = 1.15, ["rbicep"] = 1.15, ["lbicep"] = 1.15 };

            // The end of each limb: rigid on its SOCOM II joint, turned like the limb.
            private static readonly Dictionary<string, string> LimbEnd = new()
            {
                ["rhand"] = "rforearm", ["lhand"] = "lforearm", ["rfoot"] = "rcalf", ["lfoot"] = "lcalf", ["rtoe"] = "rfoot", ["ltoe"] = "lfoot",
                ["head"] = "neck",
            };
            private readonly Dictionary<string, string> _map;

            // Limbs: SOCOM II bone -> the child joint giving its direction; the end bones follow their limb.
            private static readonly Dictionary<string, string> LimbChild = new()
            {
                ["rscap"] = "rbicep", ["rbicep"] = "rforearm", ["rforearm"] = "rhand", ["lscap"] = "lbicep", ["lbicep"] = "lforearm", ["lforearm"] = "lhand",
                ["rthigh"] = "rcalf", ["rcalf"] = "rfoot", ["lthigh"] = "lcalf", ["lcalf"] = "lfoot",
                ["spinelo"] = "spinehi", ["spinehi"] = "neck", ["neck"] = "head",
            };

            public Rig(Source src, Socom2Templates.Character tpl, List<string> warnings)
            {
                Source = src.Mesh.Skeleton ?? throw new InvalidOperationException("A character export needs a skinned mesh");
                Target = tpl.Skeleton;
                Scale = src.Scale;
                _map = new Dictionary<string, string>(src.BoneMap ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                Mids = Target.Bones.Where(b => b.MatrixId >= 0).GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                foreach (var (s2, name) in src.Anchors ?? new Dictionary<string, string>())
                {
                    // An anchor may list alternative spellings ("Spine1|Spine 2"): the first the skeleton has.
                    int i = -1;
                    foreach (string alt in name.Split('|'))
                        if ((i = Source.Bones.FindIndex(b => b.Name.Equals(alt, StringComparison.OrdinalIgnoreCase))) >= 0) break;
                    if (i >= 0 && Mids.ContainsKey(s2)) Anchor[s2] = i; else warnings.Add($"bone {s2}: anchor {name} not found");
                }
                // A continuous fit onto alg_UN01's proportions (its torso is shorter and its shoulders lower than a
                // scaled ped's): the body is scaled with its hip / thigh line on SOCOM II's thigh joints; each chain
                // segment (spine, upper spine, neck; scapula, upper arm, forearm; thigh, calf) starts where its parent
                // put it and is turned + stretched along itself so its far joint lands on SOCOM II's joint; hands, feet,
                // toes and the head sit rigidly on their joint, turned like their segment. Nothing is pinned twice, so
                // the mesh only bends / stretches smoothly; vertices blend these by weight.
                double[] identity = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
                double[] scaled = { Scale, 0, 0, 0, Scale, 0, 0, 0, Scale };
                double[] Mid(string a, string b, Func<string, double[]?> pos) => pos(a) is { } x && pos(b) is { } y ? new[] { (x[0] + y[0]) / 2, (x[1] + y[1]) / 2, (x[2] + y[2]) / 2 } : pos("hips") ?? new double[3];
                double[]? SrcPos(string s2) => Anchor.TryGetValue(s2, out int i) ? Pos(Source.Bones[i].World) : null;
                double[]? S2Pos(string s2) => Mids.TryGetValue(s2, out var m) ? Pos(m.World) : null;
                var hipSrc = Mid("rthigh", "lthigh", SrcPos);
                var hipS2 = Mid("rthigh", "lthigh", S2Pos);
                (double[] Map, double[] Move, double[] Rot) global = (scaled, new[] { hipS2[0] - Scale * hipSrc[0], hipS2[1] - Scale * hipSrc[1], hipS2[2] - Scale * hipSrc[2] }, identity);
                foreach (var bone in Target.Bones)
                {
                    if (Place.ContainsKey(bone.Name)) continue;
                    var parent = global;
                    for (string? n = CaToSocom2.S2Parent.GetValueOrDefault(bone.Name); n != null; n = CaToSocom2.S2Parent.GetValueOrDefault(n))
                        if (Place.TryGetValue(n, out var pp)) { parent = pp; break; }
                    var place = parent;
                    if (LimbChild.TryGetValue(bone.Name, out var child) && Anchor.TryGetValue(bone.Name, out int a) && Anchor.TryGetValue(child, out int ac)
                        && Mids.TryGetValue(child, out var s2Child))
                    {
                        double[] j = Apply(parent, Pos(Source.Bones[a].World)), c = Apply(parent, Pos(Source.Bones[ac].World)), to = Pos(s2Child.World);
                        var u = Dir(j, c);
                        double len = Dist(j, c), k = len > 1e-6 ? Dist(j, to) / len : 1;
                        var turn = Between(u, Dir(j, to));
                        var stretch = new double[9];
                        // along u: k (joints land); across it: the girth (broader shoulders / upper arms, else 1)
                        double m = Girth.GetValueOrDefault(bone.Name, 1.0);
                        for (int r = 0; r < 3; r++) for (int q = 0; q < 3; q++) stretch[r * 3 + q] = (r == q ? m : 0) + (k - m) * u[r] * u[q];
                        var t = Mul3(turn, stretch);
                        // v' = j + T (parent(v) - j)
                        var d = new[] { parent.Move[0] - j[0], parent.Move[1] - j[1], parent.Move[2] - j[2] };
                        place = (Mul3(t, parent.Map), new[]
                        {
                            j[0] + t[0] * d[0] + t[1] * d[1] + t[2] * d[2],
                            j[1] + t[3] * d[0] + t[4] * d[1] + t[5] * d[2],
                            j[2] + t[6] * d[0] + t[7] * d[1] + t[8] * d[2],
                        }, Mul3(turn, parent.Rot));
                    }
                    else if (LimbEnd.TryGetValue(bone.Name, out var limb) && Place.TryGetValue(limb, out var lp) && Anchor.TryGetValue(bone.Name, out int ea))
                        place = Rigid(Mul3(lp.Rot, scaled), Pos(bone.World), Pos(Source.Bones[ea].World), lp.Rot);
                    Place[bone.Name] = place;
                }
            }

            private static double[] Apply((double[] Map, double[] Move, double[] Rot) p, double[] v)
            {
                var r = p.Map;
                return new[]
                {
                    r[0] * v[0] + r[1] * v[1] + r[2] * v[2] + p.Move[0],
                    r[3] * v[0] + r[4] * v[1] + r[5] * v[2] + p.Move[1],
                    r[6] * v[0] + r[7] * v[1] + r[8] * v[2] + p.Move[2],
                };
            }

            /// <summary>v' = map v + move with the source joint landing on the SOCOM II joint.</summary>
            private static (double[] Map, double[] Move, double[] Rot) Rigid(double[] map, double[] s2Joint, double[] srcJoint, double[] rot) =>
                (map, new[]
                {
                    s2Joint[0] - (map[0] * srcJoint[0] + map[1] * srcJoint[1] + map[2] * srcJoint[2]),
                    s2Joint[1] - (map[3] * srcJoint[0] + map[4] * srcJoint[1] + map[5] * srcJoint[2]),
                    s2Joint[2] - (map[6] * srcJoint[0] + map[7] * srcJoint[1] + map[8] * srcJoint[2]),
                }, rot);

            private static double Dist(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));

            /// <summary>The SOCOM II bone (with a matrix) a source bone skins to: its mapping, else its parents'.</summary>
            public string Target2(int sourceBone)
            {
                for (int i = sourceBone; i >= 0; i = Source.Bones[i].Parent)
                    if (_map.TryGetValue(Source.Bones[i].Name, out var s2))
                    {
                        for (string? n = s2; n != null; n = CaToSocom2.S2Parent.GetValueOrDefault(n))
                            if (Mids.ContainsKey(n)) return n;
                    }
                return "hips";
            }

            public double[] R(string b) => Place.TryGetValue(b, out var p) ? p.Rot : new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

            /// <summary>A source bind vertex (or normal) in SOCOM II's model space: its influences' placements blended
            /// by weight (linear blend skinning of the retarget, so no seams).</summary>
            public double[] World(IReadOnlyList<(string Bone, double Weight)> influences, double x, double y, double z, bool direction)
            {
                var o = new double[3];
                foreach (var (bone, w) in influences)
                {
                    if (!Place.TryGetValue(bone, out var p)) continue;
                    var r = direction ? p.Rot : p.Map;
                    o[0] += w * (r[0] * x + r[1] * y + r[2] * z + (direction ? 0 : p.Move[0]));
                    o[1] += w * (r[3] * x + r[4] * y + r[5] * z + (direction ? 0 : p.Move[1]));
                    o[2] += w * (r[6] * x + r[7] * y + r[8] * z + (direction ? 0 : p.Move[2]));
                }
                return o;
            }

            /// <summary>A SOCOM II model-space point / direction in bone b's bind frame.</summary>
            public (double X, double Y, double Z) Local(string b, double[] v, bool direction)
            {
                var s2 = Mids[b].World;
                double x = v[0], y = v[1], z = v[2];
                if (!direction) { x -= s2[12]; y -= s2[13]; z -= s2[14]; }
                return (s2[0] * x + s2[1] * y + s2[2] * z, s2[4] * x + s2[5] * y + s2[6] * z, s2[8] * x + s2[9] * y + s2[10] * z);
            }

            /// <summary>K_b^-1 = R_a^T Place_b^T R_S2b as a quaternion (x, y, z, w), or null for an unanchored bone.</summary>
            public double[]? KInverse(string b)
            {
                if (!Anchor.TryGetValue(b, out int a)) return null;
                double[] ra = Rot(Source.Bones[a].World), rs = Rot(Mids[b].World), rm = R(b);
                var m = Mul3(Mul3(Transpose(ra), Transpose(rm)), rs);
                return Quat(m);
            }

            public static double[] Pos(float[] m) => new double[] { m[12], m[13], m[14] };

            /// <summary>Rotation part of a column-major 4x4 as row-major 3x3.</summary>
            public static double[] Rot(float[] m) => new double[] { m[0], m[4], m[8], m[1], m[5], m[9], m[2], m[6], m[10] };

            private static double[] Dir(double[] a, double[] b)
            {
                double x = b[0] - a[0], y = b[1] - a[1], z = b[2] - a[2], n = Math.Sqrt(x * x + y * y + z * z);
                return n > 1e-9 ? new[] { x / n, y / n, z / n } : new double[] { 1, 0, 0 };
            }

            /// <summary>Shortest rotation taking unit a onto unit b (row-major 3x3).</summary>
            private static double[] Between(double[] a, double[] b)
            {
                double cx = a[1] * b[2] - a[2] * b[1], cy = a[2] * b[0] - a[0] * b[2], cz = a[0] * b[1] - a[1] * b[0];
                double c = a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
                if (c < -0.9999) return new double[] { -1, 0, 0, 0, -1, 0, 0, 0, 1 };
                double k = 1 / (1 + c);
                return new[]
                {
                    cx * cx * k + c, cx * cy * k - cz, cx * cz * k + cy,
                    cy * cx * k + cz, cy * cy * k + c, cy * cz * k - cx,
                    cz * cx * k - cy, cz * cy * k + cx, cz * cz * k + c,
                };
            }
        }

        private static double[] Transpose(double[] m) => new[] { m[0], m[3], m[6], m[1], m[4], m[7], m[2], m[5], m[8] };

        private static double[] Mul3(double[] a, double[] b)
        {
            var r = new double[9];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
            return r;
        }

        /// <summary>Row-major 3x3 rotation -> unit quaternion (x, y, z, w).</summary>
        private static double[] Quat(double[] m)
        {
            double t = m[0] + m[4] + m[8], x, y, z, w;
            if (t > 0) { double s = Math.Sqrt(t + 1) * 2; w = 0.25 * s; x = (m[7] - m[5]) / s; y = (m[2] - m[6]) / s; z = (m[3] - m[1]) / s; }
            else if (m[0] > m[4] && m[0] > m[8]) { double s = Math.Sqrt(1 + m[0] - m[4] - m[8]) * 2; w = (m[7] - m[5]) / s; x = 0.25 * s; y = (m[1] + m[3]) / s; z = (m[2] + m[6]) / s; }
            else if (m[4] > m[8]) { double s = Math.Sqrt(1 + m[4] - m[0] - m[8]) * 2; w = (m[2] - m[6]) / s; x = (m[1] + m[3]) / s; y = 0.25 * s; z = (m[5] + m[7]) / s; }
            else { double s = Math.Sqrt(1 + m[8] - m[0] - m[4]) * 2; w = (m[3] - m[1]) / s; x = (m[2] + m[6]) / s; y = (m[5] + m[7]) / s; z = 0.25 * s; }
            return CaToSocom2.Norm(new[] { x, y, z, w });
        }

        // ================================================================== characters

        private sealed record Influence(string Bone, int Mid, double Weight);

        private static (string, int, int, int) Character(Source src, Rig rig, Socom2Templates templates, CaToSocom2.Options o,
            List<(string, byte[])> files, List<string> warnings)
        {
            var tpl = templates.CharacterTemplate();
            var mesh = src.Mesh;
            int clamped = 0;
            short C(double v)
            {
                long r = (long)Math.Round(v);
                if (r is < short.MinValue or > short.MaxValue) clamped++;
                return (short)Math.Clamp(r, short.MinValue, short.MaxValue);
            }

            var texNames = new List<(string Source, string Name)>();
            var sections = new List<(string Texture, List<(List<(int Mid, byte[] Block)> Vertex, byte[] Draw)> Groups)>();
            int triangles = 0;
            foreach (var chunk in mesh.Chunks)
            {
                if (chunk.SkinBones == null || chunk.SkinWeights == null || chunk.Triangles.Length == 0) continue;
                string texName = TextureName(texNames, chunk.Texture, o.PackName);

                // per vertex: its SOCOM II influences (merged per bone, Q15 weights summing to 32767)
                var infl = new List<Influence>[chunk.VertexCount];
                for (int v = 0; v < chunk.VertexCount; v++)
                {
                    var merged = new Dictionary<string, double>();
                    for (int k = 0; k < ModelMesh.SkinInfluences; k++)
                    {
                        int b = chunk.SkinBones[v * ModelMesh.SkinInfluences + k];
                        float w = chunk.SkinWeights[v * ModelMesh.SkinInfluences + k];
                        if (b < 0 || w <= 0) continue;
                        string s2 = rig.Target2(b);
                        merged[s2] = merged.GetValueOrDefault(s2) + w;
                    }
                    if (merged.Count == 0) merged["hips"] = 1;
                    double sum = merged.Values.Sum();
                    infl[v] = merged.OrderByDescending(x => x.Value).Take(4).Select(x => new Influence(x.Key, rig.Mids[x.Key].MatrixId, x.Value / sum)).ToList();
                }

                // every vertex placed once in SOCOM II's model space (blended over its influences)
                var placed = new double[chunk.VertexCount][];
                var normals = new double[chunk.VertexCount][];
                for (int v = 0; v < chunk.VertexCount; v++)
                {
                    var w = infl[v].Select(i => (i.Bone, i.Weight)).ToList();
                    placed[v] = rig.World(w, chunk.Positions[3 * v], chunk.Positions[3 * v + 1], chunk.Positions[3 * v + 2], false);
                    normals[v] = rig.World(w, chunk.Normals[3 * v], chunk.Normals[3 * v + 1], chunk.Normals[3 * v + 2], true);
                }

                var groups = new List<(List<(int, byte[])>, byte[])>();
                foreach (var (slots, tris) in Split(chunk.Triangles, GroupVerts, GroupTris))
                {
                    // bones of the group, the one covering most slots first (its batch writes every slot)
                    var bones = slots.SelectMany(v => infl[v].Select(i => i.Bone)).GroupBy(b => b).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
                    var blocks = new List<(int, byte[])>();
                    for (int bi = 0; bi < bones.Count; bi++)
                    {
                        string bone = bones[bi];
                        int mid = rig.Mids[bone].MatrixId;
                        var verts = new List<short[]>();
                        for (int s = 0; s < slots.Count; s++)
                        {
                            int v = slots[s];
                            int q = Q15(infl[v], bone);
                            if (q == 0 && bi > 0) continue;
                            // A weight-0 entry (the first batch covers every slot) only initialises the slot: no position.
                            var (lx, ly, lz) = q == 0 ? (0, 0, 0) : rig.Local(bone, placed[v], false);
                            var (nx, ny, nz) = rig.Local(bone, normals[v], true);
                            double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                            if (nl > 1e-9) { nx /= nl; ny /= nl; nz /= nl; }
                            verts.Add(new[] { C(lx * CaToSocom2.CharacterScale), C(ly * CaToSocom2.CharacterScale), C(lz * CaToSocom2.CharacterScale), (short)(2 * s),
                                C(nx * 32767), C(ny * 32767), C(nz * 32767), (short)q });
                        }
                        short flags = bones.Count == 1 ? (short)6 : bi == 0 ? (short)2 : bi == bones.Count - 1 ? (short)5 : (short)1;
                        blocks.Add((mid, CaToSocom2.VertexBlock(flags, (short)tris.Count, (short)mid, verts.ToArray())));
                    }
                    var uv = new short[slots.Count * 2];
                    for (int s = 0; s < slots.Count; s++)
                    {
                        uv[2 * s] = C(chunk.Uvs[2 * slots[s]] * 4096);
                        uv[2 * s + 1] = C(chunk.Uvs[2 * slots[s] + 1] * 4096);
                    }
                    groups.Add((blocks, DrawBlock(slots.Count, tris, uv)));
                    triangles += tris.Count;
                }
                sections.Add((texName, groups));
            }
            if (clamped > 0) warnings.Add($"{clamped} vertex values overflowed 16 bits and were clamped");
            var (data, start, refCount) = CaToSocom2.EncodeCharacterMesh(sections);
            var (texNodes, palNodes) = Textures(src, texNames, o.PackName, tpl, null, warnings);
            var geo = new ZedTree.Record(o.ModelName, tpl.Geo.Data, tpl.Geo.Children);
            var meshRec = new ZedTree.Record($"MESH_{o.ModelName}", data, new[]
            {
                new ZedTree.Record($"MESH_{o.ModelName}_START", BitConverter.GetBytes(start)),
                new ZedTree.Record("ref_count", BitConverter.GetBytes(refCount)),
                new ZedTree.Record("mtx_count", BitConverter.GetBytes(CaToSocom2.MtxCount)),
            });
            CaToSocom2.AddLibrary(files, "CLIB", tpl.Library, texNodes, palNodes, geo, meshRec);
            return ("CLIB", triangles, texNodes.Count, palNodes.Count);
        }

        /// <summary>A vertex's weight on <paramref name="bone"/> in Q15; the heaviest influence takes the rounding so a
        /// vertex sums to 32767.</summary>
        private static int Q15(List<Influence> infl, string bone)
        {
            var q = infl.Select(i => (int)Math.Round(i.Weight * 32767)).ToArray();
            q[0] += 32767 - q.Sum();
            int k = infl.FindIndex(i => i.Bone == bone);
            return k < 0 ? 0 : q[k];
        }

        /// <summary>Triangles split into batches of at most <paramref name="maxVerts"/> distinct vertices and
        /// <paramref name="maxTris"/> triangles: (vertices by slot, triangles as slot indices).</summary>
        private static IEnumerable<(List<int> Slots, List<(int, int, int)> Tris)> Split(int[] tris, int maxVerts, int maxTris)
        {
            int i = 0;
            while (i < tris.Length / 3)
            {
                var slot = new Dictionary<int, int>();
                var slots = new List<int>();
                var outTris = new List<(int, int, int)>();
                while (i < tris.Length / 3 && outTris.Count < maxTris)
                {
                    int a = tris[3 * i], b = tris[3 * i + 1], c = tris[3 * i + 2];
                    int need = new[] { a, b, c }.Distinct().Count(x => !slot.ContainsKey(x));
                    if (slots.Count + need > maxVerts) break;
                    foreach (int x in new[] { a, b, c }) if (!slot.ContainsKey(x)) { slot[x] = slots.Count; slots.Add(x); }
                    outTris.Add((slot[a], slot[b], slot[c]));
                    i++;
                }
                if (outTris.Count == 0) { i++; continue; }
                yield return (slots, outTris);
            }
        }

        /// <summary>A skinned group's draw block (as retail): STCYCL 0x102 + UNPACK V4-8 triangles (slot*3) at
        /// idx = 4 + 3*verts; STCYCL 0x101 + UNPACK V4-32 four qwords (GIF tags, (idx, 0, verts, tris), 0); STCYCL 0x103 +
        /// UNPACK V2-16 UVs at 5; MSCNT; padded to a qword.</summary>
        private static byte[] DrawBlock(int verts, List<(int A, int B, int C)> tris, short[] uv)
        {
            int idx = 4 + 3 * verts;
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(0x01000102u); w.Write(0x6E00C000u | (uint)(tris.Count & 0xFF) << 16 | (uint)idx);
            foreach (var (a, b, c) in tris) { w.Write((byte)(3 * a)); w.Write((byte)(3 * b)); w.Write((byte)(3 * c)); w.Write((byte)3); }
            w.Write(0x01000101u); w.Write(0x6C048000u);
            w.Write(0x8000u); w.Write(0x303EC000u); w.Write(0x412u); w.Write(0u);
            w.Write(0x8000u | (uint)verts); w.Write(0x303DC000u); w.Write(0x412u); w.Write(0u);
            w.Write((uint)idx); w.Write(0u); w.Write((uint)verts); w.Write((uint)tris.Count);
            w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u);
            w.Write(0x01000103u); w.Write(0x65008005u | (uint)(verts & 0xFF) << 16);
            foreach (short s in uv) w.Write(s);
            w.Write(0x17000000u);
            while (ms.Length % 16 != 0) w.Write((byte)0);
            return ms.ToArray();
        }

        // ================================================================== static models (weapons, props)

        private static (string, int, int, int) Static(Source src, Socom2Templates templates, CaToSocom2.Options o,
            List<(string, byte[])> files, List<string> warnings)
        {
            bool weapon = src.Kind == "weapon";
            var tpl = templates.StaticTemplate(weapon ? "weapon" : "prop");
            var chr = templates.CharacterTemplate();
            float s = src.Scale;
            var texNames = new List<(string Source, string Name)>();
            var secs = new List<CaToSocom2.StaticSection>();
            int verts = 0, tris = 0;
            float[] lo = { float.MaxValue, float.MaxValue, float.MaxValue }, hi = { float.MinValue, float.MinValue, float.MinValue };
            foreach (var chunk in src.Mesh.Chunks)
            {
                if (chunk.Triangles.Length == 0) continue;
                string texName = TextureName(texNames, chunk.Texture, o.PackName);
                var chunks = new List<CaToSocom2.StaticChunk>();
                foreach (var (slots, sub) in Split(chunk.Triangles, ChunkVerts, ChunkTris))
                {
                    var p = new float[slots.Count * 3];
                    for (int i = 0; i < slots.Count; i++)
                        for (int k = 0; k < 3; k++)
                        {
                            p[3 * i + k] = chunk.Positions[3 * slots[i] + k] * s;
                            lo[k] = Math.Min(lo[k], p[3 * i + k]); hi[k] = Math.Max(hi[k], p[3 * i + k]);
                        }
                    byte[] geometry = EncodeChunk(p, slots.Select(v => (chunk.Normals[3 * v], chunk.Normals[3 * v + 1], chunk.Normals[3 * v + 2])).ToList(),
                        slots.Select(v => (chunk.Uvs[2 * v], chunk.Uvs[2 * v + 1])).ToList(), sub, weapon);
                    var colour = new byte[(4 * slots.Count + 15) & ~15];
                    for (int i = 0; i < slots.Count; i++)
                        for (int k = 0; k < 4; k++)
                            colour[4 * i + k] = chunk.Colors != null && chunk.Colors.Length >= 4 * slots[i] + 4 ? chunk.Colors[4 * slots[i] + k] : (byte)0x80;
                    chunks.Add(new CaToSocom2.StaticChunk(colour, (0x01000103u, 0x6E00C006u | (uint)(slots.Count & 0xFF) << 16), geometry));
                    verts += slots.Count; tris += sub.Count;
                }
                secs.Add(new CaToSocom2.StaticSection(CaToSocom2.NameField(texName), tpl.Material, tpl.MaterialVif, chunks));
            }
            if (secs.Count == 0) throw new InvalidOperationException($"{src.Mesh.Name}: nothing to export (no triangles)");
            var centre = new[] { (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2 };
            float[] ident = Matrix4.Identity();
            ZedTree.Record Vis(uint flags) => new("visuals", new byte[0], new[] { new ZedTree.Record("vis", new byte[0], new[] { new ZedTree.Record("vparams", CaToSocom2.VParams(flags, 0, tris, verts, centre)) }) });

            List<(string Key, List<CaToSocom2.StaticSection> Sections)> visuals;
            ZedTree.Record geo;
            string libName;
            if (weapon)
            {
                // The held layout: <name>_high / _low (one visual each), then firepoint, firepoint_shell, aimpoint, Gun_box.
                var lods = new[] { "high", "low" }.Select(tag => new ZedTree.Record($"{o.ModelName}_{tag}", new byte[0], new[]
                {
                    new ZedTree.Record("nparams", CaToSocom2.NParams(ident, lo, hi, 1, 0x80040055)), Vis(0x000BC0FA),
                })).ToList();
                var defaults = new Dictionary<string, float[]>
                {
                    ["firepoint"] = new[] { hi[0], centre[1], centre[2] },
                    ["firepoint_shell"] = new[] { centre[0], hi[1], centre[2] },
                    ["aimpoint"] = new[] { lo[0] * 0.5f, hi[1], centre[2] },
                    ["Gun_box"] = new[] { 0f, 0f, 0f },
                };
                var points = CaToSocom2.HeldOrder.Select(n =>
                {
                    float[] at = src.Points != null && src.Points.TryGetValue(n, out var pt) ? new[] { pt[0] * s, pt[1] * s, pt[2] * s } : defaults[n];
                    var m = Matrix4.Identity(); m[12] = at[0]; m[13] = at[1]; m[14] = at[2];
                    return new ZedTree.Record(n, new byte[0], new[] { new ZedTree.Record("nparams", CaToSocom2.NParams(m, lo, hi, 1, 0x80800055)) });
                }).ToList();
                if (src.Points?.ContainsKey("firepoint") != true) warnings.Add("no muzzle point in the source: firepoint placed at the front of the bounds");
                geo = new ZedTree.Record(o.ModelName, new byte[0], new[]
                {
                    new ZedTree.Record("nparams", CaToSocom2.NParams(ident, lo, hi, 7, 0x80800055)),
                    new ZedTree.Record("children", new byte[0], lods.Concat(points).ToList()),
                });
                visuals = new() { ("N000_000", secs), ("N001_000", secs) };
                libName = tpl.Library.Name;
            }
            else
            {
                // A map prop: one node with one visual, positions in 1/16 units.
                geo = new ZedTree.Record(o.ModelName, new byte[0], new[] { new ZedTree.Record("nparams", CaToSocom2.NParams(ident, lo, hi, 7, 0x80840051)), Vis(0x0009C0FA) });
                visuals = new() { ("N000_000", secs) };
                libName = "PROP";
                if (src.Mesh.Chunks.Count > 0 && src.Kind == "vehicle") warnings.Add("vehicles export as static props (no wheels / seats)");
            }
            var (data, offsets) = CaToSocom2.EncodeStatic(visuals);
            var mdl = new ZedTree.Record(o.ModelName, data, visuals.Select(v => new ZedTree.Record(v.Key, BitConverter.GetBytes(offsets[v.Key]))).ToList());
            var (texNodes, palNodes) = Textures(src, texNames, o.PackName, chr, o.MaxTextureSize, warnings);
            CaToSocom2.AddLibrary(files, libName, tpl.Library, texNodes, palNodes, geo, mdl);
            return (libName, tris, texNodes.Count, palNodes.Count);
        }

        /// <summary>s2static.encode_chunk: GIF tags + (idx, 0, verts, tris) + scale, V4-16 vertices (x, y, z, nx)(u, v,
        /// ny, nz), V4-8 triangles (slot*3), V3-16 face normals, MSCNT. Weapons store positions / scale * 32768
        /// (visual flag 0x20000), props in 1/16 units (scale 1).</summary>
        private static byte[] EncodeChunk(float[] pos, List<(float, float, float)> normals, List<(float, float)> uvs, List<(int A, int B, int C)> tris, bool scaled)
        {
            int nv = normals.Count, nt = tris.Count, idx = 4 + 3 * nv;
            float scale = 1f;
            if (scaled) { scale = 0; foreach (float f in pos) scale = Math.Max(scale, Math.Abs(f)); scale = scale > 0 ? scale * 1.0001f : 1f; }
            float k = scaled ? 32768f / scale : 16f;
            static short C(double v) => (short)Math.Clamp((int)Math.Round(v), -32767, 32767);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(0x01000101u); w.Write(0x6C048000u);
            w.Write(0x8000u); w.Write(0x303EC000u); w.Write(0x412u); w.Write(0u);
            w.Write(0x8000u | (uint)nv); w.Write(0x303DC000u); w.Write(0x412u); w.Write(0u);
            w.Write((uint)idx); w.Write(0u); w.Write((uint)nv); w.Write((uint)nt);
            w.Write(0u); w.Write(0u); w.Write(0u); w.Write(scale);
            w.Write(0x01000203u); w.Write(0x6D008004u | (uint)(2 * nv & 0xFF) << 16);
            for (int i = 0; i < nv; i++)
            {
                var (nx, ny, nz) = normals[i];
                var (u, v) = uvs[i];
                foreach (short x in new[] { C(pos[3 * i] * k), C(pos[3 * i + 1] * k), C(pos[3 * i + 2] * k), C(nx * 32767), C(u * 4096), C(v * 4096), C(ny * 32767), C(nz * 32767) }) w.Write(x);
            }
            w.Write(0x01000102u); w.Write(0x6E00C000u | (uint)(nt & 0xFF) << 16 | (uint)idx);
            foreach (var (a, b, c) in tris) { w.Write((byte)(3 * a)); w.Write((byte)(3 * b)); w.Write((byte)(3 * c)); w.Write((byte)3); }
            w.Write(0x01000102u); w.Write(0x69008000u | (uint)(nt & 0xFF) << 16 | (uint)(idx + 1));
            foreach (var (a, b, c) in tris)
            {
                double e1x = pos[3 * b] - pos[3 * a], e1y = pos[3 * b + 1] - pos[3 * a + 1], e1z = pos[3 * b + 2] - pos[3 * a + 2];
                double e2x = pos[3 * c] - pos[3 * a], e2y = pos[3 * c + 1] - pos[3 * a + 1], e2z = pos[3 * c + 2] - pos[3 * a + 2];
                double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x, ln = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (ln < 1e-12) ln = 1;
                w.Write(C(nx / ln * 32766)); w.Write(C(ny / ln * 32766)); w.Write(C(nz / ln * 32766));
            }
            while (ms.Length % 4 != 0) w.Write((byte)0);
            w.Write(0x17000000u);
            while (ms.Length % 16 != 0) w.Write((byte)0);
            return ms.ToArray();
        }

        // ================================================================== textures

        private static string TextureName(List<(string Source, string Name)> names, string? source, string pack)
        {
            source ??= "";
            int i = names.FindIndex(n => n.Source == source);
            if (i >= 0) return names[i].Name;
            string name = $"{pack}_{names.Count:x2}.tif";
            names.Add((source, name));
            return name;
        }

        /// <summary>Each texture as an 8-bit texdat (median-cut, sample header / GIF tail with TEX0 = index, palette id)
        /// plus its 32-bit CSM1 palette (format 0x400) in the pack's block of ids.</summary>
        private static (List<ZedTree.Record>, List<ZedTree.Record>) Textures(Source src, List<(string Source, string Name)> names, string pack,
            Socom2Templates.Character samples, int? maxSize, List<string> warnings)
        {
            var texNodes = new List<ZedTree.Record>();
            var palNodes = new List<ZedTree.Record>();
            int index = 0;
            foreach (var (source, name) in names)
            {
                var img = string.IsNullOrEmpty(source) ? null : src.Texture(source);
                if (img == null) { warnings.Add($"texture {source}: not found - a grey one is used"); img = (Enumerable.Repeat((byte)0x80, 8 * 8 * 4).ToArray(), 8, 8); }
                var (rgba, w, h) = img.Value;
                int cap = maxSize ?? 256;
                while (Math.Max(w, h) > cap && w > 1 && h > 1) (rgba, w, h) = (Half(rgba, w, h), w / 2, h / 2);
                if ((w & (w - 1)) != 0 || (h & (h - 1)) != 0) { warnings.Add($"texture {source}: {w}x{h} is not a power of two"); }
                index++;
                int palId = CaToSocom2.PaletteBase(pack) + palNodes.Count;
                byte[] indices = ZedTextureFile.Quantize8(rgba, w, h, out byte[] palette);
                var par = new byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(par, (uint)palId);
                BinaryPrimitives.WriteUInt32LittleEndian(par.AsSpan(4), 0x400);
                palNodes.Add(new ZedTree.Record($"texpal_{palId}", new byte[0], new[] { new ZedTree.Record("par", par), new ZedTree.Record("buf", ZedTextureFile.EncodeClutBuffer(palette, false)) }));
                texNodes.Add(new ZedTree.Record(name, new byte[0], new[] { new ZedTree.Record("texdat", TexDat8(indices, w, h, index, palId, samples.Sample8)) }));
            }
            return (texNodes, palNodes);
        }

        private static byte[] Half(byte[] rgba, int w, int h)
        {
            var o = new byte[(w / 2) * (h / 2) * 4];
            for (int y = 0; y < h / 2; y++)
                for (int x = 0; x < w / 2; x++)
                    for (int k = 0; k < 4; k++)
                        o[(y * (w / 2) + x) * 4 + k] = (byte)((rgba[((2 * y) * w + 2 * x) * 4 + k] + rgba[((2 * y) * w + 2 * x + 1) * 4 + k]
                            + rgba[((2 * y + 1) * w + 2 * x) * 4 + k] + rgba[((2 * y + 1) * w + 2 * x + 1) * 4 + k] + 2) / 4);
            return o;
        }

        /// <summary>An 8-bit texdat from a SOCOM II sample: 0x20 header (size, pixel bytes, index, 32-bit palette flag),
        /// the indices, the sample's GIF tail with TEX0 rewritten (TBP0 index, TBW, PSMT8, TW/TH, CBP, CPSM 32-bit, CSM1).</summary>
        private static byte[] TexDat8(byte[] pix, int w, int h, int index, int palId, byte[] sample)
        {
            var hdr = sample[..0x20];
            BinaryPrimitives.WriteUInt16LittleEndian(hdr, (ushort)w);
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(2), (ushort)h);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4), (uint)pix.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(8), (uint)index);
            hdr[0x0F] = 0x13;
            var tail = sample[(0x20 + (int)BitConverter.ToUInt32(sample, 4))..];
            int at = -1;
            for (int i = 0; i + 16 <= tail.Length; i += 16) if (BitConverter.ToUInt64(tail, i + 8) == 0x06) { at = i; break; }
            if (at < 0) throw new InvalidDataException("The SOCOM II texdat sample has no TEX0");
            ulong tex0 = BitConverter.ToUInt64(tail, at);
            ulong Log2(int v) => (ulong)Math.Max(0, (int)Math.Round(Math.Log2(v)));
            tex0 &= ~((0x3FFFUL) | (0x3FUL << 14) | (0x3FUL << 20) | (0xFUL << 26) | (0xFUL << 30) | (0x3FFFUL << 37) | (0xFUL << 51) | (1UL << 55) | (0x1FUL << 56));
            tex0 |= (ulong)index | ((ulong)Math.Max(1, w / 64) << 14) | (0x13UL << 20) | (Log2(w) << 26) | (Log2(h) << 30) | ((ulong)palId << 37);
            BinaryPrimitives.WriteUInt64LittleEndian(tail.AsSpan(at), tex0);
            return hdr.Concat(pix).Concat(tail).ToArray();
        }

        // ================================================================== clips

        /// <summary>A source clip on seal_stand's layout at 30 fps: each anchored SOCOM II bone takes
        /// W_b(t) = G_a(t) K_b^-1 (local = parent world^-1 W_b); aimnodes cancels the hips; the rest keep seal_stand's
        /// values; the hips position is seal_stand's plus the source hips joint's travel from its bind (scaled).</summary>
        private static MotionClip Retarget(SourceClip clip, Rig rig, MotionClip stand)
        {
            int frames = Math.Max(1, clip.Frames);
            var names = stand.Tracks.Select(t => t.Name).ToList();
            var refRot = stand.Tracks.ToDictionary(t => t.Name, t => CaToSocom2.Q(t.Rotations[0]));
            var refPos = stand.Tracks.ToDictionary(t => t.Name, t => t.Positions[0]);
            var kinv = names.ToDictionary(n => n, n => rig.Mids.ContainsKey(n) ? rig.KInverse(n) : null);
            var outRot = names.ToDictionary(n => n, _ => new List<double[]>());
            var outPos = new List<double[]>();
            string ParentOf(string n) => CaToSocom2.S2Parent.TryGetValue(n, out var p) && names.Contains(p) ? p : "skel_root";
            double[] hipsBind = rig.Anchor.TryGetValue("hips", out int ha) ? Rig.Pos(rig.Source.Bones[ha].World) : new double[3];
            double[] rootRot = refRot.GetValueOrDefault("skel_root", new double[] { 0, 0, 0, 1 });

            for (int k = 0; k <= frames; k++)
            {
                var worlds = clip.Worlds(k);
                var sw = new Dictionary<string, double[]> { ["skel_root"] = rootRot };
                var done = new HashSet<string> { "skel_root" };
                for (int pass = 0; pass < names.Count && done.Count < names.Count; pass++)
                    foreach (string n in names)
                    {
                        if (done.Contains(n) || !done.Contains(ParentOf(n))) continue;
                        var par = sw[ParentOf(n)];
                        double[] loc;
                        if (kinv[n] is { } ki && rig.Anchor.TryGetValue(n, out int a) && a < worlds.Count)
                            loc = CaToSocom2.Norm(CaToSocom2.QMul(CaToSocom2.Inv(par), CaToSocom2.QMul(Quat(Rig.Rot(worlds[a])), ki)));
                        else if (n == "aimnodes" && outRot["hips"].Count > 0) loc = CaToSocom2.Inv(outRot["hips"][^1]);
                        else loc = refRot[n];
                        sw[n] = CaToSocom2.Norm(CaToSocom2.QMul(par, loc));
                        outRot[n].Add(loc);
                        done.Add(n);
                    }
                if (rig.Anchor.TryGetValue("hips", out int h) && h < worlds.Count)
                {
                    var p = Rig.Pos(worlds[h]);
                    var d = CaToSocom2.RotateQ(CaToSocom2.Inv(rootRot), new[] { (p[0] - hipsBind[0]) * rig.Scale, (p[1] - hipsBind[1]) * rig.Scale, (p[2] - hipsBind[2]) * rig.Scale });
                    var b = refPos["hips"];
                    outPos.Add(new[] { b[0] + d[0] / MotionClip.PositionScale, b[1] + d[1] / MotionClip.PositionScale, b[2] + d[2] / MotionClip.PositionScale });
                }
            }

            var result = new MotionClip { Duration = clip.Duration, Intervals = frames, NormMin = stand.NormMin, NormMax = stand.NormMax, Pad = stand.Pad };
            short C(double v) => (short)Math.Clamp((int)Math.Round(v), -32767, 32767);
            foreach (var st in stand.Tracks)
            {
                string n = st.Name;
                var rots = (n == "skel_root" || outRot[n].Count == 0 ? Enumerable.Repeat(refRot[n], frames + 1) : outRot[n])
                    .Select(q => new[] { C(q[0] * 32767), C(q[1] * 32767), C(q[2] * 32767), C(q[3] * 32767) }).ToList();
                var track = new MotionClip.Track { RawName = st.RawName, Record = st.Record };
                short[] still = st.Positions[0].Length >= 4 ? st.Positions[0] : new[] { st.Positions[0][0], st.Positions[0][1], st.Positions[0][2], MotionClip.StaticPositionW };
                if (n == "hips" && outPos.Count == frames + 1)
                {
                    track.Positions = outPos.Select(p => new[] { C(p[0]), C(p[1]), C(p[2]) }).ToList();
                    track.Rotations = rots;
                }
                else if (n == "skel_root" || rots.All(r => r.SequenceEqual(rots[0])))
                {
                    track.Positions = new() { still };
                    track.Rotations = new() { rots[0] };
                }
                else
                {
                    track.Positions = new() { still };
                    track.Rotations = rots;
                }
                result.Tracks.Add(track);
            }
            return result;
        }
    }
}
