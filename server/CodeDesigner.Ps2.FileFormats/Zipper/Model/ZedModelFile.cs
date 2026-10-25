using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Model
{
    /// <summary>
    /// Decoder/encoder for Zipper PS2 ".mdl" payloads stored inside ZED
    /// containers (WEAP_MDL.ZED, WORL_MDL.ZED). Mirrors zed-model-parser.ts
    /// on the webview side.
    ///
    /// Geometry chunk (one VU1 batch; verified by the SOCOM II pack builders
    /// in Mods/SOCOMCA, whose output renders in game):
    ///   chunk + 0x00 = 0x01000101   STCYCL 0x101
    ///   chunk + 0x04 = 0x6C048000   UNPACK V4-32 @0, 4 qwords
    ///   chunk + 0x08 = GIF tag, GIF tag, (index addr, 0, verts, tris), (0, 0, 0, scale)
    ///   chunk + 0x30 = vertexCount   (int32)
    ///   chunk + 0x34 = triangleCount (int32)
    ///   chunk + 0x44 = scale         (float32)
    ///   chunk + 0x48 = 03 02 00 01   STCYCL 0x203 (2 of every 3 qwords; the third is the colour)
    ///   chunk + 0x4C = UNPACK V4-16 @4
    ///   chunk + 0x50 = vertex records, 0x10 bytes each: int16 x, y, z, nx | int16 u, v, ny, nz
    ///                  (u, v 12-bit fixed point, 4096 = 1.0; normals Q15)
    ///   vertexEnd + 0x08 = triangle table, 4 bytes each (a*3, b*3, c*3, 3)
    ///
    /// Positions: int16 * scale / 32768 for weapons (GEO vparams flag 0x20000
    /// set) and int16 * scale / 16 for props, vehicles and map objects.
    ///
    /// A model node's N&lt;node&gt;_&lt;visual&gt; children (4-byte payloads) point at
    /// DMA chains that bind the chunks: per material section a kind-6 tag
    /// (.tif name), a kind-3 tag (GS material registers), then per chunk a
    /// kind-4 tag (per-vertex RGBA, 0x80 = 1.0: the baked lighting) and a
    /// kind-2 tag (the geometry chunk). <see cref="ParseVisualChains"/> walks
    /// them and <see cref="Parse(byte[], IReadOnlyList{ChainOffset})"/> decodes
    /// the chunks with their exact texture and colours.
    /// </summary>
    public static class ZedModelFile
    {
        /// <summary>vparams flag: positions are int16 / 32768 * scale (weapons).</summary>
        public const uint VparamsFlagScaleNormalised = 0x20000;

        /// <summary>How int16 vertex positions map to units.</summary>
        public enum PositionUnit
        {
            /// <summary>int16 * scale / 32768 (weapons).</summary>
            Scale,
            /// <summary>int16 * scale / 16 (props, vehicles, map objects).</summary>
            Sixteenth,
        }

        /// <summary>Position unit implied by a GEO vparams flags word.</summary>
        public static PositionUnit PositionUnitForFlags(uint vparamsFlags) =>
            (vparamsFlags & VparamsFlagScaleNormalised) != 0 ? PositionUnit.Scale : PositionUnit.Sixteenth;

        /// <summary>
        /// A vertex position in units: int16 * scale / divisor plus the chunk
        /// origin (header +0x38..+0x40). int16 / 16 only reaches +-2048 units,
        /// so world chunks further out carry their offset in the origin.
        /// </summary>
        public static (float X, float Y, float Z) VertexPosition(Chunk chunk, Vertex v, PositionUnit unit)
        {
            float divisor = unit == PositionUnit.Sixteenth ? 16f : 32768f;
            var (ox, oy, oz) = chunk.Origin;
            return (v.X * chunk.Scale / divisor + ox, v.Y * chunk.Scale / divisor + oy, v.Z * chunk.Scale / divisor + oz);
        }
        public const uint Magic0 = 0x01000101;
        public const uint Magic1 = 0x6C048000;
        public const uint Magic2 = 0x00008000;
        public const int  ChunkHeaderSize = 0x50;
        public const int  VertexRecordSize = 0x10;
        public const int  TriangleCommandGap = 0x08;
        public const int  TriangleRecordSize = 0x04;

        public sealed class Vertex
        {
            public short X { get; set; }
            public short Y { get; set; }
            public short Z { get; set; }
            // Second element of the record: (u, v, ny, nz). X/Y/Z/W is the first
            // element (x, y, z, nx).
            public short W { get; set; }
            // Bytes 0x08..0x0f after XYZW carry u, v, ny, nz. We preserve them
            // verbatim so encode round-trips with no edits.
            public byte[] Trailer { get; set; } = Array.Empty<byte>();
            /// <summary>Texture coordinate (12-bit fixed point: 4096 = 1.0).</summary>
            public float U => Trailer.Length >= 2 ? BitConverter.ToInt16(Trailer, 0) / 4096f : 0f;
            public float V => Trailer.Length >= 4 ? BitConverter.ToInt16(Trailer, 2) / 4096f : 0f;
            /// <summary>
            /// Baked vertex colour from the chain's colour block (0..255, 0x80 =
            /// 1.0). Null when the chunk was decoded without its chain.
            /// </summary>
            public byte? R { get; set; }
            public byte? G { get; set; }
            public byte? B { get; set; }
            public byte? A { get; set; }
        }

        public sealed class Chunk
        {
            public int Offset { get; set; }
            public int VertexCount { get; set; }
            public int TriangleCount { get; set; }
            public float Scale { get; set; }
            /// <summary>Chunk origin in world units (header +0x38..+0x40; 0 on weapons).</summary>
            public (float X, float Y, float Z) Origin { get; set; }
            public List<Vertex> Vertices { get; } = new();
            // 4 bytes per triangle: index a, b, c, flags. Indices are packed
            // VU slot offsets (real index = packed / 3).
            public byte[] TriangleTable { get; set; } = Array.Empty<byte>();
            // Opaque chunk header bytes (0x50) preserved verbatim.
            public byte[] RawHeader { get; set; } = Array.Empty<byte>();
            // Opaque gap between vertex block end and triangle table.
            public byte[] RawTriangleCommand { get; set; } = Array.Empty<byte>();
            // Bytes between triangle-table end and the start of the next
            // chunk (or end of file). Contains the .tif name and other
            // material data — preserved verbatim except when texture rebind
            // is requested.
            public byte[] RawTail { get; set; } = Array.Empty<byte>();
            // Decoded .tif reference: from the DMA chain's name field when
            // parsed with chain offsets, else found by scanning the tail for
            // printable ASCII ending in ".tif". null when the chunk has no
            // explicit texture binding.
            public string? TextureName { get; set; }
            /// <summary>"chain" (exact, from the DMA chain) or "tail" (string scan).</summary>
            public string? TextureBinding { get; set; }
            /// <summary>The N&lt;node&gt;_&lt;visual&gt; stub whose chain drew this chunk.</summary>
            public string? VisualKey { get; set; }
            /// <summary>True when every vertex carries R/G/B/A from the chain's colour block.</summary>
            public bool HasVertexColors { get; set; }
        }

        /// <summary>A visual's chain offset inside the model payload (from an N* child stub).</summary>
        public sealed class ChainOffset
        {
            public int Offset { get; set; }
            public string? Key { get; set; }
        }

        public sealed class ChainChunkRef
        {
            public int GeometryOffset { get; set; }
            public int GeometryLength { get; set; }
            public int? ColorOffset { get; set; }
            public int ColorLength { get; set; }
        }

        public sealed class ChainSection
        {
            public string? TextureName { get; set; }
            public int? MaterialOffset { get; set; }
            public int MaterialLength { get; set; }
            public List<ChainChunkRef> Chunks { get; } = new();
        }

        public sealed class VisualChain
        {
            public int Offset { get; set; }
            public string? Key { get; set; }
            public List<ChainSection> Sections { get; } = new();
        }

        public sealed class Model
        {
            public List<Chunk> Chunks { get; } = new();
            // Raw bytes before the first chunk (rare prefix). Preserved on round-trip.
            public byte[] PrefixBytes { get; set; } = Array.Empty<byte>();
            // Bytes after the last chunk's tail (also rare). Preserved on round-trip.
            public byte[] SuffixBytes { get; set; } = Array.Empty<byte>();
            // All .tif references found anywhere in the payload (model-wide,
            // not just per-chunk tails).
            public List<string> TextureRefs { get; } = new();
            /// <summary>
            /// True when the chunks were decoded through the DMA chains. Such a
            /// model is a view of the visuals, not the whole payload, so it
            /// cannot be re-encoded.
            /// </summary>
            public bool FromChains { get; set; }
        }

        // -------------------------------------------------------------------
        // Decode
        // -------------------------------------------------------------------

        public static bool LooksLikeModel(byte[] bytes) => FindChunkOffsets(bytes).Count > 0;

        public static Model Parse(byte[] bytes)
        {
            var model = new Model();
            var offsets = FindChunkOffsets(bytes);

            int firstOffset = offsets.Count > 0 ? offsets[0] : bytes.Length;
            model.PrefixBytes = Slice(bytes, 0, firstOffset);

            for (int i = 0; i < offsets.Count; i++)
            {
                int off = offsets[i];
                int nextOff = i + 1 < offsets.Count ? offsets[i + 1] : bytes.Length;
                Chunk? chunk = ParseChunk(bytes, off, nextOff);
                if (chunk != null) model.Chunks.Add(chunk);
            }

            if (model.Chunks.Count > 0)
            {
                Chunk last = model.Chunks[model.Chunks.Count - 1];
                int suffixStart = last.Offset + ChunkSize(last);
                if (suffixStart < bytes.Length)
                    model.SuffixBytes = Slice(bytes, suffixStart, bytes.Length);
            }

            foreach (string tif in ExtractTextureRefs(bytes, 0, bytes.Length))
                model.TextureRefs.Add(tif);

            return model;
        }

        /// <summary>
        /// Parses the DMA chains the given stubs point at. Chains that fail
        /// validation (unknown tag kinds, out-of-range addresses, a geometry
        /// tag without a chunk header) are dropped; null when none validate.
        /// </summary>
        public static List<VisualChain>? ParseVisualChains(byte[] bytes, IReadOnlyList<ChainOffset> offsets)
        {
            var seen = new HashSet<int>();
            var chains = new List<VisualChain>();
            foreach (var entry in offsets)
            {
                if (!seen.Add(entry.Offset)) continue;
                VisualChain? chain = ParseVisualChain(bytes, entry.Offset, entry.Key);
                if (chain != null) chains.Add(chain);
            }
            if (chains.Count == 0) return null;
            chains.Sort((a, b) =>
            {
                int byKey = string.CompareOrdinal(a.Key ?? "", b.Key ?? "");
                return byKey != 0 ? byKey : a.Offset.CompareTo(b.Offset);
            });
            return chains;
        }

        private static VisualChain? ParseVisualChain(byte[] bytes, int offset, string? key)
        {
            if (offset < 0 || offset + 16 > bytes.Length) return null;
            int count = (int)(ReadU32(bytes, offset) & 0xffff);
            if (count == 0 || count > 8192) return null;
            if (offset + 16 + count * 16 > bytes.Length) return null;

            var chain = new VisualChain { Offset = offset, Key = key };
            (int offset, int length)? pendingColor = null;
            for (int i = 0; i < count; i++)
            {
                int tag = offset + 16 + i * 16;
                uint lo = ReadU32(bytes, tag);
                long addr = ReadU32(bytes, tag + 4);
                // Kind in bits 16-23; bit 24 is a flag (seen on material tags).
                int kind = (int)((lo >> 16) & 0xff);
                int length = (int)(lo & 0xffff) * 16;
                if (addr > bytes.Length) return null;
                switch (kind)
                {
                    case 6:
                        chain.Sections.Add(new ChainSection { TextureName = ReadChainName(bytes, (int)addr) });
                        pendingColor = null;
                        break;
                    case 3:
                        if (chain.Sections.Count == 0 || addr + length > bytes.Length) return null;
                        chain.Sections[^1].MaterialOffset = (int)addr;
                        chain.Sections[^1].MaterialLength = length;
                        break;
                    case 4:
                        if (addr + length > bytes.Length) return null;
                        pendingColor = ((int)addr, length);
                        break;
                    case 2:
                        if (addr + ChunkHeaderSize > bytes.Length || addr + length > bytes.Length) return null;
                        if (ReadU32(bytes, (int)addr) != Magic0 || ReadU32(bytes, (int)addr + 4) != Magic1) return null;
                        if (!ChunkCountsLookSane(bytes, (int)addr)) return null;
                        if (chain.Sections.Count == 0) chain.Sections.Add(new ChainSection());
                        chain.Sections[^1].Chunks.Add(new ChainChunkRef
                        {
                            GeometryOffset = (int)addr,
                            GeometryLength = length,
                            ColorOffset = pendingColor?.offset,
                            ColorLength = pendingColor?.length ?? 0,
                        });
                        pendingColor = null;
                        break;
                    default:
                        return null;
                }
            }
            bool anyChunk = false;
            foreach (var section in chain.Sections) anyChunk |= section.Chunks.Count > 0;
            return anyChunk ? chain : null;
        }

        private static bool ChunkCountsLookSane(byte[] bytes, int offset)
        {
            int vertexCount = ReadI32(bytes, offset + 0x30);
            int triangleCount = ReadI32(bytes, offset + 0x34);
            if (vertexCount <= 0 || vertexCount > 4096) return false;
            if (triangleCount < 0 || triangleCount > 4096) return false;
            return offset + ChunkHeaderSize + vertexCount * VertexRecordSize + TriangleCommandGap + triangleCount * TriangleRecordSize <= bytes.Length;
        }

        private static string? ReadChainName(byte[] bytes, int offset)
        {
            if (offset < 0 || offset >= bytes.Length) return null;
            var sb = new StringBuilder();
            for (int i = offset; i < Math.Min(bytes.Length, offset + 64); i++)
            {
                byte b = bytes[i];
                if (b == 0) break;
                if (b < 0x20 || b > 0x7e) return null;
                sb.Append((char)b);
            }
            string text = sb.ToString().Trim();
            if (text.Length == 0) return null;
            int slash = Math.Max(text.LastIndexOf('/'), text.LastIndexOf('\\'));
            return (slash >= 0 ? text.Substring(slash + 1) : text).ToLowerInvariant();
        }

        /// <summary>
        /// Chain offsets for a model node in a flat ZED node list: its direct
        /// children with 4-byte payloads. The list is in pre-order with each
        /// node's Type holding its child count.
        /// </summary>
        public static List<ChainOffset> CollectChainOffsets(IReadOnlyList<ZED.ZedNode> nodes, int nodeIndex)
        {
            var result = new List<ChainOffset>();
            if (nodeIndex < 0 || nodeIndex >= nodes.Count) return result;
            int childCount = (int)nodes[nodeIndex].Type;
            int cursor = nodeIndex + 1;
            for (int c = 0; c < childCount && cursor < nodes.Count; c++)
            {
                var child = nodes[cursor];
                if (child.Data.Length == 4)
                    result.Add(new ChainOffset { Offset = (int)ReadU32(child.Data, 0), Key = child.Name });
                cursor = SkipSubtree(nodes, cursor);
            }
            return result;
        }

        private static int SkipSubtree(IReadOnlyList<ZED.ZedNode> nodes, int index)
        {
            int next = index + 1;
            int children = (int)nodes[index].Type;
            for (int c = 0; c < children && next < nodes.Count; c++)
                next = SkipSubtree(nodes, next);
            return next;
        }

        /// <summary>
        /// Decodes the chunks the chains reference, with their exact texture
        /// and vertex colours. Falls back to <see cref="Parse(byte[])"/> when
        /// no chain validates.
        /// </summary>
        public static Model Parse(byte[] bytes, IReadOnlyList<ChainOffset> chainOffsets)
        {
            var chains = chainOffsets.Count > 0 ? ParseVisualChains(bytes, chainOffsets) : null;
            if (chains == null) return Parse(bytes);

            var model = new Model { FromChains = true };
            // A visual can list the same geometry twice (alternative chains
            // over one mesh): decode it once per visual. Different visuals
            // sharing geometry are instances at different GEO nodes and each
            // keeps its own copy.
            foreach (var chain in chains)
            {
                var seenGeometry = new HashSet<int>();
                foreach (var section in chain.Sections)
                {
                    foreach (var r in section.Chunks)
                    {
                        if (!seenGeometry.Add(r.GeometryOffset)) continue;
                        Chunk? chunk = ParseChunk(bytes, r.GeometryOffset, r.GeometryOffset + r.GeometryLength);
                        if (chunk == null) continue;
                        if (section.TextureName != null)
                        {
                            chunk.TextureName = section.TextureName;
                            chunk.TextureBinding = "chain";
                        }
                        chunk.VisualKey = chain.Key ?? $"chain@0x{chain.Offset:x}";
                        AttachVertexColors(chunk, bytes, r);
                        model.Chunks.Add(chunk);
                    }
                }
            }
            if (model.Chunks.Count == 0) return Parse(bytes);

            foreach (string tif in ExtractTextureRefs(bytes, 0, bytes.Length))
                model.TextureRefs.Add(tif);
            foreach (var chunk in model.Chunks)
            {
                if (chunk.TextureName != null && !model.TextureRefs.Contains(chunk.TextureName, StringComparer.OrdinalIgnoreCase))
                    model.TextureRefs.Add(chunk.TextureName);
            }
            return model;
        }

        private static void AttachVertexColors(Chunk chunk, byte[] bytes, ChainChunkRef r)
        {
            if (r.ColorOffset == null) return;
            int needed = chunk.VertexCount * 4;
            if (r.ColorLength < needed || r.ColorOffset.Value + needed > bytes.Length) return;
            for (int i = 0; i < chunk.VertexCount; i++)
            {
                int o = r.ColorOffset.Value + i * 4;
                var v = chunk.Vertices[i];
                v.R = bytes[o];
                v.G = bytes[o + 1];
                v.B = bytes[o + 2];
                v.A = bytes[o + 3];
            }
            chunk.HasVertexColors = true;
        }

        private static List<int> FindChunkOffsets(byte[] bytes)
        {
            var offsets = new List<int>();
            int limit = bytes.Length - ChunkHeaderSize;
            for (int i = 0; i <= limit; i += 0x10)
            {
                if (ReadU32(bytes, i + 0x00) != Magic0) continue;
                if (ReadU32(bytes, i + 0x04) != Magic1) continue;
                if (ReadU32(bytes, i + 0x08) != Magic2) continue;
                if (bytes[i + 0x48] != 0x03 || bytes[i + 0x49] != 0x02
                    || bytes[i + 0x4A] != 0x00 || bytes[i + 0x4B] != 0x01) continue;

                int vertexCount = ReadI32(bytes, i + 0x30);
                int triangleCount = ReadI32(bytes, i + 0x34);
                if (vertexCount <= 0 || vertexCount > 4096) continue;
                if (triangleCount < 0 || triangleCount > 4096) continue;
                int expectedEnd = i + ChunkHeaderSize
                    + vertexCount * VertexRecordSize
                    + TriangleCommandGap
                    + triangleCount * TriangleRecordSize;
                if (expectedEnd > bytes.Length) continue;

                offsets.Add(i);
            }
            return offsets;
        }

        private static Chunk? ParseChunk(byte[] bytes, int offset, int nextChunkOffset)
        {
            int vertexCount = ReadI32(bytes, offset + 0x30);
            int triangleCount = ReadI32(bytes, offset + 0x34);
            float scale = BitConverter.ToSingle(bytes, offset + 0x44);
            if (!float.IsFinite(scale) || scale == 0) scale = 1f;

            int vertexStart = offset + ChunkHeaderSize;
            int vertexBlockSize = vertexCount * VertexRecordSize;
            if (vertexStart + vertexBlockSize > bytes.Length) return null;

            float ReadOrigin(int o)
            {
                float value = BitConverter.ToSingle(bytes, offset + o);
                return float.IsFinite(value) && Math.Abs(value) < 1e6f ? value : 0f;
            }
            var chunk = new Chunk
            {
                Offset = offset,
                VertexCount = vertexCount,
                TriangleCount = triangleCount,
                Scale = scale,
                Origin = (ReadOrigin(0x38), ReadOrigin(0x3C), ReadOrigin(0x40)),
                RawHeader = Slice(bytes, offset, offset + ChunkHeaderSize),
            };

            for (int i = 0; i < vertexCount; i++)
            {
                int o = vertexStart + i * VertexRecordSize;
                chunk.Vertices.Add(new Vertex
                {
                    X = BitConverter.ToInt16(bytes, o + 0),
                    Y = BitConverter.ToInt16(bytes, o + 2),
                    Z = BitConverter.ToInt16(bytes, o + 4),
                    W = BitConverter.ToInt16(bytes, o + 6),
                    Trailer = Slice(bytes, o + 8, o + VertexRecordSize),
                });
            }

            int triCommand = vertexStart + vertexBlockSize;
            int triData = triCommand + TriangleCommandGap;
            chunk.RawTriangleCommand = Slice(bytes, triCommand, triData);

            int triBytes = triangleCount * TriangleRecordSize;
            int triEnd = triData + triBytes;
            if (triEnd > bytes.Length) return null;
            chunk.TriangleTable = Slice(bytes, triData, triEnd);

            int tailEnd = Math.Min(nextChunkOffset, bytes.Length);
            chunk.RawTail = Slice(bytes, triEnd, tailEnd);
            chunk.TextureName = FindTifInRange(bytes, triEnd, tailEnd);
            chunk.TextureBinding = chunk.TextureName != null ? "tail" : null;

            return chunk;
        }

        public static int ChunkSize(Chunk c) =>
            ChunkHeaderSize
            + c.Vertices.Count * VertexRecordSize
            + TriangleCommandGap
            + c.TriangleTable.Length
            + c.RawTail.Length;

        // -------------------------------------------------------------------
        // Encode
        // -------------------------------------------------------------------

        public sealed class VertexTransform
        {
            public float ScaleX { get; set; } = 1f;
            public float ScaleY { get; set; } = 1f;
            public float ScaleZ { get; set; } = 1f;
            public short TranslateX { get; set; }
            public short TranslateY { get; set; }
            public short TranslateZ { get; set; }
        }

        public sealed class ChunkEdit
        {
            public int ChunkIndex { get; set; }
            public VertexTransform? Transform { get; set; }
            // New tif filename (lowercased, including extension). When set the
            // tail string referencing the old name is rewritten.
            public string? NewTextureName { get; set; }
        }

        /// <summary>
        /// Re-emits the model bytes. Without edits the output is byte-for-byte
        /// identical to the original payload. With edits, only the affected
        /// fields are mutated; everything else (opaque header bytes, vertex
        /// trailers, triangle table, tail bytes) is preserved verbatim.
        /// </summary>
        public static byte[] Encode(Model model, IReadOnlyList<ChunkEdit>? edits = null)
        {
            if (model.FromChains)
                throw new InvalidOperationException("A chain-decoded model is a view of its visuals and cannot be re-encoded; parse without chain offsets to edit it.");
            using var ms = new MemoryStream();
            ms.Write(model.PrefixBytes, 0, model.PrefixBytes.Length);

            for (int i = 0; i < model.Chunks.Count; i++)
            {
                Chunk c = model.Chunks[i];
                ChunkEdit? edit = null;
                if (edits != null)
                {
                    for (int e = 0; e < edits.Count; e++)
                        if (edits[e].ChunkIndex == i) { edit = edits[e]; break; }
                }
                WriteChunk(ms, c, edit);
            }

            ms.Write(model.SuffixBytes, 0, model.SuffixBytes.Length);
            return ms.ToArray();
        }

        private static void WriteChunk(Stream stream, Chunk c, ChunkEdit? edit)
        {
            // Header bytes preserved verbatim except vertexCount/triangleCount/scale,
            // which our parse normalizes but the bytes are still authoritative
            // (we read them back from RawHeader on round-trip).
            byte[] header = (byte[])c.RawHeader.Clone();
            stream.Write(header, 0, header.Length);

            VertexTransform? t = edit?.Transform;
            for (int i = 0; i < c.Vertices.Count; i++)
            {
                Vertex v = c.Vertices[i];
                short x = v.X, y = v.Y, z = v.Z;
                if (t != null)
                {
                    x = ClampToShort((int)Math.Round(x * t.ScaleX) + t.TranslateX);
                    y = ClampToShort((int)Math.Round(y * t.ScaleY) + t.TranslateY);
                    z = ClampToShort((int)Math.Round(z * t.ScaleZ) + t.TranslateZ);
                }
                WriteI16(stream, x);
                WriteI16(stream, y);
                WriteI16(stream, z);
                WriteI16(stream, v.W);
                stream.Write(v.Trailer, 0, v.Trailer.Length);
            }

            stream.Write(c.RawTriangleCommand, 0, c.RawTriangleCommand.Length);
            stream.Write(c.TriangleTable, 0, c.TriangleTable.Length);

            byte[] tail = c.RawTail;
            if (edit?.NewTextureName != null && c.TextureName != null)
                tail = ReplaceTifInTail(tail, c.TextureName, edit.NewTextureName);
            stream.Write(tail, 0, tail.Length);
        }

        private static byte[] ReplaceTifInTail(byte[] tail, string oldName, string newName)
        {
            // Find oldName as ASCII inside tail and overwrite in place. The
            // engine reads up to the next null/non-printable byte, so a name
            // longer than the original would walk into following data — we
            // require the new name to fit within the original slot.
            byte[] oldBytes = Encoding.ASCII.GetBytes(oldName);
            byte[] newBytes = Encoding.ASCII.GetBytes(newName);
            int found = IndexOf(tail, oldBytes, 0, true);
            if (found < 0) return tail; // not found; leave untouched
            if (newBytes.Length > oldBytes.Length)
                throw new InvalidOperationException(
                    $"New texture name '{newName}' is longer than original '{oldName}'.");

            var outBuf = (byte[])tail.Clone();
            Buffer.BlockCopy(newBytes, 0, outBuf, found, newBytes.Length);
            // Pad remainder of old slot with zeros so leftover characters
            // don't append onto the new name.
            for (int i = newBytes.Length; i < oldBytes.Length; i++)
                outBuf[found + i] = 0;
            return outBuf;
        }

        // -------------------------------------------------------------------
        // Low-level helpers
        // -------------------------------------------------------------------

        private static uint ReadU32(byte[] bytes, int o) => BitConverter.ToUInt32(bytes, o);
        private static int ReadI32(byte[] bytes, int o) => BitConverter.ToInt32(bytes, o);

        private static void WriteI16(Stream s, short v)
        {
            s.WriteByte((byte)v);
            s.WriteByte((byte)(v >> 8));
        }

        private static short ClampToShort(int v) =>
            (short)Math.Clamp(v, short.MinValue, short.MaxValue);

        private static byte[] Slice(byte[] src, int from, int to)
        {
            int len = Math.Max(0, to - from);
            var dst = new byte[len];
            if (len > 0) Buffer.BlockCopy(src, from, dst, 0, len);
            return dst;
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start, bool caseInsensitive)
        {
            if (needle.Length == 0 || haystack.Length - start < needle.Length) return -1;
            int limit = haystack.Length - needle.Length;
            for (int i = start; i <= limit; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    byte a = haystack[i + j], b = needle[j];
                    if (caseInsensitive)
                    {
                        if (a >= 'A' && a <= 'Z') a = (byte)(a + 32);
                        if (b >= 'A' && b <= 'Z') b = (byte)(b + 32);
                    }
                    if (a != b) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        private static string? FindTifInRange(byte[] bytes, int start, int end)
        {
            end = Math.Min(end, bytes.Length);
            int runStart = -1;
            for (int i = start; i < end; i++)
            {
                byte b = bytes[i];
                bool printable = b >= 0x20 && b <= 0x7e;
                if (printable)
                {
                    if (runStart < 0) runStart = i;
                }
                else
                {
                    if (runStart >= 0 && i - runStart >= 5)
                    {
                        string text = Encoding.ASCII.GetString(bytes, runStart, i - runStart);
                        int dot = text.IndexOf(".tif", StringComparison.OrdinalIgnoreCase);
                        if (dot > 0)
                        {
                            int s = dot;
                            while (s > 0 && IsNameChar(text[s - 1])) s--;
                            string name = text.Substring(s, dot - s + 4).ToLowerInvariant();
                            if (name.Length >= 5) return name;
                        }
                    }
                    runStart = -1;
                }
            }
            return null;
        }

        private static IEnumerable<string> ExtractTextureRefs(byte[] bytes, int start, int end)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int runStart = -1;
            int clampedEnd = Math.Min(end, bytes.Length);
            for (int i = start; i < clampedEnd; i++)
            {
                byte b = bytes[i];
                bool printable = b >= 0x20 && b <= 0x7e;
                if (printable)
                {
                    if (runStart < 0) runStart = i;
                }
                else
                {
                    if (runStart >= 0 && i - runStart >= 4)
                    {
                        string text = Encoding.ASCII.GetString(bytes, runStart, i - runStart);
                        int idx = 0;
                        while (idx < text.Length)
                        {
                            int dot = text.IndexOf(".tif", idx, StringComparison.OrdinalIgnoreCase);
                            if (dot < 0) break;
                            int s = dot;
                            while (s > 0 && IsNameChar(text[s - 1])) s--;
                            if (dot > s)
                            {
                                string name = text.Substring(s, dot - s + 4);
                                if (seen.Add(name)) yield return name;
                            }
                            idx = dot + 4;
                        }
                    }
                    runStart = -1;
                }
            }
        }

        private static bool IsNameChar(char c) =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
            (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.';
    }
}
