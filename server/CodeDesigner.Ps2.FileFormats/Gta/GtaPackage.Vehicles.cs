using System.Globalization;

namespace CodeDesigner.Ps2.FileFormats.Gta;

public sealed partial class GtaPackage
{
    private readonly List<byte[]> _carPalette = new();
    private readonly Dictionary<string, int[]> _carPaint = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (float Front, float Rear)> _wheelSizes = new(StringComparer.OrdinalIgnoreCase);

    public (float Front, float Rear) WheelSizes(Model model) => _wheelSizes.GetValueOrDefault(model.Name, (1f, 1f));

    private void RegisterWheelSizes(string[] cols)
    {
        if (float.TryParse(cols[12], CultureInfo.InvariantCulture, out var front) && float.TryParse(cols[13], CultureInfo.InvariantCulture, out var rear)
            && float.IsFinite(front) && float.IsFinite(rear) && front > 0 && rear > 0)
            _wheelSizes[cols[1]] = (front, rear);
    }

    private void LoadCarPaint(byte[] data)
    {
        foreach (var (section, cols) in Ide(data))
        {
            if (section == "col" && cols.Length >= 3)
            {
                // Preserve the slot's ordinal even when a custom palette includes a malformed row.
                _carPalette.Add(byte.TryParse(cols[0], out var r) && byte.TryParse(cols[1], out var g)
                    && byte.TryParse(cols[2], out var b) ? new[] { r, g, b } : new byte[] { 255, 255, 255 });
            }
            if (section is "car" or "car4")
            {
                int count = section == "car4" ? 4 : 2;
                if (cols.Length <= count) continue;
                var scheme = new int[count];
                if (Enumerable.Range(0, count).All(i => int.TryParse(cols[i + 1], out scheme[i])))
                    _carPaint[cols[0]] = scheme;
            }
        }
    }

    // Preview the first paint scheme in CARCOLS. The DFF encodes each slot as a special RGB colour.
    private byte[] PreviewMaterialColor(Model model, byte[] rgba)
    {
        if (model.Kind != "vehicle") return rgba;
        int slot = (rgba[0], rgba[1], rgba[2]) switch
        {
            (60, 255, 0) => 0, (255, 0, 175) => 1, (0, 255, 255) => 2, (255, 0, 255) => 3, _ => -1,
        };
        if (slot < 0) return rgba;
        if (_carPaint.TryGetValue(model.Name, out var scheme) && slot < scheme.Length && (uint)scheme[slot] < _carPalette.Count)
        {
            var rgb = _carPalette[scheme[slot]];
            return new[] { rgb[0], rgb[1], rgb[2], rgba[3] };
        }
        return new byte[] { 255, 255, 255, rgba[3] };
    }
}
