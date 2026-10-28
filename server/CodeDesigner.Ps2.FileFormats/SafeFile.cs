namespace CodeDesigner.Ps2.FileFormats
{
    /// <summary>
    /// Writing a package or archive without leaving it half-written: the bytes go to <c>&lt;path&gt;.tmp</c> next
    /// to the target, then replace it in one move. Writing over an existing file first keeps <c>&lt;path&gt;.bak</c>
    /// (once, so the backup stays the original).
    /// </summary>
    public static class SafeFile
    {
        public static void Write(string path, byte[] bytes, bool backup = true)
        {
            string full = Path.GetFullPath(path);
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (backup) Backup(full);
            string tmp = full + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, full, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        /// <summary>Copies <paramref name="path"/> to <c>&lt;path&gt;.bak</c> unless the file is new or a backup exists.</summary>
        public static void Backup(string path)
        {
            string bak = path + ".bak";
            if (File.Exists(path) && !File.Exists(bak)) File.Copy(path, bak);
        }
    }
}
