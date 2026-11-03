using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;
using CodeDesigner.Ps2.FileFormats.Zipper.RDR;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace MapBuilder.Server.Services
{
    // Custom weapons as memory-card packs: a donor weapon's definition (zweapon.rdr, the armory's language table) and
    // the pack that gives it a session's model, icon, name and stats (WeaponPack).
    internal sealed partial class ZipperAssetService
    {
        /// <summary>
        /// ASSET_WEAPON_DEFINITION {donor, socom2Run?}: the donor's weapon table fields, its ammo's fields and the
        /// language entries a pack would rewrite (its short name and captions).
        /// </summary>
        private static string WeaponDefinition(string? json)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var a = doc.RootElement;
                string donor = Str(a, "donor") ?? throw new ArgumentException("\"donor\" (a weapon of zweapon.rdr) is required");
                var (_, run) = Socom2TemplatesFor(Str(a, "socom2Run"));
                var table = new WeaponTable(RecordOf(Path.Combine(run, "ZWEAPON.ZAR"), "zweapon.rdr"));
                int w = table.FindWeapon(donor);
                var fields = table.Fields(w);
                var ammo = table.AmmoOf(w).Select(n => new { name = n, fields = table.Fields(table.FindAmmo(n)) }).ToList();
                var lang = new LanguageTable(RecordOf(Path.Combine(run, "LOCALE", "STATES", "UIMNLOC.ZAR"), ".rdr"));
                string? name = fields.GetValueOrDefault("DisplayName") as string, description = fields.GetValueOrDefault("Description") as string;
                var texts = new Dictionary<string, string>();
                if (name != null) foreach (string k in lang.KeysWithText(name)) texts[k] = lang.Text(k) ?? string.Empty;
                if (description != null) foreach (var (k, _) in lang.Captions(description)) texts[k] = lang.Text(k) ?? string.Empty;
                // atom: the donor's record in zweapon.rdr (the Asset Viewer opens the table there).
                return Ok("ASSET_WEAPON_DEFINITION", new { donor, atom = w, fields, ammo, texts, run });
            }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException or InvalidDataException or IOException or JsonException)
            {
                return Responses.Error($"ASSET_WEAPON_DEFINITION error: {ex.Message}");
            }
        }

        /// <summary>
        /// ASSET_WEAPON_PACK {model, donor, pack, name?, caption?, description?, stats?, ammo?, texts?, icon?, outputDir?,
        /// folderCard?, socom2Run?, dryRun?}: the session's weapon as a memory-card pack (see WeaponPack).
        /// </summary>
        private static object WeaponPackOf(AssetSession s, string? json)
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var a = doc.RootElement;
            var (templates, run) = Socom2TemplatesFor(Str(a, "socom2Run"));
            var options = new WeaponPack.Options
            {
                Pack = Str(a, "pack") ?? throw new ArgumentException("\"pack\" (m + up to 6 letters/digits, e.g. mrgun) is required"),
                Model = Str(a, "model") ?? throw new ArgumentException("\"model\" (the session's weapon) is required"),
                Donor = Str(a, "donor") ?? throw new ArgumentException("\"donor\" (the weapon it takes over, e.g. F57) is required"),
                Name = Str(a, "name"), Caption = Str(a, "caption"), Description = Str(a, "description"),
                Stats = Scalars(a, "stats"), Ammo = Scalars(a, "ammo"),
                Texts = a.TryGetProperty("texts", out var t) && t.ValueKind == JsonValueKind.Object
                    ? t.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty) : new(),
                Icon = !(a.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.False),
            };
            var r = WeaponPack.Build(s.Package, options, templates);
            string outputDir = Str(a, "outputDir") ?? DefaultExportDir();
            bool dryRun = a.TryGetProperty("dryRun", out var dr) && dr.ValueKind == JsonValueKind.True;
            var written = dryRun ? new List<string>() : WeaponPack.Write(r, options.Pack, outputDir, Str(a, "folderCard"));
            return new
            {
                dryRun, pack = options.Pack.ToUpperInvariant(), run, written,
                files = r.Files.Select(f => new { path = f.Path, size = f.Data.Length }),
                donor = r.DonorInternalName, oldName = r.OldName, oldDescription = r.OldDescription,
                fields = r.Fields, ammoType = r.AmmoType, texts = r.TextsChanged,
                textures = r.Textures, palettes = r.Palettes, icon = r.IconTexture, warnings = r.Warnings,
            };
        }

        private static Dictionary<string, object> Scalars(JsonElement a, string name)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            if (!a.TryGetProperty(name, out var o) || o.ValueKind != JsonValueKind.Object) return d;
            foreach (var p in o.EnumerateObject())
                d[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.Number => p.Value.TryGetInt32(out int i) && !p.Value.GetRawText().Contains('.') ? i : p.Value.GetSingle(),
                    JsonValueKind.String => p.Value.GetString()!,
                    _ => throw new ArgumentException($"{name}.{p.Name} is a number or a string"),
                };
            return d;
        }

        private static byte[] RecordOf(string zarPath, string record)
        {
            if (!File.Exists(zarPath)) throw new FileNotFoundException($"{zarPath} is needed (SOCOM II's RUN folder)", zarPath);
            var zar = ZedContainer.FromBytes(File.ReadAllBytes(zarPath));
            var node = zar.Nodes.FirstOrDefault(n => record.StartsWith('.')
                ? n.Name.EndsWith(record, StringComparison.OrdinalIgnoreCase) : n.Name.Equals(record, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"{Path.GetFileName(zarPath)} has no {record}");
            return node.Data;
        }
    }
}
