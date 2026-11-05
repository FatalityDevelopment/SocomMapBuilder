using System.Globalization;
using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Gta;

public sealed partial class GtaPackage
{
    public byte[] PlacementFileBytes(string file)
    {
        var rows = Placements.Where(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase)).ToDictionary(p => p.Row);
        var deleted = DeletedTextRows();
        if (_streamFiles.TryGetValue(file, out var stream))
        {
            var data = _imgByArchive[stream.Archive].Read(stream.Entry) ?? throw new KeyNotFoundException(stream.Entry);
            return WriteStream(data, rows, deleted);
        }
        var source = _disc.GetValueOrDefault(file) ?? throw new KeyNotFoundException($"{file} is not on the disc");
        return WriteText(source.Read(), file, rows, deleted);
    }

    private byte[] WriteStream(byte[] data, Dictionary<int, Placement> rows, Dictionary<string, int[]> deleted)
    {
        const int stride = 40;
        int count = BitConverter.ToInt32(data, 4), start = BitConverter.ToInt32(data, 28);
        int oldEnd = checked(start + stride * count);
        int delta = checked(stride * (rows.Count - count));
        // Preserve all other sections, including car generators, byte for byte. The header has six
        // (offset, reserved) pairs beginning at 28. Only sections following INST shift.
        var output = new byte[checked(data.Length + delta)];
        data.AsSpan(0, start).CopyTo(output);
        data.AsSpan(oldEnd).CopyTo(output.AsSpan(oldEnd + delta));
        BitConverter.TryWriteBytes(output.AsSpan(4), rows.Count);
        for (int i = 36; i <= 68; i += 8)
        {
            int offset = BitConverter.ToInt32(data, i);
            if (offset > 0 && offset >= oldEnd) BitConverter.TryWriteBytes(output.AsSpan(i), checked(offset + delta));
        }
        int at = start;
        foreach (var p in rows.Values.OrderBy(p => p.Row))
        {
            var record = output.AsSpan(at, stride);
            bool added = p.Row >= count;
            if (!added) data.AsSpan(start + p.Row * stride, stride).CopyTo(record);
            if (added || _placementOriginals.ContainsKey(Key(p.File, p.Row)))
            {
                var g = GameTransform(p.Matrix);
                float[] values = { g.X, g.Y, g.Z, g.Qx, g.Qy, g.Qz, g.Qw };
                for (int i = 0; i < values.Length; i++) BitConverter.TryWriteBytes(record[(4 * i)..], values[i]);
            }
            BitConverter.TryWriteBytes(record[28..], p.Id);
            BitConverter.TryWriteBytes(record[32..], p.Interior);
            BitConverter.TryWriteBytes(record[36..], SavedLod(p, deleted));
            at += stride;
        }
        return output;
    }

    private byte[] WriteText(byte[] data, string file, Dictionary<int, Placement> rows, Dictionary<string, int[]> deleted)
    {
        // Latin-1 is a byte-preserving view including any high bytes in comments.
        string text = Encoding.Latin1.GetString(data);
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n');
        var output = new StringBuilder(text.Length);
        string? section = null;
        int ordinal = 0;
        bool appended = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i], line = raw.Split('#')[0].Trim();
            if (line.Length > 0)
            {
                if (section == null) section = line.ToLowerInvariant();
                else if (line.Equals("end", StringComparison.OrdinalIgnoreCase))
                {
                    if (section == "inst" && !appended)
                    {
                        foreach (var p in rows.Values.Where(p => p.Row >= FirstAddedRow).OrderBy(p => p.Row))
                        {
                            var g = GameTransform(p.Matrix);
                            output.Append(string.Join(", ", p.Id.ToString(CultureInfo.InvariantCulture), p.Name, p.Interior.ToString(CultureInfo.InvariantCulture),
                                Num(g.X), Num(g.Y), Num(g.Z), Num(g.Qx), Num(g.Qy), Num(g.Qz), Num(g.Qw), "-1")).Append(newline);
                        }
                        appended = true;
                    }
                    section = null;
                }
                else if (section == "inst")
                {
                    int row = ordinal++;
                    if (rows.TryGetValue(row, out var p))
                    {
                        bool moved = _placementOriginals.ContainsKey(Key(file, row));
                        int lod = SavedLod(p, deleted);
                        if (moved || lod != p.LodRow)
                        {
                            int commentAt = raw.IndexOf('#');
                            string suffix = commentAt < 0 ? (raw.EndsWith('\r') ? "\r" : "") : " " + raw[commentAt..];
                            var columns = line.Split(',').Select(x => x.Trim()).ToArray();
                            if (moved)
                            {
                                var g = GameTransform(p.Matrix);
                                string[] fields = { Num(g.X), Num(g.Y), Num(g.Z), Num(g.Qx), Num(g.Qy), Num(g.Qz), Num(g.Qw) };
                                fields.CopyTo(columns, 3);
                            }
                            columns[10] = lod.ToString(CultureInfo.InvariantCulture);
                            raw = string.Join(", ", columns) + suffix;
                        }
                    }
                    else if (_placementOriginals.ContainsKey(Key(file, row))) continue;
                }
            }
            output.Append(raw);
            if (i + 1 < lines.Length) output.Append('\n');
        }
        if (!appended && rows.Values.Any(p => p.Row >= FirstAddedRow))
            throw new InvalidDataException($"No completed INST section in {file}.");
        return Encoding.Latin1.GetBytes(output.ToString());
    }
}
