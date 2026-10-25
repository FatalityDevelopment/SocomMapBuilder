namespace MapBuilder.Server
{
    /// <summary>
    /// One request to the backend: the command name plus its arguments. Field names match the protocol CodeDesigner's
    /// Bridge used (ASSET_*, ZIPPER_*), so the web pages send the same shapes.
    /// </summary>
    public class CommandMessage
    {
        public string Command { get; set; } = string.Empty;
        /// <summary>Path to a .ZDB, .CDB, .ZAR or .ZED, or an unpacked package folder.</summary>
        public string? InputPath { get; set; }
        public string? OutputPath { get; set; }
        public string? OutputDirectory { get; set; }
        public string? FilePath { get; set; }
        /// <summary>Inline payload (base64 bytes for ZIPPER_INSPECT_RDR_BYTES).</summary>
        public string? Data { get; set; }
        public string? Query { get; set; }

        // ── Asset sessions (ASSET_*) ──
        /// <summary>Session id returned by ASSET_OPEN.</summary>
        public string? AssetSession { get; set; }
        /// <summary>Root archive leaf inside the package (e.g. "M51_MDL.ZED").</summary>
        public string? AssetArchive { get; set; }
        /// <summary>Asset name: a model record, a .tif, a scene path.</summary>
        public string? AssetName { get; set; }
        /// <summary>Command arguments as a JSON object string.</summary>
        public string? AssetJson { get; set; }

        // ── Asset Viewer (ZIPPER_*) ──
        public int? ZipperEntryIndex { get; set; }
        public string? ZipperSection { get; set; }
        /// <summary>Token returned from ZIPPER_PRELOAD_MAP_PACKAGE.</summary>
        public string? ZipperPreloadToken { get; set; }

        // ── Weapons (ZIPPER_LIST_WEAPONS, ZIPPER_GET_WEAPON_TEMPLATE, ZIPPER_WRITE_WEAPON, …) ──
        /// <summary>Name of the source weapon entry inside the archive.</summary>
        public string? WeaponName { get; set; }
        /// <summary>Name to give the new weapon entry inside the archive.</summary>
        public string? WeaponNewName { get; set; }
        /// <summary>Base64 model bytes (.mdl payload) to add as a new entry.</summary>
        public string? WeaponModelData { get; set; }
        /// <summary>Base64 texture bytes (.tif payload) to add as a new entry.</summary>
        public string? WeaponTextureData { get; set; }
        /// <summary>Base64 palette bytes (CLUT) to add as a new entry; optional.</summary>
        public string? WeaponPaletteData { get; set; }
        /// <summary>Optional explicit texture-entry name when adding a sibling .tif.</summary>
        public string? WeaponTextureEntryName { get; set; }
        /// <summary>Optional explicit palette-entry name when adding a sibling palette.</summary>
        public string? WeaponPaletteEntryName { get; set; }
        /// <summary>Path to a PNG/JPG image used as the new texture source.</summary>
        public string? WeaponImagePath { get; set; }
        /// <summary>Optional inline base64 PNG bytes (alternative to WeaponImagePath).</summary>
        public string? WeaponImageData { get; set; }
        /// <summary>Optional JSON for mesh edits (vertex transform + per-chunk texture rebind).</summary>
        public string? WeaponMeshEditsJson { get; set; }
    }
}
