using System.Text.Json;
using System.Text.Json.Nodes;
using SysWatch.App.Data;

namespace SysWatch.App.Settings;

/// <summary>Пороги для уведомлений в трее. Minutes — сколько минут подряд нагрузка должна держаться выше порога.</summary>
public sealed record AlertSettings(bool Enabled, int CpuPct, int MemPct, int Minutes, int DiskFreePct, int GpuTemp);

public sealed record StorageInfo(string Dir, long Bytes, int Days, long? Oldest, long AvgDayBytes);

public sealed record SettingsView(int RetentionDays, int DetailDays, AlertSettings Alerts, StorageInfo Storage);

public sealed record SettingsUpdate(int? RetentionDays, int? DetailDays, AlertSettings? Alerts);

/// <summary>
/// Настройки SysWatch в C:\ProgramData\SysWatch\settings.json. Этот же файл читает агент (срок хранения),
/// поэтому неизвестные ключи сохраняем как есть.
/// </summary>
public sealed class AppSettings(DayFiles files)
{
    public static readonly string File = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SysWatch", "settings.json");

    public static readonly int[] RetentionChoices = [30, 90, 180, 365];

    public static readonly AlertSettings DefaultAlerts = new(true, 90, 90, 5, 10, 85);

    private readonly object _gate = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private JsonObject Load()
    {
        try
        {
            if (System.IO.File.Exists(File) && JsonNode.Parse(System.IO.File.ReadAllText(File)) is JsonObject o) return o;
        }
        catch (Exception) { /* испорченный файл — начинаем с умолчаний */ }
        return new JsonObject();
    }

    private static T Read<T>(JsonObject o, string key, T fallback)
    {
        try { return o[key] is { } n ? n.Deserialize<T>(Json) ?? fallback : fallback; }
        catch (Exception) { return fallback; }
    }

    public int RetentionDays { get { lock (_gate) return Math.Clamp(Read(Load(), "retentionDays", 90), 1, 3650); } }
    public int DetailDays { get { lock (_gate) { var o = Load(); return Math.Clamp(Read(o, "detailDays", 30), 1, Math.Max(1, Read(o, "retentionDays", 90))); } } }
    private (DateTime At, AlertSettings Value)? _alertsCache;

    /// <summary>Пороги читаются трей-таймером каждые 5 секунд — держим в памяти полминуты, а не читаем файл каждый раз.</summary>
    public AlertSettings Alerts
    {
        get
        {
            lock (_gate)
            {
                if (_alertsCache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(30)) return c.Value;
                var v = Read(Load(), "alerts", DefaultAlerts);
                _alertsCache = (DateTime.UtcNow, v);
                return v;
            }
        }
    }

    public SettingsView View()
    {
        lock (_gate)
        {
            var o = Load();
            int retention = Math.Clamp(Read(o, "retentionDays", 90), 1, 3650);
            return new SettingsView(retention, Math.Clamp(Read(o, "detailDays", 30), 1, retention),
                Read(o, "alerts", DefaultAlerts), Storage());
        }
    }

    public SettingsView Save(SettingsUpdate u)
    {
        lock (_gate)
        {
            var o = Load();
            if (u.RetentionDays is int r)
            {
                r = Math.Clamp(r, 7, 3650);
                o["retentionDays"] = r;
                o["detailDays"] = Math.Min(30, r);
            }
            if (u.DetailDays is int d) o["detailDays"] = Math.Clamp(d, 1, Read(o, "retentionDays", 90));
            if (u.Alerts is { } a)
                o["alerts"] = JsonSerializer.SerializeToNode(a with
                {
                    CpuPct = Math.Clamp(a.CpuPct, 50, 100),
                    MemPct = Math.Clamp(a.MemPct, 50, 100),
                    Minutes = Math.Clamp(a.Minutes, 1, 60),
                    DiskFreePct = Math.Clamp(a.DiskFreePct, 1, 50),
                    GpuTemp = Math.Clamp(a.GpuTemp, 60, 110)
                }, Json);
            o.Remove("ai"); // локальный ИИ убран — старую секцию не храним
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            var tmp = File + ".tmp";
            System.IO.File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            System.IO.File.Move(tmp, File, overwrite: true);
            _alertsCache = null;
        }
        return View();
    }

    /// <summary>Сколько места занимает история агента.</summary>
    private StorageInfo Storage()
    {
        long bytes = 0; int days = 0; DateOnly? oldest = null;
        try
        {
            if (Directory.Exists(files.Dir))
                foreach (var f in Directory.EnumerateFiles(files.Dir, "*.db*"))
                {
                    bytes += new FileInfo(f).Length;
                    if (f.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                        && DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", out var d))
                    {
                        days++;
                        if (oldest is null || d < oldest) oldest = d;
                    }
                }
        }
        catch (Exception) { /* нет доступа — покажем что набрали */ }
        long? oldestUnix = oldest is { } od ? new DateTimeOffset(od.ToDateTime(TimeOnly.MinValue)).ToUnixTimeSeconds() : null;
        return new StorageInfo(files.Dir, bytes, days, oldestUnix, days > 0 ? bytes / days : 0);
    }
}
