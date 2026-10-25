using System.Text.Json;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// Read-only directory listing for the page's file browser (the browser itself cannot see file paths). The server
    /// binds to 127.0.0.1, so only the local user's browser can list the local disk.
    /// </summary>
    internal sealed class FileSystemService : ICommandService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public void Register(CommandRegistry reg)
        {
            reg.Add(_ => Ok("FS_PLACES", Places()), "FS_PLACES");
            reg.Add(c => List(c.InputPath), "FS_LIST");
        }

        private static string Ok(string command, object data) =>
            Responses.Json(new CommandResponse { Success = true, Command = command, Data = JsonSerializer.Serialize(data, JsonOptions) });

        // Drives plus the user's common folders, for the browser's sidebar.
        private static object Places()
        {
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d =>
            {
                string label = "";
                try { label = d.VolumeLabel; } catch { /* unreadable label */ }
                return new { name = d.Name.TrimEnd('\\'), path = d.RootDirectory.FullName, label, kind = d.DriveType.ToString() };
            }).ToList();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var places = new[]
            {
                ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
                ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                ("Downloads", Path.Combine(home, "Downloads")),
                ("Home", home),
            }.Where(p => p.Item2.Length > 0 && Directory.Exists(p.Item2)).Select(p => new { name = p.Item1, path = p.Item2 }).ToList();
            return new { drives, places };
        }

        private static string List(string? inputPath)
        {
            try
            {
                string path = string.IsNullOrWhiteSpace(inputPath)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                    : Path.GetFullPath(inputPath.Trim().Trim('"'));
                // A file path lists its folder (with the file preselected by the page).
                if (File.Exists(path)) path = Path.GetDirectoryName(path)!;
                if (!Directory.Exists(path)) return Responses.Error($"FS_LIST: no folder {path}");
                var dir = new DirectoryInfo(path);
                var entries = new List<object>();
                foreach (var info in SafeEntries(dir))
                {
                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    bool isDir = info is DirectoryInfo;
                    entries.Add(new
                    {
                        name = info.Name,
                        path = info.FullName,
                        dir = isDir,
                        size = isDir ? (long?)null : ((FileInfo)info).Length,
                        modified = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                    });
                }
                return Ok("FS_LIST", new { path = dir.FullName, parent = dir.Parent?.FullName, entries });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
            {
                return Responses.Error($"FS_LIST: {ex.Message}");
            }
        }

        // Folders first, then files, each by name; entries that cannot be read are skipped.
        private static IEnumerable<FileSystemInfo> SafeEntries(DirectoryInfo dir)
        {
            var dirs = new List<FileSystemInfo>();
            var files = new List<FileSystemInfo>();
            foreach (var info in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                (info is DirectoryInfo ? dirs : files).Add(info);
            }
            return dirs.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Concat(files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase));
        }
    }
}
