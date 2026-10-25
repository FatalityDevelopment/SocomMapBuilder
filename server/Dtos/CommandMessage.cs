namespace MapBuilder.Server
{
    /// <summary>
    /// One request to the map builder: the asset command name plus its arguments. Field names match the
    /// ASSET_* protocol that CodeDesigner's Bridge used, so the web client sends the same shapes.
    /// </summary>
    public class CommandMessage
    {
        public string Command { get; set; } = string.Empty;
        /// <summary>Path to a .ZDB or an unpacked package folder (ASSET_OPEN).</summary>
        public string? InputPath { get; set; }
        public string? OutputPath { get; set; }
        /// <summary>Session id returned by ASSET_OPEN.</summary>
        public string? AssetSession { get; set; }
        public string? AssetArchive { get; set; }
        /// <summary>Asset name: a model record, a .tif, a scene path.</summary>
        public string? AssetName { get; set; }
        /// <summary>Command arguments as a JSON object string.</summary>
        public string? AssetJson { get; set; }
    }
}
