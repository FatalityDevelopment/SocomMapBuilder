using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Zipper.ZDB
{

    // =========================================================================
    // Archive
    // =========================================================================

    /// <summary>
    /// Reads, writes and manipulates ZDB archives (PS2 game data format).
    /// Supports Type-1 (magic 0xC8) and Type-2 (magic 0xFC) variants.
    /// </summary>
    public sealed class ZdbArchive : IDisposable
    {
        // ------------------------------------------------------------------
        // Private header structs
        // ------------------------------------------------------------------

        private struct Type1Header
        {
            public uint MainHeaderSize;    // 0x00  always 0xC8
            public uint FileHeaderSize;    // 0x04  always 0x58
            public uint Version;           // 0x08
            public uint ArchiveHeaderSize; // 0x0C  main + all file headers
            public uint BodySize;          // 0x10
            public uint UnusedId;          // 0x14  sentinel 0x4532ABCD
            public uint Compression;       // 0x18
            public ushort Errors;            // 0x1C
            public ushort Warnings;          // 0x1E
            // 0x20-0xC3  padding
            // 0xC4  FileCount
        }

        private struct Type2Header
        {
            public uint MainHeaderSize; // 0x00  always 0xFC
            public uint Unknown1;       // 0x04
            public uint UnknownId;      // 0x08  sentinel 0x12345678
            public uint Unknown2;       // 0x0C
            public uint Unknown3;       // 0x10
            public string Version;        // 0x14-0x97  null-terminated
            // 0x98  FileCount
            // 0x9C  FileHeaderSize  always 0x5C
        }

        // ------------------------------------------------------------------
        // State
        // ------------------------------------------------------------------

        private int _archiveType; // 0 = none, 1, 2
        private Type1Header _head1;
        private Type2Header _head2;

        private readonly List<ZdbEntry> _entries = new List<ZdbEntry>();

        // ------------------------------------------------------------------
        // Factory methods
        // ------------------------------------------------------------------

        /// <summary>Opens a ZDB archive from disk.</summary>
        /// <exception cref="FileNotFoundException"/>
        /// <exception cref="InvalidDataException"/>
        public static ZdbArchive Open(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Archive file not found.", filePath);

            var archive = new ZdbArchive();
            archive.Load(File.ReadAllBytes(filePath));
            return archive;
        }

        /// <summary>Opens a ZDB archive from a raw byte array.</summary>
        /// <exception cref="InvalidDataException"/>
        public static ZdbArchive FromBytes(byte[] data)
        {
            var archive = new ZdbArchive();
            archive.Load(data);
            return archive;
        }

        /// <summary>Creates a new, empty archive of type 1 or 2.</summary>
        /// <exception cref="ArgumentOutOfRangeException"/>
        public static ZdbArchive CreateNew(int type)
        {
            byte[] blank = type switch
            {
                1 => BuildBlankType1(),
                2 => BuildBlankType2(),
                _ => throw new ArgumentOutOfRangeException(nameof(type), "Must be 1 or 2.")
            };
            return FromBytes(blank);
        }

        private ZdbArchive() { }

        // ------------------------------------------------------------------
        // Properties
        // ------------------------------------------------------------------

        /// <summary>Archive variant (1 or 2; 3 = memory-card pack .CDB). Returns 0 when nothing is loaded.</summary>
        public int ArchiveType => _archiveType;

        /// <summary>True for a memory-card pack (<c>&lt;NAME&gt;.CDB</c>).</summary>
        public bool IsMemoryCardPack => _archiveType == 3;

        /// <summary>True when <paramref name="path"/> names a .ZDB package or a .CDB memory-card pack.</summary>
        public static bool IsArchivePath(string path) =>
            path.EndsWith(".zdb", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cdb", StringComparison.OrdinalIgnoreCase);

        /// <summary>All entries in the archive.</summary>
        public IReadOnlyList<ZdbEntry> Entries => _entries;

        /// <summary>Number of entries.</summary>
        public int EntryCount => _entries.Count;

        // ------------------------------------------------------------------
        // Entry access
        // ------------------------------------------------------------------

        /// <summary>Returns the entry at <paramref name="index"/>.</summary>
        public ZdbEntry GetEntry(int index) => _entries[index];

        /// <summary>
        /// Finds an entry by exact full path (case-insensitive). Returns null if not found.
        /// </summary>
        public ZdbEntry? FindEntry(string fullPath) =>
            _entries.FirstOrDefault(e =>
                string.Equals(
                    NormalizeForComparison(e.FullPath),
                    NormalizeForComparison(fullPath),
                    StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------------
        // Mutation
        // ------------------------------------------------------------------

        /// <summary>
        /// Adds a file from disk into the archive at the given internal path.
        /// <paramref name="varName"/> is only used by Type-1 archives.
        /// </summary>
        public ZdbEntry AddFile(string diskFilePath, string archivePath, uint varName = 0) =>
            AddEntry(archivePath, File.ReadAllBytes(diskFilePath), varName);

        /// <summary>Adds raw bytes into the archive at the given internal path.</summary>
        public ZdbEntry AddEntry(string archivePath, byte[] data, uint varName = 0)
        {
            EnsureOpen();

            string normalized = NormalizeArchivePath(archivePath);
            var entry = new ZdbEntry
            {
                FullPath = normalized,
                Name = PathFileName(normalized.Replace('\\', '/')),
                Data = (byte[])data.Clone(),
            };

            if (_archiveType == 1)
            {
                entry.VarName = varName;
                entry.Checksum = ZdbChecksum.Compute(data);
            }

            _entries.Add(entry);
            return entry;
        }

        /// <summary>Type 1 (ZDB v6): adds a resource with its tag (ASST, BONE ...) and format version.</summary>
        public ZdbEntry AddResource(string name, byte[] data, string tag, uint version)
        {
            if (_archiveType != 1) throw new InvalidOperationException("Resources (tag + version) are a type-1 (ZDB v6) feature");
            if (tag.Length != 4) throw new ArgumentException("A resource tag is 4 characters", nameof(tag));
            var e = AddEntry(name, data, BitConverter.ToUInt32(System.Text.Encoding.ASCII.GetBytes(tag)));
            e.DbId = version;
            return e;
        }

        /// <summary>
        /// Replaces the data of an existing entry by index.
        /// Automatically recalculates the checksum for Type-1 archives.
        /// </summary>
        public void ReplaceEntryData(int index, byte[] newData)
        {
            EnsureOpen();
            ZdbEntry entry = _entries[index];
            entry.Data = (byte[])newData.Clone();
            if (_archiveType == 1)
                entry.Checksum = ZdbChecksum.Compute(newData);
        }

        /// <summary>Removes the entry at <paramref name="index"/>.</summary>
        public void RemoveEntry(int index)
        {
            EnsureOpen();
            _entries.RemoveAt(index);
        }

        /// <summary>
        /// Removes all entries whose full path matches (case-insensitive).
        /// Returns the number of entries removed.
        /// </summary>
        public int RemoveEntry(string fullPath)
        {
            EnsureOpen();
            string target = NormalizeForComparison(fullPath);
            return _entries.RemoveAll(e =>
                string.Equals(NormalizeForComparison(e.FullPath), target,
                    StringComparison.OrdinalIgnoreCase));
        }

        // ------------------------------------------------------------------
        // Extraction
        // ------------------------------------------------------------------

        /// <summary>Writes an entry's data to <paramref name="outputPath"/> on disk.</summary>
        public void ExtractEntry(int index, string outputPath)
        {
            EnsureOpen();
            File.WriteAllBytes(outputPath, _entries[index].Data);
        }

        /// <summary>
        /// Extracts every entry, recreating the internal directory structure
        /// under <paramref name="outputDirectory"/>.
        /// </summary>
        public void ExtractAll(string outputDirectory)
        {
            EnsureOpen();
            foreach (ZdbEntry entry in _entries)
            {
                string dest = Path.Combine(
                    outputDirectory,
                    entry.FullPath.Replace('\\', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, entry.Data);
            }
        }

        // ------------------------------------------------------------------
        // Checksum helpers (Type-1 only)
        // ------------------------------------------------------------------

        /// <summary>Recalculates and updates checksums for all Type-1 entries. No-op on Type-2.</summary>
        public void FixAllChecksums()
        {
            if (_archiveType != 1) return;
            foreach (ZdbEntry entry in _entries)
                entry.Checksum = ZdbChecksum.Compute(entry.Data);
        }

        // ------------------------------------------------------------------
        // Save
        // ------------------------------------------------------------------

        /// <summary>Serialises the archive to disk.</summary>
        public void Save(string filePath)
        {
            EnsureOpen();
            File.WriteAllBytes(filePath, ToBytes());
        }

        /// <summary>Serialises the archive to a byte array.</summary>
        public byte[] ToBytes()
        {
            EnsureOpen();
            return _archiveType switch
            {
                1 => SerializeType1(),
                3 => Package.PackageSave.BuildCdb(_entries.Select(e => (e.FullPath, e.Data)).ToList()),
                _ => SerializeType2(),
            };
        }

        // ------------------------------------------------------------------
        // Directory listing
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns a shallow listing of the directory at <paramref name="dirPath"/>
        /// (immediate children only). Works at any nesting depth.
        /// Pass <c>"z:"</c> or <c>""</c> for the archive root.
        ///
        /// <code>
        ///   [DIR] .
        ///   [DIR] ..
        ///   [DIR] subfolderName
        ///   [FILE] 0002 filename.ext
        /// </code>
        /// </summary>
        public string GetDirectoryListing(string dirPath) =>
            BuildListing(dirPath, recursive: false);

        /// <summary>
        /// Returns a full recursive listing of everything under <paramref name="dirPath"/>,
        /// indented by depth. Pass <c>"z:"</c> or <c>""</c> for the entire archive.
        ///
        /// <code>
        ///   [DIR] .
        ///   [DIR] ..
        ///   [DIR]   levels
        ///   [FILE]    0001 world1.bin
        ///   [DIR]   textures
        ///   [FILE]    0003 sky.tex
        /// </code>
        /// </summary>
        public string GetDirectoryListingRecursive(string dirPath) =>
            BuildListing(dirPath, recursive: true);

        private string BuildListing(string dirPath, bool recursive)
        {
            EnsureOpen();

            string prefix = NormalizeForComparison(dirPath);

            // Collect every entry that lives somewhere under this prefix.
            // For each one we record its index and the path relative to prefix.
            var hits = new List<(int Index, ZdbEntry Entry, string Relative)>();

            for (int i = 0; i < _entries.Count; i++)
            {
                ZdbEntry e = _entries[i];
                string entryDir = NormalizeForComparison(GetParentDirectory(e.FullPath));

                if (!IsUnderOrEqual(entryDir, prefix)) continue;

                string relative = entryDir.Length > prefix.Length
                    ? entryDir.Substring(prefix.Length).TrimStart('/')
                    : string.Empty;

                hits.Add((i, e, relative));
            }

            var sb = new StringBuilder();

            if (!recursive)
            {
                // Shallow: files directly here + names of immediate subdirectories.
                var subdirs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var children = new List<(int Index, ZdbEntry Entry)>();

                foreach ((int idx, ZdbEntry e, string rel) in hits)
                {
                    string[] parts = rel.Split(
                        new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length == 0) children.Add((idx, e));
                    else subdirs.Add(parts[0]);
                }

                sb.AppendLine("[DIR] .");
                sb.AppendLine("[DIR] ..");
                foreach (string sub in subdirs)
                    sb.AppendLine($"[DIR] {sub}");
                foreach ((int idx, ZdbEntry entry) in children)
                    sb.AppendLine($"[FILE] {idx:X4} {entry.Name}");
            }
            else
            {
                // Recursive: build a sorted tree and print it depth-first.
                sb.AppendLine("[DIR] .");
                sb.AppendLine("[DIR] ..");
                AppendRecursive(sb, prefix, hits, depth: 1);
            }

            return sb.ToString();
        }

        // Recursively appends one level of the tree to sb.
        private void AppendRecursive(
            StringBuilder sb,
            string currentPrefix,
            List<(int Index, ZdbEntry Entry, string Relative)> hits,
            int depth)
        {
            string indent = new string(' ', depth * 2);

            // Immediate subdirectory names at this level.
            var subdirs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            // Files directly in this directory.
            var files = new List<(int Index, ZdbEntry Entry)>();

            foreach ((int idx, ZdbEntry e, string rel) in hits)
            {
                // rel is relative to the *original* prefix, so subtract currentPrefix portion.
                string normCurrent = NormalizeForComparison(currentPrefix);
                string entryDir = NormalizeForComparison(GetParentDirectory(e.FullPath));

                if (!IsUnderOrEqual(entryDir, normCurrent)) continue;

                string localRel = entryDir.Length > normCurrent.Length
                    ? entryDir.Substring(normCurrent.Length).TrimStart('/')
                    : string.Empty;

                string[] parts = localRel.Split(
                    new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length == 0) files.Add((idx, e));
                else subdirs.Add(parts[0]);
            }

            foreach (string sub in subdirs)
            {
                sb.AppendLine($"{indent}[DIR] {sub}");
                AppendRecursive(sb, currentPrefix + "/" + sub, hits, depth + 1);
            }

            foreach ((int idx, ZdbEntry entry) in files)
                sb.AppendLine($"{indent}[FILE] {idx:X4} {entry.Name}");
        }

        // ------------------------------------------------------------------
        // Wildcard search  (name AND path)
        // ------------------------------------------------------------------

        /// <summary>
        /// Searches for entries matching <paramref name="pattern"/> against
        /// <em>both</em> the file name and the full archive path.
        ///
        /// Wildcard tokens:
        /// <list type="bullet">
        ///   <item><c>?</c>  – any single character (not a path separator).</item>
        ///   <item><c>*</c>  – any sequence of characters within a single directory segment.</item>
        ///   <item><c>**</c> – any sequence of characters including path separators.</item>
        /// </list>
        ///
        /// <example><code>
        /// // By name
        /// archive.FindEntries("*.bin")            // any .bin file
        /// archive.FindEntries("level??.dat")      // level01.dat, level99.dat, …
        ///
        /// // By path  (single directory)
        /// archive.FindEntries("data/levels/*.bin")
        ///
        /// // By path  (any depth)
        /// archive.FindEntries("data/**/*.bin")
        /// archive.FindEntries("z:/textures/**")
        /// </code></example>
        /// </summary>
        public IReadOnlyList<ZdbEntry> FindEntries(string pattern)
        {
            EnsureOpen();
            string normPattern = NormalizeForComparison(pattern);
            return _entries
                .Where(e =>
                    WildcardMatch(e.Name, pattern) ||
                    WildcardMatch(NormalizeForComparison(e.FullPath), normPattern))
                .ToList();
        }

        /// <summary>
        /// Searches by file name only.
        /// <example><code>
        /// archive.FindByName("*.bin")
        /// archive.FindByName("level??.dat")
        /// </code></example>
        /// </summary>
        public IReadOnlyList<ZdbEntry> FindByName(string pattern)
        {
            EnsureOpen();
            return _entries
                .Where(e => WildcardMatch(e.Name, pattern))
                .ToList();
        }

        /// <summary>
        /// Searches by full archive path only. Use <c>**</c> to cross directory boundaries.
        /// <example><code>
        /// archive.FindByPath("data/levels/*.bin")   // files directly in data/levels/
        /// archive.FindByPath("data/**/*.bin")        // .bin files at any depth under data/
        /// archive.FindByPath("z:/textures/**")       // everything under textures/
        /// </code></example>
        /// </summary>
        public IReadOnlyList<ZdbEntry> FindByPath(string pattern)
        {
            EnsureOpen();
            string normPattern = NormalizeForComparison(pattern);
            return _entries
                .Where(e => WildcardMatch(NormalizeForComparison(e.FullPath), normPattern))
                .ToList();
        }

        // ------------------------------------------------------------------
        // IDisposable
        // ------------------------------------------------------------------

        public void Dispose()
        {
            _entries.Clear();
            _archiveType = 0;
        }

        // ====================================================================
        // Private – loading
        // ====================================================================

        private void Load(byte[] data)
        {
            if (data.Length < 4)
                throw new InvalidDataException("Data too short to be a ZDB archive.");

            uint magic = BitConverter.ToUInt32(data, 0);
            switch (magic)
            {
                case 0xC8: LoadType1(data); break;
                case 0xFC: LoadType2(data); break;
                // Memory-card pack (<NAME>.CDB, the loader mounts mc0:/BASCUS-97275<NAME>/): the type-2 entry table
                // with a 0x110 header and 0x70-byte entries; written back the way PackageSave.BuildCdb writes packs.
                case 0x110: LoadType2(data); _archiveType = 3; break;
                default:
                    throw new InvalidDataException(
                        $"Unrecognised ZDB magic 0x{magic:X8}. Expected 0xC8, 0xFC or 0x110 (memory-card pack).");
            }
        }

        private void LoadType1(byte[] data)
        {
            _archiveType = 1;
            _entries.Clear();

            _head1 = new Type1Header
            {
                MainHeaderSize = R32(data, 0x00),
                FileHeaderSize = R32(data, 0x04),
                Version = R32(data, 0x08),
                ArchiveHeaderSize = R32(data, 0x0C),
                BodySize = R32(data, 0x10),
                UnusedId = R32(data, 0x14),
                Compression = R32(data, 0x18),
                Errors = R16(data, 0x1C),
                Warnings = R16(data, 0x1E),
            };

            uint fileCount = R32(data, 0xC4);
            uint cursor = _head1.MainHeaderSize;

            for (uint i = 0; i < fileCount; i++)
            {
                string fullPath = ReadCString(data, cursor);
                string name = PathFileName(fullPath.Replace('\\', '/'));

                uint varName = R32(data, cursor + 0x40);
                uint dbId = R32(data, cursor + 0x44);
                uint entry = R32(data, cursor + 0x48);
                uint size = R32(data, cursor + 0x4C);
                uint checksum = R32(data, cursor + 0x50);

                _entries.Add(new ZdbEntry
                {
                    FullPath = fullPath,
                    Name = name,
                    Data = ReadBlock(data, entry + _head1.ArchiveHeaderSize, size),
                    VarName = varName,
                    DbId = dbId,
                    Checksum = checksum,
                });

                cursor += _head1.FileHeaderSize;
            }
        }

        private void LoadType2(byte[] data)
        {
            _archiveType = 2;
            _entries.Clear();

            _head2 = new Type2Header
            {
                MainHeaderSize = R32(data, 0x00),
                Unknown1 = R32(data, 0x04),
                UnknownId = R32(data, 0x08),   // preserved as-is (e.g. 0x3F8898BE in M51.ZDB)
                Unknown2 = R32(data, 0x0C),
                Unknown3 = R32(data, 0x10),
                Version = ReadCString(data, 0x14),
            };

            uint fileCount = R32(data, 0x98);
            uint cursor = 0xA0;

            for (uint i = 0; i < fileCount; i++)
            {
                uint headSize = R32(data, cursor);

                // Preserve the raw 0x40-byte name field verbatim — bytes after the null
                // terminator are non-zero garbage in real archives (confirmed M51.ZDB).
                byte[] rawName = ReadBlock(data, cursor + 4, 0x40);
                string fullPath = ReadCString(data, cursor + 4);
                string name = PathFileName(fullPath.Replace('\\', '/'));

                uint entry = R32(data, cursor + 0x44);
                uint size = R32(data, cursor + 0x48);
                // +0x4C..+0x57 = three zero padding words (confirmed zero in M51.ZDB)
                uint trailingWord = R32(data, cursor + 0x58); // non-zero in real archives

                _entries.Add(new ZdbEntry
                {
                    FullPath = fullPath,
                    Name = name,
                    Data = ReadBlock(data, entry, size),
                    RawNameField = rawName,
                    TrailingWord = trailingWord,
                });

                cursor += headSize;
            }
        }

        // ====================================================================
        // Private – serialisation
        // ====================================================================

        private byte[] SerializeType1()
        {
            int fileCount = _entries.Count;

            // Pre-calculate 16-byte-aligned body offsets.
            var offsets = new uint[fileCount];
            uint bodySize = 0;
            for (int i = 0; i < fileCount; i++)
            {
                offsets[i] = bodySize;
                bodySize = Align16(bodySize + (uint)_entries[i].Data.Length);
            }

            uint archiveHeaderSize = 0xC8u + (uint)(fileCount * 0x58);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            // Main header
            bw.Write(0x000000C8u);       // MainHeaderSize
            bw.Write(0x00000058u);       // FileHeaderSize
            bw.Write(0x00000006u);       // Version
            bw.Write(archiveHeaderSize); // ArchiveHeaderSize
            bw.Write(bodySize);          // BodySize
            bw.Write(0x4532ABCDu);       // UnusedId sentinel
            bw.Write(0x00000000u);       // Compression
            bw.Write((ushort)0);         // Errors
            bw.Write((ushort)0);         // Warnings
            for (int i = 0x20; i < 0xC4; i += 4)
                bw.Write(0u);
            bw.Write((uint)fileCount);   // FileCount @ 0xC4

            // Per-file headers
            for (int i = 0; i < fileCount; i++)
            {
                ZdbEntry e = _entries[i];
                WritePaddedString(bw, e.FullPath, 0x40);
                bw.Write(e.VarName);
                bw.Write(e.DbId);
                bw.Write(offsets[i]);
                bw.Write(e.Size);
                bw.Write(e.Checksum);
                bw.Write(0u); // reserved
            }

            // Body (16-byte aligned)
            for (int i = 0; i < fileCount; i++)
            {
                bw.Write(_entries[i].Data);
                uint written = (uint)_entries[i].Data.Length;
                while ((written & 0xF) != 0) { bw.Write((byte)0); written++; }
            }

            return ms.ToArray();
        }

        private byte[] SerializeType2()
        {
            int fileCount = _entries.Count;
            uint dataStart = 0xA0u + (uint)(fileCount * 0x5C);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            // Main header — write UnknownId as read (not hardcoded 0x12345678,
            // which is only the APACHE00.ZDB sentinel; M51.ZDB has 0x3F8898BE).
            bw.Write(0x000000FCu);
            bw.Write(_head2.Unknown1);
            bw.Write(_head2.UnknownId);   // preserved verbatim from load
            bw.Write(_head2.Unknown2);
            bw.Write(_head2.Unknown3);

            string ver = _head2.Version ?? string.Empty;
            if (ver.Length > 0)
            {
                bw.Write(ver.ToCharArray());
                for (int i = 0x14 + ver.Length; i < 0x98; i++) bw.Write((byte)0);
            }
            else
            {
                for (int i = 0x14; i < 0x98; i++) bw.Write((byte)0);
            }

            bw.Write((uint)fileCount);  // @ 0x98
            bw.Write(0x0000005Cu);      // FileHeaderSize @ 0x9C

            // Per-file headers
            uint running = dataStart;
            foreach (ZdbEntry e in _entries)
            {
                bw.Write(0x0000005Cu);  // FileHeadSize

                // Write the name field: use the preserved raw bytes when available
                // (they contain non-zero garbage after the null in real archives),
                // otherwise fall back to writing the string with zero padding.
                if (e.RawNameField != null && e.RawNameField.Length == 0x40)
                {
                    bw.Write(e.RawNameField);
                }
                else
                {
                    WritePaddedString(bw, e.FullPath, 0x40);
                }

                bw.Write(running);      // absolute data offset @ +0x44
                bw.Write(e.Size);       // @ +0x48
                bw.Write(0u);           // padding +0x4C (confirmed zero)
                bw.Write(0u);           // padding +0x50 (confirmed zero)
                bw.Write(0u);           // padding +0x54 (confirmed zero)
                bw.Write(e.TrailingWord); // preserved @ +0x58 (non-zero in real archives)

                running += e.Size;
            }

            // Body
            foreach (ZdbEntry e in _entries)
                bw.Write(e.Data);

            return ms.ToArray();
        }

        // ====================================================================
        // Private – blank archive builders
        // ====================================================================

        private static byte[] BuildBlankType1()
        {
            var b = new byte[0xC8];
            b[0x00] = 0xC8; b[0x04] = 0x58; b[0x08] = 0x06;
            // Sentinel 0x12345678 little-endian @ 0x14
            b[0x14] = 0x78; b[0x15] = 0x56; b[0x16] = 0x34; b[0x17] = 0x12;
            return b;
        }

        private static byte[] BuildBlankType2()
        {
            var b = new byte[0xFC];
            b[0x00] = 0xFC; b[0x04] = 0x01;
            // Sentinel 0x12345678 little-endian @ 0x08
            b[0x08] = 0x78; b[0x09] = 0x56; b[0x0A] = 0x34; b[0x0B] = 0x12;
            b[0x0C] = 0x02;
            return b;
        }

        // ====================================================================
        // Private – binary helpers
        // ====================================================================

        private static uint R32(byte[] d, uint o) => BitConverter.ToUInt32(d, (int)o);
        private static ushort R16(byte[] d, uint o) => BitConverter.ToUInt16(d, (int)o);

        private static byte[] ReadBlock(byte[] data, uint offset, uint length)
        {
            var buf = new byte[length];
            Buffer.BlockCopy(data, (int)offset, buf, 0, (int)length);
            return buf;
        }

        private static string ReadCString(byte[] data, uint offset)
        {
            var sb = new StringBuilder();
            while (offset < data.Length && data[offset] != 0)
                sb.Append((char)data[offset++]);
            return sb.ToString();
        }

        private static void WritePaddedString(BinaryWriter bw, string value, int width)
        {
            int written = 0;
            foreach (char c in value)
            {
                if (written == width) return;
                bw.Write(c);
                written++;
            }
            for (; written < width; written++)
                bw.Write((byte)0);
        }

        private static uint Align16(uint v) => (v + 0xFu) & ~0xFu;

        // ====================================================================
        // Private – path helpers
        // ====================================================================

        /// <summary>
        /// Stores in the on-disk format: strips "z:/", converts slashes to backslash,
        /// collapses duplicate separators.
        /// </summary>
        private static string NormalizeArchivePath(string path)
        {
            if (path.StartsWith("z:/", StringComparison.OrdinalIgnoreCase))
                path = path.Substring(3);
            else if (path.StartsWith("z:", StringComparison.OrdinalIgnoreCase))
                path = path.Substring(2);

            path = path.Replace('/', '\\');

            while (path.Contains("\\\\"))
                path = path.Replace("\\\\", "\\");

            return path;
        }

        /// <summary>
        /// Comparison form: lowercase, forward slashes, no leading "z:", no trailing slash.
        /// </summary>
        private static string NormalizeForComparison(string path)
        {
            string p = path.Replace('\\', '/').ToLowerInvariant();

            if (p.StartsWith("z:/")) p = p.Substring(3);
            else if (p.StartsWith("z:")) p = p.Substring(2);

            while (p.Contains("//"))
                p = p.Replace("//", "/");

            return p.TrimEnd('/');
        }

        /// <summary>
        /// Returns the parent directory of a path (everything before the last separator).
        /// </summary>
        private static string GetParentDirectory(string fullPath)
        {
            string p = fullPath.Replace('\\', '/');
            int lastSlash = p.LastIndexOf('/');
            return lastSlash < 0 ? string.Empty : p.Substring(0, lastSlash);
        }

        /// <summary>
        /// True when <paramref name="entryDir"/> equals <paramref name="prefix"/>
        /// or is a subdirectory of it. An empty prefix matches everything (archive root).
        /// </summary>
        private static bool IsUnderOrEqual(string entryDir, string prefix)
        {
            if (prefix.Length == 0) return true;
            return string.Equals(entryDir, prefix, StringComparison.OrdinalIgnoreCase)
                || entryDir.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Returns the file-name component of a forward-slash path.</summary>
        private static string PathFileName(string path)
        {
            string tmp = (path + "/").Replace("//", "/");
            string[] parts = tmp.Split('/');
            return parts.Length >= 2 ? parts[^2] : path;
        }

        // ====================================================================
        // Private – wildcard matching
        // ====================================================================

        /// <summary>
        /// Case-insensitive wildcard match.
        /// <list type="bullet">
        ///   <item><c>?</c>  – matches exactly one character that is not a path separator.</item>
        ///   <item><c>*</c>  – matches any sequence of non-separator characters (same directory).</item>
        ///   <item><c>**</c> – matches any sequence of characters including path separators.</item>
        /// </list>
        /// </summary>
        /// <remarks>
        /// Uses an iterative backtracking algorithm to avoid recursion overhead on long paths.
        /// </remarks>
        private static bool WildcardMatch(string input, string pattern)
        {
            // ip  = current position in input
            // pp  = current position in pattern
            // lastStarIp / lastStarPp = position saved when we last hit a '*' or '**'
            int ip = 0, pp = 0;
            int lastStarIp = -1, lastStarPp = -1;
            bool lastStarWasDouble = false; // whether the saved star was **

            while (ip < input.Length)
            {
                if (pp < pattern.Length)
                {
                    char pc = pattern[pp];
                    char ic = input[ip];

                    // Double-star (**): crosses directory separators unconditionally.
                    if (pc == '*' && pp + 1 < pattern.Length && pattern[pp + 1] == '*')
                    {
                        lastStarIp = ip;
                        lastStarPp = pp;
                        lastStarWasDouble = true;
                        pp += 2; // skip both *
                        continue;
                    }

                    // Single-star (*): does NOT cross a '/' separator.
                    if (pc == '*')
                    {
                        lastStarIp = ip;
                        lastStarPp = pp;
                        lastStarWasDouble = false;
                        pp++;
                        continue;
                    }

                    // Question mark (?): matches any single char except '/'.
                    bool charOk = pc == '?'
                        ? ic != '/'
                        : char.ToLowerInvariant(pc) == char.ToLowerInvariant(ic);

                    if (charOk) { ip++; pp++; continue; }
                }

                // Mismatch – backtrack to the most recent star.
                if (lastStarIp >= 0)
                {
                    // A single * cannot consume a '/' separator.
                    if (!lastStarWasDouble && input[lastStarIp] == '/')
                        return false;

                    // Let the star consume one more character and retry.
                    lastStarIp++;
                    ip = lastStarIp;
                    pp = lastStarPp + (lastStarWasDouble ? 2 : 1);
                    continue;
                }

                return false;
            }

            // Consume any trailing '*' or '**' at the end of the pattern.
            while (pp < pattern.Length && pattern[pp] == '*')
                pp++;

            return pp == pattern.Length;
        }

        private void EnsureOpen()
        {
            if (_archiveType == 0)
                throw new InvalidOperationException("No archive is currently loaded.");
        }
    }

    
}
