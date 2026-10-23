using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using SysWatch.App.Data;

namespace SysWatch.App.Programs;

public sealed record ProgramRow(
    string Id, string Name, string? Publisher, string? Version, long? Installed, long Size,
    long? LastUsed, string? LastUsedBy, string? Location, string Kind, bool CanUninstall);

public sealed record ProgramList(long ScannedAt, bool PrefetchAvailable, IReadOnlyList<ProgramRow> Items);

/// <summary>
/// Установленные программы и когда каждая последний раз запускалась.
/// Источники запуска: Prefetch Windows (месяцы истории), история агента (7 дней), запущенные сейчас процессы.
/// </summary>
public sealed class Installed(DayFiles files, Func<HashSet<string>> runningPaths)
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallKey32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly object _gate = new();
    private ProgramList? _cache;
    private readonly Dictionary<string, (string Command, bool Msi)> _uninstall = new(StringComparer.OrdinalIgnoreCase);

    // Библиотеки, среды выполнения и драйверы не «запускаются» сами — их не советуем удалять.
    private static readonly string[] RuntimeWords =
    [
        "Redistributable", "Runtime", "SDK", "Driver", "Microsoft .NET", "Windows Software Development Kit",
        "Visual C++", "Update for", "Language Pack", "Targeting Pack", "Shared Framework", "Host FX", "AppHost",
        "WebView2", "DirectX", "Microsoft Edge Update", "Intel", "NVIDIA", "Realtek", "AMD", "Chipset",
    ];

    public ProgramList List(bool refresh)
    {
        lock (_gate)
        {
            if (!refresh && _cache is not null && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _cache.ScannedAt < 600)
                return _cache;
            _cache = Build();
            return _cache;
        }
    }

    private ProgramList Build()
    {
        var prefetch = PrefetchLastRun(out bool prefetchOk);
        var history = HistoryLastRun();
        var running = runningPaths();
        var rows = new List<ProgramRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _uninstall.Clear();

        foreach (var (hive, path) in new[] { (Registry.LocalMachine, UninstallKey), (Registry.LocalMachine, UninstallKey32), (Registry.CurrentUser, UninstallKey) })
        {
            using var root = hive.OpenSubKey(path);
            if (root is null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                try
                {
                    using var k = root.OpenSubKey(sub);
                    if (k?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                    if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (k.GetValue("ParentKeyName") is not null) continue; // обновления к другим программам
                    if (k.GetValue("ReleaseType") is string rt && (rt.Contains("Update") || rt.Contains("Hotfix"))) continue;

                    var version = k.GetValue("DisplayVersion") as string;
                    if (!seen.Add(name + "|" + version)) continue; // одна программа в 32- и 64-битной ветке

                    var location = Clean(k.GetValue("InstallLocation") as string);
                    var icon = ExeFromIcon(k.GetValue("DisplayIcon") as string);
                    if (string.IsNullOrEmpty(location) && icon is not null) location = Path.GetDirectoryName(icon);

                    long size = k.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0;
                    var exes = Exes(location, icon);
                    var (lastUsed, by) = LastRun(exes, location, prefetch, history, running);

                    var uninstall = k.GetValue("QuietUninstallString") as string ?? k.GetValue("UninstallString") as string;
                    bool msi = k.GetValue("WindowsInstaller") is int wi && wi == 1;
                    var id = $"{(hive == Registry.CurrentUser ? "HKCU" : "HKLM")}|{path}|{sub}";
                    if (!string.IsNullOrWhiteSpace(uninstall)) _uninstall[id] = (msi ? $"msiexec.exe /x {sub}" : uninstall, msi);

                    string kind = RuntimeWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)) ? "runtime"
                        : exes.Count == 0 ? "unknown" : "app";

                    rows.Add(new ProgramRow(id, name.Trim(), (k.GetValue("Publisher") as string)?.Trim(), version,
                        ParseDate(k.GetValue("InstallDate") as string), size, lastUsed, by, location, kind,
                        _uninstall.ContainsKey(id)));
                }
                catch (Exception) { /* битая запись в реестре — пропускаем */ }
            }
        }

        return new ProgramList(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), prefetchOk,
            rows.OrderBy(r => r.LastUsed ?? 0).ThenByDescending(r => r.Size).ToList());
    }

    /// <summary>Запустить штатный деинсталлятор программы. Решение и подтверждение — в его собственном окне.</summary>
    public bool Uninstall(string id)
    {
        (string Command, bool Msi) entry;
        lock (_gate)
            if (!_uninstall.TryGetValue(id, out entry)) return false;

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c start \"\" {entry.Command}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        lock (_gate) _cache = null; // список пересоберём при следующем запросе
        return true;
    }

    // ── Последний запуск ─────────────────────────────────────────────────

    private static Dictionary<string, long> PrefetchLastRun(out bool ok)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
        ok = false;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.pf"))
            {
                ok = true;
                // Имя вида CHROME.EXE-1A2B3C4D.pf: файл обновляется при каждом запуске программы.
                int dash = f.Name.LastIndexOf('-');
                if (dash <= 0) continue;
                var exe = f.Name[..dash];
                long ts = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeSeconds();
                if (!map.TryGetValue(exe, out var old) || ts > old) map[exe] = ts;
            }
        }
        catch (Exception) { /* Prefetch выключен или нет доступа */ }
        return map;
    }

    private Dictionary<string, long> HistoryLastRun()
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, path) in files.For(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 8 * 86400))
        {
            try
            {
                using var c = DayFiles.Open(path);
                foreach (var r in c.Cmd("SELECT path, MAX(first_seen) FROM process WHERE path IS NOT NULL GROUP BY path").Rows())
                {
                    var p = r.GetString(0);
                    long ts = r.GetInt64(1);
                    if (!map.TryGetValue(p, out var old) || ts > old) map[p] = ts;
                }
            }
            catch (Exception) { }
        }
        return map;
    }

    private static (long? Ts, string? By) LastRun(List<string> exes, string? location, Dictionary<string, long> prefetch,
        Dictionary<string, long> history, HashSet<string> running)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (exes.Any(running.Contains) ||
            (location is not null && running.Any(p => p.StartsWith(location, StringComparison.OrdinalIgnoreCase))))
            return (now, "running");

        long best = 0;
        string? by = null;
        foreach (var exe in exes)
        {
            if (history.TryGetValue(exe, out var h) && h > best) { best = h; by = "history"; }
            if (prefetch.TryGetValue(Path.GetFileName(exe), out var p) && p > best) { best = p; by = "prefetch"; }
        }
        if (location is not null)
            foreach (var (p, h) in history)
                if (h > best && p.StartsWith(location, StringComparison.OrdinalIgnoreCase)) { best = h; by = "history"; }

        return best > 0 ? (best, by) : (null, null);
    }

    /// <summary>Exe-файлы программы: из DisplayIcon и из папки установки (два уровня вглубь, не больше 60).</summary>
    private static List<string> Exes(string? location, string? icon)
    {
        var list = new List<string>();
        if (icon is not null) list.Add(icon);
        if (!string.IsNullOrEmpty(location) && Directory.Exists(location))
        {
            try
            {
                var opts = new System.IO.EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, MaxRecursionDepth = 2 };
                foreach (var f in Directory.EnumerateFiles(location, "*.exe", opts))
                {
                    var name = Path.GetFileName(f);
                    if (name.Contains("unins", StringComparison.OrdinalIgnoreCase) || name.Contains("update", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(f);
                    if (list.Count >= 60) break;
                }
            }
            catch (Exception) { }
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ExeFromIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        var p = icon.Trim().Trim('"');
        int comma = p.LastIndexOf(',');
        if (comma > 2) p = p[..comma];
        p = p.Trim().Trim('"');
        return p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p) ? p : null;
    }

    private static string? Clean(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Trim('"').TrimEnd('\\');
        return p.Length > 3 ? p : null; // «C:\» как папка программы — не принимаем
    }

    private static long? ParseDate(string? yyyymmdd) =>
        DateTime.TryParseExact(yyyymmdd, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
            ? new DateTimeOffset(d).ToUnixTimeSeconds()
            : null;
}
