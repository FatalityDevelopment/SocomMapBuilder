using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeDesigner.Ps2.FileFormats.Zipper.RDR
{
    /// <summary>
    /// A language table (<c>RUN/LOCALE/STATES/*LOC.ZAR</c>, e.g. UIMnLOC.rdr): a list of records
    /// [ID, [133], MSG_ID, ["_133_Model18_MSG"], LOCALIZED, ["Model 18"]]. The armory shows a weapon by these texts:
    /// its short name (the text equal to the weapon's DisplayName, "Model 18") and its caption ("AUTOMATIC PISTOL:
    /// MODEL 18", ending in its Description). Text is set on the record's own atom: retail records share values.
    /// </summary>
    public sealed class LanguageTable
    {
        public RdrDocument Document { get; }

        public LanguageTable(byte[] rdr) => Document = RdrDocument.Parse(rdr);

        public byte[] ToBytes() => Document.ToBytes();

        /// <summary>Every message: its key (MSG_ID) and the value list holding its text (LOCALIZED).</summary>
        public IEnumerable<(string Key, int TextList)> Entries()
        {
            for (int record = 0; record < Document.AtomCount; record++)
            {
                if (Document.TypeOf(record) != RdrDocument.List) continue;
                int key = ValueList(record, "MSG_ID"), text = ValueList(record, "LOCALIZED");
                if (key < 0 || text < 0) continue;
                if (Document.Children(key).Select(Document.ValueOf).FirstOrDefault() is string k) yield return (k, text);
            }
        }

        private int ValueList(int record, string name)
        {
            var items = Document.Children(record).ToList();
            for (int i = 0; i + 1 < items.Count; i++)
                if (Document.TypeOf(items[i]) == RdrDocument.String && Document.ValueOf(items[i]) as string == name
                    && Document.TypeOf(items[i + 1]) == RdrDocument.List) return items[i + 1];
            return -1;
        }

        private string? TextOf(int list) => Document.Children(list).Select(Document.ValueOf).FirstOrDefault() as string;

        public string? Text(string key) => Entries().Where(e => e.Key == key).Select(e => TextOf(e.TextList)).FirstOrDefault();

        public void SetText(string key, string text)
        {
            var hits = Entries().Where(e => e.Key == key).ToList();
            if (hits.Count == 0) throw new KeyNotFoundException($"The language table has no {key}");
            foreach (var (_, list) in hits) Document.SetString(Document.Own(list), text);
        }

        /// <summary>Keys whose text is <paramref name="text"/> (case-insensitive).</summary>
        public List<string> KeysWithText(string text) =>
            Entries().Where(e => string.Equals(TextOf(e.TextList), text, StringComparison.OrdinalIgnoreCase)).Select(e => e.Key).Distinct().ToList();

        /// <summary>Caption keys: texts "&lt;KIND&gt;: &lt;name&gt;" (case-insensitive), with their kind.</summary>
        public List<(string Key, string Kind)> Captions(string name) =>
            Entries().Select(e => (e.Key, Text: TextOf(e.TextList) ?? string.Empty))
                .Where(e => e.Text.EndsWith(": " + name, StringComparison.OrdinalIgnoreCase) && e.Text.Length > name.Length + 2)
                .Select(e => (e.Key, e.Text[..^(name.Length + 2)])).Distinct().ToList();
    }
}
