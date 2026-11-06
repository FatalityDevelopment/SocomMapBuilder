using System.Globalization;
using System.Text.RegularExpressions;
using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Zipper.Ca;

namespace CodeDesigner.Ps2.FileFormats.Ftb3
{
    /// <summary>
    /// Fireteam Bravo 3's characters (<c>common\characters</c>): one .ssg per part, named after the character
    /// (<c>Sandman_woodland.ssg</c> the body, <c>_H</c> the head, <c>_G</c> gloves, <c>_P</c> pouches, <c>_T</c> torso gear;
    /// <c>_U</c> is another uniform and is not drawn), all skinned to one skeleton (<c>skeleton\skelly.skel</c>).
    /// </summary>
    public sealed partial class Ftb3Package
    {
        /// <summary>The archive name characters are listed under.</summary>
        public const string CharacterArchive = "characters";

        /// <summary>A character: its name and the part files drawn together (body first).</summary>
        public sealed record Character(string Name, List<string> Files);

        private static readonly string[] PartSuffixes = { "", "_H", "_G", "_P", "_T" };

        /// <summary>How much larger than its model's scale a weighted vertex is drawn.</summary>
        public const float SkinnedScale = 8;

        private List<Character>? _characters;
        private readonly Dictionary<string, SsgArchive> _characterFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ModelMesh> _characterMeshes = new(StringComparer.OrdinalIgnoreCase);
        private CharacterSkeleton? _skeleton;
        private List<float[]>? _inverseBind;

        /// <summary>The disc's <c>common\characters</c> folder (next to <c>levels</c>), or null.</summary>
        private string? CharacterDir
        {
            get
            {
                string? level = Levels.Values.FirstOrDefault();
                string? usrdir = level == null ? null : Path.GetDirectoryName(Path.GetDirectoryName(level));
                string dir = usrdir == null ? "" : Path.Combine(usrdir, "common", "characters");
                return Directory.Exists(dir) ? dir : null;
            }
        }

        /// <summary>The disc's characters: each body .ssg with a head part (<c>&lt;name&gt;_H.ssg</c>); none when the
        /// package was opened from a level copied off the disc.</summary>
        public List<Character> Characters()
        {
            if (_characters != null) return _characters;
            var list = new List<Character>();
            if (CharacterDir is { } dir && File.Exists(Path.Combine(dir, "skeleton", "skelly.skel")))
                foreach (string body in Directory.GetFiles(dir, "*.ssg").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileNameWithoutExtension(body);
                    if (!File.Exists(Path.Combine(dir, name + "_H.ssg"))) continue;
                    var parts = PartSuffixes.Select(s => Path.Combine(dir, name + s + ".ssg")).Where(File.Exists).ToList();
                    list.Add(new Character(name, parts));
                }
            return _characters = list;
        }

        public Character? FindCharacter(string name) => Characters().FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        private SsgArchive CharacterFile(string path)
        {
            if (!_characterFiles.TryGetValue(path, out var a)) _characterFiles[path] = a = SsgArchive.Open(path);
            return a;
        }

        /// <summary>
        /// The shared skeleton: <c>skelly.skel</c> is text, <c>(skeleton skelly 20)</c>, then each bone's parent
        /// (<c>-1 ; parent of 0 (hips) is -1</c>), then each bone's local rotation (a quaternion) and translation and
        /// its inverse bind matrix (4×4, row vectors, translation in row 3), named in a comment (<c>; 0 (hips)</c>). The
        /// names are CA's (hips, spinelo … leftElbow). Bind-pose world matrices are the inverses.
        /// </summary>
        public CharacterSkeleton? Skeleton()
        {
            if (_skeleton != null) return _skeleton;
            if (CharacterDir is not { } dir) return null;
            string path = Path.Combine(dir, "skeleton", "skelly.skel");
            if (!File.Exists(path)) return null;
            var (names, parents, inverse) = ParseSkeleton(File.ReadAllText(path));
            var worlds = inverse.Select(Matrix4.InverseAffine).ToList();
            var bones = new List<CharacterSkeleton.Bone>();
            for (int i = 0; i < names.Count; i++)
            {
                int parent = parents[i];
                var local = parent < 0 ? worlds[i] : Matrix4.Multiply(Matrix4.InverseAffine(worlds[parent]), worlds[i]);
                bones.Add(new CharacterSkeleton.Bone(names[i], i, parent, i, local, worlds[i]));
            }
            _inverseBind = inverse;
            return _skeleton = CharacterSkeleton.FromBones(bones);
        }

        /// <summary>Bone names, parents and inverse bind matrices from <c>skelly.skel</c>'s text.</summary>
        public static (List<string> Names, List<int> Parents, List<float[]> InverseBind) ParseSkeleton(string text)
        {
            var parents = new List<int>();
            foreach (Match m in Regex.Matches(text, @"^\s*(-?\d+)\s*;\s*parent of (\d+)", RegexOptions.Multiline))
                parents.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            var names = new List<string>();
            var inverse = new List<float[]>();
            const string F = @"(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)";
            string row = $@"\s*{F}\s+{F}\s+{F}\s+{F}";
            var bone = new Regex($@"\(\s*{F}\s+{F}\s+{F}\s+{F}\s*\)\s*\(\s*{F}\s+{F}\s+{F}\s*\)\s*\({row}{row}{row}{row}\s*\)\s*;\s*\d+\s*\(([^)]+)\)");
            foreach (Match m in bone.Matches(text))
            {
                var mat = new float[16];
                for (int k = 0; k < 16; k++) mat[k] = float.Parse(m.Groups[8 + k].Value, CultureInfo.InvariantCulture);
                inverse.Add(mat);
                names.Add(m.Groups[24].Value.Trim());
            }
            if (names.Count == 0 || parents.Count < names.Count) throw new InvalidDataException("skelly.skel: no bones read");
            return (names, parents.Take(names.Count).ToList(), inverse);
        }

        /// <summary>
        /// A character in its bind pose, skinned: every model of its part files, each mesh piece one group. A skinned
        /// piece's vertices (VTYPE 0xC532) start with four u16 weights (0x8000 = 1.0) for the bones its strip names:
        /// a strip header's +0x10 holds four bone indices, one byte each (0xFF none). An unskinned piece goes on its
        /// strip's first bone (the hips when that is no bone).
        /// </summary>
        public ModelMesh BuildCharacter(Character c)
        {
            if (_characterMeshes.TryGetValue(c.Name, out var hit)) return hit;
            var skeleton = Skeleton() ?? throw new InvalidDataException("The character skeleton (common\\characters\\skeleton\\skelly.skel) is not on this disc");
            var groups = new List<SkinnedMesh.Group>();
            foreach (string file in c.Files)
            {
                var a = CharacterFile(file);
                var textures = a.Entries.Where(e => e.Type == SsgArchive.Texture && e.Size >= 0x20).GroupBy(e => a.U32(e.Offset)).ToDictionary(g => g.Key, g => g.First().Name);
                for (int i = 0; i < a.Entries.Count; i++)
                {
                    if (a.Entries[i].Type != SsgArchive.Model) continue;
                    float scale = a.F32(a.Entries[i].Offset + 0x4C) is > 0 and < 65536 and var k ? k : 256f;
                    foreach (var piece in PiecesOf(a, i))
                    {
                        uint hash = a.U32(piece.Offset);
                        string texture = textures.TryGetValue(hash, out var t) ? t : $"ftb3_{hash:x8}";
                        if (SkinnedPiece(a, piece, scale, skeleton.Bones.Count, _inverseBind!, groups.Count, texture) is { } g) groups.Add(g);
                    }
                }
            }
            var mesh = ModelMesh.FromGroups(CharacterArchive, c.Name, skeleton, groups);
            return _characterMeshes[c.Name] = mesh;
        }

        private static SkinnedMesh.Group? SkinnedPiece(SsgArchive a, SsgArchive.Entry piece, float scale, int boneCount, IReadOnlyList<float[]> inverseBind, int section, string texture)
        {
            var d = a.Data;
            int o = piece.Offset, end = piece.Offset + piece.Size;
            uint vtype = 0;
            for (int k = 0x40; k < 0x50; k += 4) if (a.U32(o + k) >> 24 == 0x12) vtype = a.U32(o + k) & 0xFFFFFF;
            var fmt = new PspGe.VertexFormat(vtype);
            if (fmt.Stride == 0) return null;
            // Weighted vertices are drawn 8 times larger than the model's scale says (8, so 64): on all 847 character
            // models the stored bounds need exactly that. Level models (unweighted) use their scale as it is.
            if (fmt.Weights > 0) scale *= SkinnedScale;
            var world = new[] { scale, 0, 0, 0, 0, scale, 0, 0, 0, 0, scale, 0, 0, 0, 0, 1f };
            int strips = (int)(a.U32(o + 0x50) & 0xFFFF);
            float uvScale = a.F32(o + 0x58), uvOffset = a.F32(o + 0x5C);
            if (!float.IsFinite(uvScale) || uvScale == 0 || !float.IsFinite(uvOffset)) (uvScale, uvOffset) = (1, 0);
            var pos = new List<float>(); var uv = new List<float>(); var nrm = new List<float>(); var col = new List<byte>();
            var vertices = new List<List<SkinnedMesh.Part>>();
            var tris = new List<int>();
            int p = o + 0x70;
            for (int s = 0; s < strips && p + 0x20 <= end; s++)
            {
                int bytes = (int)a.U32(p + 0x18);
                uint prim = a.U32(p + 0x1C);
                int n = (int)(prim & 0xFFFF), type = (int)((prim >> 16) & 7);
                int v = p + 0x20;
                if (bytes < 0 || v + bytes > end || n * fmt.Stride > bytes) break;
                var palette = Enumerable.Range(0, 4).Select(k => (int)d[p + 0x10 + k]).ToArray();
                int first = pos.Count / 3;
                for (int i = 0; i < n; i++)
                {
                    int at = v + i * fmt.Stride;
                    fmt.Read(d, at, world, pos, uv, nrm, col);
                    int vi = pos.Count / 3 - 1;
                    var parts = new List<SkinnedMesh.Part>();
                    float total = 0;
                    // Positions are in the bind pose (model space); each influence keeps its bone-local copy.
                    void Add(int bone, float w)
                    {
                        var m = inverseBind[bone];
                        var (lx, ly, lz) = Matrix4.Apply(m, pos[3 * vi], pos[3 * vi + 1], pos[3 * vi + 2]);
                        float nx = nrm[3 * vi], ny = nrm[3 * vi + 1], nz = nrm[3 * vi + 2];
                        parts.Add(new SkinnedMesh.Part(bone, lx, ly, lz, m[0] * nx + m[4] * ny + m[8] * nz, m[1] * nx + m[5] * ny + m[9] * nz, m[2] * nx + m[6] * ny + m[10] * nz, w));
                        total += w;
                    }
                    for (int k = 0; k < fmt.Weights && k < 4; k++)
                    {
                        float w = fmt.Weight switch { 1 => d[at + k] / 128f, 2 => BitConverter.ToUInt16(d, at + 2 * k) / 32768f, _ => BitConverter.ToSingle(d, at + 4 * k) };
                        if (w > 0 && palette[k] < boneCount) Add(palette[k], w);
                    }
                    if (parts.Count == 0) Add(palette[0] < boneCount ? palette[0] : 0, 1);
                    else if (MathF.Abs(total - 1) > 1e-3f) for (int k = 0; k < parts.Count; k++) parts[k] = parts[k] with { Weight = parts[k].Weight / total };
                    vertices.Add(parts);
                }
                void Tri(int x, int y, int z) { if (x != y && y != z && x != z) { tris.Add(first + x); tris.Add(first + y); tris.Add(first + z); } }
                if (type == 4) for (int i = 0; i + 2 < n; i++) { if ((i & 1) == 0) Tri(i, i + 1, i + 2); else Tri(i + 1, i, i + 2); }
                else if (type == 3) for (int i = 0; i + 2 < n; i += 3) Tri(i, i + 1, i + 2);
                p = v + bytes;
            }
            if (tris.Count == 0) return null;
            var uvs = uv.ToArray();
            for (int i = 0; i < uvs.Length; i++) uvs[i] = uvs[i] * uvScale + uvOffset;
            for (int i = 1; i < uvs.Length; i += 2) uvs[i] = 1f - uvs[i];
            return new SkinnedMesh.Group
            {
                Texture = texture, Colors = fmt.Colour != 0 ? col.ToArray() : null, Section = section, DrawOffset = piece.Offset,
                Vertices = vertices.ToArray(), Uvs = uvs, Triangles = tris.ToArray(),
            };
        }

        /// <summary>A character's texture by its entry name (or <c>ftb3_&lt;hash&gt;</c>), from its part files.</summary>
        public Texture? FindCharacterTexture(Character c, string name)
        {
            uint? hash = name.StartsWith("ftb3_", StringComparison.OrdinalIgnoreCase) && uint.TryParse(name[5..], NumberStyles.HexNumber, null, out uint h) ? h : null;
            foreach (string file in c.Files)
            {
                var a = CharacterFile(file);
                var e = a.Entries.FirstOrDefault(e => e.Type == SsgArchive.Texture && e.Size >= 0x20 && (hash is uint x ? a.U32(e.Offset) == x : e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
                if (e != null) return DecodeTexture(a, e);
            }
            return null;
        }
    }
}
