using System;
using System.IO;
using System.Linq;

namespace CodeDesigner.Ps2.FileFormats.Zipper.ZED
{
    /// <summary>
    /// Replaces one record's bytes in a ZED / ZAR file and leaves every other byte as it was (header, string table,
    /// the other records, the padding between them). <see cref="ZedContainer.ToBytes"/> repacks the whole body; this
    /// is for editing one record in place. A record that fits in its slot (up to the next record) is written there;
    /// a bigger one grows the slot, by whole alignment units, and the records after it move up by as much.
    /// </summary>
    public static class ZedSplice
    {
        private const int FixedHeaderSize = 0x64;
        private const int RecordSize = 0x10;

        public static byte[] Replace(byte[] container, int index, byte[] data)
        {
            if (container.Length < FixedHeaderSize) throw new InvalidDataException("Too short to be a ZED/ZAR container.");
            int slots = BitConverter.ToInt32(container, 0x04);
            int stringSize = BitConverter.ToInt32(container, 0x08);
            int align = Math.Max(16, BitConverter.ToInt32(container, 0x10));
            uint bodySize = BitConverter.ToUInt32(container, 0x54);
            int records = FixedHeaderSize + stringSize + 0x10;
            int count = slots - 1;
            if (index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index), $"No record #{index}");
            int recordEnd = records + count * RecordSize;
            int bodyStart = bodySize == 0 ? (recordEnd + align - 1) / align * align : container.Length - (int)bodySize;
            int bodyLength = container.Length - bodyStart;

            int rec = records + index * RecordSize;
            int offset = BitConverter.ToInt32(container, rec + 4), size = BitConverter.ToInt32(container, rec + 8);
            if (size == 0)
            {
                // A record without a payload gets one at the end of the body.
                if (data.Length == 0) return (byte[])container.Clone();
                offset = (bodyLength + 15) / 16 * 16;
            }
            // Its slot: up to the next payload, or the end of the body.
            int next = Enumerable.Range(0, count)
                .Select(i => (Offset: BitConverter.ToInt32(container, records + i * RecordSize + 4), Size: BitConverter.ToInt32(container, records + i * RecordSize + 8)))
                .Where(r => r.Size > 0 && r.Offset > offset).Select(r => r.Offset).DefaultIfEmpty(Math.Max(bodyLength, offset)).Min();
            int slot = next - offset;
            int grow = data.Length > slot ? (data.Length - slot + align - 1) / align * align : 0;

            var output = new byte[container.Length + grow + (size == 0 ? Math.Max(0, offset - bodyLength) : 0)];
            int at = bodyStart + offset;
            // Everything up to the end of the record's slot as it was (its padding too), then the record over it;
            // a grown slot's new bytes are zeros.
            Buffer.BlockCopy(container, 0, output, 0, Math.Min(at + slot, container.Length));
            Buffer.BlockCopy(data, 0, output, at, data.Length);
            int tailFrom = bodyStart + next, tailTo = at + slot + grow;
            if (tailFrom < container.Length) Buffer.BlockCopy(container, tailFrom, output, tailTo, container.Length - tailFrom);

            BitConverter.GetBytes(offset).CopyTo(output, rec + 4);
            BitConverter.GetBytes(data.Length).CopyTo(output, rec + 8);
            if (grow > 0)
                for (int i = 0; i < count; i++)
                {
                    int r = records + i * RecordSize;
                    int o = BitConverter.ToInt32(output, r + 4);
                    if (i != index && BitConverter.ToInt32(output, r + 8) > 0 && o > offset) BitConverter.GetBytes(o + grow).CopyTo(output, r + 4);
                }
            if (bodySize != 0) BitConverter.GetBytes((uint)(output.Length - bodyStart)).CopyTo(output, 0x54);
            return output;
        }
    }
}
