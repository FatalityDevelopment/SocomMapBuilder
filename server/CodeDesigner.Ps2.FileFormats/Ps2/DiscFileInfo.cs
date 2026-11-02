using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Ps2
{
    /// <summary>What can be read from the disc's movie and program files without playing or running them.</summary>
    public static class DiscFileInfo
    {
        public sealed record Movie(int Width, int Height, double FrameRate, double Seconds, Dictionary<string, int> Streams, string? Sound);

        private static readonly double[] FrameRates = { 0, 23.976, 24, 25, 29.97, 30, 50, 59.94, 60 };

        /// <summary>A .PSS (MPEG-2 program stream): picture size and rate from the sequence header, length from the
        /// first and last pack clocks, and packets per stream in the first few MB.</summary>
        public static Movie ReadPss(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] head = Read(fs, 0, (int)Math.Min(fs.Length, 4 << 20));
            byte[] tail = Read(fs, Math.Max(0, fs.Length - (256 << 10)), (int)Math.Min(fs.Length, 256 << 10));
            int width = 0, height = 0;
            double rate = 0;
            var streams = new Dictionary<string, int>();
            double? first = null;
            // Pack by pack, packet by packet (payload bytes can look like start codes); resync on anything else.
            for (int i = 0; i + 14 < head.Length;)
            {
                if (head[i] != 0 || head[i + 1] != 0 || head[i + 2] != 1) { i++; continue; }
                byte id = head[i + 3];
                if (id == 0xBA) { first ??= Scr(head, i + 4); i += 14 + (head[i + 13] & 7); continue; }
                if (id == 0xB9) break;
                if (id < 0xBB) { i++; continue; }
                int length = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(i + 4));
                int end = Math.Min(head.Length, i + 6 + length);
                if (id != 0xBB && id != 0xBE)
                {
                    string name = id switch { 0xBD => "private audio (SPU ADPCM / PCM)", >= 0xC0 and <= 0xDF => $"MPEG audio {id - 0xC0}", >= 0xE0 and <= 0xEF => $"video {id - 0xE0}", _ => $"stream 0x{id:X2}" };
                    streams[name] = streams.GetValueOrDefault(name) + 1;
                }
                // The video's sequence header (picture size, frame rate) is inside its first packets.
                if (id is >= 0xE0 and <= 0xEF && width == 0)
                    for (int j = i + 6; j + 8 < end; j++)
                        if (head[j] == 0 && head[j + 1] == 0 && head[j + 2] == 1 && head[j + 3] == 0xB3)
                        {
                            width = (head[j + 4] << 4) | (head[j + 5] >> 4);
                            height = ((head[j + 5] & 0x0F) << 8) | head[j + 6];
                            int code = head[j + 7] & 0x0F;
                            rate = code < FrameRates.Length ? FrameRates[code] : 0;
                            break;
                        }
                i = end;
            }
            double? last = null;
            for (int i = tail.Length - 12; i >= 0; i--)
                if (tail[i] == 0 && tail[i + 1] == 0 && tail[i + 2] == 1 && tail[i + 3] == 0xBA) { last = Scr(tail, i + 4); break; }
            double seconds = first is { } a && last is { } b && b > a ? b - a : 0;
            // The sound track's "SShd" header (see PssAudio): type, rate, channels.
            string? sound = null;
            int sshd = head.AsSpan().IndexOf("SShd"u8);
            if (sshd >= 0 && sshd + 20 <= head.Length)
            {
                int type = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(sshd + 8));
                int hz = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(sshd + 12));
                int channels = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(sshd + 16));
                sound = $"{(type == 1 ? "16-bit PCM" : $"type 0x{type:X}")}, {hz} Hz, {(channels == 2 ? "stereo" : channels == 1 ? "mono" : $"{channels} channels")}";
            }
            return new Movie(width, height, rate, seconds, streams, sound);
        }

        // MPEG-2 pack header system clock (90 kHz base), in seconds.
        private static double Scr(byte[] d, int o)
        {
            if (o + 5 > d.Length || (d[o] & 0xC0) != 0x40) return 0;
            long scr = ((long)(d[o] >> 3) & 7) << 30 | ((long)d[o] & 3) << 28 | (long)d[o + 1] << 20 | ((long)(d[o + 2] >> 3) & 0x1F) << 15
                | ((long)d[o + 2] & 3) << 13 | (long)d[o + 3] << 5 | (long)(d[o + 4] >> 3);
            return scr / 90000.0;
        }

        public sealed record ElfSection(string Name, string Type, uint Address, uint Size);
        public sealed record Elf(string Kind, string Machine, uint Entry, int Segments, IReadOnlyList<ElfSection> Sections, string? IopModule, string? IopVersion);

        /// <summary>An ELF (the game) or IRX (an IOP module, whose .iopmod section names it).</summary>
        public static Elf? ReadElf(byte[] d)
        {
            if (d.Length < 0x34 || d[0] != 0x7F || d[1] != 'E' || d[2] != 'L' || d[3] != 'F' || d[4] != 1) return null;
            ushort type = U16(d, 0x10), machine = U16(d, 0x12);
            uint entry = U32(d, 0x18), shoff = U32(d, 0x20);
            ushort phnum = U16(d, 0x2C), shentsize = U16(d, 0x2E), shnum = U16(d, 0x30), shstrndx = U16(d, 0x32);
            var sections = new List<ElfSection>();
            string? module = null, version = null;
            if (shoff > 0 && shentsize >= 40 && shoff + (long)shnum * shentsize <= d.Length)
            {
                uint strOff = shstrndx < shnum ? U32(d, (int)(shoff + shstrndx * shentsize + 0x10)) : 0;
                for (int i = 0; i < shnum; i++)
                {
                    int h = (int)(shoff + i * shentsize);
                    string name = CString(d, (int)(strOff + U32(d, h)));
                    uint stype = U32(d, h + 4), offset = U32(d, h + 0x10), size = U32(d, h + 0x14);
                    if (stype == 0) continue;
                    sections.Add(new ElfSection(name, stype switch { 1 => "progbits", 2 => "symtab", 3 => "strtab", 8 => "nobits", 9 => "rel", 0x70000080 => "iopmod", _ => $"0x{stype:X}" }, U32(d, h + 0x0C), size));
                    // .iopmod: module info, entry, gp, text / data / bss sizes, a version word, then the name.
                    if (stype == 0x70000080 && offset + 0x1C < d.Length)
                    {
                        ushort v = U16(d, (int)offset + 0x18);
                        version = $"{v >> 8}.{v & 0xFF}";
                        module = CString(d, (int)offset + 0x1A);
                    }
                }
            }
            string kind = type switch { 2 => "executable", 0xFF80 => "IOP module (IRX)", 1 => "relocatable", _ => $"type 0x{type:X}" };
            return new Elf(kind, machine == 8 ? "MIPS" : $"0x{machine:X}", entry, phnum, sections, module, version);
        }

        /// <summary>True when the bytes read as text (CNF files, lists): printable ASCII, tabs and line breaks.</summary>
        public static bool LooksLikeText(ReadOnlySpan<byte> d)
        {
            if (d.Length == 0) return false;
            var sample = d[..Math.Min(d.Length, 8192)];
            int printable = 0;
            foreach (byte b in sample) if (b is >= 0x20 and < 0x7F or (byte)'\r' or (byte)'\n' or (byte)'\t') printable++;
            return printable >= sample.Length * 0.97;
        }

        /// <summary>Printable ASCII runs of four or more characters, in file order, without repeats.</summary>
        public static List<string> Strings(ReadOnlySpan<byte> d, int max = 2000)
        {
            var seen = new HashSet<string>();
            var list = new List<string>();
            var sb = new StringBuilder();
            for (int i = 0; i <= d.Length && list.Count < max; i++)
            {
                if (i < d.Length && d[i] is >= 0x20 and < 0x7F) { sb.Append((char)d[i]); continue; }
                if (sb.Length >= 4 && seen.Add(sb.ToString())) list.Add(sb.ToString());
                sb.Clear();
            }
            return list;
        }

        private static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));
        private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));

        private static string CString(byte[] d, int o)
        {
            if (o < 0 || o >= d.Length) return "";
            int end = Array.IndexOf(d, (byte)0, o);
            return Encoding.ASCII.GetString(d, o, (end < 0 ? d.Length : end) - o);
        }

        private static byte[] Read(Stream s, long offset, int count)
        {
            var buf = new byte[count];
            s.Position = offset;
            int read = 0;
            while (read < count) { int n = s.Read(buf, read, count - read); if (n <= 0) break; read += n; }
            return read == count ? buf : buf[..read];
        }
    }
}
