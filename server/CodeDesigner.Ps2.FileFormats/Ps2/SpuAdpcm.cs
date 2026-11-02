using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Ps2
{
    /// <summary>
    /// The SPU2's 4-bit ADPCM: .VAG files (a 48-byte "VAGp" header, big-endian) and the bodies of sound banks
    /// (989 Studios .BNK "SBlk" banks, Sony .HD / .BD pairs), which hold many samples back to back. Each 16-byte
    /// block is a shift / predictor byte, a flags byte and 28 nibbles; flag bit 0 ends a sample.
    /// </summary>
    public static class SpuAdpcm
    {
        public const int BlockSize = 16;
        private static readonly int[,] Coefficients = { { 0, 0 }, { 60, 0 }, { 115, -52 }, { 98, -55 }, { 122, -60 } };

        public sealed record VagHeader(int Version, int DataSize, int SampleRate, string Name, int DataOffset);

        /// <summary>The header of a .VAG, or null when the bytes do not start with "VAGp".</summary>
        public static VagHeader? ReadVag(ReadOnlySpan<byte> data)
        {
            if (data.Length < 0x30 || data[0] != 'V' || data[1] != 'A' || data[2] != 'G' || data[3] != 'p') return null;
            int version = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(data[0x0C..]);
            int rate = (int)BinaryPrimitives.ReadUInt32BigEndian(data[0x10..]);
            var nameBytes = data.Slice(0x20, 16);
            int end = nameBytes.IndexOf((byte)0);
            string name = Encoding.ASCII.GetString(end < 0 ? nameBytes : nameBytes[..end]);
            return new VagHeader(version, size, rate, name, 0x30);
        }

        /// <summary>Samples in a run of ADPCM blocks (a bank body): offset and length in bytes of each, in order.</summary>
        public static List<(int Offset, int Length)> SplitSamples(ReadOnlySpan<byte> body)
        {
            var samples = new List<(int, int)>();
            int start = -1;
            bool audible = false;
            for (int pos = 0; pos + BlockSize <= body.Length; pos += BlockSize)
            {
                byte flags = body[pos + 1];
                bool silent = body.Slice(pos, BlockSize).IndexOfAnyExcept((byte)0) < 0;
                // Flag 7: an unused block between samples.
                if (flags == 7) { Close(pos); continue; }
                if (start < 0) { if (silent && flags == 0) continue; start = pos; }
                if (!silent) audible = true;
                if ((flags & 1) != 0) Close(pos + BlockSize);
            }
            Close(body.Length - body.Length % BlockSize);
            return samples;

            void Close(int end)
            {
                if (start >= 0 && audible && end - start >= BlockSize * 2) samples.Add((start, end - start));
                start = -1;
                audible = false;
            }
        }

        /// <summary>Decodes ADPCM blocks to 16-bit mono PCM (stops at the first end flag after the first block).</summary>
        public static short[] Decode(ReadOnlySpan<byte> blocks, int maxSamples = int.MaxValue)
        {
            int count = Math.Min(blocks.Length / BlockSize * 28, maxSamples);
            var pcm = new short[count];
            int h1 = 0, h2 = 0, n = 0;
            for (int pos = 0; pos + BlockSize <= blocks.Length && n < count; pos += BlockSize)
            {
                int shift = blocks[pos] & 0x0F;
                int predictor = Math.Min(blocks[pos] >> 4, 4);
                byte flags = blocks[pos + 1];
                if (flags == 7) break;
                if (shift > 12) shift = 9;
                int f0 = Coefficients[predictor, 0], f1 = Coefficients[predictor, 1];
                for (int i = 0; i < 28 && n < count; i++)
                {
                    int b = blocks[pos + 2 + i / 2];
                    int nibble = (i & 1) == 0 ? b & 0x0F : b >> 4;
                    int s = (short)(nibble << 12) >> shift;
                    s += (h1 * f0 + h2 * f1 + 32) >> 6;
                    s = Math.Clamp(s, short.MinValue, short.MaxValue);
                    pcm[n++] = (short)s;
                    h2 = h1;
                    h1 = s;
                }
                if ((flags & 1) != 0 && pos > 0) break;
            }
            return n == count ? pcm : pcm[..n];
        }

        /// <summary>16-bit mono PCM as a .WAV file.</summary>
        public static byte[] Wav(short[] pcm, int sampleRate)
        {
            using var ms = new MemoryStream(44 + pcm.Length * 2);
            using var w = new BinaryWriter(ms);
            w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(pcm.Length * 2);
            foreach (short s in pcm) w.Write(s);
            w.Flush();
            return ms.ToArray();
        }

        /// <summary>Where a bank's ADPCM body is: an "SBlk" .BNK names it in its header; a .BD is all body.</summary>
        public static (int Offset, int Length)? BankBody(ReadOnlySpan<byte> data)
        {
            if (data.Length >= 0x20 && data.Slice(0x18, 4).SequenceEqual("SBlk"u8))
            {
                int offset = BinaryPrimitives.ReadInt32LittleEndian(data[0x10..]);
                int length = BinaryPrimitives.ReadInt32LittleEndian(data[0x14..]);
                if (offset > 0 && length > 0 && (long)offset + length <= data.Length) return (offset, length);
            }
            return null;
        }
    }
}
