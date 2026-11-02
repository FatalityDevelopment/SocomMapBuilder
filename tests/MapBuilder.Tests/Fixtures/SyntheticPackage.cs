using System.Text;
using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;
using MapBuilder.Server;
using MapBuilder.Server.Services;
using Xunit;

namespace MapBuilder.Tests.Fixtures
{
    /// <summary>
    /// A small unpacked map package written to a temp folder, built from scratch (no game files):
    /// <list type="bullet">
    /// <item><c>TST_GEO.ZED</c>: <c>models</c> > <c>worldmodel</c> (at the origin) > <c>children</c> > two placed
    /// references, <c>crate_1</c> at (10, 0, 20) and <c>crate_2</c> at (-30, 0, 5), both naming <c>crate</c>.</item>
    /// <item><c>TST_MDL.ZED</c>: top-level model records <c>crate</c> and <c>tree</c> (opaque payloads).</item>
    /// <item><c>CLUTTER.ZAR</c>: <c>Clutter</c> > <c>tree</c> with two placements, at (1, 0, 2) and (3, 0, 4).</item>
    /// <item><c>AIMAPS.MPS</c>: the two-point MPS of <see cref="MapPointsTests"/> (Alpha1, Bravo1).</item>
    /// </list>
    /// </summary>
    internal sealed class SyntheticPackage : IDisposable
    {
        public string Folder { get; }

        public SyntheticPackage()
        {
            Folder = Path.Combine(Path.GetTempPath(), "mb-pkg-" + Guid.NewGuid().ToString("N"), "TST");
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(Path.Combine(Folder, "TST_GEO.ZED"), Geo());
            File.WriteAllBytes(Path.Combine(Folder, "TST_MDL.ZED"), Mdl());
            File.WriteAllBytes(Path.Combine(Folder, "CLUTTER.ZAR"), Clutter());
            File.WriteAllBytes(Path.Combine(Folder, "AIMAPS.MPS"), MapPointsTests.Mps());
        }

        public void Dispose()
        {
            try { Directory.Delete(Path.GetDirectoryName(Folder)!, recursive: true); } catch (IOException) { }
        }

        /// <summary>The package's files as <c>TST.ZDB</c> beside the folder (entries under <c>RUN\MP\TST\</c>); returns its path.</summary>
        public string WriteZdb()
        {
            string path = Path.Combine(Path.GetDirectoryName(Folder)!, "TST.ZDB");
            using var zdb = CodeDesigner.Ps2.FileFormats.Zipper.ZDB.ZdbArchive.CreateNew(2);
            foreach (string file in Directory.GetFiles(Folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                zdb.AddEntry(@"RUN\MP\TST\" + Path.GetFileName(file), File.ReadAllBytes(file));
            File.WriteAllBytes(path, zdb.ToBytes());
            return path;
        }

        public static float[] Translation(float x, float y, float z) =>
            new[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1f };

        private static byte[] Floats(float[] values, int length)
        {
            var b = new byte[length];
            for (int i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(b, i * 4);
            return b;
        }

        // nparams: the 16-float local transform, then bounds min (16-18) and max (19-21).
        private static byte[] Nparams(float x, float y, float z) =>
            Floats(Translation(x, y, z).Concat(new[] { -1f, -1, -1, 1, 1, 1 }).ToArray(), 22 * 4);

        private static byte[] Ascii(string text)
        {
            var b = new byte[(text.Length + 1 + 3) & ~3];
            Encoding.ASCII.GetBytes(text).CopyTo(b, 0);
            return b;
        }

        private static byte[] Geo()
        {
            var c = ZedContainer.CreateNew();
            c.AddNode("models", Array.Empty<byte>(), 1);
            c.AddNode("worldmodel", Array.Empty<byte>(), 2);
            c.AddNode("nparams", Nparams(0, 0, 0));
            c.AddNode("children", Array.Empty<byte>(), 2);
            c.AddNode("crate_1", Array.Empty<byte>(), 2);
            c.AddNode("nparams", Nparams(10, 0, 20));
            c.AddNode("model_name", Ascii("crate"));
            c.AddNode("crate_2", Array.Empty<byte>(), 2);
            c.AddNode("nparams", Nparams(-30, 0, 5));
            c.AddNode("model_name", Ascii("crate"));
            c.RootValue = 1;
            return c.ToBytes();
        }

        private static byte[] Mdl()
        {
            var c = ZedContainer.CreateNew();
            c.AddNode("crate", Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
            c.AddNode("tree", Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray());
            c.RootValue = 2;
            return c.ToBytes();
        }

        private static byte[] Clutter()
        {
            var c = ZedContainer.CreateNew();
            c.AddNode("Clutter", Array.Empty<byte>(), 1);
            c.AddNode("tree", Array.Empty<byte>(), 2);
            foreach (var (x, z) in new[] { (1f, 2f), (3f, 4f) })
            {
                c.AddNode("a_clutter", Array.Empty<byte>(), 2);
                c.AddNode("params", Floats(Translation(x, 0, z), 96));
                c.AddNode("scale_inverse", BitConverter.GetBytes(1f));
            }
            c.RootValue = 1;
            return c.ToBytes();
        }
    }

    /// <summary>Runs backend commands the way /api/command does, against a fresh registry.</summary>
    internal sealed class Backend
    {
        public CommandRegistry Registry { get; } = new();

        public Backend()
        {
            new ZipperAssetService().Register(Registry);
            new PackageFilesService().Register(Registry);
        }

        /// <summary>The raw reply.</summary>
        public CommandResponse Send(CommandMessage m)
        {
            Assert.True(Registry.TryGet(m.Command, out var handler), $"no command {m.Command}");
            return JsonSerializer.Deserialize<CommandResponse>(handler!(m))!;
        }

        /// <summary>The reply's data; fails the test when the command fails.</summary>
        public JsonElement Ok(CommandMessage m)
        {
            var r = Send(m);
            Assert.True(r.Success, $"{m.Command} failed: {r.Error}");
            return JsonDocument.Parse(r.Data ?? "null").RootElement.Clone();
        }

        /// <summary>The error of a command that must fail.</summary>
        public string Fails(CommandMessage m)
        {
            var r = Send(m);
            Assert.False(r.Success, $"{m.Command} should have failed");
            return r.Error ?? string.Empty;
        }

        public string Open(string path) => Ok(new CommandMessage { Command = "ASSET_OPEN", InputPath = path }).GetProperty("status").GetProperty("session").GetString()!;

        public JsonElement Edit(string session, object op) =>
            Ok(new CommandMessage { Command = "ASSET_EDIT", AssetSession = session, AssetJson = JsonSerializer.Serialize(op) });

        public string EditFails(string session, object op) =>
            Fails(new CommandMessage { Command = "ASSET_EDIT", AssetSession = session, AssetJson = JsonSerializer.Serialize(op) });

        public JsonElement Undo(string session) => Ok(new CommandMessage { Command = "ASSET_UNDO", AssetSession = session });

        public JsonElement Redo(string session) => Ok(new CommandMessage { Command = "ASSET_REDO", AssetSession = session });

        public JsonElement Status(string session) => Ok(new CommandMessage { Command = "ASSET_STATUS", AssetSession = session });

        public JsonElement Scene(string session) =>
            Ok(new CommandMessage { Command = "ASSET_MAP_SCENE", AssetSession = session, AssetJson = "{\"limit\":1000}" });

        public JsonElement Points(string session) => Ok(new CommandMessage { Command = "ASSET_MAP_POINTS", AssetSession = session });

        public void Close(string session) => Send(new CommandMessage { Command = "ASSET_CLOSE", AssetSession = session });

        /// <summary>The session's in-memory bytes of one root leaf.</summary>
        public static byte[] Bytes(string session, string leaf) =>
            ZipperAssetService.Find(session)!.Package.Entry(leaf)!.Data;
    }
}
