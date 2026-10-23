using SysWatch.App.Anomalies;
using SysWatch.App.Data;
using SysWatch.App.Dev;
using SysWatch.App.Disk;
using SysWatch.App.Sys;

namespace SysWatch.App.Reports;

/// <summary>Один пункт «что не так»: насколько серьёзно, что именно и куда перейти, чтобы разобраться.</summary>
/// <param name="Severity">critical, warn, info.</param>
/// <param name="Action">Раздел окна (#disk, #system…), где это исправляется.</param>
public sealed record Finding(string Id, string Severity, string Title, string Detail, string Action, string ActionLabel, int Penalty);

public sealed record HealthFacts(
    bool AgentRunning, double? AgentCpu, double CpuAvg, double MemAvg, double MemPeak,
    string? SystemDrive, long DriveFree, long DriveTotal, long UptimeSeconds,
    int Slowdowns7d, int Crashes7d, int Anomalies7d, double? GpuPeakTemp, double DataHours);

public sealed record HealthReport(long Ts, int Score, string Grade, string GradeKey, IReadOnlyList<Finding> Findings, HealthFacts Facts,
    IReadOnlyList<ReportPoint> Points);

/// <summary>
/// Оценка здоровья компьютера 0–100: из 100 вычитаются баллы за каждую найденную проблему
/// (место на диске, сбои, перегрузки, перегрев, износ, аномалии, автозагрузка, устаревшие SDK).
/// Только по данным, которые SysWatch уже собирает; результат кешируется на минуту.
/// </summary>
public sealed class Health(Queries queries, Insights insights, AnomalyStore anomalies, DiskService disk, Startup startup, DevEnv dev, GpuQueries gpu)
{
    private readonly object _gate = new();
    private (long At, string Lang, HealthReport Report)? _cache;

    public HealthReport Get(bool refresh = false)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_gate)
        {
            if (!refresh && _cache is { } c && now - c.At < 60 && c.Lang == L.Lang) return c.Report;
        }
        var report = Build(now);
        lock (_gate) _cache = (now, L.Lang, report);
        return report;
    }

    private static T? Try<T>(Func<T> f) where T : class
    {
        try { return f(); } catch (Exception) { return null; }
    }

    private HealthReport Build(long now)
    {
        // Источники независимы — читаем параллельно: журналы событий и WMI бывают медленными.
        var dayTask = Task.Run(() => Try(() => insights.Build(now - 86400, now)));
        var weekTask = Task.Run(() => { try { return insights.CountSlowdowns(now - 7 * 86400, now); } catch (Exception) { return 0; } });
        var crashTask = Task.Run(() => Try(() => Crashes.List(7)));
        var disksTask = Task.Run(() => Try(Hardware.Disks));
        var batteryTask = Task.Run(() => Try(Hardware.Battery));
        var startupTask = Task.Run(() => Try(startup.List));
        var gpuTask = Task.Run(() => Try(() => gpu.Get(1440)));
        Task.WaitAll(dayTask, weekTask, crashTask, disksTask, batteryTask, startupTask, gpuTask);

        var day = dayTask.Result;
        var agent = queries.Agent();
        var drives = Try(disk.Drives) ?? [];
        var sys = drives.FirstOrDefault(d => d.IsSystem);
        var crashes = crashTask.Result;
        var anomalyList = anomalies.List(now - 7 * 86400).Items;
        var findings = new List<Finding>();

        void Add(string id, string severity, int penalty, string action, string actionLabel, string title, string detail) =>
            findings.Add(new Finding(id, severity, title, detail, action, actionLabel, penalty));

        // Агент
        if (!agent.Running)
            Add("agent", "critical", 30, "#now", L.T("Подробнее"), L.T("Агент не записывает данные"),
                L.T("Служба SysWatch Agent не работает: история, отчёты и поиск аномалий не пополняются. Переустановите SysWatch или запустите службу в «Службах» Windows."));

        // Место на дисках
        foreach (var d in drives.Where(d => d.Total > 0))
        {
            double freePct = d.Free * 100.0 / d.Total;
            var name = d.Root.TrimEnd('\\');
            if (d.IsSystem && freePct < 5)
                Add("space-" + name, "critical", 25, "#disk", L.T("Освободить место"), L.T("На диске {0} почти не осталось места", name),
                    L.T("Свободно {0:F0}% ({1}). Windows и программы начнут сбоить, обновления не установятся.", freePct, Size(d.Free)));
            else if (d.IsSystem && freePct < 10)
                Add("space-" + name, "warn", 15, "#disk", L.T("Освободить место"), L.T("Мало места на диске {0}", name),
                    L.T("Свободно {0:F0}% ({1}). Обычно места хватает, когда свободно больше 15%.", freePct, Size(d.Free)));
            else if (d.IsSystem && freePct < 15)
                Add("space-" + name, "info", 5, "#disk", L.T("Посмотреть"), L.T("Место на диске {0} заканчивается", name),
                    L.T("Свободно {0:F0}% ({1}).", freePct, Size(d.Free)));
            else if (!d.IsSystem && freePct < 5)
                Add("space-" + name, "warn", 8, "#disk", L.T("Посмотреть"), L.T("Диск {0} почти заполнен", name),
                    L.T("Свободно {0:F0}% ({1}).", freePct, Size(d.Free)));
        }

        // Здоровье дисков
        foreach (var d in disksTask.Result ?? [])
        {
            if (d.Health == "unhealthy")
                Add("disk-" + d.Name, "critical", 30, "#system", L.T("Подробнее"), L.T("Диск {0} неисправен", d.Name),
                    L.T("Windows сообщает о неисправности. Сделайте резервную копию важных данных как можно скорее."));
            else if (d.Health == "warning")
                Add("disk-" + d.Name, "warn", 15, "#system", L.T("Подробнее"), L.T("Диск {0}: есть предупреждения", d.Name),
                    L.T("Windows видит признаки проблем с этим диском. Стоит сделать резервную копию."));
            if (d.Wear is { } wear && 100 - wear < 10)
                Add("wear-" + d.Name, "critical", 20, "#system", L.T("Подробнее"), L.T("SSD {0} почти изношен", d.Name),
                    L.T("Осталось {0}% ресурса записи.", 100 - wear));
            else if (d.Wear is { } w2 && 100 - w2 < 20)
                Add("wear-" + d.Name, "warn", 10, "#system", L.T("Подробнее"), L.T("Ресурс SSD {0} заканчивается", d.Name),
                    L.T("Осталось {0}% ресурса записи.", 100 - w2));
            if ((d.ReadErrors ?? 0) + (d.WriteErrors ?? 0) > 0)
                Add("errors-" + d.Name, "warn", 10, "#system", L.T("Подробнее"), L.T("Ошибки чтения или записи на диске {0}", d.Name),
                    L.T("Чтение: {0}, запись: {1}. Единичные ошибки бывают, но если число растёт — диск стоит заменить.", d.ReadErrors ?? 0, d.WriteErrors ?? 0));
        }

        // Сбои за неделю
        int crashCount = 0;
        if (crashes is not null)
        {
            int Count(string kind) => crashes.Counts.TryGetValue(kind, out var n) ? n : 0;
            int bsod = Count("bsod"), power = Count("power"), gpuResets = Count("gpu"), hw = Count("hardware"), diskErr = Count("disk"), upd = Count("update");
            crashCount = bsod + power + gpuResets + hw + diskErr;
            if (bsod > 0) Add("bsod", "critical", Math.Min(30, 20 * bsod), "#system", L.T("Сбои"), L.T("Синих экранов за неделю: {0}", bsod),
                L.T("Частые причины — драйверы, перегрев, неисправная память или диск. Коды ошибок — в разделе «Система → Сбои»."));
            if (power > 0) Add("power", "warn", Math.Min(16, 8 * power), "#system", L.T("Сбои"), L.T("Внезапных выключений за неделю: {0}", power),
                L.T("Компьютер выключался без завершения работы: зависание, питание или удержание кнопки."));
            if (gpuResets > 0) Add("gpu-reset", "warn", Math.Min(12, 6 * gpuResets), "#gpu", L.T("Видеокарта"), L.T("Сбросов видеодрайвера за неделю: {0}", gpuResets),
                L.T("Экран гас на пару секунд. Частые причины — перегрев, разгон или версия драйвера."));
            if (hw > 0) Add("whea", "warn", 10, "#system", L.T("Сбои"), L.T("Аппаратные ошибки за неделю: {0}", hw),
                L.T("Windows зафиксировала ошибки оборудования (WHEA). Повторяющиеся ошибки — повод проверить память, процессор и питание."));
            if (diskErr > 0) Add("disk-errors", "critical", 20, "#system", L.T("Сбои"), L.T("Ошибки диска в журнале: {0}", diskErr),
                L.T("Сделайте резервную копию и проверьте диск."));
            if (upd > 0) Add("update", "info", 4, "#system", L.T("Сбои"), L.T("Обновления Windows не установились: {0}", upd),
                L.T("Откройте Центр обновления Windows и повторите установку."));
        }

        // Перегрузки и память
        int slowCount = weekTask.Result;
        if (slowCount >= 10)
            Add("slow", "warn", 10, "#report", L.T("Почему тормозило"), L.T("Компьютер часто перегружен: {0} раз за неделю", slowCount),
                L.T("Процессор или память были на пределе. В отчёте видно, какие программы виноваты."));
        else if (slowCount >= 3)
            Add("slow", "info", 5, "#report", L.T("Почему тормозило"), L.T("Перегрузки за неделю: {0}", slowCount),
                L.T("В отчёте видно, какие программы виноваты."));

        if (day is { Hours: > 2 } && day.AvgMem > 85)
            Add("memory", "warn", 10, "#now", L.T("Кто занимает память"), L.T("Памяти не хватает"),
                L.T("За сутки память была занята в среднем на {0:F0}%. Windows выгружает данные на диск, и всё работает медленнее.", day.AvgMem));

        // Видеокарта
        double? gpuPeak = gpuTask.Result?.Adapters.Select(a => a.PeakTemp).Where(t => t is not null).Max();
        if (gpuPeak >= 90)
            Add("gpu-temp", "warn", 10, "#gpu", L.T("Видеокарта"), L.T("Видеокарта перегревается"),
                L.T("Максимум за сутки — {0:F0} °C. Почистите охлаждение, проверьте, не закрыты ли вентиляционные отверстия.", gpuPeak));
        else if (gpuPeak >= 85)
            Add("gpu-temp", "info", 4, "#gpu", L.T("Видеокарта"), L.T("Видеокарта сильно нагревается"),
                L.T("Максимум за сутки — {0:F0} °C.", gpuPeak));

        // Батарея
        var bat = batteryTask.Result;
        if (bat is { Present: true, WearPct: >= 40 })
            Add("battery", "warn", 8, "#system", L.T("Батарея"), L.T("Батарея сильно изношена"),
                L.T("Ёмкость меньше заводской на {0}%.", bat.WearPct));
        else if (bat is { Present: true, WearPct: >= 25 })
            Add("battery", "info", 3, "#system", L.T("Батарея"), L.T("Батарея заметно изношена"),
                L.T("Ёмкость меньше заводской на {0}%.", bat.WearPct));

        // Аномалии
        int high = anomalyList.Count(a => a.Severity == "high"), medium = anomalyList.Count(a => a.Severity == "medium");
        if (high > 0)
            Add("anomalies-high", "warn", Math.Min(20, 10 * high), "#anomalies", L.T("Аномалии"), L.T("Важных аномалий за неделю: {0}", high),
                L.T("Например, программа без цифровой подписи выходит в сеть. Проверьте, знакомы ли вам эти программы."));
        if (medium > 0)
            Add("anomalies-medium", "info", Math.Min(9, 3 * medium), "#anomalies", L.T("Аномалии"), L.T("Аномалий, требующих внимания: {0}", medium),
                L.T("Новые программы в автозагрузке, открытые в сеть порты, долгая нагрузка."));

        // Автозагрузка
        var items = startupTask.Result ?? [];
        int unsigned = items.Count(i => i.Enabled && i.Signature == "unsigned");
        int enabled = items.Count(i => i.Enabled);
        if (unsigned > 0)
            Add("startup-unsigned", "warn", Math.Min(10, 5 * unsigned), "#system", L.T("Автозагрузка"), L.T("Программы без подписи в автозагрузке: {0}", unsigned),
                L.T("Запускаются вместе с Windows, а проверить, кто их выпустил, нельзя."));
        if (enabled > 12)
            Add("startup-many", "info", 3, "#system", L.T("Автозагрузка"), L.T("Много программ в автозагрузке: {0}", enabled),
                L.T("Каждая замедляет запуск Windows и держит память. Лишние можно выключить."));

        // Разработка: устаревшие SDK (только если раздел уже сканировался — здесь сканирование не запускаем)
        var devData = dev.Cached;
        if (devData is not null)
        {
            int eol = devData.Groups.SelectMany(g => g.Items).Count(i => i.Support == "eol");
            if (eol > 0)
                Add("dev-eol", "info", Math.Min(8, 2 * eol), "#dev", L.T("Разработка"), L.T("Компонентов без поддержки: {0}", eol),
                    L.T("Старые SDK и среды выполнения не получают исправлений безопасности и занимают место."));
        }

        // Аптайм
        long uptime = Environment.TickCount64 / 1000;
        if (uptime > 14 * 86400)
            Add("uptime", "info", 3, "#now", L.T("Подробнее"), L.T("Компьютер не перезагружался {0} дн.", uptime / 86400),
                L.T("Перезагрузка завершает установку обновлений и освобождает память."));

        int score = Math.Clamp(100 - findings.Sum(f => f.Penalty), 0, 100);
        var (gradeKey, grade) = score switch
        {
            >= 90 => ("great", L.T("Отлично")),
            >= 75 => ("good", L.T("Хорошо")),
            >= 50 => ("fair", L.T("Есть проблемы")),
            _ => ("poor", L.T("Требует внимания")),
        };

        var order = new Dictionary<string, int> { ["critical"] = 0, ["warn"] = 1, ["info"] = 2 };
        var facts = new HealthFacts(agent.Running, agent.Cpu, day?.AvgCpu ?? 0, day?.AvgMem ?? 0, day?.PeakMem ?? 0,
            sys?.Root, sys?.Free ?? 0, sys?.Total ?? 0, uptime, slowCount, crashCount, anomalyList.Count, gpuPeak, day?.Hours ?? 0);

        return new HealthReport(now, score, grade, gradeKey,
            findings.OrderBy(f => order[f.Severity]).ThenByDescending(f => f.Penalty).ToList(), facts, day?.Points ?? []);
    }

    private static string Size(long b) => b >= 1L << 30 ? L.T("{0:F1} ГБ", b / (double)(1L << 30)) : L.T("{0} МБ", b >> 20);
}
