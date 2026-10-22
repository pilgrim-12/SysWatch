using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SysWatch.App.Data;

namespace SysWatch.App.Anomalies;

/// <summary>
/// Раз в минуту просматривает новые записи агента и ищет аномалии по правилам.
/// Ничего не вычисляет постоянно: читает только то, что появилось с прошлой проверки.
/// </summary>
public sealed class Detector(DayFiles files, Signatures signatures, AnomalyStore store)
{
    private const double CpuThresholdPct = 30;   // % всех ядер
    private const int CpuMinutes = 5;
    private const long MemGrowthBytes = 1L << 30; // 1 ГБ за час

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "msedgewebview2.exe", "firefox.exe", "opera.exe", "brave.exe",
        "vivaldi.exe", "browser.exe", "svchost.exe",
    };

    private static readonly string OwnDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SysWatch");
    private static readonly int Cores = Environment.ProcessorCount;

    private DateOnly _historyDay;
    private bool _hasHistory;
    private readonly HashSet<string> _oldPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _oldAppDomains = new(StringComparer.OrdinalIgnoreCase);

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Проверка. Возвращает новые аномалии (для уведомлений).</summary>
    public List<Anomaly> Run()
    {
        var found = new List<Anomaly>();
        long now = Now();
        long last = long.TryParse(store.Get("last_ts"), out var l) ? l : now - 600;
        long since = last - 120; // перекрытие: агент пишет минутные корзины с опозданием до минуты

        LoadHistory();
        store.Set("learning", _hasHistory ? "0" : "1");

        foreach (var (_, path) in files.For(since))
        {
            using var c = DayFiles.Open(path);
            NewProcesses(c, since, found);
            Network(c, since, found);
            NewDomains(c, since, found);
            Cpu(c, now, found);
            Memory(c, now, found);
        }

        AutostartChanges(now, found);
        store.Set("last_ts", now.ToString());
        return found;
    }

    private void Add(List<Anomaly> found, long ts, string rule, string severity, string app, string title, string detail, string key)
    {
        if (store.Add(ts, rule, severity, app, title, detail, key) is { } a) found.Add(a);
    }

    // ── История за прошлые дни: какие программы и адреса уже встречались ──

    private void LoadHistory()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_historyDay == today) return;

        _oldPaths.Clear();
        _oldAppDomains.Clear();
        _hasHistory = false;

        foreach (var f in files.OlderThan(today))
        {
            _hasHistory = true;
            try
            {
                using var c = DayFiles.Open(f);
                foreach (var r in c.Cmd("SELECT DISTINCT path FROM process WHERE path IS NOT NULL").Rows())
                    _oldPaths.Add(r.GetString(0));
                foreach (var r in c.Cmd("SELECT DISTINCT p.image, d.domain FROM dns d JOIN process p ON p.id = d.proc_id").Rows())
                    _oldAppDomains.Add(r.GetString(0) + "|" + r.GetString(1));
            }
            catch (SqliteException) { /* повреждённый или занятый файл — пропускаем */ }
        }
        _historyDay = today;
    }

    // ── Новые процессы: запуск из временных папок, впервые увиденные программы ──

    private static bool IsTempPath(string p) =>
        p.Contains(@"\AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase) ||
        p.Contains(@"\Windows\Temp\", StringComparison.OrdinalIgnoreCase) ||
        p.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase);

    private void NewProcesses(SqliteConnection c, long since, List<Anomaly> found)
    {
        var rows = c.Cmd("""
            SELECT image, path, first_seen,
                   EXISTS(SELECT 1 FROM process q WHERE q.path = p.path AND q.first_seen < p.first_seen)
            FROM process p WHERE first_seen > $since AND path IS NOT NULL
            """, ("$since", since)).Rows()
            .Select(r => (Image: r.GetString(0), Path: r.GetString(1), Ts: r.GetInt64(2), SeenToday: r.GetInt64(3) != 0))
            .ToList();

        foreach (var p in rows)
        {
            if (p.Path.StartsWith(OwnDir, StringComparison.OrdinalIgnoreCase)) continue;

            if (IsTempPath(p.Path))
                Add(found, p.Ts, "temp_exec", "medium", p.Image,
                    "Запуск из временной папки",
                    $"{p.Path}. Так запускаются установщики, но и вредоносные программы тоже.",
                    $"temp_exec|{p.Path}");

            if (_hasHistory && !p.SeenToday && !_oldPaths.Contains(p.Path))
                Add(found, p.Ts, "new_program", "low", p.Image,
                    "Новая программа",
                    $"Впервые запущена: {p.Path}",
                    $"new_program|{p.Path}");
        }
    }

    // ── Сеть: программа без цифровой подписи обменивается данными ──

    private void Network(SqliteConnection c, long since, List<Anomaly> found)
    {
        var rows = c.Cmd("""
            SELECT p.image, p.path, COALESCE(c.domain, c.remote_ip), SUM(c.bytes_tx + c.bytes_rx), MIN(c.ts)
            FROM conn_1m c JOIN process p ON p.id = c.proc_id
            WHERE c.ts > $since AND p.path IS NOT NULL
            GROUP BY p.path, COALESCE(c.domain, c.remote_ip)
            ORDER BY 4 DESC
            """, ("$since", since)).Rows()
            .Select(r => (Image: r.GetString(0), Path: r.GetString(1), Dest: r.GetString(2), Bytes: r.Long0(3), Ts: r.GetInt64(4)))
            .Where(x => !(IPAddress.TryParse(x.Dest, out var ip) && IPAddress.IsLoopback(ip)))
            .ToList();

        foreach (var g in rows.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (g.Key.StartsWith(OwnDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (signatures.Check(g.Key).Status != "unsigned") continue;

            var first = g.First();
            var dests = string.Join(", ", g.Select(x => x.Dest).Distinct().Take(3));
            Add(found, first.Ts, "unsigned_net", "high", first.Image,
                "Программа без подписи выходит в сеть",
                $"{g.Key} подключалась к {dests}. У файла нет цифровой подписи издателя.",
                $"unsigned_net|{g.Key}");
        }
    }

    // ── Программа впервые запросила домен (браузеры и svchost не считаем: у них это норма) ──

    private void NewDomains(SqliteConnection c, long since, List<Anomaly> found)
    {
        if (!_hasHistory) return;

        var rows = c.Cmd("""
            SELECT p.image, d.domain, MIN(d.ts),
                   EXISTS(SELECT 1 FROM dns e JOIN process q ON q.id = e.proc_id
                          WHERE q.image = p.image AND e.domain = d.domain AND e.ts <= $since)
            FROM dns d JOIN process p ON p.id = d.proc_id
            WHERE d.ts > $since GROUP BY p.image, d.domain
            """, ("$since", since)).Rows()
            .Select(r => (Image: r.GetString(0), Domain: r.GetString(1), Ts: r.GetInt64(2), SeenToday: r.GetInt64(3) != 0))
            .ToList();

        foreach (var x in rows)
        {
            if (x.SeenToday || Browsers.Contains(x.Image)) continue;
            if (_oldAppDomains.Contains(x.Image + "|" + x.Domain)) continue;
            Add(found, x.Ts, "new_domain", "low", x.Image,
                "Новый адрес у программы",
                $"{x.Image} впервые обратилась к {x.Domain}",
                $"new_domain|{x.Image}|{x.Domain}");
        }
    }

    // ── Долгая высокая нагрузка на процессор ──

    private void Cpu(SqliteConnection c, long now, List<Anomaly> found)
    {
        long end = now / 60 * 60;            // начало текущей (неполной) минуты
        long start = end - CpuMinutes * 60;

        var perApp = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in c.Cmd("""
            SELECT p.image, m.ts, SUM(m.cpu_ms) FROM proc_1m m JOIN process p ON p.id = m.proc_id
            WHERE m.ts >= $start AND m.ts < $end GROUP BY p.image, m.ts
            """, ("$start", start), ("$end", end)).Rows())
        {
            int i = (int)((r.GetInt64(1) - start) / 60);
            if (i < 0 || i >= CpuMinutes) continue;
            var arr = perApp.TryGetValue(r.GetString(0), out var a) ? a : perApp[r.GetString(0)] = new double[CpuMinutes];
            arr[i] += r.Long0(2);
        }

        foreach (var (app, ms) in perApp)
        {
            var pct = ms.Select(v => v / (60_000.0 * Cores) * 100).ToArray();
            if (pct.Any(p => p < CpuThresholdPct)) continue;
            var hour = DateTimeOffset.FromUnixTimeSeconds(end).ToLocalTime().ToString("yyyyMMddHH");
            Add(found, end, "cpu", "medium", app,
                "Долгая высокая нагрузка",
                $"{app} держит процессор на {pct.Average():F0}% всех ядер уже {CpuMinutes} минут подряд",
                $"cpu|{app}|{hour}");
        }
    }

    // ── Память программы растёт: похоже на утечку ──

    private void Memory(SqliteConnection c, long now, List<Anomaly> found)
    {
        var mem = c.MemColumn();
        var series = new Dictionary<string, SortedDictionary<long, long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in c.Cmd($"""
            SELECT p.image, m.ts, SUM(m.{mem}) FROM proc_1m m JOIN process p ON p.id = m.proc_id
            WHERE m.ts >= $from GROUP BY p.image, m.ts
            """, ("$from", now - 65 * 60)).Rows())
        {
            var s = series.TryGetValue(r.GetString(0), out var x) ? x : series[r.GetString(0)] = new SortedDictionary<long, long>();
            s[r.GetInt64(1)] = r.Long0(2);
        }

        foreach (var (app, s) in series)
        {
            if (s.Count < 10) continue;
            var (t0, m0) = (s.Keys.First(), s.Values.First());
            var (t1, m1) = (s.Keys.Last(), s.Values.Last());
            if (t1 - t0 < 50 * 60 || m0 <= 0) continue;              // нужен почти час наблюдений
            if (m1 - m0 < MemGrowthBytes || m1 < m0 * 1.5) continue;

            // Рост должен быть устойчивым, а не одним скачком: не больше 10% откатов вниз.
            var values = s.Values.ToArray();
            int drops = values.Zip(values.Skip(1), (a, b) => b < a * 0.95 ? 1 : 0).Sum();
            if (drops > values.Length / 10) continue;

            var bucket = DateTimeOffset.FromUnixTimeSeconds(t1).ToLocalTime();
            Add(found, t1, "memory", "medium", app,
                "Память растёт",
                $"{app}: {Gb(m0)} → {Gb(m1)} за час без снижения. Похоже на утечку памяти.",
                $"memory|{app}|{bucket:yyyyMMdd}{bucket.Hour / 6}");
        }
    }

    private static string Gb(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):F1} ГБ" : $"{b / (1 << 20)} МБ";

    // ── Автозагрузка: новые записи с прошлой проверки ──

    private void AutostartChanges(long now, List<Anomaly> found)
    {
        List<AutostartEntry> current;
        try { current = Autostart.Snapshot(); }
        catch (Exception) { return; }

        var saved = store.Get("autostart");
        var keys = current.Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        store.Set("autostart", JsonSerializer.Serialize(keys));
        if (saved is null) return; // первый снимок — это база, не тревога

        var known = JsonSerializer.Deserialize<HashSet<string>>(saved) ?? [];
        known = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);

        foreach (var e in current.Where(e => !known.Contains(e.Key)))
        {
            if (e.Command.Contains(OwnDir, StringComparison.OrdinalIgnoreCase)) continue;
            var app = string.IsNullOrEmpty(e.Command) ? e.Name : Path.GetFileName(e.Command.Trim('"').Split('"')[0]);
            Add(found, now, "autostart", e.Critical ? "high" : "medium", string.IsNullOrEmpty(app) ? e.Name : app,
                "Новое в автозагрузке",
                string.IsNullOrEmpty(e.Command) ? $"{e.Where}: {e.Name}" : $"{e.Where}: {e.Name} → {e.Command}",
                $"autostart|{e.Key}");
        }
    }
}
