using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Assets;

namespace MapBuilder.Server.Services.Assets
{
    /// <summary>Shared answers of the ASSET_* commands, whatever game a session holds.</summary>
    internal static class AssetJson
    {
        internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

        internal sealed record ModelFilter(string? Kind, string? Query, string? Archive);

        internal sealed record MeshArgs(bool? Arrays);

        internal sealed record AnimationFilter(string? Query, string? Source);

        internal static string Ok(string command, object data) => Responses.Ok(command, data);

        internal static T? Parse<T>(string? json) where T : class =>
            string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, Options);

        internal static string SessionId(string path) =>
            $"{Path.GetFileNameWithoutExtension(path).ToLowerInvariant()}-{Guid.NewGuid().ToString("N")[..6]}";

        /// <summary>ASSET_MODEL_MESH for any game's <see cref="ModelMesh"/>: chunks with base64 arrays, skin, skeleton.</summary>
        internal static object Mesh(ModelMesh mesh, string archive, string name, string game, bool arrays, object? locators = null)
        {
            static string F(float[] a) { var bytes = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, bytes, 0, bytes.Length); return Convert.ToBase64String(bytes); }
            static string I(int[] a) { var bytes = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, bytes, 0, bytes.Length); return Convert.ToBase64String(bytes); }
            return new
            {
                archive, name, revision = 0, textureNamespace = (string?)null, game,
                boundsMin = mesh.BoundsMin, boundsMax = mesh.BoundsMax,
                chunks = mesh.Chunks.Select(c => new
                {
                    c.Index, c.GeometryOffset, c.Visual, node = c.Node, c.Texture, c.MaterialBlend, unit = c.Unit, c.Matrix, c.Scale, c.Origin,
                    c.VertexCount, triangleCount = c.Triangles.Length / 3, hasColors = c.Colors != null,
                    positions = arrays ? F(c.Positions) : null,
                    uvs = arrays ? F(c.Uvs) : null,
                    normals = arrays ? F(c.Normals) : null,
                    colors = arrays && c.Colors != null ? Convert.ToBase64String(c.Colors) : null,
                    triangles = arrays ? I(c.Triangles) : null,
                    skinBones = arrays && c.SkinBones != null ? I(c.SkinBones) : null,
                    skinWeights = arrays && c.SkinWeights != null ? F(c.SkinWeights) : null,
                    skinPositions = arrays && c.SkinPositions != null ? F(c.SkinPositions) : null,
                    skinNormals = arrays && c.SkinNormals != null ? F(c.SkinNormals) : null,
                }).ToList(),
                skinInfluences = mesh.Skinned ? ModelMesh.SkinInfluences : (int?)null,
                locators,
                skeleton = mesh.Skeleton?.Bones.Select(b => new { b.Name, b.MatrixId, b.Parent, b.GeoIndex, b.Local, b.World }).ToList(),
            };
        }
    }
}
