using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.ZAR
{
    /// <summary>
    /// Archive-shaped facade for ZAR files.
    /// Under the hood the format uses the same record container as ZED files, but this wrapper
    /// exposes the ZDB-like archive workflow for files that behave like a real archive.
    /// </summary>
    public sealed class ZarArchive : IDisposable
    {
        private readonly ZedContainer _container;

        private ZarArchive(ZedContainer container)
        {
            _container = container;
        }

        public uint RootValue
        {
            get => _container.RootValue;
            set => _container.RootValue = value;
        }

        public uint StringBasePointer
        {
            get => _container.StringBasePointer;
            set => _container.StringBasePointer = value;
        }

        public uint HeaderWord5C
        {
            get => _container.HeaderWord5C;
            set => _container.HeaderWord5C = value;
        }

        public uint DataAlignment
        {
            get => _container.DataAlignment;
            set => _container.DataAlignment = value;
        }

        public IReadOnlyList<ZedNode> Entries => _container.Entries;
        public int EntryCount => _container.EntryCount;
        public ZedContainer Container => _container;

        public static ZarArchive Open(string filePath) => new ZarArchive(ZedContainer.Open(filePath));
        public static ZarArchive FromBytes(byte[] data) => new ZarArchive(ZedContainer.FromBytes(data));

        public static ZarArchive CreateNew(
            uint stringBasePointer = 0x10000000u,
            uint rootValue = 0,
            uint headerWord5C = 0,
            uint dataAlignment = ZedContainer.DefaultDataAlignment) =>
            new ZarArchive(ZedContainer.CreateNew(stringBasePointer, rootValue, headerWord5C, dataAlignment));

        public ZedNode GetEntry(int index) => _container.GetEntry(index);
        public ZedNode? FindEntry(string name) => _container.FindEntry(name);
        public ZedNode AddEntry(string name, byte[] data, uint type = 0) => _container.AddEntry(name, data, type);
        public void RenameEntry(int index, string newName) => _container.RenameNode(index, newName);
        public void ReplaceEntryData(int index, byte[] newData) => _container.ReplaceEntryData(index, newData);
        public void RemoveEntry(int index) => _container.RemoveEntry(index);
        public int RemoveEntry(string name) => _container.RemoveEntry(name);
        public void ExtractEntry(int index, string outputPath) => _container.ExtractEntry(index, outputPath);
        public void ExtractAll(string outputDirectory) => _container.ExtractAll(outputDirectory);
        public void RepackData() => _container.RepackData();
        public void Save(string filePath) => _container.Save(filePath);
        public byte[] ToBytes() => _container.ToBytes();
        public void Dispose() => _container.Dispose();
    }
}
