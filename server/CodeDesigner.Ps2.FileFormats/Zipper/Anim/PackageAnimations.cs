using System.Text.RegularExpressions;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Anim
{
    /// <summary>
    /// Character animation clips of a package: its own <c>MOTION_S.ZAR</c> (one record per clip, editable) and the
    /// shared <c>MOTION_P.ZAR</c> next to it in <c>RUN/</c> (read-only here; copy a clip into the package to change
    /// it). Edits return the new <c>MOTION_S.ZAR</c> bytes, like <see cref="PackageEdits"/>.
    /// </summary>
    public static class PackageAnimations
    {
        public const string PackageLeaf = "MOTION_S.ZAR";
        public const string SharedFile = "MOTION_P.ZAR";

        public sealed record ClipRef(string Name, string Source, bool Editable, int Index, byte[] Data);

        private static readonly Regex NamePattern = new(@"^[A-Za-z0-9_]{1,63}$");

        /// <summary>The package's clips first, then the shared ones (a shared clip the package also has is listed once, as the package's).</summary>
        public static List<ClipRef> List(ZipperPackage pkg, IReadOnlyList<ZedNode>? shared)
        {
            var list = new List<ClipRef>();
            if (pkg.Entry(PackageLeaf) is { } e)
            {
                var nodes = pkg.Container(e.Leaf).Nodes;
                for (int i = 0; i < nodes.Count; i++)
                    if (MotionClip.LooksLikeClip(nodes[i].Data)) list.Add(new ClipRef(nodes[i].Name, e.Leaf, true, i, nodes[i].Data));
            }
            var own = new HashSet<string>(list.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            if (shared != null)
                for (int i = 0; i < shared.Count; i++)
                    if (!own.Contains(shared[i].Name) && MotionClip.LooksLikeClip(shared[i].Data)) list.Add(new ClipRef(shared[i].Name, SharedFile, false, i, shared[i].Data));
            return list;
        }

        /// <summary><c>RUN/MOTION_P.ZAR</c> beside the package (a .ZDB's folder, or the unpacked folder itself), or null.</summary>
        public static string? SharedPath(ZipperPackage pkg)
        {
            string dir = pkg.IsZdb ? Path.GetDirectoryName(pkg.SourcePath) ?? "." : pkg.SourcePath;
            foreach (string candidate in new[] { Path.Combine(dir, SharedFile), Path.Combine(Path.GetDirectoryName(dir) ?? dir, SharedFile) })
                if (File.Exists(candidate)) return candidate;
            return null;
        }

        public static ClipRef? Find(ZipperPackage pkg, IReadOnlyList<ZedNode>? shared, string name, string? source = null) =>
            List(pkg, shared).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && (source == null || c.Source.Equals(source, StringComparison.OrdinalIgnoreCase)));

        public static void CheckName(string name)
        {
            if (!NamePattern.IsMatch(name)) throw new ArgumentException($"Clip name '{name}': use 1-63 letters, digits or _");
        }

        /// <summary>Adds <paramref name="clip"/> as <paramref name="name"/> (after <paramref name="after"/>, else at the end).</summary>
        public static Dictionary<string, byte[]> Add(ZipperPackage pkg, string name, MotionClip clip, string? after = null)
        {
            CheckName(name);
            var (leaf, c) = Open(pkg);
            if (c.Nodes.Any(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"The package already has a clip '{name}'");
            int at = after != null ? IndexOf(c, after) + 1 : c.Nodes.Count;
            uint type = c.Nodes.Count > 0 ? c.Nodes[0].Type : 0;
            c.InsertNode(at, name, clip.Encode(), type);
            return One(leaf, c);
        }

        public static Dictionary<string, byte[]> Replace(ZipperPackage pkg, string name, MotionClip clip)
        {
            var (leaf, c) = Open(pkg);
            c.ReplaceNodeData(IndexOf(c, name), clip.Encode());
            return One(leaf, c);
        }

        public static Dictionary<string, byte[]> Delete(ZipperPackage pkg, string name)
        {
            var (leaf, c) = Open(pkg);
            c.RemoveNode(IndexOf(c, name));
            return One(leaf, c);
        }

        public static Dictionary<string, byte[]> Rename(ZipperPackage pkg, string name, string newName)
        {
            CheckName(newName);
            var (leaf, c) = Open(pkg);
            int i = IndexOf(c, name);
            if (c.Nodes.Where((n, k) => k != i).Any(n => n.Name.Equals(newName, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"The package already has a clip '{newName}'");
            c.RenameNode(i, newName);
            return One(leaf, c);
        }

        private static (string Leaf, ZedContainer Container) Open(ZipperPackage pkg)
        {
            var e = pkg.Entry(PackageLeaf) ?? throw new InvalidOperationException($"This package has no {PackageLeaf}");
            return (e.Leaf, ZedContainer.FromBytes(e.Data));
        }

        private static int IndexOf(ZedContainer c, string name)
        {
            for (int i = 0; i < c.Nodes.Count; i++) if (c.Nodes[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
            throw new KeyNotFoundException($"The package has no clip '{name}' (shared MOTION_P clips are read-only: copy one in first)");
        }

        private static Dictionary<string, byte[]> One(string leaf, ZedContainer c) =>
            new(StringComparer.OrdinalIgnoreCase) { [leaf] = c.ToBytes() };
    }
}
