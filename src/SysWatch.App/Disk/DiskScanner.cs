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
        AttributesToSkip = 0, // скрытые и системные считаем (pagefile, hiberfil); ссылки отсекаем ниже — по цели, а не по атрибуту
        ReturnSpecialDirectories = false,
    };

    private readonly List<DiskNode> _nodes = new();
    private long _dirs, _bytes, _files;
    private long _lastReport;
    private Action<ScanProgress>? _progress;

    /// <summary>Сколько уже обойдено: для полосы прогресса.</summary>
    public sealed record ScanProgress(long Bytes, long Files, long Dirs, string Current);
    private CancellationToken _ct;

    public sealed record Result(List<DiskNode> Nodes, long Size, long Files, long Dirs, double Seconds);

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool SetThreadPriority(IntPtr thread, int priority);
    private const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
    private const int THREAD_MODE_BACKGROUND_END = 0x00020000;

    public Result Scan(string root, Action<ScanProgress> progress, CancellationToken ct)
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
                // Ссылки и точки соединения не обходим — иначе считали бы одно и то же дважды.
                // Но OneDrive (рабочий стол, документы) тоже помечен как reparse point — это не ссылка, его считаем.
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(e)) continue;
                if (e is FileInfo f)
                {
                    long len;
                    try { len = OnlyInCloud(f.Attributes) ? 0 : f.Length; } catch { continue; }
                    size += len;
                    files++;
                    _bytes += len;
                    _files++;
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

        _dirs++;
        // Отчёт о прогрессе не чаще 4 раз в секунду.
        long t = Environment.TickCount64;
        if (t - _lastReport > 250)
        {
            _lastReport = t;
            _progress?.Invoke(new ScanProgress(_bytes, _files, _dirs, dir));
        }
        if (depth <= 1 || size >= Threshold)
            _nodes.Add(new DiskNode(dir, parent, true, size, files));
        return (size, files);
    }

    /// <summary>Символическая ссылка или точка соединения (junction). Облачные заглушки OneDrive ссылками не являются.</summary>
    public static bool IsLink(FileSystemInfo e)
    {
        try { return e.LinkTarget is not null; }
        catch (Exception) { return true; } // не смогли прочитать — безопаснее пропустить
    }

    /// <summary>Файл OneDrive «только в облаке»: на диске не занимает места, скачается при открытии.</summary>
    public static bool OnlyInCloud(FileAttributes a) =>
        ((int)a & (0x00400000 /* RECALL_ON_DATA_ACCESS */ | 0x00040000 /* RECALL_ON_OPEN */)) != 0 || a.HasFlag(FileAttributes.Offline);

    /// <summary>Сколько точки восстановления (теневые копии) занимают на диске root. null — не удалось узнать.</summary>
    public static long? ShadowStorage(string root)
    {
        try
        {
            // Буква диска → DeviceID тома (\\?\Volume{...}\), по нему ищем хранилище теневых копий.
            string? deviceId = null;
            using (var volumes = new ManagementObjectSearcher("SELECT DeviceID, DriveLetter FROM Win32_Volume"))
                foreach (ManagementObject v in volumes.Get())
                    using (v)
                        if (string.Equals(v["DriveLetter"] as string, root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                            deviceId = v["DeviceID"] as string;
            if (deviceId is null) return null;

            long total = 0;
            using var searcher = new ManagementObjectSearcher("SELECT UsedSpace, Volume FROM Win32_ShadowStorage");
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    // Volume — ссылка вида Win32_Volume.DeviceID="\\\\?\\Volume{...}\\"
                    var reference = (o["Volume"] as string ?? "").Replace("\\\\", "\\");
                    if (reference.Contains(deviceId, StringComparison.OrdinalIgnoreCase))
                        total += Convert.ToInt64(o["UsedSpace"]);
                }
            }
            return total;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
