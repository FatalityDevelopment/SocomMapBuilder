using System;
using System.Buffers.Binary;
using System.IO;

namespace CodeDesigner.Ps2.FileFormats.Ps2
{
    /// <summary>
    /// The sound track of a .PSS movie, which players other than the PS2's (browsers, ffmpeg) do not read: MPEG-2
    /// private stream 1 (0xBD) packets whose payloads (after a 4-byte sub-stream header, id 0xA0) join into
    /// "SShd" + size + header (type, rate, channels, interleave …) and "SSbd" + size + the samples, each channel's in
    /// turn, <c>interleave</c> bytes at a time. Type 1 is 16-bit little-endian PCM (every movie on the SOCOM II disc).
    /// </summary>
    public static class PssAudio
    {
        public sealed record Format(int Type, int Rate, int Channels, int Interleave);

        /// <summary>Writes the movie's sound to <paramref name="wavPath"/> as a WAV; null (and no file) when the movie
        /// has none this reads (no SShd header, or audio other than PCM).</summary>
        public static Format? ExtractWav(string pssPath, string wavPath)
        {
            using var input = new BufferedStream(new FileStream(pssPath, FileMode.Open, FileAccess.Read, FileShare.Read), 1 << 20);
            var stream = new MemoryStream();
            Format? format = null;
            long dataStart = -1;
            FileStream? output = null;
            byte[] pending = Array.Empty<byte>();
            long written = 0;
            bool done = false;
            try
            {
                foreach (byte[] chunk in AudioPayloads(input))
                {
                    if (format == null)
                    {
                        // Gather the start of the track until its headers are complete.
                        stream.Write(chunk);
                        var buf = stream.GetBuffer().AsSpan(0, (int)stream.Length);
                        if (buf.Length < 8) continue;
                        if (!buf[..4].SequenceEqual("SShd"u8)) return null;
                        int hdr = BinaryPrimitives.ReadInt32LittleEndian(buf[4..]);
                        if (buf.Length < 8 + hdr + 8) continue;
                        var h = buf.Slice(8, hdr);
                        format = new Format(BinaryPrimitives.ReadInt32LittleEndian(h), BinaryPrimitives.ReadInt32LittleEndian(h[4..]),
                            BinaryPrimitives.ReadInt32LittleEndian(h[8..]), BinaryPrimitives.ReadInt32LittleEndian(h[12..]));
                        if (format.Type != 1 || format.Channels is < 1 or > 8 || format.Rate is < 8000 or > 96000 || format.Interleave <= 0 || format.Interleave % 2 != 0) return null;
                        if (!buf.Slice(8 + hdr, 4).SequenceEqual("SSbd"u8)) return null;
                        output = new FileStream(wavPath, FileMode.Create, FileAccess.Write);
                        output.Write(new byte[44]);
                        dataStart = 8 + hdr + 8;
                        pending = buf[(int)dataStart..].ToArray();
                    }
                    else
                    {
                        pending = Concat(pending, chunk);
                    }
                    written += Deinterleave(ref pending, format, output!);
                }
                if (output == null || format == null) return null;
                WriteWavHeader(output, format, written);
                done = true;
                return format;
            }
            finally
            {
                output?.Dispose();
                if (!done) { try { File.Delete(wavPath); } catch (IOException) { } }
            }
        }

        // Whole rounds (one interleave block per channel) become interleaved frames; the rest waits for more data.
        private static long Deinterleave(ref byte[] pending, Format f, Stream output)
        {
            int round = f.Interleave * f.Channels;
            int rounds = pending.Length / round;
            if (rounds == 0) return 0;
            var frames = new byte[rounds * round];
            int samplesPerBlock = f.Interleave / 2;
            for (int r = 0; r < rounds; r++)
                for (int c = 0; c < f.Channels; c++)
                {
                    int src = r * round + c * f.Interleave;
                    for (int s = 0; s < samplesPerBlock; s++)
                    {
                        int dst = r * round + (s * f.Channels + c) * 2;
                        frames[dst] = pending[src + s * 2];
                        frames[dst + 1] = pending[src + s * 2 + 1];
                    }
                }
            output.Write(frames);
            pending = pending[(rounds * round)..];
            return frames.Length;
        }

        private static void WriteWavHeader(FileStream output, Format f, long dataBytes)
        {
            output.Position = 0;
            using var w = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true);
            int size = (int)Math.Min(int.MaxValue - 36, dataBytes);
            w.Write("RIFF"u8); w.Write(36 + size); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)f.Channels); w.Write(f.Rate); w.Write(f.Rate * f.Channels * 2); w.Write((short)(f.Channels * 2)); w.Write((short)16);
            w.Write("data"u8); w.Write(size);
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }

        // The payloads of the private stream 1 audio packets, in order: pack by pack, packet by packet.
        private static System.Collections.Generic.IEnumerable<byte[]> AudioPayloads(Stream s)
        {
            var code = new byte[4];
            while (true)
            {
                if (!ReadFull(s, code, 4)) yield break;
                if (code[0] != 0 || code[1] != 0 || code[2] != 1)
                {
                    // Out of step: slide one byte.
                    s.Position -= 3;
                    continue;
                }
                byte id = code[3];
                if (id == 0xB9) yield break;
                if (id == 0xBA)
                {
                    var pack = new byte[10];
                    if (!ReadFull(s, pack, 10)) yield break;
                    s.Position += pack[9] & 7;
                    continue;
                }
                if (id < 0xBB) continue;
                var len = new byte[2];
                if (!ReadFull(s, len, 2)) yield break;
                int length = BinaryPrimitives.ReadUInt16BigEndian(len);
                if (id != 0xBD) { s.Position += length; continue; }
                var packet = new byte[length];
                if (!ReadFull(s, packet, length)) yield break;
                // MPEG-2 PES header (two flag bytes, a length, then that much), then the sub-stream header.
                int at = 3 + packet[2];
                int sub = Array.FindIndex(packet, at, Math.Min(4, Math.Max(0, length - at)), b => b is >= 0xA0 and <= 0xAF);
                if (sub < 0) continue;
                int start = sub + 3;
                if (start < length) yield return packet[start..];
            }
        }

        private static bool ReadFull(Stream s, byte[] buf, int count)
        {
            int read = 0;
            while (read < count) { int n = s.Read(buf, read, count - read); if (n <= 0) return false; read += n; }
            return true;
        }
    }
}
