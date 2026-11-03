using System.Text.Json;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// The ASSET_EDIT ops as data: each op's fields with their JSON schema. One table gives assets_edit its
    /// <c>op</c> schema (a <c>oneOf</c> with a branch per op) and checks an op before it runs: unknown fields
    /// (a typo like <c>postion</c>), missing required fields and wrong types are errors. AssetService still checks
    /// the values themselves (ranges, vector lengths, names that exist).
    /// </summary>
    internal static class EditOps
    {
        internal sealed record Spec(string Op, string Summary, string[] Required, (string Name, Dictionary<string, object> Schema)[] Fields);

        private static (string, Dictionary<string, object>) F(string name, string type, string description) =>
            (name, new Dictionary<string, object> { ["type"] = type, ["description"] = description });

        private static (string, Dictionary<string, object>) Int(string name, string description, int? min = null, int? max = null)
        {
            var d = new Dictionary<string, object> { ["type"] = "integer", ["description"] = description };
            if (min is { } lo) d["minimum"] = lo;
            if (max is { } hi) d["maximum"] = hi;
            return (name, d);
        }

        private static (string, Dictionary<string, object>) Vec(string name, int n, string description) =>
            (name, new Dictionary<string, object>
            {
                ["type"] = "array", ["description"] = description, ["items"] = new Dictionary<string, object> { ["type"] = "number" },
                ["minItems"] = n, ["maxItems"] = n,
            });

        private static (string, Dictionary<string, object>) Arr(string name, string itemType, string description) =>
            (name, new Dictionary<string, object> { ["type"] = "array", ["description"] = description, ["items"] = new Dictionary<string, object> { ["type"] = itemType } });

        private static (string, Dictionary<string, object>) OneOf(string name, string description, params string[] values) =>
            (name, new Dictionary<string, object> { ["type"] = "string", ["description"] = description, ["enum"] = values });

        // A number, or [x, y, z] per axis.
        private static (string, Dictionary<string, object>) Scale3(string description) =>
            ("scale", new Dictionary<string, object> { ["description"] = description });

        private static readonly (string, Dictionary<string, object>)[] Placement =
        {
            Vec("position", 3, "World [x, y, z] (y is height)."),
            F("yaw", "number", "Degrees about Y."),
            new("scale", new Dictionary<string, object> { ["type"] = "number", ["description"] = "Uniform scale, > 0.", ["exclusiveMinimum"] = 0 }),
            Vec("matrix", 16, "A full column-major transform instead of position / yaw / scale."),
        };

        private static readonly (string, Dictionary<string, object>)[] VertexTransform =
        {
            Vec("translate", 3, "[x, y, z] model units."),
            Vec("rotate", 3, "[x, y, z] degrees, applied X then Y then Z."),
            Scale3("A number or [x, y, z]; not 0."),
            Vec("pivot", 3, "Centre of rotation and scale (default: the centre of the affected vertices)."),
            Vec("matrix", 16, "A column-major matrix instead of translate / rotate / scale."),
        };

        private static readonly (string, Dictionary<string, object>) Archive = F("archive", "string", "The model's *_MDL.ZED leaf.");
        private static readonly (string, Dictionary<string, object>) Model = F("model", "string", "The model (assets_list_models).");
        private static readonly (string, Dictionary<string, object>) Scene = F("scene", "string", "GEO namespace (default: the map's).");
        private static readonly (string, Dictionary<string, object>) Image1 = F("pngBase64", "string", "The image as base64 PNG.");
        private static readonly (string, Dictionary<string, object>) Image2 = F("imagePath", "string", "A PNG file (instead of pngBase64).");
        private static readonly (string, Dictionary<string, object>) Materials = Arr("materials", "object",
            "Instead of an image: up to 8 [{color: [r, g, b]} | {light, dark}], a 64x64 skin of shaded strips that addMesh shapes' material indices pick.");

        private static readonly (string, Dictionary<string, object>)[] MeshInput =
        {
            Arr("shapes", "object", "[{kind: lathe {profile: [[along, radius]], caps?} | cylinder {start, end, radius, radiusEnd?, caps?} | ring {at, inner, outer, thickness?} | " +
                "sphere {radius, scale?, rings?} | box {min, max, lean?, radius? (rounded edges), bevelSegments? (1-4), puff? (bulge the faces' middles: a stuffed pouch), divisions?, faceUv?: {\"-z\": rect, ...}} | " +
                "strap {path: [[x, y, z]], width, thickness?, around?: [x, y, z] (wraps round it) | outward?: [x, y, z], steps? (curve pieces per span), caps?}, " +
                "material (0-7), uvRect?: [u0, v0, u1, v1] (map into that rectangle of a painted texture instead of the material strip; v up), subdivide?: 0-2 (smooth it, 4× triangles a level; edges sharper than crease?: 60° stay sharp), noise?: amount | {amount, scale?: 0.4, seed?} (wrinkles along the normals), axis?: x|y|z (default x), center?, segments?, squash?: [u, w]}]."),
            Vec("light", 3, "shapes: the light the shades are baked from (default [0.35, 0.85, 0.45])."),
            F("obj", "string", "OBJ text."), F("objPath", "string", "An OBJ file (instead of obj)."), F("yUp", "boolean", "OBJ is Y up (default true)."),
            F("scale", "number", "OBJ scale."), Vec("offset", 3, "OBJ offset."), F("primitive", "object", "{kind: cube | plane, size?, center?}."),
            F("mesh", "object", "{positions, triangles, normals?, uvs?, colors?}."), F("meshPath", "string", "A JSON file holding mesh (instead of mesh).")
        };

        private static (string, Dictionary<string, object>)[] With(IEnumerable<(string, Dictionary<string, object>)> a, params (string, Dictionary<string, object>)[] b) => a.Concat(b).ToArray();

        internal static readonly Spec[] All =
        {
            new("moveClutter", "Move a clutter placement.", new[] { "clutterIndex" }, With(Placement, Int("clutterIndex", "From assets_map_scene.", 0))),
            new("addClutter", "Add a clutter placement (answers newClutterIndex).", new[] { "model" }, With(Placement, F("model", "string", "A package model."))),
            new("deleteClutter", "Remove a clutter placement.", new[] { "clutterIndex" }, new[] { Int("clutterIndex", "From assets_map_scene.", 0) }),
            new("moveInstance", "Move a placed scene node (world space).", new[] { "geoIndex" }, With(Placement, Int("geoIndex", "From assets_map_scene.", 0), Scene)),
            new("duplicateInstance", "Copy a scene node as a sibling (answers newGeoIndex).", new[] { "geoIndex" },
                With(Placement, Int("geoIndex", "From assets_map_scene.", 0), Scene, F("name", "string", "Name of the copy."))),
            new("placeInstance", "Place any package model as a new reference, copied from the nearest placed one (answers newGeoIndex).", new[] { "model" },
                With(Placement, F("model", "string", "A package model."), F("name", "string", "Name of the new node."),
                    Int("templateGeoIndex", "The placed reference to copy (default: the nearest).", 0), Scene)),
            new("deleteInstance", "Remove a scene node (never the worldmodel).", new[] { "geoIndex" }, new[] { Int("geoIndex", "From assets_map_scene.", 0), Scene }),
            new("movePoint", "Move an AIMAPS point (snaps to its grid cell).", new[] { "name", "position" },
                new[] { F("name", "string", "From assets_map_points."), Vec("position", 3, "World [x, y, z]; y is ignored.") }),
            new("setPoint", "Rename, retype, change the team of or move an AIMAPS point.", new[] { "name" },
                new[] { F("name", "string", "From assets_map_points."), F("newName", "string", "2-15 printable characters, unique."),
                    Int("team", "Team byte.", 0, 255), OneOf("kind", "Point kind.", "start", "waypoint", "other"), Vec("position", 3, "World [x, y, z].") }),
            new("placePrefab", "Place a saved prefab around a position (one undo step).", new[] { "prefab", "position" },
                new[] { F("prefab", "string", "From assets_list_prefabs."), Vec("position", 3, "World [x, y, z] of the prefab's origin."), F("yaw", "number", "Degrees about Y.") }),
            new("replaceTexture", "Replace a texture's pixels from an image (or a materials skin).", new[] { "texture" },
                new[] { F("texture", "string", "The .tif."), Image1, Image2, Materials, F("namespace", "string", "TXR namespace to prefer.") }),
            new("paintTexture", "Colour where a model draws a texture, by body part (a character's bones, else the model's visuals; assets_uv_layout shows them): a tint keeps the texture's shading and detail, fill replaces it.",
                new[] { "texture", "model", "regions" },
                new[] { F("texture", "string", "The .tif to paint."), F("model", "string", "The model whose parts pick the places (MESH_seal_A_woodland_LO)."),
                    F("archive", "string", "The model's *_MDL.ZED (default: found by name)."),
                    Arr("regions", "object", "[{parts: [bone or visual names] | all: true, color: [r, g, b] 0-255, fill?: false (tint, keeping shading), strength?: 0-1 (default 1), " +
                        "uv?: [u0, v0, u1, v1] (only there: part of a bone's area, such as the lower face; assets_uv_layout gives each part's bounds)}], applied in order."),
                    F("namespace", "string", "TXR namespace to prefer (default the model's).") }),
            new("retexture", "Point a model's sections at another texture.", new[] { "archive", "model", "from", "to" },
                new[] { Archive, Model, F("from", "string", "The texture now named."), F("to", "string", "The texture to name (must exist)."), F("visual", "string", "Only this visual.") }),
            new("tintVertexColors", "Multiply / add the baked vertex colours.", new[] { "archive", "model" },
                new[] { Archive, Model, Vec("multiply", 3, "[r, g, b] factors."), Vec("add", 3, "[r, g, b] added (0-255)."), F("visual", "string", "Only this visual."),
                    F("texture", "string", "Only sections with this texture."), F("alpha", "number", "Set alpha (0-255).") }),
            new("transformVertices", "Move / rotate / scale vertices.", new[] { "archive", "model" },
                With(VertexTransform, Archive, Model, Arr("chunks", "integer", "Only these chunks (geometry offsets)."),
                    F("vertices", "object", "{geometryOffset: [vertex indices]} to move only these."))),
            new("setVertices", "Set vertex positions / uvs / colours / normals.", new[] { "archive", "model" },
                new[] { Archive, Model, Int("geometryOffset", "The chunk (with vertices)."), Arr("vertices", "object", "[{i, position?, uv?, color?, normal?}]."),
                    Arr("chunks", "object", "[{geometryOffset, vertices}] for several chunks.") }),
            new("deleteFaces", "Delete triangles.", new[] { "archive", "model", "faces" },
                new[] { Archive, Model, F("faces", "object", "{geometryOffset: [triangle indices]}.") }),
            new("duplicateFaces", "Copy triangles, transformed.", new[] { "archive", "model", "faces" },
                With(VertexTransform, Archive, Model, F("faces", "object", "{geometryOffset: [triangle indices]}."))),
            new("addMesh", "Add geometry to a model visual: shapes, OBJ, raw arrays or a primitive.", new[] { "archive", "model", "visual", "texture" },
                new[] { Archive, Model, F("visual", "string", "An N<node>_<visual> key."), F("texture", "string", "The texture to use (for shapes: a materials skin, addTexture materials)."),
                    F("replace", "boolean", "Replace the visual's geometry.") }.Concat(MeshInput).ToArray()),
            new("addCharacterMesh", "Add geometry to a skinned character (MESH_*) as a new material section; positions in the character's bind pose (model space). With a bone it moves rigidly with that bone (a helmet, a pack); without, each vertex takes the skin weights of the nearest body vertex, so it bends with the body under it (sleeves, a coat, knee pads across a joint).",
                new[] { "archive", "model", "texture" },
                new[] { Archive, Model, F("bone", "string", "Optional: a skeleton bone (assets_describe_model skeleton) to follow rigidly; vertices stay within 8 units of it."),
                    F("texture", "string", "A texture in the character's TXR namespace (addTexture materials for shapes), at most 15 characters."),
                    F("lowDetail", "boolean", "Also add it to the character's lower-detail mesh (MESH_x_1 / MESH_x_1_LO) when there is one (default true)."),
                    F("conform", "object", "true | {offset? (default 0.06), reach? (default 0.5)}: first push vertices behind or within offset of the body's surface out to offset above it, each carrying its piece within reach along, so a vest panel or band takes the chest's curve instead of floating or cutting in.") }.Concat(MeshInput).ToArray()),
            new("scaleBones", "Grow or thin a skinned character's body parts: each named bone's vertex parts scaled in that bone's own frame about its joint, so the change fades across joints with no seam. Use this to reshape a body (thicker arms, broader chest, bigger head). On limbs and the spine use thickness (scale and length also stretch the part past the next joint); scale suits end bones (head, hands). The lower-detail mesh follows.",
                new[] { "archive", "model", "bones" },
                new[] { Archive, Model,
                    F("bones", "object", "{bone: scale | {scale?, thickness? (across the bone), length? (along it, towards its children)}}, e.g. {rbicep: {thickness: 1.3}, head: 0.9}. Bones from assets_describe_model skeleton."),
                    F("lowDetail", "boolean", "Also scale the lower-detail mesh (default true).") }),
            new("transformCharacter", "Move / rotate / scale a skinned character's vertices in its bind pose (model space): all, or a section, a texture or the vertices whose heaviest bone is listed. The selection has a hard edge, so a partial selection steps at its border: to reshape body parts use scaleBones. Skinning stays; the lower-detail mesh follows.",
                new[] { "archive", "model" },
                With(VertexTransform, Archive, Model, Arr("bones", "string", "Only vertices whose heaviest bone is one of these (assets_describe_model skeleton)."),
                    F("texture", "string", "Only sections drawn with this texture."), Int("section", "Only this section.", 0),
                    F("lowDetail", "boolean", "Also transform the lower-detail mesh (default true)."))),
            new("removeCharacterSection", "Remove material sections from a skinned character (MESH_*): those drawn with a texture (e.g. gear added earlier), or one by number.",
                new[] { "archive", "model" },
                new[] { Archive, Model, F("texture", "string", "Remove every section drawn with this texture."),
                    Int("section", "Or this section (assets_describe_model chunks' visual sectionN).", 0) }),
            new("hideVisual", "Hide a model visual (it keeps one zero-area triangle; addMesh can fill it again).", new[] { "archive", "model", "visual" },
                new[] { Archive, Model, F("visual", "string", "An N<node>_<visual> key.") }),
            new("newWeapon", "Copy a weapon (its visuals and GEO definition: LODs, firepoint, aimpoint) under a new name, to reshape with addMesh / hideVisual.", new[] { "template", "name" },
                new[] { F("template", "string", "The weapon to copy (e.g. desert_eagle)."), F("name", "string", "The new weapon."), F("archive", "string", "The template's *_MDL.ZED (default: found by name).") }),
            new("newProp", "Copy a map prop (its visuals and GEO definition) under a new name, to reshape with addMesh / hideVisual, fitBounds, and place with placeInstance.",
                new[] { "template", "name" },
                new[] { F("template", "string", "The prop to copy (assets_list_models kind prop, e.g. a crate)."),
                    F("name", "string", "The new prop: not ending in _<digits> or _LO, which scene names drop when they resolve."),
                    F("archive", "string", "The template's *_MDL.ZED (default: found by name).") }),
            new("fitBounds", "Refit a model's bounds to its geometry: its GEO definition and every placed reference of it (after reshaping).", new[] { "model" },
                new[] { Model }),
            new("importModel", "Copy a model (with its textures, palettes and definition) from another package.", new[] { "archive", "name" },
                new[] { F("source", "string", "The other package's path."), F("sourceSession", "string", "Or an open session."),
                    F("archive", "string", "The source model's *_MDL.ZED."), F("name", "string", "The source model."),
                    F("targetArchive", "string", "Where it goes here."), F("newName", "string", "A new name.") }),
            new("importTexture", "Copy a texture (and its palette) from another package.", new[] { "name" },
                new[] { F("source", "string", "The other package's path."), F("sourceSession", "string", "Or an open session."), F("name", "string", "The source .tif."),
                    F("sourceNamespace", "string", "Its TXR namespace."), F("namespace", "string", "Where it goes here."), F("newName", "string", "A new name.") }),
            new("addTexture", "Create a texture from an image, or a material-strip skin for addMesh shapes.", new[] { "name" },
                new[] { F("name", "string", "The new .tif (at most 15 characters for retail models)."), Image1, Image2, Materials, F("namespace", "string", "TXR namespace."),
                    Int("width", "Power of two, 8-512.", 8, 512), Int("height", "Power of two, 8-512.", 8, 512), Int("maxSize", "Longest side when rounding (default 256).", 8, 512),
                    Int("paletteId", "Reuse this palette.", 0) }),
            new("addPalette", "Create a 256-colour palette from an image.", Array.Empty<string>(),
                new[] { Image1, Image2, F("namespace", "string", "PAL namespace."), F("is16", "boolean", "16-bit (default: 32-bit when the image has soft alpha).") }),
            new("setPalette", "Set a palette's 256 colours in place, keeping its depth.", new[] { "archive", "id" },
                new[] { F("archive", "string", "The *_PAL.ZED leaf."), Int("id", "The palette (texpal_<id>).", 0),
                    Vec("rgba", 1024, "256 RGBA colours, 0-255 each (or pngBase64 / imagePath: the image's first 256 pixels)."), Image1, Image2 }),
            new("addAnimation", "Add a package clip copied from another clip.", new[] { "name" },
                new[] { F("name", "string", "The new clip."), F("from", "string", "The clip to copy (default seal_stand)."), F("fromSource", "string", "package or shared."),
                    F("after", "string", "Insert after this clip."), Int("frames", "Resample to this many frames.", 1), F("duration", "number", "Seconds, 0-600.") }),
            new("deleteAnimation", "Delete a package clip.", new[] { "name" }, new[] { F("name", "string", "The clip.") }),
            new("renameAnimation", "Rename a package clip.", new[] { "name", "newName" }, new[] { F("name", "string", "The clip."), F("newName", "string", "Its new name.") }),
            new("setAnimation", "Retime a clip and / or replace bone tracks.", new[] { "name" },
                new[] { F("name", "string", "The clip."), Int("frames", "Resample to this many frames.", 1), F("duration", "number", "Seconds, 0-600."),
                    Arr("bones", "object", "[{name, positions?: [[x,y,z]], rotations?: [[x,y,z,w]]}]: 1 key or one per frame.") }),
            new("setAnimationKey", "Key one bone at a frame (or every frame).", new[] { "name", "bone" },
                new[] { F("name", "string", "The clip."), F("bone", "string", "The bone."), Int("frame", "The frame.", 0), F("allFrames", "boolean", "Key every frame (a static track)."),
                    Vec("rotation", 4, "[x, y, z, w] quaternion."), Vec("position", 3, "[x, y, z] units.") }),
            new("batch", "Several ops as one undo step (all or nothing).", new[] { "ops" },
                new (string, Dictionary<string, object>)[] { ("ops", new Dictionary<string, object>
                {
                    ["type"] = "array", ["minItems"] = 1, ["description"] = "The ops, each an object like this one's branches.",
                    ["items"] = new Dictionary<string, object> { ["type"] = "object" },
                }) }),
        };

        private static readonly Dictionary<string, Spec> ByName = All.ToDictionary(s => s.Op, StringComparer.Ordinal);

        // Fields every op takes.
        private static readonly (string Name, Dictionary<string, object> Schema)[] Common =
        {
            F("label", "string", "Undo label."),
            F("dryRun", "boolean", "Check and report what the edit would do, changing nothing."),
        };

        private static Dictionary<string, Dictionary<string, object>> Properties(Spec spec)
        {
            var props = new Dictionary<string, Dictionary<string, object>> { ["op"] = new() { ["const"] = spec.Op } };
            foreach (var (name, schema) in spec.Fields.Concat(Common)) props[name] = schema;
            return props;
        }

        /// <summary>The assets_edit <c>op</c> schema: one closed object branch per op, told apart by <c>op</c>.</summary>
        internal static Dictionary<string, object> Schema() => new()
        {
            ["type"] = "object",
            ["description"] = "The edit: an object whose \"op\" names one of the branches below.",
            ["required"] = new[] { "op" },
            ["oneOf"] = All.Select(spec => new Dictionary<string, object>
            {
                ["type"] = "object",
                ["title"] = spec.Op,
                ["description"] = spec.Summary,
                ["properties"] = Properties(spec),
                ["required"] = spec.Required.Prepend("op").ToArray(),
                ["additionalProperties"] = false,
            }).ToList(),
        };

        /// <summary>Null when <paramref name="op"/> (and every op of a batch) fits its branch; else the error.</summary>
        internal static string? Check(JsonElement op, string where = "op")
        {
            if (op.ValueKind != JsonValueKind.Object) return $"{where} must be an object";
            string? name = op.TryGetProperty("op", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (name == null) return $"{where} needs \"op\" (one of {string.Join(", ", All.Select(s => s.Op))})";
            if (!ByName.TryGetValue(name, out var spec)) return $"{where}: unknown op \"{name}\" (one of {string.Join(", ", All.Select(s => s.Op))})";
            var props = Properties(spec);
            foreach (var f in op.EnumerateObject())
                if (!props.ContainsKey(f.Name))
                    return $"{where} {name}: unknown field \"{f.Name}\" (takes {string.Join(", ", props.Keys.Where(k => k != "op").OrderBy(k => k))})";
            foreach (string r in spec.Required)
                if (!op.TryGetProperty(r, out var v) || v.ValueKind == JsonValueKind.Null) return $"{where} {name}: \"{r}\" is required";
            foreach (var f in op.EnumerateObject())
            {
                if (f.Name == "op" || f.Value.ValueKind == JsonValueKind.Null) continue;
                if (McpServer.CheckValue(f.Name, props[f.Name], f.Value) is { } error) return $"{where} {name}: {error}";
            }
            // The vertex ops' scale has no single type: a number or [x, y, z].
            if (op.TryGetProperty("scale", out var sc) && !props["scale"].ContainsKey("type") && sc.ValueKind is not (JsonValueKind.Number or JsonValueKind.Array))
                return $"{where} {name}: \"scale\" is a number or [x, y, z]";
            if (name == "batch")
            {
                int i = 0;
                foreach (var inner in op.GetProperty("ops").EnumerateArray())
                    if (Check(inner, $"{where}.ops[{i++}]") is { } error) return error;
            }
            return null;
        }
    }
}
