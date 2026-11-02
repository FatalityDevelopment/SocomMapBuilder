using System.Buffers.Binary;
using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Ps2;
using CodeDesigner.Ps2.FileFormats.Zipper.RDR;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;
using MapBuilder.Server;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>The Asset Viewer: its listings and previews, and the disc formats only it reads.</summary>
    public sealed class ViewerTests : IDisposable
    {
        private readonly SyntheticPackage _pkg = new();
        private readonly Backend _b = new();

        public void Dispose() => _pkg.Dispose();

        private JsonElement List(string path, params int[] inner) =>
            _b.Ok(new CommandMessage { Command = "VIEWER_LIST", InputPath = path, AssetJson = JsonSerializer.Serialize(new { inner }) });

        private JsonElement Preview(string path, params int[] inner) =>
            _b.Ok(new CommandMessage { Command = "VIEWER_PREVIEW", InputPath = path, AssetJson = JsonSerializer.Serialize(new { inner }) });

        private static JsonElement Item(JsonElement listing, string name) =>
            listing.GetProperty("items").EnumerateArray().First(i => i.GetProperty("name").GetString() == name);

        // ------------------------------------------------------------------ listings

        [Fact]
        public void A_folder_lists_its_files_by_kind()
        {
            var l = List(_pkg.Folder);
            Assert.Equal("folder", l.GetProperty("kind").GetString());
            Assert.Equal("models", Item(l, "TST_MDL.ZED").GetProperty("kind").GetString());
            Assert.Equal("geo", Item(l, "TST_GEO.ZED").GetProperty("kind").GetString());
            Assert.Equal("archive", Item(l, "CLUTTER.ZAR").GetProperty("kind").GetString());
            Assert.Equal("mps", Item(l, "AIMAPS.MPS").GetProperty("kind").GetString());
        }

        [Fact]
        public void A_model_archive_lists_the_editors_models_from_a_session()
        {
            var l = List(Path.Combine(_pkg.Folder, "TST_MDL.ZED"));
            Assert.False(string.IsNullOrEmpty(l.GetProperty("session").GetString()));
            var items = l.GetProperty("items").EnumerateArray().ToList();
            Assert.NotEmpty(items);
            Assert.All(items, i => Assert.Equal("model", i.GetProperty("kind").GetString()));
            Assert.Equal("TST_MDL.ZED", items[0].GetProperty("ref").GetProperty("archive").GetString());
        }

        [Fact]
        public void A_package_lists_its_files_with_their_paths_in_the_package()
        {
            string zdb = _pkg.WriteZdb();
            var l = List(zdb);
            Assert.Equal("package", l.GetProperty("kind").GetString());
            var mdl = Item(l, "TST_MDL.ZED");
            Assert.EndsWith("TST_MDL.ZED", mdl.GetProperty("entryPath").GetString());
            // Into a root entry: its models; into CLUTTER.ZAR: its records.
            int at = mdl.GetProperty("inner")[0].GetInt32();
            Assert.Equal("model", List(zdb, at).GetProperty("items")[0].GetProperty("kind").GetString());
            int clutter = Item(l, "CLUTTER.ZAR").GetProperty("inner")[0].GetInt32();
            Assert.NotEmpty(List(zdb, clutter).GetProperty("items").EnumerateArray());
        }

        [Fact]
        public void An_editor_opening_the_package_takes_over_the_viewers_session()
        {
            // A loose file's package is its folder: the viewer's "Open in Model Editor" opens that.
            var listing = List(Path.Combine(_pkg.Folder, "TST_MDL.ZED"));
            string viewer = listing.GetProperty("session").GetString()!;
            string editor = _b.Open(listing.GetProperty("package").GetString()!);
            Assert.Equal(viewer, editor);
            _b.Close(editor);
        }

        [Fact]
        public void Previews_and_extract_read_the_records_as_stored()
        {
            string zdb = _pkg.WriteZdb();
            var l = List(zdb);
            int mps = Item(l, "AIMAPS.MPS").GetProperty("inner")[0].GetInt32();
            Assert.Equal("mps", Preview(zdb, mps).GetProperty("type").GetString());

            string target = Path.Combine(Path.GetDirectoryName(_pkg.Folder)!, "out", "AIMAPS.MPS");
            _b.Ok(new CommandMessage { Command = "VIEWER_EXTRACT", InputPath = zdb, OutputPath = target, AssetJson = JsonSerializer.Serialize(new { inner = new[] { mps } }) });
            Assert.Equal(File.ReadAllBytes(Path.Combine(_pkg.Folder, "AIMAPS.MPS")), File.ReadAllBytes(target));
        }

        // ------------------------------------------------------------------ editing

        private JsonElement Edit(string path, int[] inner, object op) =>
            _b.Ok(new CommandMessage { Command = "VIEWER_EDIT", InputPath = path, AssetJson = JsonSerializer.Serialize(op).TrimEnd('}') + $",\"inner\":{JsonSerializer.Serialize(inner)}}}" });

        private static int RdrValue(JsonElement tree, int atom)
        {
            if (tree[0].GetInt32() == atom) return tree[2].GetInt32();
            if (tree[1].GetString() != "list") return int.MinValue;
            foreach (var c in tree[3].EnumerateArray()) { int v = RdrValue(c, atom); if (v != int.MinValue) return v; }
            return int.MinValue;
        }

        [Fact]
        public void An_rdr_inside_an_archive_edits_with_undo_redo_and_save()
        {
            string dir = Path.Combine(Path.GetDirectoryName(_pkg.Folder)!, "LOOSE");
            Directory.CreateDirectory(dir);
            var zar = ZedContainer.CreateNew();
            zar.AddEntry("first.bin", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17 });
            zar.AddEntry("menu.rdr", RdrDocumentTests.Sample());
            string path = Path.Combine(dir, "MENU.ZAR");
            File.WriteAllBytes(path, zar.ToBytes());
            byte[] before = File.ReadAllBytes(path);

            var r = Edit(path, new[] { 1 }, new { op = "rdrSet", atom = 5, value = "42" });
            string session = r.GetProperty("session").GetString()!;
            Assert.Equal(42, RdrValue(Preview(path, 1).GetProperty("tree"), 5));
            Assert.Equal(session, List(path).GetProperty("editSession").GetString());

            _b.Undo(session);
            Assert.Equal(3, RdrValue(Preview(path, 1).GetProperty("tree"), 5));
            _b.Redo(session);

            string outDir = Path.Combine(dir, "out");
            _b.Ok(new CommandMessage { Command = "ASSET_SAVE", AssetSession = session, AssetJson = JsonSerializer.Serialize(new { mode = "zdb", outputPath = outDir }) });
            byte[] saved = File.ReadAllBytes(Path.Combine(outDir, "MENU.ZAR"));
            var nodes = ZedContainer.FromBytes(saved).Nodes;
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17 }, nodes[0].Data);
            Assert.Equal(42, RdrDocument.Parse(nodes[1].Data).ValueOf(5));
            // Only the edited value's four bytes changed.
            Assert.Equal(before.Length, saved.Length);
            Assert.InRange(before.Zip(saved).Count(p => p.First != p.Second), 1, 4);
            _b.Close(session);
        }

        [Fact]
        public void An_ai_map_in_a_package_renames_and_text_files_edit()
        {
            string zdb = _pkg.WriteZdb();
            int mps = Item(List(zdb), "AIMAPS.MPS").GetProperty("inner")[0].GetInt32();
            var maps = Preview(zdb, mps).GetProperty("maps");
            Assert.True(maps.GetArrayLength() > 0);
            var r = Edit(zdb, new[] { mps }, new { op = "mpsMap", map = 0, name = "renamed" });
            Assert.Equal("renamed", Preview(zdb, mps).GetProperty("maps")[0].GetProperty("name").GetString());
            // Records keep their positions once the package has edits.
            Assert.Equal(mps, Item(List(zdb), "AIMAPS.MPS").GetProperty("inner")[0].GetInt32());
            Assert.Contains("AIMAPS.MPS", r.GetProperty("status").GetProperty("dirty").EnumerateArray().Select(x => x.GetString()));
            _b.Close(r.GetProperty("session").GetString()!);

            string text = Path.Combine(_pkg.Folder, "NOTES.CNF");
            File.WriteAllText(text, "BOOT2 = cdrom0:\\X;1\n");
            var t = Edit(text, Array.Empty<int>(), new { op = "text", text = "VMODE = PAL\n" });
            Assert.Equal("VMODE = PAL\n", Preview(text).GetProperty("text").GetString());
            _b.Fails(new CommandMessage { Command = "VIEWER_EDIT", InputPath = text, AssetJson = "{\"op\":\"nothing\"}" });
            _b.Close(t.GetProperty("session").GetString()!);
        }

        // ------------------------------------------------------------------ sound stores

        // A ZAR laid out like RUN/SOUNDS/*STORE.ZAR: no body size, the body on the next 2048-byte sector.
        private static byte[] Store(out byte[][] payloads)
        {
            var c = ZedContainer.CreateNew();
            payloads = new[] { Enumerable.Range(0, 40).Select(i => (byte)i).ToArray(), Enumerable.Range(0, 24).Select(i => (byte)(200 - i)).ToArray() };
            c.AddEntry("A.vag", payloads[0]);
            c.AddEntry("B.vag", payloads[1]);
            byte[] normal = c.ToBytes();
            int slots = BitConverter.ToInt32(normal, 4), strings = BitConverter.ToInt32(normal, 8), body = BitConverter.ToInt32(normal, 0x54);
            int recordEnd = 0x64 + strings + 0x10 + (slots - 1) * 0x10;
            var store = new byte[0x800 + body];
            Array.Copy(normal, store, recordEnd);
            Array.Copy(normal, normal.Length - body, store, 0x800, body);
            BinaryPrimitives.WriteUInt32LittleEndian(store.AsSpan(0x10), 0x800);
            BinaryPrimitives.WriteUInt32LittleEndian(store.AsSpan(0x54), 0);
            return store;
        }

        [Fact]
        public void Sound_stores_read_whole_and_through_their_record_table()
        {
            byte[] store = Store(out var payloads);
            var nodes = ZedContainer.FromBytes(store).Nodes;
            Assert.Equal(payloads[0], nodes[0].Data);
            Assert.Equal(payloads[1], nodes[1].Data);

            string path = Path.Combine(Path.GetDirectoryName(_pkg.Folder)!, "STORE.ZAR");
            File.WriteAllBytes(path, store);
            var index = ZedIndex.Open(path);
            Assert.Equal(new[] { "A.vag", "B.vag" }, index.Entries.Select(e => e.Name));
            Assert.Equal(payloads[1], index.Read(1));
        }

        // ------------------------------------------------------------------ SPU ADPCM

        private static byte[] Block(byte flags, int nibble) =>
            new byte[] { 0x0C, flags }.Concat(Enumerable.Repeat((byte)(nibble | nibble << 4), 14)).ToArray();

        [Fact]
        public void Adpcm_decodes_until_the_end_flag()
        {
            // Shift 12, no prediction: each sample is its signed nibble.
            byte[] blocks = Block(0, 3).Concat(Block(1, 0xF)).Concat(Block(0, 5)).ToArray();
            short[] pcm = SpuAdpcm.Decode(blocks);
            Assert.Equal(56, pcm.Length);
            Assert.All(pcm.Take(28), s => Assert.Equal(3, s));
            Assert.All(pcm.Skip(28), s => Assert.Equal(-1, s));
        }

        [Fact]
        public void A_bank_body_splits_at_end_flags_and_skips_silence()
        {
            byte[] silence = new byte[16];
            byte[] body = silence.Concat(Block(0, 2)).Concat(Block(1, 2)).Concat(silence).Concat(Block(0, 4)).Concat(Block(1, 4)).ToArray();
            var samples = SpuAdpcm.SplitSamples(body);
            Assert.Equal(new[] { (16, 32), (64, 32) }, samples);
        }

        [Fact]
        public void Vag_headers_and_wav_files()
        {
            var vag = new byte[0x30 + 32];
            "VAGp"u8.CopyTo(vag);
            BinaryPrimitives.WriteInt32BigEndian(vag.AsSpan(0x0C), 32);
            BinaryPrimitives.WriteInt32BigEndian(vag.AsSpan(0x10), 22050);
            "TEST"u8.CopyTo(vag.AsSpan(0x20));
            var h = SpuAdpcm.ReadVag(vag)!;
            Assert.Equal((32, 22050, "TEST", 0x30), (h.DataSize, h.SampleRate, h.Name, h.DataOffset));

            byte[] wav = SpuAdpcm.Wav(new short[] { 1, -1 }, 22050);
            Assert.Equal(48, wav.Length);
            Assert.Equal(22050, BitConverter.ToInt32(wav, 24));
        }

        // ------------------------------------------------------------------ movies

        // A PSS: a pack header, then private-stream-1 packets carrying the sound track (SShd: PCM, 48 kHz, stereo,
        // 4-byte interleave), split across packets mid-block, with a video packet and some zero padding between.
        private static byte[] Pss()
        {
            var track = new List<byte>();
            track.AddRange("SShd"u8.ToArray()); track.AddRange(BitConverter.GetBytes(0x18));
            foreach (int v in new[] { 1, 48000, 2, 4, 0, 0 }) track.AddRange(BitConverter.GetBytes(v));
            track.AddRange("SSbd"u8.ToArray()); track.AddRange(BitConverter.GetBytes(16));
            // Left samples 1, 2 then right 101, 102; then left 3, 4 and right 103, 104.
            foreach (short v in new short[] { 1, 2, 101, 102, 3, 4, 103, 104 }) track.AddRange(BitConverter.GetBytes(v));
            byte[] all = track.ToArray();
            static byte[] Audio(byte[] part) =>
                new byte[] { 0, 0, 1, 0xBD }.Concat(BitConverter.GetBytes((ushort)(3 + 5 + 4 + part.Length)).Reverse())
                    .Concat(new byte[] { 0x81, 0x80, 0x05, 0x21, 0, 1, 0, 1, 0xFF, 0xA0, 0, 0 }).Concat(part).ToArray();
            byte[] pack = { 0, 0, 1, 0xBA, 0x44, 0, 4, 0, 4, 1, 0, 0, 3, 0xF8 };
            byte[] video = { 0, 0, 1, 0xE0, 0, 4, 1, 2, 3, 4 };
            return pack.Concat(Audio(all[..50])).Concat(video).Concat(new byte[64]).Concat(pack).Concat(Audio(all[50..])).Concat(new byte[] { 0, 0, 1, 0xB9 }).ToArray();
        }

        [Fact]
        public void Movie_sound_comes_out_as_an_interleaved_wav()
        {
            string dir = Path.GetDirectoryName(_pkg.Folder)!;
            string pss = Path.Combine(dir, "TEST.PSS"), wav = Path.Combine(dir, "TEST.WAV");
            File.WriteAllBytes(pss, Pss());
            var format = PssAudio.ExtractWav(pss, wav);
            Assert.Equal(new PssAudio.Format(1, 48000, 2, 4), format);
            byte[] w = File.ReadAllBytes(wav);
            Assert.Equal(48000, BitConverter.ToInt32(w, 24));
            Assert.Equal(2, BitConverter.ToInt16(w, 22));
            var samples = Enumerable.Range(0, (w.Length - 44) / 2).Select(i => BitConverter.ToInt16(w, 44 + i * 2)).ToArray();
            Assert.Equal(new short[] { 1, 101, 2, 102, 3, 103, 4, 104 }, samples);
        }

        [Fact]
        public void A_movie_without_a_sound_track_writes_no_wav()
        {
            string dir = Path.GetDirectoryName(_pkg.Folder)!;
            string pss = Path.Combine(dir, "SILENT.PSS"), wav = Path.Combine(dir, "SILENT.WAV");
            File.WriteAllBytes(pss, new byte[] { 0, 0, 1, 0xBA, 0x44, 0, 4, 0, 4, 1, 0, 0, 3, 0xF8, 0, 0, 1, 0xE0, 0, 2, 1, 2, 0, 0, 1, 0xB9 });
            Assert.Null(PssAudio.ExtractWav(pss, wav));
            Assert.False(File.Exists(wav));
        }

        // ------------------------------------------------------------------ icons and screens

        [Fact]
        public void Icons_turn_to_y_up_and_keep_their_texture_top_down()
        {
            const int verts = 3, stride = 8 + 8 + 4 + 4;
            int anim = 20 + verts * stride, tex = anim + 20;
            var ico = new byte[tex + 128 * 128 * 2];
            BinaryPrimitives.WriteUInt32LittleEndian(ico, 0x00010000);
            BinaryPrimitives.WriteInt32LittleEndian(ico.AsSpan(4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(ico.AsSpan(8), 0x07);
            BinaryPrimitives.WriteSingleLittleEndian(ico.AsSpan(12), 1f);
            BinaryPrimitives.WriteInt32LittleEndian(ico.AsSpan(16), verts);
            BinaryPrimitives.WriteInt16LittleEndian(ico.AsSpan(20), 4096);          // x = 1.0
            BinaryPrimitives.WriteInt16LittleEndian(ico.AsSpan(22), -8192);         // y = -2.0 (up, in the icon's Y-down axes)
            BinaryPrimitives.WriteInt16LittleEndian(ico.AsSpan(20 + 8 + 10), 1024); // v = 0.25 (after the shape and the normal)
            BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(anim), 1);          // animation header, no frames
            // The first stored row (the top) is red; the rest black, which is see-through.
            for (int x = 0; x < 128; x++) BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(tex + x * 2), 0x001F);

            var icon = Ps2Icon.Parse(ico);
            Assert.Equal(new[] { 1f, 2f }, icon.Positions[..2]);
            Assert.Equal(0.25f, icon.Uvs[1]);
            Assert.NotNull(icon.Texture);
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, icon.Texture![..4]);
            Assert.Equal(0, icon.Texture[128 * 4 + 3]);
        }

        [Theory]
        [InlineData(4)]
        [InlineData(3)]
        public void Screens_guess_their_width_and_pixel_size(int bpp)
        {
            const int w = 640, h = 448;
            var data = new byte[w * h * bpp];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * bpp;
                    data[i] = (byte)(x / 3);
                    data[i + 1] = (byte)(y / 2);
                    data[i + 2] = (byte)((x + y) / 5);
                }
            Assert.Equal(new RawScreen.Guess(w, h, bpp), RawScreen.GuessLayout(data));
        }
    }
}
