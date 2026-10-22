using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace SysWatch.App.Disk;

/// <summary>
/// Обход диска: размер каждой папки снизу вверх. Символические ссылки и junction-точки не проходим
/// (иначе возможны циклы и двойной счёт). Поток работает в фоновом режиме Windows: низкий приоритет CPU и диска.
/// </summary>
public sealed class DiskScanner
{
    public const long Threshold = 100L << 20; // 100 МБ

    private static readonly System.IO.EnumerationOptions Opts = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint, // скрытые и системные файлы считаем: pagefile, hiberfil
        ReturnSpecialDirectories = false,
    };

    private readonly List<DiskNode> _nodes = new();
    private long _dirs;
    private Action<string>? _progress;
    private CancellationToken _ct;

    public sealed record Result(List<DiskNode> Nodes, long Size, long Files, long Dirs, double Seconds);

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool SetThreadPriority(IntPtr thread, int priority);
    private const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
    private const int THREAD_MODE_BACKGROUND_END = 0x00020000;

    public Result Scan(string root, Action<string> progress, CancellationToken ct)
    {
        _progress = progress;
        _ct = ct;
        var sw = Stopwatch.StartNew();
        SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN);
        try
        {
            var (size, files) = Walk(root, null, 0);
            return new Result(_nodes, size, files, _dirs, sw.Elapsed.TotalSeconds);
        }
        finally
        {
            SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END);
        }
    }

    private (long Size, long Files) Walk(string dir, string? parent, int depth)
    {
        _ct.ThrowIfCancellationRequested();
        long size = 0, files = 0;
        var subdirs = new List<string>();

        try
        {
            foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Opts))
            {
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (e is FileInfo f)
                {
                    long len;
                    try { len = f.Length; } catch { continue; }
                    size += len;
                    files++;
                    if (len >= Threshold || depth == 0)
                        _nodes.Add(new DiskNode(f.FullName, dir, false, len, 1));
                }
                else
                {
                    subdirs.Add(e.FullName);
                }
            }
        }
        catch (Exception) { /* нет доступа или папка исчезла во время обхода */ }

        foreach (var s in subdirs)
        {
            var (a, b) = Walk(s, dir, depth + 1);
            size += a;
            files += b;
        }

        if (++_dirs % 2000 == 0) _progress?.Invoke(dir);
        if (depth <= 1 || size >= Threshold)
            _nodes.Add(new DiskNode(dir, parent, true, size, files));
        return (size, files);
    }

    /// <summary>Сколько занимают точки восстановления (теневые копии). null — не удалось узнать.</summary>
    public static long? ShadowStorage()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT UsedSpace FROM Win32_ShadowStorage");
            long total = 0;
            foreach (ManagementObject o in searcher.Get())
                using (o) total += Convert.ToInt64(o["UsedSpace"]);
            return total;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
