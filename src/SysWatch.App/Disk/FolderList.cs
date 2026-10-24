namespace SysWatch.App.Disk;

public sealed record FolderEntry(string Path, string Name, bool IsDir, long Size, long Modified, bool Hidden);
public sealed record FolderListing(string Path, bool Exists, int Total, long FilesSize, bool Truncated, IReadOnlyList<FolderEntry> Entries);

/// <summary>Что лежит в папке прямо сейчас: файлы с точным размером и подпапки (размер папок — из снимка диска).</summary>
public static class FolderList
{
    public const int Limit = 2000;

    public static FolderListing Get(string path, Func<string, long?> snapshotSize)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return new(path, false, 0, 0, false, []);
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };
        var list = new List<FolderEntry>();
        int total = 0;
        long filesSize = 0;
        try
        {
            foreach (var fsi in new DirectoryInfo(path).EnumerateFileSystemInfos("*", opts))
            {
                total++;
                bool dir = fsi is DirectoryInfo;
                long size = 0;
                if (!dir)
                {
                    try { size = ((FileInfo)fsi).Length; } catch (IOException) { }
                    filesSize += size;
                }
                else size = snapshotSize(fsi.FullName) ?? -1;
                if (list.Count < Limit * 4)
                    list.Add(new FolderEntry(fsi.FullName, fsi.Name, dir, size, SafeTime(fsi),
                        fsi.Attributes.HasFlag(FileAttributes.Hidden) || fsi.Attributes.HasFlag(FileAttributes.System)));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { /* показываем, что успели */ }

        var sorted = list.OrderByDescending(e => e.IsDir).ThenByDescending(e => e.Size).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Take(Limit).ToList();
        return new(path, true, total, filesSize, total > sorted.Count, sorted);
    }

    private static long SafeTime(FileSystemInfo f)
    {
        try { return new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeSeconds(); } catch (Exception) { return 0; }
    }

    /// <summary>Открыть проводник и выделить файл или папку.</summary>
    public static bool Reveal(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        path = Path.GetFullPath(path);
        if (File.Exists(path) || Directory.Exists(path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            return true;
        }
        var parent = Path.GetDirectoryName(path);
        if (parent is null || !Directory.Exists(parent)) return false;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{parent}\"") { UseShellExecute = false });
        return true;
    }
}
