using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Zipper.MPS
{
    /// <summary>
    /// Reader for Zipper '.mps' AI map files (e.g. AIMAPS.MPS shipped with a
    /// mission ZDB). Each AI map is a 2D grid plus world-space bounds and a
    /// human-readable name.
    ///
    /// Layout reverse-engineered from the working HTML reference
    /// (zed_map_viewer_solution.html parseAiMaps, lines 1385-1403):
    ///
    ///   0x04 u32 mapCount  (expected entry count)
    ///   0x28 .. EOF       sequence of fixed records, marked by a leading
    ///                     u32 == 0x17. From the marker:
    ///                       +0x04 f32 minX
    ///                       +0x08 f32 minY
    ///                       +0x0C f32 minZ
    ///                       +0x10 f32 maxX
    ///                       +0x14 f32 maxY
    ///                       +0x18 f32 maxZ
    ///                       +0x1C u32 width
    ///                       +0x20 u32 height
    ///                       +0x24 f32 scaleX
    ///                       +0x28 f32 scaleZ
    ///                       +0x38 char[32] name (null-padded)
    ///
    /// The scanner walks the body in 4-byte increments, skipping anything that
    /// doesn't satisfy the marker + sanity bounds (matches HTML behaviour).
    /// </summary>
    public sealed class MpsFile
    {
        public uint MapCount { get; init; }
        public IReadOnlyList<MpsMap> Maps { get; init; } = Array.Empty<MpsMap>();

        public static MpsFile Parse(byte[] data)
        {
            if (data == null || data.Length < 0x28)
                throw new InvalidDataException($"MPS file too small ({data?.Length ?? 0} bytes).");

            uint mapCount = BitConverter.ToUInt32(data, 0x04);
            var maps = new List<MpsMap>();

            for (int o = 0x28; o <= data.Length - 0x80; o += 4)
            {
                if (BitConverter.ToUInt32(data, o) != 0x17) continue;

                // Bounds check the whole record before reading; the scanner can
                // see false-positive marker words anywhere in the body.
                if (o + 0x58 > data.Length) continue;

                uint width = BitConverter.ToUInt32(data, o + 0x1C);
                uint height = BitConverter.ToUInt32(data, o + 0x20);
                float scaleX = BitConverter.ToSingle(data, o + 0x24);
                float scaleZ = BitConverter.ToSingle(data, o + 0x28);

                if (width == 0 || width > 5000) continue;
                if (height == 0 || height > 5000) continue;
                if (!IsFinitePositive(scaleX) || !IsFinitePositive(scaleZ)) continue;

                string name = ReadFixedString(data, o + 0x38, 0x20);
                if (string.IsNullOrEmpty(name)) continue;

                maps.Add(new MpsMap
                {
                    Offset = (uint)o,
                    Name = name,
                    Width = width,
                    Height = height,
                    MinX = BitConverter.ToSingle(data, o + 0x04),
                    MinY = BitConverter.ToSingle(data, o + 0x08),
                    MinZ = BitConverter.ToSingle(data, o + 0x0C),
                    MaxX = BitConverter.ToSingle(data, o + 0x10),
                    MaxY = BitConverter.ToSingle(data, o + 0x14),
                    MaxZ = BitConverter.ToSingle(data, o + 0x18),
                    ScaleX = scaleX,
                    ScaleZ = scaleZ,
                });

                // Once we've reached the declared count we stop — extra hits
                // past that are almost always alignment noise.
                if (mapCount > 0 && maps.Count >= mapCount) break;
            }

            return new MpsFile { MapCount = mapCount, Maps = maps };
        }

        private static bool IsFinitePositive(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f && value < 1_000_000f;

        private static string ReadFixedString(byte[] data, int offset, int maxLen)
        {
            int end = Math.Min(offset + maxLen, data.Length);
            var sb = new StringBuilder();
            for (int i = offset; i < end; i++)
            {
                byte b = data[i];
                if (b == 0) break;
                if (b < 0x20 || b > 0x7e) return string.Empty;
                sb.Append((char)b);
            }
            return sb.ToString().Trim();
        }
    }

    /// <summary>One AI map record inside an MPS file.</summary>
    public sealed class MpsMap
    {
        public uint Offset { get; init; }
        public string Name { get; init; } = string.Empty;
        public uint Width { get; init; }
        public uint Height { get; init; }
        public float MinX { get; init; }
        public float MinY { get; init; }
        public float MinZ { get; init; }
        public float MaxX { get; init; }
        public float MaxY { get; init; }
        public float MaxZ { get; init; }
        public float ScaleX { get; init; }
        public float ScaleZ { get; init; }
    }
}
