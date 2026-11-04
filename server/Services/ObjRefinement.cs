using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;

namespace MapBuilder.Server.Services;

internal static class ObjRefinement
{
    private sealed record Brush(float[] Center, float[] Radius, float[] Scale);
    private sealed record Options(int Levels = 1, float Crease = 105, float Strength = 0.75f, Brush[]? Brushes = null);

    internal static string Run(CommandMessage request)
    {
        var options = JsonSerializer.Deserialize<Options>(request.AssetJson ?? "{}", new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new ArgumentException("Expected refinement options");
        if (options.Levels is < 1 or > 2 || options.Crease is < 0 or > 180 || options.Strength is < 0 or > 1)
            throw new ArgumentException("Use 1–2 levels, a crease angle of 0–180 degrees and a strength of 0–1.");

        const int maxChars = 20_000_000;
        string source = request.Data ?? (request.InputPath is { } path && new FileInfo(path).Length <= maxChars
            ? File.ReadAllText(path) : throw new ArgumentException("Supply an OBJ file of at most 20 MB."));
        if (source.Length > maxChars) throw new ArgumentException("The OBJ is too large (20 MB limit).");
        int children = 1 << (2 * options.Levels);
        var directives = new Dictionary<int, List<string>>();
        var mesh = MeshTopology.ParseObj(source, onDirective: (face, line) =>
        {
            int index = face * children;
            if (!directives.TryGetValue(index, out var lines)) directives[index] = lines = new();
            lines.Add(line);
        });
        int before = mesh.Triangles.Count;
        if (before == 0 || before * (long)children > 250_000)
            throw new ArgumentException("Expected faces and at most 250,000 output triangles. Try fewer levels or a smaller mesh.");

        if (options.Brushes is { } brushes)
        {
            if (brushes.Length > 32) throw new ArgumentException("Use at most 32 shaping brushes.");
            foreach (var brush in brushes) Shape(mesh, brush);
        }
        mesh = MeshRefine.Refine(mesh, options.Levels, options.Crease, null, strength: options.Strength);
        var obj = new StringBuilder("# Rounded with MapBuilder\n");
        void Attribute(string tag, float[] values)
        {
            obj.Append(tag);
            foreach (float value in values)
            {
                if (!float.IsFinite(value)) throw new ArgumentException("The mesh contains an out-of-range coordinate.");
                obj.Append(' ').Append(value.ToString("R", CultureInfo.InvariantCulture));
            }
            obj.Append('\n');
        }
        foreach (var p in mesh.Positions) Attribute("v", p);
        foreach (var uv in mesh.Uvs) Attribute("vt", uv);
        foreach (var n in mesh.Normals) Attribute("vn", n);
        for (int i = 0; i < mesh.Triangles.Count; i++)
        {
            if (directives.TryGetValue(i, out var lines))
                foreach (string line in lines) obj.AppendLine(line);
            var (a, b, c) = mesh.Triangles[i];
            obj.Append('f');
            foreach (int vertex in new[] { a + 1, b + 1, c + 1 })
                obj.Append(' ').Append(vertex).Append('/').Append(vertex).Append('/').Append(vertex);
            obj.Append('\n');
        }
        string result = obj.ToString();
        if (request.OutputPath is { } output)
        {
            if (!string.Equals(Path.GetExtension(output), ".obj", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The output must be a new .obj file.");
            string temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetRandomFileName());
            try
            {
                File.WriteAllText(temp, result);
                File.Move(temp, output); // Never replace the input or an existing output.
            }
            finally { File.Delete(temp); }
        }
        return Responses.Ok(request.Command, new { before, triangles = mesh.Triangles.Count, request.OutputPath, obj = request.OutputPath == null ? result : null });
    }

    // Scale around a local pivot. Smoothstep falloff brings the displacement and its slope to zero at the rim.
    private static void Shape(MeshTopology.InputMesh mesh, Brush brush)
    {
        if (brush?.Center is not { Length: 3 } || brush.Radius is not { Length: 3 } || brush.Scale is not { Length: 3 }
            || brush.Radius.Any(r => r <= 0) || brush.Scale.Any(s => s <= 0))
            throw new ArgumentException("Each brush needs XYZ center, positive XYZ radius and positive XYZ scale.");
        foreach (var p in mesh.Positions)
        {
            float d2 = 0;
            for (int k = 0; k < 3; k++)
            {
                float delta = (p[k] - brush.Center[k]) / brush.Radius[k];
                d2 += delta * delta;
            }
            if (d2 >= 1) continue;
            float t = 1 - MathF.Sqrt(d2), weight = t * t * (3 - 2 * t);
            for (int k = 0; k < 3; k++) p[k] += (p[k] - brush.Center[k]) * (brush.Scale[k] - 1) * weight;
        }
    }
}
