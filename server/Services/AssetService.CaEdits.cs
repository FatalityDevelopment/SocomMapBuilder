using System.Text.Json;
using CodeDesigner.Ps2.FileFormats;
using CodeDesigner.Ps2.FileFormats.Zipper.Ca;

namespace MapBuilder.Server.Services;

internal sealed partial class ZipperAssetService
{
    internal sealed record CaStep(string Description, Dictionary<int, byte[]> Before, Dictionary<int, byte[]> After);

    private static string[] CaResourceNames(CaSession s, IEnumerable<int> indices) => indices
        .Select(i => s.Package.Resources[i]).Select(r => $"{r.File}/{r.Name} [{r.Tag}]").Order().ToArray();

    private static object CaHistory(CaSession s)
    {
        object[] Steps(IEnumerable<CaStep> steps) => steps
            .Select(step => new { description = step.Description, archives = CaResourceNames(s, step.Before.Keys) }).ToArray();
        return new { revision = s.Revision, undo = Steps(s.Undo), redo = Steps(s.Redo), dirty = CaResourceNames(s, s.Dirty) };
    }

    private static object CaEdit(CaSession s, string? json)
    {
        using var doc = JsonDocument.Parse(json ?? "{}");
        var a = doc.RootElement;
        if (EditOps.Check(a) is { } error) throw new ArgumentException(error);
        string op = Str(a, "op")!;
        if (a.TryGetProperty("revision", out _) && Int(a, "revision") != s.Revision)
            throw new InvalidOperationException("The CA map changed. Refresh before applying this edit.");
        string Mission()
        {
            if (Str(a, "mission") is { } expected && !expected.Equals(s.Mission, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The selected mission changed to {s.Mission}. Refresh before editing {expected}.");
            return s.Mission ?? throw new InvalidOperationException("Select a CA mission first.");
        }

        string description;
        Dictionary<CaPackage.Resource, byte[]> changes;
        string[] warnings = Array.Empty<string>();
        switch (op)
        {
            case "movePoint":
            case "setPoint":
                if (a.TryGetProperty("team", out _) || a.TryGetProperty("kind", out _))
                    throw new NotSupportedException("CA point edits support position and name only.");
                string name = Str(a, "name")!;
                string? newName = Str(a, "newName");
                var pointWarnings = new List<string>();
                changes = CaMissionEdits.Point(s.Package, Mission(), name, newName, Vec(a, "position", 3), pointWarnings);
                description = $"{s.Mission}: {(newName == null ? "move" : "rename")} {name}";
                if (newName != null && newName != name)
                    pointWarnings.Add("Actor start references are updated. Compiled actions and scripts may still refer to the old point name.");
                warnings = pointWarnings.ToArray();
                break;
            case "mapTable":
                string table = Str(a, "table")!;
                changes = CaMissionEdits.Table(s.Package, Mission(), table, a.GetProperty("path"), a.GetProperty("values"));
                description = $"{s.Mission}: edit {table}";
                break;
            case "moveInstance":
                string instance = Str(a, "instance") ?? throw new ArgumentException("A CA move needs the instance id from ASSET_MAP_SCENE.");
                var moved = CaWorldEdits.Move(s.Package, instance, matrix => Placement(a, matrix));
                changes = new() { [moved.Resource] = moved.Data };
                description = $"move {instance}";
                warnings = new[] { CaWorldEdits.SpatialWarning };
                break;
            default:
                throw new NotSupportedException($"CA maps support movePoint, setPoint, mapTable and moveInstance. '{op}' is unavailable.");
        }

        description = Str(a, "label") ?? description;
        var before = new Dictionary<int, byte[]>();
        var after = new Dictionary<int, byte[]>();
        foreach (var (resource, bytes) in changes)
        {
            if (resource.Data.AsSpan().SequenceEqual(bytes)) continue;
            int index = s.Package.Resources.IndexOf(resource);
            if (index < 0) throw new InvalidOperationException("The resource is outside this package.");
            before.Add(index, resource.Data);
            after.Add(index, bytes);
        }
        var changed = CaResourceNames(s, after.Keys);
        if (a.TryGetProperty("dryRun", out var dry) && dry.ValueKind == JsonValueKind.True)
            return new { dryRun = true, wouldDo = new { description, changed, warnings } };
        if (after.Count != 0)
        {
            foreach (var (index, data) in before) s.Clean.TryAdd(index, data);
            ApplyCaResources(s, after);
            s.Undo.Push(new CaStep(description, before, after));
            s.Redo.Clear();
        }
        return new { revision = s.Revision, description, changed, warnings };
    }

    private static void ApplyCaResources(CaSession s, Dictionary<int, byte[]> data)
    {
        s.Package.ReplaceResources(data);
        foreach (var (index, bytes) in data)
        {
            if (s.Clean[index].AsSpan().SequenceEqual(bytes)) s.Dirty.Remove(index);
            else s.Dirty.Add(index);
        }
        s.Revision++;
    }

    private static object CaUndoRedo(CaSession s, bool undo)
    {
        var from = undo ? s.Undo : s.Redo;
        if (!from.TryPeek(out var step)) throw new InvalidOperationException(undo ? "Nothing to undo" : "Nothing to redo");
        ApplyCaResources(s, undo ? step.Before : step.After);
        from.Pop();
        (undo ? s.Redo : s.Undo).Push(step);
        return new { revision = s.Revision, description = step.Description, undone = undo ? step.Description : null,
            redone = undo ? null : step.Description, changed = CaResourceNames(s, step.Before.Keys) };
    }

    private static object CaSave(CaSession s, string? json)
    {
        var args = Parse<SaveArgs>(json);
        if (s.Package.IsPsp) return PspSave(s, args);
        if (args?.Mode != null && !args.Mode.Equals("zdb", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("CA maps save as complete location archives or standalone v6 ZDBs.");
        if (args?.Leaves != null) throw new NotSupportedException("CA saves include the complete archive.");
        if (s.Dirty.Count == 0) throw new InvalidOperationException("No changes to save");
        if (string.IsNullOrWhiteSpace(args?.OutputPath)) throw new ArgumentException("Choose a separate writable output folder or file for the CA copy.");
        string source = s.Package.SourcePath;
        string path = Path.GetFullPath(args.OutputPath);
        if (Directory.Exists(path) || Path.EndsInDirectorySeparator(path) || !Path.HasExtension(path))
        {
            string filename = Path.GetExtension(source).Equals(".ZAR", StringComparison.OrdinalIgnoreCase) && s.Package.Location is { } location
                ? CaPackage.ArchiveName(location) : Path.GetFileName(source);
            path = Path.Combine(path, filename);
        }
        if (!Path.GetExtension(path).Equals(Path.GetExtension(source), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The output copy needs the source extension {Path.GetExtension(source)}.");
        WriteCaCopy(s, path);
        return new { written = new[] { path }, leaves = CaResourceNames(s, s.Dirty), revision = s.Revision };
    }

    /// <summary>
    /// Fireteam Bravo 2: every ZDB with an edit, written into the output folder at its place under RUN
    /// (<c>&lt;output&gt;\FORT_MP\ISLAND_F.ZDB</c>), so a folder save mirrors the disc and a disc-image save finds each
    /// file's place.
    /// </summary>
    private static object PspSave(CaSession s, SaveArgs? args)
    {
        if (args?.Mode != null && !args.Mode.Equals("zdb", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Fireteam Bravo 2 maps save as ZDB copies.");
        if (s.Dirty.Count == 0) throw new InvalidOperationException("No changes to save");
        if (string.IsNullOrWhiteSpace(args?.OutputPath)) throw new ArgumentException("Choose a writable output folder for the edited ZDBs.");
        string folder = Path.GetFullPath(args.OutputPath);
        if (File.Exists(folder) || Path.HasExtension(folder) && !Directory.Exists(folder))
            throw new ArgumentException("Fireteam Bravo 2 saves write a folder of ZDBs: choose a folder (or a .ISO for a disc copy).");
        var written = new List<string>();
        lock (CaWriteGate)
        {
            foreach (var (rel, bytes) in s.Package.BuildPspCopies(s.Dirty))
            {
                string path = Path.Combine(folder, rel);
                string source = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Directory.Exists(s.Package.SourcePath) ? Path.Combine(s.Package.SourcePath, "x") : s.Package.SourcePath)!) ?? "", rel);
                if (Path.GetFullPath(path).Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Choose a folder other than the game's RUN folder for the edited copy.");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                SafeFile.Write(path, bytes);
                written.Add(path);
            }
        }
        return new { written, leaves = CaResourceNames(s, s.Dirty), revision = s.Revision };
    }

    private static readonly object CaWriteGate = new();

    private static void WriteCaCopy(CaSession s, string path)
    {
        lock (CaWriteGate)
        {
            if (path.Equals(s.Package.SourcePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a separate output path for the CA copy.");
            if (CaSessions.Values.Any(other => other.Package.SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase))
                || Sessions.Values.Any(other => other.Package.SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The output copy is open in another session. Close it or choose a new path.");
            SafeFile.Write(path, s.Package.BuildCopy(s.Dirty));
        }
    }
}
