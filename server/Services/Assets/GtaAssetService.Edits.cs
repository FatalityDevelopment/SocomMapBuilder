using System.Text.Json;
using CodeDesigner.Ps2.FileFormats;
using CodeDesigner.Ps2.FileFormats.Gta;

namespace MapBuilder.Server.Services.Assets;

internal static partial class GtaAssetService
{
    internal sealed record Step(GtaPackage.Model Model, string Description, byte[] Before, byte[] After);
    private static readonly object FileGate = new();
    private static string Label(GtaPackage.Model m) => $"{m.Archive}/{m.Name}.dff";

    private static object Edit(Session s, string? json)
    {
        using var doc = JsonDocument.Parse(json ?? "{}");
        var op = doc.RootElement;
        if (EditOps.Check(op) is { } error) throw new ArgumentException(error);
        if (op.GetProperty("op").GetString() is not ("gtaFrame" or "gtaMaterial" or "gtaGeometry" or "gtaUv"))
            throw new NotSupportedException("Use gtaFrame, gtaMaterial, gtaGeometry or gtaUv for a native DFF edit.");
        if (op.GetProperty("revision").GetInt32() != s.Revision) throw new InvalidOperationException("The model changed. Refresh before editing.");
        var model = Find(s, op.GetProperty("model").GetString(), op.GetProperty("archive").GetString());
        if (!s.Package.IsEditable(model)) throw new InvalidOperationException("Select an individual clothing DFF to edit the assembled CJ model.");
        var before = s.Package.ReadModelData(model);
        int Index(string key) => op.GetProperty(key).GetInt32();
        float[] Vector(string key) => op.GetProperty(key).EnumerateArray().Select(n => n.GetSingle()).ToArray();
        byte[] after;
        string action;
        switch (op.GetProperty("op").GetString())
        {
            case "gtaFrame":
                after = GtaDffEdits.MoveFrame(before, Index("frame"), Vector("position"));
                action = "Move frame";
                break;
            case "gtaMaterial":
                after = GtaDffEdits.SetMaterial(before, Index("geometry"), Index("material"),
                    op.TryGetProperty("color", out var color) ? color.EnumerateArray().Select(n => n.GetInt32()).ToArray() : null,
                    op.TryGetProperty("texture", out var tex) ? tex.GetString() : null);
                action = "Edit material";
                break;
            case "gtaGeometry":
                after = GtaGeometryEdits.Transform(before, Index("geometry"), Vector("factors"), Vector("offset"));
                action = "Transform geometry";
                break;
            case "gtaUv":
                after = GtaUvEdits.Set(before, ReadUvEdits(op.GetProperty("chunks")));
                action = "Edit UVs";
                break;
            default: throw new NotSupportedException();
        }
        string description = op.TryGetProperty("label", out var l) ? l.GetString() ?? action : $"{action} in {model.Name}";
        bool differs = !before.AsSpan().SequenceEqual(after);
        var changed = differs ? new[] { Label(model) } : Array.Empty<string>();
        if (op.TryGetProperty("dryRun", out var dry) && dry.ValueKind == JsonValueKind.True)
            return new { dryRun = true, wouldDo = new { description, changed } };
        if (differs)
        {
            s.Originals.TryAdd(model, before);
            Apply(s, model, after);
            s.Undo.Push(new Step(model, description, before, after));
            s.Redo.Clear();
        }
        return new { revision = s.Revision, description, changed };
    }

    private static List<GtaUvEdits.Chunk> ReadUvEdits(JsonElement chunks)
    {
        var edits = new List<GtaUvEdits.Chunk>();
        foreach (var chunk in chunks.EnumerateArray())
        {
            var vertices = new List<GtaUvEdits.Vertex>();
            foreach (var vertex in chunk.GetProperty("vertices").EnumerateArray())
            {
                var uv = vertex.GetProperty("uv");
                if (uv.GetArrayLength() != 2) throw new ArgumentException("Supply [u, v] for each UV vertex.");
                vertices.Add(new(vertex.GetProperty("i").GetInt32(), uv[0].GetSingle(), uv[1].GetSingle()));
            }
            edits.Add(new(chunk.GetProperty("geometryOffset").GetInt32(), vertices));
        }
        return edits;
    }

    private static void Apply(Session s, GtaPackage.Model model, byte[] data)
    {
        bool clean = s.Originals[model].AsSpan().SequenceEqual(data);
        s.Package.SetModelData(model, clean ? null : data);
        if (clean) s.Dirty.Remove(model);
        else s.Dirty.Add(model);
        s.Revision++;
    }

    private static object UndoRedo(Session s, bool undo)
    {
        var source = undo ? s.Undo : s.Redo;
        if (!source.TryPeek(out var step)) throw new InvalidOperationException(undo ? "Nothing to undo" : "Nothing to redo");
        Apply(s, step.Model, undo ? step.Before : step.After);
        source.Pop();
        (undo ? s.Redo : s.Undo).Push(step);
        return new { revision = s.Revision, step.Description, changed = new[] { Label(step.Model) } };
    }

    private static object History(Session s)
    {
        object[] Rows(IEnumerable<Step> steps) => steps.Select(step => new { step.Description, archives = new[] { Label(step.Model) } }).ToArray();
        return new { revision = s.Revision, undo = Rows(s.Undo), redo = Rows(s.Redo), dirty = s.Dirty.Select(Label).Order().ToArray() };
    }

    private sealed record SaveArgs(string? Mode, string? OutputPath, string? Archive, string? Model);

    private static object Save(Session s, string? json)
    {
        var args = AssetJson.Parse<SaveArgs>(json);
        if (string.IsNullOrWhiteSpace(args?.OutputPath)) throw new ArgumentException("Choose a separate .DFF or .IMG output file.");
        string full = Path.GetFullPath(args.OutputPath);
        string[] leaves;
        lock (FileGate)
        {
            if (Sessions.Values.Any(open => open.Package.IsSourceFile(full))) throw new InvalidOperationException("Choose a separate output file outside the open source assets.");
            switch (args.Mode?.ToLowerInvariant())
            {
                case "dff" when Path.GetExtension(full).Equals(".dff", StringComparison.OrdinalIgnoreCase):
                    var model = Find(s, args.Model, args.Archive);
                    if (!s.Package.IsEditable(model)) throw new InvalidOperationException("Choose an individual source DFF.");
                    SafeFile.Write(full, s.Package.ReadModelData(model));
                    leaves = new[] { Label(model) };
                    break;
                case "img" when !string.IsNullOrEmpty(args.Archive):
                    s.Package.SaveImgCopy(args.Archive, full);
                    leaves = s.Dirty.Where(m => m.Archive.Equals(args.Archive, StringComparison.OrdinalIgnoreCase)).Select(Label).Order().ToArray();
                    break;
                default: throw new ArgumentException("Save a model with mode dff, or all edited models in one archive with mode img.");
            }
        }
        return new { written = new[] { full }, leaves, revision = s.Revision };
    }
}
