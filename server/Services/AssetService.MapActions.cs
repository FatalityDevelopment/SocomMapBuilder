using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.RDR;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace MapBuilder.Server.Services;

internal sealed partial class ZipperAssetService
{
    private const string MissionReader = "READERM.ZAR";
    private const string ActionRecord = "actions.rdr";

    private static IEnumerable<(GeoNode Node, string Path)> ActionTargets(GeoNode parent, string path = "")
    {
        foreach (var node in parent.SceneChildren)
        {
            string childPath = path + "/" + node.Name;
            yield return (node, childPath);
            foreach (var child in ActionTargets(node, childPath)) yield return child;
        }
    }

    private static int ActionList(RdrDocument doc)
    {
        int[] group = doc.Children(0).ToArray();
        if (group.Length == 1)
        {
            int[] pair = doc.Children(group[0]).ToArray();
            if (pair.Length == 2 && Equals(doc.ValueOf(pair[0]), "actions") && doc.TypeOf(pair[1]) == RdrDocument.List)
                return pair[1];
        }
        throw new InvalidDataException("Unrecognized actions.rdr structure.");
    }

    private static object MapActions(AssetSession s)
    {
        var map = s.Package.MapScene() ?? throw new InvalidOperationException("No map scene.");
        var reader = s.Package.Entry(MissionReader);
        var records = reader == null ? Array.Empty<ZedNode>() : s.Package.Container(MissionReader).Nodes.ToArray();
        var action = records.FirstOrDefault(n => n.Name.Equals(ActionRecord, StringComparison.OrdinalIgnoreCase));
        var rows = new List<Dictionary<string, object?[]>>();
        if (action != null)
        {
            var doc = RdrDocument.Parse(action.Data);
            foreach (int row in doc.Children(ActionList(doc)))
            {
                var fields = new Dictionary<string, object?[]>();
                int[] pairs = doc.Children(row).ToArray();
                for (int i = 0; i < pairs.Length; i += 2)
                    fields.Add((string)doc.ValueOf(pairs[i])!, doc.Children(pairs[i + 1]).Select(doc.ValueOf).ToArray());
                rows.Add(fields);
            }
        }
        var valves = new List<string>();
        if (records.FirstOrDefault(n => n.Name.Equals("valves.rdr", StringComparison.OrdinalIgnoreCase)) is { } valveRecord)
        {
            var doc = RdrDocument.Parse(valveRecord.Data);
            foreach (int group in doc.Children(0))
            foreach (var pair in doc.Children(group).Chunk(2))
            {
                if (pair.Length != 2 || !Equals(doc.ValueOf(pair[0]), "valves")) continue;
                foreach (int valve in doc.Children(pair[1]))
                foreach (var field in doc.Children(valve).Chunk(2))
                    if (field.Length == 2 && Equals(doc.ValueOf(field[0]), "name") &&
                        doc.Children(field[1]).Select(doc.ValueOf).FirstOrDefault() is string name) valves.Add(name);
            }
        }
        var animations = new List<string>();
        if (s.Package.Entry("MZANIM.ZAR") != null)
        {
            var nodes = s.Package.Container("MZANIM.ZAR").Nodes;
            for (int i = 0; i < nodes.Count; i++)
                if (nodes[i].Name == "Animation_List")
                    animations.AddRange(ZedTree.Children(nodes, i).Select(index => nodes[index].Name));
        }
        return new
        {
            revision = s.Revision, hasReader = reader != null, rows, valves, animations = animations.Distinct(),
            targets = ActionTargets(map.Scene.MapRoot).Select(t => new { index = t.Node.Index, name = t.Node.Name, path = t.Path, model = t.Node.ModelReference,
                faces = t.Node.Child("di")?.Children.Where(n => n.Name == "di" && n.Child("points")?.Data.Length == 64 && n.Child("params")?.Data.Length == 12)
                    .Select(n => n.Index).ToArray() ?? Array.Empty<int>() }).ToArray(),
            tables = records.Where(n => n.Name.EndsWith(".rdr", StringComparison.OrdinalIgnoreCase)).Select(n => n.Name).ToArray(),
            icons = s.Package.Entry("HUD_TXR.ZED") == null ? Array.Empty<string>() : s.Package.Container("HUD_TXR.ZED").Nodes
                .Select(n => n.Name).Where(n => n.StartsWith("action_", StringComparison.OrdinalIgnoreCase)).Distinct().ToArray()
        };
    }

    private static void CheckActionRevision(AssetSession s, JsonElement a)
    {
        if (Int(a, "revision") != s.Revision) throw new InvalidOperationException("The map changed. Reopen Actions before applying this edit.");
    }

    private static object EditMapAction(AssetSession s, JsonElement a)
    {
        CheckActionRevision(s, a);
        var entry = s.Package.Entry(MissionReader) ?? throw new InvalidOperationException("This package has no mission reader.");
        var reader = ZedContainer.FromBytes(entry.Data);
        var record = reader.FindEntry(ActionRecord);
        var doc = record == null ? RdrDocument.Create() : RdrDocument.Parse(record.Data);
        if (record == null)
            doc.WriteFields(doc.AppendList(0), new Dictionary<string, object[]> { ["actions"] = Array.Empty<object>() });
        int list = ActionList(doc);
        int ordinal = Int(a, "row");
        int[] rows = doc.Children(list).ToArray();
        var changes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (ordinal < -1 || ordinal >= rows.Length) throw new ArgumentException("Unknown action row.");
        if (!a.TryGetProperty("fields", out var input))
        {
            if (ordinal < 0) throw new ArgumentException("Select an action to remove.");
            doc.RemoveChild(list, ordinal);
        }
        else
        {
            var fields = new Dictionary<string, object[]>();
            foreach (var field in input.EnumerateObject())
            {
                var values = field.Value.EnumerateArray().ToArray();
                object[] typed = field.Name switch
                {
                    "range" or "elevation" or "points" or "team" when values.Length == 1 => new object[] { values[0].GetInt32() },
                    "one_shot" or "tcm" when values.Length == 0 => Array.Empty<object>(),
                    "anim" when values.Length > 0 => values.Select(v => (object)ActionString(v)).ToArray(),
                    "node" or "valve" or "bitmap" or "descriptor" or "type" when values.Length == 1 => new object[] { ActionString(values[0]) },
                    _ => throw new ArgumentException($"Unsupported action field or value count for {field.Name}.")
                };
                fields.Add(field.Name, typed);
            }
            if (!fields.TryGetValue("node", out var nodes)) throw new ArgumentException("Choose a named GEO target.");
            string name = (string)nodes[0];
            var map = s.Package.MapScene() ?? throw new InvalidOperationException("No map scene.");
            // Existing unresolved retail bindings may be edited without retargeting them.
            var current = ordinal < 0 ? Array.Empty<int>() : doc.Children(rows[ordinal]).ToArray();
            bool sameTarget = current.Chunk(2).Any(p => Equals(doc.ValueOf(p[0]), "node") && Equals(doc.ValueOf(doc.FirstChild(p[1])), name));
            if (OptInt(a, "renamePlacement") is int index)
            {
                var targets = ActionTargets(map.Scene.MapRoot).ToList();
                var node = targets.FirstOrDefault(t => t.Node.Index == index).Node;
                if (ordinal != -1 || node == null || node.ModelReference.Length == 0)
                    throw new ArgumentException("Only a placed model reference can be renamed for a new action.");
                if (name != name.Trim() || targets.Any(t => t.Node.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Choose an unused placement name without leading or trailing spaces.");
                string leaf = map.Namespace + "_GEO.ZED";
                var geo = ZedContainer.FromBytes(s.Package.Entry(leaf)!.Data);
                geo.RenameNode(index, name);
                changes[leaf] = geo.ToBytes();
            }
            else if (!sameTarget && ActionTargets(map.Scene.MapRoot).Count(t => t.Node.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) != 1)
                throw new ArgumentException("A new binding needs a uniquely named placed GEO node.");
            doc.WriteFields(ordinal == -1 ? doc.AppendList(list) : rows[ordinal], fields);
        }
        byte[] bytes;
        if (record == null)
        {
            reader.AddEntry(ActionRecord, doc.ToBytes());
            reader.RootValue++;
            bytes = reader.ToBytes();
        }
        else bytes = ZedSplice.Replace(entry.Data, reader.Nodes.ToList().IndexOf(record), doc.ToBytes());
        changes[entry.Leaf] = bytes;
        return ApplyEdit(s, Str(a, "label") ?? "Edit map action", _ => changes);
    }

    private static string ActionString(JsonElement value) =>
        value.GetString() is { Length: > 0 } text && text.All(c => c is >= ' ' and <= '~') ? text :
            throw new ArgumentException("Action names and references must be printable ASCII.");
}
