using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;

namespace MapBuilder.Server.Services;

/// <summary>
/// ASSET_MAP_COLLISION: what is solid on a SOCOM II map (GEO <c>di</c> faces, docs/formats/collision.md), for the
/// Map Builder's Collision layer and for agents checking a map: counts, the placed props that have no faces, and the
/// faces in world space (in an area, up to a limit). With a model, that model's own faces in model space.
/// </summary>
internal sealed partial class ZipperAssetService
{
    private sealed record CollisionFilter(float[]? Area, string? Model, int? MaxFaces, bool? Faces);

    private static object MapCollision(AssetSession s, string? json)
    {
        var f = Parse<CollisionFilter>(json) ?? new CollisionFilter(null, null, null, null);
        var pkg = s.Package;
        if (f.Model is { Length: > 0 } modelName)
        {
            var model = pkg.ResolveModel(modelName) ?? throw new ArgumentException($"No model '{modelName}' in the package");
            var own = CollisionEdits.Faces(pkg, model);
            return new { model = model.Name, faceCount = own.Count, faces = own.Select(Row).ToList() };
        }
        if (f.Area is { } a && a.Length != 4) throw new ArgumentException("area is [minX, minZ, maxX, maxZ]");
        var map = pkg.MapScene() ?? throw new InvalidOperationException("The package has no map scene (no GEO with a worldmodel)");
        int max = Math.Clamp(f.MaxFaces ?? 6000, 0, 60000);

        int worldFaces = 0, propFaces = 0, worldNodes = 0, propNodes = 0;
        var faces = new List<object>();
        bool truncated = false;
        var seen = new HashSet<int>();
        foreach (var (node, face, world) in CollisionEdits.WorldFaces(map.Scene))
        {
            bool prop = node.ModelReference.Length > 0;
            if (seen.Add(node.Index)) { if (prop) propNodes++; else worldNodes++; }
            if (prop) propFaces++; else worldFaces++;
            if (f.Faces == false || f.Area is { } area && !Centred(world, area)) continue;
            if (faces.Count >= max) { truncated = true; continue; }
            faces.Add(new
            {
                geoIndex = node.Index, node = node.Name, kind = prop ? "prop" : "world",
                material = face.Profile.Material, up = face.Normal()[1] > 0.01f,
                points = world.Select(v => MathF.Round(v, 3)).ToArray(),
            });
        }

        // Placed props with nothing solid: walked and shot through in game.
        var bare = new List<object>();
        void Visit(GeoNode n)
        {
            if (n.IsSceneNode && n.ModelReference.Length > 0 && n.Child("di") == null && pkg.ResolveModel(n.ModelReference) is { } m
                && m.Kind is not ("character" or "weapon"))
            {
                var t = n.WorldTransform;
                if (f.Area is not { } ar || t[12] >= ar[0] && t[12] <= ar[2] && t[14] >= ar[1] && t[14] <= ar[3])
                    bare.Add(new { geoIndex = n.Index, node = n.Name, model = m.Name, position = new[] { t[12], t[13], t[14] } });
            }
            foreach (var c in n.SceneChildren) Visit(c);
        }
        Visit(map.Scene.MapRoot);
        return new
        {
            world = new { nodes = worldNodes, faces = worldFaces },
            props = new { nodes = propNodes, faces = propFaces },
            withoutCollision = bare.Take(200).ToList(),
            withoutCollisionCount = bare.Count,
            faces,
            truncated,
        };
    }

    private static object Row(GeoCollision.Face face) => new
    {
        material = face.Profile.Material, up = face.Normal()[1] > 0.01f, points = face.Points.Select(v => MathF.Round(v, 3)).ToArray(),
    };

    private static bool Centred(float[] world, float[] area)
    {
        int n = world.Length / 3;
        float x = 0, z = 0;
        for (int i = 0; i < n; i++) { x += world[i * 3]; z += world[i * 3 + 2]; }
        x /= n; z /= n;
        return x >= area[0] && x <= area[2] && z >= area[1] && z <= area[3];
    }
}
