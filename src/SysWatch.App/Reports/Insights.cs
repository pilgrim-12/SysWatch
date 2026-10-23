using Microsoft.Data.Sqlite;
using SysWatch.App.Anomalies;
using SysWatch.App.Data;
using SysWatch.App.Disk;

namespace SysWatch.App.Reports;

public sealed record Culprit(string App, double Cpu, double CpuUsual, long Mem, long MemGrowth, double Io, string? Hint);

public sealed record StartedProc(string App, long Ts, string? Hint);

/// <summary>Разбор одного отрезка времени: что было загружено и кто виноват.</summary>
public sealed record Why(
    long From, long To, bool HasData, double AvgCpu, double PeakCpu, double AvgMem, double PeakMem,
    IReadOnlyList<string> Kinds, string Verdict, IReadOnlyList<string> Details,
    IReadOnlyList<Culprit> Culprits, IReadOnlyList<StartedProc> Started);

public sealed record ReportApp(string Name, double Cpu, long Mem, long Io, long Net);

public sealed record ReportPoint(long Ts, double? Cpu, double? Mem);

public sealed record RuleCount(string Rule, int Count);

public sealed record Report(
    long From, long To, double Hours, double AvgCpu, double PeakCpu, double AvgMem, double PeakMem,
    long NetTx, long NetRx, long IoRead, long IoWrite,
    IReadOnlyList<ReportPoint> Points,
    IReadOnlyList<ReportApp> TopCpu, IReadOnlyList<ReportApp> TopMem, IReadOnlyList<ReportApp> TopIo, IReadOnlyList<ReportApp> TopNet,
    IReadOnlyList<Why> Episodes, int SlowMinutes,
    IReadOnlyList<RuleCount> Anomalies, IReadOnlyList<Anomaly> Notable, IReadOnlyList<string> NewPrograms,
    DriveInfoView? SystemDrive, Growth? DiskGrowth);

/// <summary>
/// «Почему тормозило?» и отчёт за период. Только правила по данным агента — без сети и без моделей:
/// перегрузка процессора или памяти, кто её дал, кто был необычно активен, что запустилось перед этим.
/// </summary>
public sealed class Insights(DayFiles files, AnomalyStore anomalies, DiskService disk)
{
    private const double CpuBusy = 75, MemBusy = 90;
    private static readonly int Cores = Environment.ProcessorCount;
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static long FileTimeToUnix(long ft) => ft / 10_000_000 - 11_644_473_600;

    /// <summary>Фоновые службы Windows, которые чаще всего тормозят компьютер, — с понятным объяснением.</summary>
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TiWorker.exe"] = L.K("установка обновлений Windows"),
        ["TrustedInstaller.exe"] = L.K("установка обновлений Windows"),
        ["MoUsoCoreWorker.exe"] = L.K("Центр обновления Windows"),
        ["usocoreworker.exe"] = L.K("Центр обновления Windows"),
        ["wuauclt.exe"] = L.K("Центр обновления Windows"),
        ["MsMpEng.exe"] = L.K("проверка Защитником Windows"),
        ["MpDefenderCoreService.exe"] = L.K("Защитник Windows"),
        ["NisSrv.exe"] = L.K("сетевая защита Защитника Windows"),
        ["SearchIndexer.exe"] = L.K("индексирование для поиска Windows"),
        ["SearchProtocolHost.exe"] = L.K("индексирование для поиска Windows"),
        ["SearchFilterHost.exe"] = L.K("индексирование для поиска Windows"),
        ["CompatTelRunner.exe"] = L.K("сбор данных совместимости для Microsoft"),
        ["DeviceCensus.exe"] = L.K("сбор данных об устройстве для Microsoft"),
        ["OneDrive.exe"] = L.K("синхронизация OneDrive"),
        ["MicrosoftEdgeUpdate.exe"] = L.K("обновление Edge"),
        ["GoogleUpdate.exe"] = L.K("обновление Chrome"),
        ["dism.exe"] = L.K("обслуживание образа Windows"),
        ["cleanmgr.exe"] = L.K("очистка диска"),
        ["defrag.exe"] = L.K("оптимизация диска"),
        ["vmmem"] = L.K("виртуальная машина (WSL или Docker)"),
        ["vmmemWSL"] = L.K("виртуальная машина WSL"),
        ["dwm.exe"] = L.K("отрисовка окон"),
        ["audiodg.exe"] = L.K("обработка звука"),
        ["WmiPrvSE.exe"] = L.K("запросы WMI от какой-то программы"),
        ["sppsvc.exe"] = L.K("проверка лицензии Windows"),
        ["msiexec.exe"] = L.K("установка программы"),
    };

    private static string? Hint(string app) => Known.TryGetValue(app, out var h) ? L.T(h) : null;

    // ── Данные ───────────────────────────────────────────────────────────

    private sealed class AppAcc { public long CpuMs, MemMax, MemFirst = -1, MemLast, Io, Net; }

    /// <summary>Минутные значения системы: ts → (процессор %, память %).</summary>
    private SortedDictionary<long, (double Cpu, double Mem)> SystemRows(long from, long to)
    {
        var map = new SortedDictionary<long, (double, double)>();
        foreach (var (_, path) in files.For(from))
        {
            try
            {
                using var c = DayFiles.Open(path);
                foreach (var r in c.Cmd("SELECT ts, cpu_pct, mem_load_pct FROM system_1m WHERE ts >= $f AND ts < $t",
                             ("$f", from), ("$t", to)).Rows())
                    map[r.GetInt64(0)] = (r.Dbl0(1), r.Dbl0(2));
            }
            catch (SqliteException) { /* файл занят агентом — пропускаем */ }
        }
        return map;
    }

    private Dictionary<string, AppAcc> Apps(long from, long to, out int minutes)
    {
        var apps = new Dictionary<string, AppAcc>(StringComparer.OrdinalIgnoreCase);
        var mins = new HashSet<long>();
        foreach (var (_, path) in files.For(from))
        {
            try
            {
                using var c = DayFiles.Open(path);
                var mem = c.MemColumn();
                foreach (var r in c.Cmd($"""
                    SELECT p.image, m.ts, SUM(m.cpu_ms), SUM(m.{mem}), SUM(m.io_read + m.io_write), SUM(m.net_tx + m.net_rx)
                    FROM proc_1m m JOIN process p ON p.id = m.proc_id
                    WHERE m.ts >= $f AND m.ts < $t GROUP BY p.image, m.ts ORDER BY m.ts
                    """, ("$f", from), ("$t", to)).Rows())
                {
                    var name = r.GetString(0);
                    if (!apps.TryGetValue(name, out var a)) apps[name] = a = new AppAcc();
                    mins.Add(r.GetInt64(1));
                    a.CpuMs += r.Long0(2);
                    long m = r.Long0(3);
                    a.MemMax = Math.Max(a.MemMax, m);
                    if (a.MemFirst < 0) a.MemFirst = m;
                    a.MemLast = m;
                    a.Io += r.Long0(4);
                    a.Net += r.Long0(5);
                }
            }
            catch (SqliteException) { }
        }
        minutes = Math.Max(1, mins.Count);
        return apps;
    }

    private List<StartedProc> StartedBetween(long from, long to)
    {
        var list = new List<StartedProc>();
        var seen = new HashSet<(string, long)>();
        foreach (var (_, path) in files.For(from))
        {
            try
            {
                using var c = DayFiles.Open(path);
                foreach (var r in c.Cmd("SELECT image, create_time FROM process WHERE create_time IS NOT NULL").Rows())
                {
                    long ts = FileTimeToUnix(r.GetInt64(1));
                    var app = r.GetString(0);
                    if (ts >= from && ts < to && seen.Add((app, ts / 60))) list.Add(new StartedProc(app, ts, Hint(app)));
                }
            }
            catch (SqliteException) { }
        }
        // Десятки svchost и conhost ничего не объясняют — показываем по одному разу на программу.
        return list.GroupBy(s => s.App, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(s => s.Ts).First())
            .OrderBy(s => s.Ts).Take(12).ToList();
    }

    // ── «Почему тормозило?» ──────────────────────────────────────────────

    /// <summary>Разобрать отрезок [from, to). Сравнение — с получасом до него.</summary>
    public Why Explain(long from, long to)
    {
        if (to - from < 60) to = from + 60;
        var sys = SystemRows(from, to);
        var now = Apps(from, to, out int minutes);
        var before = Apps(from - 1800, from, out int beforeMinutes);
        var started = StartedBetween(from - 180, to);

        if (sys.Count == 0 && now.Count == 0)
            return new Why(from, to, false, 0, 0, 0, 0, [], L.T("В это время агент не записывал данные: компьютер был выключен, спал или служба не работала."), [], [], started);

        double avgCpu = sys.Count > 0 ? sys.Values.Average(v => v.Cpu) : 0, peakCpu = sys.Count > 0 ? sys.Values.Max(v => v.Cpu) : 0;
        double avgMem = sys.Count > 0 ? sys.Values.Average(v => v.Mem) : 0, peakMem = sys.Count > 0 ? sys.Values.Max(v => v.Mem) : 0;
        double seconds = minutes * 60.0;

        double CpuPct(AppAcc a, int mins) => a.CpuMs / (mins * 60.0 * 1000 * Cores) * 100;

        var culprits = now.Select(kv =>
            {
                before.TryGetValue(kv.Key, out var b);
                long growth = b is null ? kv.Value.MemLast : kv.Value.MemMax - b.MemFirst;
                return new Culprit(kv.Key, CpuPct(kv.Value, minutes), b is null ? 0 : CpuPct(b, beforeMinutes),
                    kv.Value.MemMax, growth, kv.Value.Io / seconds, Hint(kv.Key));
            })
            .ToList();

        var kinds = new List<string>();
        var details = new List<string>();
        var shown = new List<Culprit>();

        var byCpu = culprits.OrderByDescending(c => c.Cpu).Take(5).Where(c => c.Cpu >= 1).ToList();
        var byMem = culprits.OrderByDescending(c => c.Mem).Take(5).ToList();
        var byIo = culprits.OrderByDescending(c => c.Io).Where(c => c.Io >= 20 * 1048576).Take(3).ToList();

        if (peakCpu >= CpuBusy)
        {
            kinds.Add("cpu");
            details.Add(L.T("Процессор был занят на {0:F0}% (в среднем {1:F0}%). Больше всего его загружали: {2}.", peakCpu, avgCpu,
                        string.Join(", ", byCpu.Take(3).Select(c => $"{Label(c)} — {c.Cpu:F0}%"))));
            foreach (var c in byCpu.Where(c => c.Cpu >= 5 && c.Cpu >= c.CpuUsual * 3 + 2).Take(2))
                details.Add(c.CpuUsual < 1
                    ? L.T("{0} до этого почти не работал — нагрузка возникла внезапно.", c.App)
                    : L.T("{0} обычно берёт около {1:F0}%, здесь — {2:F0}%: это всплеск, а не постоянный фон.", c.App, c.CpuUsual, c.Cpu));
            shown.AddRange(byCpu);
        }

        if (peakMem >= MemBusy)
        {
            kinds.Add("mem");
            details.Add(L.T("Память была заполнена на {0:F0}%. В таком состоянии Windows выгружает данные программ на диск, и всё начинает отвечать с задержкой. Больше всего памяти держали: {1}.", peakMem,
                        string.Join(", ", byMem.Take(3).Select(c => $"{Label(c)} — {Gb(c.Mem)}"))));
            var grower = culprits.Where(c => c.MemGrowth >= 500L * 1048576).OrderByDescending(c => c.MemGrowth).FirstOrDefault();
            if (grower is not null) details.Add(L.T("{0} вырос на {1} за полчаса до этого.", grower.App, Gb(grower.MemGrowth)));
            shown.AddRange(byMem.Take(3));
        }

        if (byIo.Count > 0)
        {
            kinds.Add("io");
            details.Add(L.T("Активно читали и писали данные: {0}. Если диск жёсткий (HDD), такой поток сам по себе тормозит остальные программы.",
                        string.Join(", ", byIo.Select(c => Label(c) + " — " + L.T("{0:F0} МБ/с", c.Io / 1048576)))));
            shown.AddRange(byIo);
        }

        var suspicious = started.Where(s => s.Hint is not null).ToList();
        if (suspicious.Count > 0)
            details.Add(L.T("Незадолго до этого запустились: {0}.", string.Join(", ", suspicious.Select(s => $"{s.App} ({s.Hint})"))));

        string verdict = kinds.Count switch
        {
            0 => L.T("Перегрузки не видно: процессор в среднем {0:F0}% (пик {1:F0}%), память {2:F0}%. Причина могла быть там, куда агент не смотрит: видеокарта, перегрев и сброс частоты, ожидание диска или сети, зависшее окно программы.", avgCpu, peakCpu, peakMem),
            _ => Lead(kinds, shown, suspicious),
        };

        return new Why(from, to, true, avgCpu, peakCpu, avgMem, peakMem, kinds, verdict, details,
            shown.DistinctBy(c => c.App).Take(6).ToList(), started);
    }

    private static string Lead(List<string> kinds, List<Culprit> shown, List<StartedProc> known)
    {
        var top = shown.FirstOrDefault();
        string what = string.Join(L.T(" и "), kinds.Select(k => k switch { "cpu" => L.T("процессор"), "mem" => L.T("память"), _ => L.T("диск") }));
        string who = top is null ? "" : " " + L.T("Главный виновник — {0}.", Label(top));
        if (top?.Hint is null && known.FirstOrDefault() is { } k) who += " " + L.T("Рядом по времени — {0}.", k.Hint);
        return L.T("Не хватало ресурсов: {0}.", what) + who;
    }

    private static string Label(Culprit c) => c.Hint is null ? c.App : $"{c.App} ({c.Hint})";

    private static string Gb(long b) => b >= 1L << 30 ? L.T("{0:F1} ГБ", b / (double)(1L << 30)) : L.T("{0} МБ", b >> 20);

    /// <summary>Эпизоды перегрузки: подряд идущие минуты с процессором ≥75% или памятью ≥90%.</summary>
    public List<(long From, long To, double Score)> Episodes(SortedDictionary<long, (double Cpu, double Mem)> sys)
    {
        var list = new List<(long, long, double)>();
        long? start = null, last = null; double score = 0;
        foreach (var (ts, v) in sys)
        {
            bool busy = v.Cpu >= CpuBusy || v.Mem >= MemBusy;
            if (busy && start is not null && ts - last!.Value <= 180) { last = ts; score += Weight(v); continue; }
            if (start is not null) list.Add((start.Value, last!.Value + 60, score));
            start = busy ? ts : null; last = busy ? ts : null; score = busy ? Weight(v) : 0;
        }
        if (start is not null) list.Add((start.Value, last!.Value + 60, score));
        // Одна «тяжёлая» минута — тоже эпизод, если процессор был почти полностью занят.
        return list.Where(e => e.Item2 - e.Item1 >= 120 || e.Item3 >= 1.2).ToList();

        static double Weight((double Cpu, double Mem) v) =>
            Math.Max(0, (v.Cpu - CpuBusy) / (100 - CpuBusy)) + Math.Max(0, (v.Mem - MemBusy) / (100 - MemBusy)) + 0.5;
    }

    /// <summary>Сколько было эпизодов перегрузки — без разбора каждого (дёшево).</summary>
    public int CountSlowdowns(long from, long to) => Episodes(SystemRows(from, to)).Count;

    public IReadOnlyList<Why> SlowDowns(long from, long to, int max = 12)
    {
        var sys = SystemRows(from, to);
        return Episodes(sys).OrderByDescending(e => e.Score).Take(max)
            .Select(e => Explain(e.From, e.To)).OrderByDescending(w => w.From).ToList();
    }

    // ── Отчёт за период ──────────────────────────────────────────────────

    public Report Build(long from, long to)
    {
        var sys = SystemRows(from, to);
        var apps = Apps(from, to, out int minutes);
        var episodes = Episodes(sys);

        long ioR = 0, ioW = 0, tx = 0, rx = 0;
        foreach (var (_, path) in files.For(from))
        {
            try
            {
                using var c = DayFiles.Open(path);
                foreach (var r in c.Cmd("SELECT SUM(io_read), SUM(io_write), SUM(net_tx), SUM(net_rx) FROM proc_1m WHERE ts >= $f AND ts < $t",
                             ("$f", from), ("$t", to)).Rows())
                { ioR += r.Long0(0); ioW += r.Long0(1); tx += r.Long0(2); rx += r.Long0(3); }
            }
            catch (SqliteException) { }
        }

        // Точки для графика: по часу на точку для недели, по 10 минут для суток.
        int step = to - from > 2 * 86400 ? 3600 : 600;
        var points = new List<ReportPoint>();
        for (long t = from - from % step; t < to; t += step)
        {
            var bucket = sys.Where(kv => kv.Key >= t && kv.Key < t + step).Select(kv => kv.Value).ToList();
            points.Add(bucket.Count == 0 ? new ReportPoint(t, null, null)
                : new ReportPoint(t, bucket.Average(v => v.Cpu), bucket.Average(v => v.Mem)));
        }

        double span = minutes * 60.0;
        var rows = apps.Select(kv => new ReportApp(kv.Key, kv.Value.CpuMs / (span * 1000 * Cores) * 100, kv.Value.MemMax, kv.Value.Io, kv.Value.Net)).ToList();

        var anomalyList = anomalies.List(from).Items.Where(a => a.Ts < to).ToList();
        var counts = anomalyList.GroupBy(a => a.Rule).Select(g => new RuleCount(g.Key, g.Count())).OrderByDescending(r => r.Count).ToList();
        var newPrograms = anomalyList.Where(a => a.Rule == "new_program").Select(a => a.App).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        var notable = anomalyList.Where(a => a.Severity is "high" or "medium").OrderByDescending(a => a.Ts).Take(10).ToList();

        DriveInfoView? sysDrive = null; Growth? growth = null;
        try
        {
            sysDrive = disk.Drives().FirstOrDefault(d => d.IsSystem);
            if (sysDrive is not null) growth = disk.Growth(sysDrive.Root);
        }
        catch (Exception) { /* диск — необязательная часть отчёта */ }

        var top = episodes.OrderByDescending(e => e.Score).Take(8).Select(e => Explain(e.From, e.To)).OrderByDescending(w => w.From).ToList();

        return new Report(
            from, to, sys.Count / 60.0,
            sys.Count > 0 ? sys.Values.Average(v => v.Cpu) : 0, sys.Count > 0 ? sys.Values.Max(v => v.Cpu) : 0,
            sys.Count > 0 ? sys.Values.Average(v => v.Mem) : 0, sys.Count > 0 ? sys.Values.Max(v => v.Mem) : 0,
            tx, rx, ioR, ioW, points,
            rows.OrderByDescending(a => a.Cpu).Take(8).ToList(),
            rows.OrderByDescending(a => a.Mem).Take(8).ToList(),
            rows.OrderByDescending(a => a.Io).Take(8).ToList(),
            rows.OrderByDescending(a => a.Net).Where(a => a.Net > 0).Take(8).ToList(),
            top, episodes.Sum(e => (int)((e.To - e.From) / 60)),
            counts, notable, newPrograms, sysDrive, growth);
    }

    public static (long From, long To) Period(int days) => (Now() - days * 86400L, Now());
}
