using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using SysWatch.App.Anomalies;
using SysWatch.App.Data;
using SysWatch.App.Dev;
using SysWatch.App.Disk;
using SysWatch.App.Sampling;
using SysWatch.App.Sys;

namespace SysWatch.App.Reports;

public sealed record ReportTemplate(string Id, string Title, string Description, int DefaultDays, bool HasPeriod);

public sealed record ReportDocView(string Template, string Title, string Html);

public sealed record ExportResult(bool Ok, string Message, string? Path);

/// <param name="Every">off, day, week.</param>
/// <param name="Format">html, pdf.</param>
public sealed record ReportSchedule(string Template, string Every, string Format, long LastRun);

/// <summary>
/// Отчёты: несколько шаблонов (здоровье, безопасность, сеть, разработка, неделя), выгрузка в HTML и PDF,
/// отчёты по расписанию в папку «Документы\SysWatch\Reports». PDF печатает Microsoft Edge в фоновом режиме
/// (он есть в любой Windows 10/11), поэтому отдельная библиотека не нужна.
/// </summary>
public sealed class ReportCenter(
    Health health, Insights insights, Queries queries, AnomalyStore anomalies, Startup startup, Firewall firewall, DevEnv dev, DiskService disk)
{
    private const string ScheduleKey = "report_schedules";

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SysWatch", "Reports");

    public IReadOnlyList<ReportTemplate> Templates() =>
    [
        new("health", L.T("Здоровье компьютера"), L.T("Оценка, проблемы и что с ними делать, диски, батарея, сбои и перегрузки."), 7, false),
        new("week", L.T("Итоги за период"), L.T("Загрузка, кто больше всего потреблял, почему тормозило, что нового появилось."), 7, true),
        new("security", L.T("Безопасность"), L.T("Аномалии, открытые в сеть порты, автозагрузка, программы без подписи, запреты сети."), 30, true),
        new("network", L.T("Сеть"), L.T("Сколько трафика и у каких программ, с какими сайтами и серверами они общались."), 7, true),
        new("dev", L.T("Среда разработки"), L.T("SDK и среды выполнения с версиями и сроками поддержки, Docker, node_modules, службы."), 0, false),
    ];

    // ── Построение ───────────────────────────────────────────────────────

    public ReportDocView Build(string template, int days)
    {
        days = Math.Clamp(days <= 0 ? 7 : days, 1, 31);
        long to = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), from = to - days * 86400L;

        if (template == "week")
            return new ReportDocView(template, L.T("Итоги за период"), ReportHtml.Render(insights.Build(from, to)));

        var doc = new Doc();
        string title;
        switch (template)
        {
            case "security": title = L.T("Безопасность"); Security(doc, from, days); break;
            case "network": title = L.T("Сеть"); Network(doc, days); break;
            case "dev": title = L.T("Среда разработки"); Development(doc); break;
            default: title = L.T("Здоровье компьютера"); Health(doc); break;
        }
        var subtitle = template is "dev" or "health"
            ? L.T("{0} · компьютер {1}", When(to), Environment.MachineName)
            : L.T("{0} — {1} · компьютер {2}", When(from), When(to), Environment.MachineName);
        return new ReportDocView(template, title, doc.Render(title, subtitle));
    }

    private void Health(Doc d)
    {
        var h = health.Get(refresh: true);
        d.Score(h.Score, h.Grade, h.GradeKey);
        d.H2(L.T("Что стоит сделать"), null);
        if (h.Findings.Count == 0) d.P(L.T("Проблем не найдено."));
        else d.Findings(h.Findings);

        var f = h.Facts;
        d.H2(L.T("Коротко"), L.T("за последние сутки"));
        d.Kv([
            (L.T("Процессор в среднем"), Pct(f.CpuAvg)),
            (L.T("Память в среднем / пик"), $"{Pct(f.MemAvg)} / {Pct(f.MemPeak)}"),
            (L.T("Без перезагрузки"), L.T("{0} дн. {1} ч", f.UptimeSeconds / 86400, f.UptimeSeconds % 86400 / 3600)),
            (L.T("Перегрузок за неделю"), f.Slowdowns7d.ToString()),
            (L.T("Сбоев за неделю"), f.Crashes7d.ToString()),
            (L.T("Аномалий за неделю"), f.Anomalies7d.ToString()),
            (L.T("Видеокарта, максимум"), f.GpuPeakTemp is { } g ? $"{g:F0} °C" : "—"),
        ]);

        var drives = disk.Drives();
        if (drives.Count > 0)
        {
            d.H2(L.T("Место на дисках"), null);
            d.Table([L.T("Диск"), L.T("Свободно"), L.T("Всего"), L.T("Свободно, %")], [1, 2, 3],
                drives.Select(x => new[] { $"{x.Root.TrimEnd('\\')} {x.Label}", Size(x.Free), Size(x.Total), x.Total > 0 ? Pct(x.Free * 100.0 / x.Total) : "—" }));
        }

        var disks = Hardware.Disks();
        if (disks.Count > 0)
        {
            d.H2(L.T("Состояние дисков"), null);
            d.Table([L.T("Диск"), L.T("Состояние"), L.T("Ресурс"), L.T("Температура"), L.T("Наработка"), L.T("Ошибки")], [2, 3, 4, 5],
                disks.Select(x => new[]
                {
                    $"{x.Name} ({x.Media}{(x.Bus.Length > 0 ? ", " + x.Bus : "")})",
                    x.Health switch { "healthy" => L.T("исправен"), "warning" => L.T("есть предупреждения"), "unhealthy" => L.T("неисправен"), _ => "—" },
                    x.Wear is { } w ? $"{100 - w}%" : "—",
                    x.Temperature is { } t ? $"{t} °C" : "—",
                    x.PowerOnHours is { } ph ? L.T("{0:N0} ч", ph) : "—",
                    ((x.ReadErrors ?? 0) + (x.WriteErrors ?? 0)).ToString(),
                }));
        }

        var bat = Hardware.Battery();
        if (bat.Present)
        {
            d.H2(L.T("Батарея"), null);
            d.Kv([
                (L.T("Заряд"), bat.Charge is { } c ? $"{c}%" : "—"),
                (L.T("Износ"), bat.WearPct is { } w ? $"{w}%" : "—"),
                (L.T("Циклов заряда"), bat.Cycles?.ToString() ?? "—"),
            ]);
        }

        var crashes = Crashes.List(30);
        d.H2(L.T("Сбои за 30 дней"), null);
        if (crashes.Items.Count == 0) d.P(L.T("Сбоев не найдено."));
        else d.Table([L.T("Когда"), L.T("Что"), L.T("Подробно")], [],
            crashes.Items.Take(40).Select(c => new[] { When(c.Ts), c.Title + (c.App is null ? "" : $" — {c.App}"), c.Detail }));

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var slow = insights.SlowDowns(now - 7 * 86400, now, 6);
        d.H2(L.T("Почему тормозило"), L.T("за неделю"));
        if (slow.Count == 0) d.P(L.T("Перегрузок не было."));
        foreach (var w in slow) d.Episode(When(w.From), w.Verdict, w.Details);
    }

    private void Security(Doc d, long from, int days)
    {
        var list = anomalies.List(from).Items;
        d.H2(L.T("Аномалии"), L.T("за {0} дн.", days));
        if (list.Count == 0) d.P(L.T("Аномалий не найдено."));
        else d.Table([L.T("Когда"), L.T("Важность"), L.T("Что"), L.T("Программа"), L.T("Подробно")], [],
            list.Take(100).Select(a => new[] { When(a.Ts), Severity(a.Severity), a.Title, a.App, a.Detail }));

        var exposed = Connections.ExposedListeners();
        d.H2(L.T("Порты, открытые в сеть"), L.T("к ним может подключиться другое устройство в сети"));
        if (exposed.Count == 0) d.P(L.T("Таких портов нет."));
        else d.Table([L.T("Программа"), L.T("Протокол"), L.T("Порт")], [2],
            exposed.Select(e => new[] { ProcessName(e.Pid), e.Proto, e.Port.ToString() }).Distinct(new RowComparer()));

        var items = startup.List();
        d.H2(L.T("Автозагрузка"), L.T("включено {0} из {1}", items.Count(i => i.Enabled), items.Count));
        d.Table([L.T("Программа"), L.T("Издатель"), L.T("Включена"), L.T("Где записано")], [],
            items.Select(i => new[]
            {
                i.Name,
                i.Signature switch { "signed" => i.Signer ?? "", "system" => "Windows", "unsigned" => L.T("без подписи"), _ => L.T("файл не найден") },
                i.Enabled ? L.T("да") : L.T("нет"),
                i.Where,
            }));

        var blocked = firewall.List();
        d.H2(L.T("Запрет выхода в сеть"), L.T("правила Брандмауэра Windows от SysWatch"));
        if (blocked.Count == 0) d.P(L.T("Запретов нет."));
        else d.Table([L.T("Программа"), L.T("С какого времени")], [], blocked.Select(b => new[] { b.Path, When(b.Since) }));

        var newPrograms = list.Where(a => a.Rule == "new_program").Select(a => a.App).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        d.H2(L.T("Впервые запускались"), L.T("за {0} дн.", days));
        if (newPrograms.Count == 0) d.P(L.T("Ничего нового."));
        else d.List(newPrograms);
    }

    private void Network(Doc d, int days)
    {
        int range = days * 1440;
        var net = queries.Network(range);
        d.Kv([
            (L.T("Получено"), Size(net.Totals.Rx)),
            (L.T("Отправлено"), Size(net.Totals.Tx)),
            (L.T("Адресов"), net.Totals.Domains.ToString()),
            (L.T("Программ в сети"), net.Totals.Apps.ToString()),
        ]);

        var apps = queries.Overview(range).Apps.Where(a => a.NetTx + a.NetRx > 0).OrderByDescending(a => a.NetTx + a.NetRx).Take(25).ToList();
        d.H2(L.T("Программы"), L.T("кто больше всего пользовался сетью"));
        d.Table([L.T("Программа"), L.T("Получено"), L.T("Отправлено"), L.T("Адресов")], [1, 2, 3],
            apps.Select(a => new[] { a.Name, Size(a.NetRx), Size(a.NetTx), a.Domains.ToString() }));

        d.H2(L.T("Адреса"), L.T("с кем общались больше всего"));
        d.Table([L.T("Адрес"), L.T("Получено"), L.T("Отправлено"), L.T("Программы"), L.T("Впервые")], [1, 2],
            net.Domains.OrderByDescending(x => x.Tx + x.Rx).Take(40)
                .Select(x => new[] { x.Domain, Size(x.Rx), Size(x.Tx), string.Join(", ", x.Apps.Take(4)), When(x.FirstSeen) }));
    }

    private void Development(Doc d)
    {
        // Раздел мог ещё не сканироваться — запускаем и ждём (до полутора минут: подсчёт размеров папок).
        var data = dev.Cached;
        if (data is null)
        {
            dev.State(false);
            for (int i = 0; i < 180 && (data = dev.Cached) is null; i++) Thread.Sleep(500);
        }
        if (data is null) { d.P(L.T("Не удалось собрать данные о среде разработки.")); return; }

        var all = data.Groups.SelectMany(g => g.Items).ToList();
        d.Kv([
            (L.T("Компонентов"), all.Count.ToString()),
            (L.T("Без поддержки"), all.Count(i => i.Support == "eol").ToString()),
            (L.T("Поддержка скоро закончится"), all.Count(i => i.Support == "soon").ToString()),
            (L.T("Занимают"), Size(all.Sum(i => i.Size ?? 0))),
        ]);

        foreach (var g in data.Groups)
        {
            d.H2(g.Title, g.Hint);
            d.Table([L.T("Компонент"), L.T("Версия"), L.T("Поддержка"), L.T("Размер"), L.T("Примечание")], [3],
                g.Items.Select(i => new[] { i.Name, i.Version, Support(i), i.Size is { } s ? Size(s) : "", i.Note ?? "" }));
        }

        if (data.Docker is { Running: true } docker)
        {
            d.H2("Docker", docker.Version);
            d.Table([L.T("Образ"), L.T("Тег"), L.T("Размер"), L.T("Контейнеров")], [2, 3],
                docker.Images.Select(i => new[] { i.Repository, i.Tag, Size(i.Size), i.Containers.ToString() }));
        }

        if (data.Projects.Count > 0)
        {
            d.H2(L.T("Проекты Node"), L.T("node_modules всего {0}", Size(data.Projects.Sum(p => p.ModulesSize))));
            d.Table([L.T("Проект"), L.T("Папка"), L.T("node_modules")], [2],
                data.Projects.Select(p => new[] { p.Name, p.Path, Size(p.ModulesSize) }));
        }

        if (data.Services.Count > 0)
        {
            d.H2(L.T("Службы"), null);
            d.Table([L.T("Служба"), L.T("Состояние"), L.T("Запуск"), L.T("Память")], [3],
                data.Services.Select(s => new[] { s.Display, s.State, s.StartMode, s.Memory is { } m ? Size(m) : "" }));
        }
    }

    // ── Выгрузка ─────────────────────────────────────────────────────────

    public ExportResult Export(string template, int days, string format, bool open) => Save(Build(template, days), format, open);

    /// <summary>
    /// Долгая подготовка, которой не место под общей блокировкой запросов к истории:
    /// для отчёта «Разработка» — первый обход SDK (до полутора минут).
    /// </summary>
    public void WarmUp(string template)
    {
        if (template != "dev" || dev.Cached is not null) return;
        dev.State(false);
        for (int i = 0; i < 180 && dev.Cached is null; i++) Thread.Sleep(500);
    }

    /// <summary>Записать готовый отчёт в папку отчётов (PDF печатает Edge — это долго, вызывать вне блокировок).</summary>
    public ExportResult Save(ReportDocView view, string format, bool open)
    {
        Directory.CreateDirectory(Folder);
        var stem = $"SysWatch {view.Title} {DateTime.Now:yyyy-MM-dd HH-mm}";
        foreach (var c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '-');
        var html = Path.Combine(Folder, stem + ".html");
        File.WriteAllText(html, view.Html, new UTF8Encoding(true));

        var result = html;
        if (format == "pdf")
        {
            var pdf = Path.Combine(Folder, stem + ".pdf");
            if (!Pdf.Print(html, pdf))
                return new ExportResult(false, L.T("Не удалось сделать PDF: нужен Microsoft Edge. Отчёт сохранён в HTML."), html);
            File.Delete(html);
            result = pdf;
        }
        if (open) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{result}\"") { UseShellExecute = false });
        return new ExportResult(true, L.T("Сохранено: {0}", result), result);
    }

    // ── Расписание ───────────────────────────────────────────────────────

    public List<ReportSchedule> Schedules()
    {
        try { return JsonSerializer.Deserialize<List<ReportSchedule>>(anomalies.Get(ScheduleKey) ?? "[]") ?? []; }
        catch (JsonException) { return []; }
    }

    public void SaveSchedules(IEnumerable<ReportSchedule> list)
    {
        var known = Schedules().GroupBy(s => s.Template).ToDictionary(g => g.Key, g => g.First());
        var clean = list.Where(s => Templates().Any(t => t.Id == s.Template) && s.Every is "off" or "day" or "week")
            .GroupBy(s => s.Template).Select(g => g.Last())
            .Select(s => s with { Format = s.Format == "pdf" ? "pdf" : "html", LastRun = known.TryGetValue(s.Template, out var k) ? k.LastRun : 0 })
            .ToList();
        anomalies.Set(ScheduleKey, JsonSerializer.Serialize(clean));
    }

    /// <summary>Сделать отчёты, которым пора (вызывается из трея раз в час). Возвращает сохранённые файлы.</summary>
    public List<string> RunDue()
    {
        var done = new List<string>();
        if (DateTime.Now.Hour < 8) return done; // ночью не делаем: утром отчёт будет свежим
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var list = Schedules();
        bool changed = false;
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            long period = s.Every switch { "day" => 86400, "week" => 7 * 86400, _ => 0 };
            if (period == 0 || now - s.LastRun < period - 1800) continue;
            try
            {
                var tpl = Templates().First(t => t.Id == s.Template);
                var r = Export(s.Template, s.Every == "day" ? 1 : Math.Max(tpl.DefaultDays, 7), s.Format, open: false);
                if (r.Path is not null) done.Add(r.Path);
            }
            catch (Exception) { /* попробуем в следующий час */ continue; }
            list[i] = s with { LastRun = now };
            changed = true;
        }
        if (changed) anomalies.Set(ScheduleKey, JsonSerializer.Serialize(list));
        return done;
    }

    // ── Форматирование ───────────────────────────────────────────────────

    private static string When(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("d MMM yyyy, HH:mm", L.Culture);
    private static string Pct(double v) => v.ToString("F0", L.Culture) + "%";

    private static string Size(long b) => b switch
    {
        >= 1L << 40 => L.T("{0:F1} ТБ", b / (double)(1L << 40)),
        >= 1L << 30 => L.T("{0:F1} ГБ", b / (double)(1L << 30)),
        >= 1L << 20 => L.T("{0:F0} МБ", b / (double)(1L << 20)),
        >= 1L << 10 => L.T("{0:F0} КБ", b / (double)(1L << 10)),
        _ => L.T("{0} Б", b),
    };

    private static string Severity(string s) => s switch { "high" => L.T("Важно"), "medium" => L.T("Внимание"), _ => L.T("К сведению") };

    private static string Support(DevItem i) => i.Support switch
    {
        "eol" => i.Until is { } u ? L.T("без поддержки с {0}", DateTimeOffset.FromUnixTimeSeconds(u).LocalDateTime.ToString("d MMM yyyy", L.Culture)) : L.T("без поддержки"),
        "soon" => L.T("до {0}", DateTimeOffset.FromUnixTimeSeconds(i.Until ?? 0).LocalDateTime.ToString("d MMM yyyy", L.Culture)),
        "lts" => "LTS",
        "ok" => L.T("поддерживается"),
        _ => "",
    };

    private static string ProcessName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName + ".exe"; }
        catch (Exception) { return $"PID {pid}"; }
    }

    private sealed class RowComparer : IEqualityComparer<string[]>
    {
        public bool Equals(string[]? a, string[]? b) => a is not null && b is not null && a.SequenceEqual(b);
        public int GetHashCode(string[] r) => string.Join("|", r).GetHashCode();
    }
}

/// <summary>Сборщик HTML-отчёта: разделы, таблицы, пары «название — значение». Оформление — как у отчёта за период.</summary>
internal sealed class Doc
{
    private readonly StringBuilder _b = new();
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    public void H2(string title, string? note) =>
        _b.Append("<h2>").Append(E(title)).Append(note is null ? "" : $" <small>{E(note)}</small>").Append("</h2>");

    public void P(string text) => _b.Append("<p>").Append(E(text)).Append("</p>");

    public void List(IEnumerable<string> items) =>
        _b.Append("<ul>").Append(string.Concat(items.Select(i => $"<li>{E(i)}</li>"))).Append("</ul>");

    public void Kv(IEnumerable<(string K, string V)> pairs)
    {
        _b.Append("<div class=\"cards\">");
        foreach (var (k, v) in pairs) _b.Append($"<div class=\"card\"><b>{E(v)}</b><span>{E(k)}</span></div>");
        _b.Append("</div>");
    }

    /// <param name="numeric">Номера колонок с числами — выравниваются вправо.</param>
    public void Table(string[] columns, int[] numeric, IEnumerable<string[]> rows)
    {
        _b.Append("<table><tr>");
        for (int i = 0; i < columns.Length; i++) _b.Append($"<th{(numeric.Contains(i) ? " class=\"n\"" : "")}>{E(columns[i])}</th>");
        _b.Append("</tr>");
        int count = 0;
        foreach (var r in rows)
        {
            _b.Append("<tr>");
            for (int i = 0; i < r.Length; i++) _b.Append($"<td{(numeric.Contains(i) ? " class=\"n\"" : "")}>{E(r[i])}</td>");
            _b.Append("</tr>");
            count++;
        }
        if (count == 0) _b.Append($"<tr><td class=\"dim\" colspan=\"{columns.Length}\">—</td></tr>");
        _b.Append("</table>");
    }

    public void Score(int score, string grade, string key)
    {
        double len = 2 * Math.PI * 52, dash = len * score / 100;
        var color = key switch { "great" => "#2f9e5b", "good" => "#1f8fa6", "fair" => "#c98a12", _ => "#c2410c" };
        _b.Append($"""
            <div class="score"><svg viewBox="0 0 120 120" width="120" height="120"><circle cx="60" cy="60" r="52" fill="none" stroke="var(--line)" stroke-width="12"/>
            <circle cx="60" cy="60" r="52" fill="none" stroke="{color}" stroke-width="12" stroke-linecap="round" stroke-dasharray="{dash.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} {len.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}" transform="rotate(-90 60 60)"/>
            <text x="60" y="68" text-anchor="middle" font-size="30" font-weight="700" fill="currentColor">{score}</text></svg>
            <div><b style="color:{color}">{E(grade)}</b><span>{E(L.T("Оценка здоровья компьютера из 100"))}</span></div></div>
            """);
    }

    public void Findings(IEnumerable<Finding> list)
    {
        foreach (var f in list)
            _b.Append($"<div class=\"ep {f.Severity}\"><h3>{E(f.Title)}</h3><p>{E(f.Detail)}</p></div>");
    }

    public void Episode(string when, string verdict, IEnumerable<string> details) =>
        _b.Append($"<div class=\"ep\"><h3>{E(when)}</h3><p>{E(verdict)}</p><ul>{string.Concat(details.Select(x => $"<li>{E(x)}</li>"))}</ul></div>");

    public string Render(string title, string subtitle) => $$"""
        <!doctype html><html lang="{{L.Lang}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>{{E(title)}} — SysWatch</title>
        <style>
        :root{--bg:#fff;--fg:#14202e;--dim:#5b6878;--line:#e3e8ee;--card:#f6f8fa;--warn:#c98a12;--bad:#c2410c;--info:#1f8fa6}
        @media (prefers-color-scheme:dark){:root{--bg:#0f1a2a;--fg:#e7edf4;--dim:#93a1b3;--line:#22324a;--card:#15233a;--warn:#F2B544;--bad:#fb923c;--info:#4FC3D9} }
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.5 "Segoe UI",system-ui,sans-serif}
        main{max-width:1000px;margin:0 auto;padding:32px 24px 48px}
        header{display:flex;align-items:center;gap:12px;border-bottom:2px solid var(--line);padding-bottom:14px;margin-bottom:8px}
        header .logo{width:34px;height:34px;border-radius:9px;background:#0f1a2a;display:grid;place-items:center}
        h1{font-size:24px;margin:0}h2{font-size:17px;margin:28px 0 10px}h2 small{font-size:13px;font-weight:400;color:var(--dim)}
        h3{font-size:14px;margin:0 0 4px}p{margin:0 0 8px}.dim{color:var(--dim)}
        .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:10px;margin-top:16px}
        .card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:10px 14px}
        .card b{display:block;font-size:19px;font-variant-numeric:tabular-nums}.card span{color:var(--dim);font-size:12.5px}
        table{width:100%;border-collapse:collapse;font-variant-numeric:tabular-nums}th,td{padding:6px 8px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top}
        th{font-size:12px;color:var(--dim);font-weight:600}td.n,th.n{text-align:right;white-space:nowrap}
        .ep{border:1px solid var(--line);border-left:4px solid var(--dim);border-radius:8px;padding:10px 14px;margin-bottom:8px;background:var(--card)}
        .ep.critical{border-left-color:var(--bad)}.ep.warn{border-left-color:var(--warn)}.ep.info{border-left-color:var(--info)}
        .ep ul{margin:6px 0 0;padding-left:18px;color:var(--dim)}
        .score{display:flex;align-items:center;gap:20px;margin:18px 0 6px}.score b{display:block;font-size:22px}.score span{color:var(--dim)}
        footer{margin-top:40px;color:var(--dim);font-size:12px;border-top:1px solid var(--line);padding-top:10px}
        @media print{body{background:#fff;color:#000}.card,.ep,tr{break-inside:avoid}h2{break-after:avoid} }
        </style></head><body><main>
        <header><div class="logo"><svg viewBox="0 0 24 24" width="22" height="22" fill="none"><path d="M3 13h4l2-6 3 10 2-6h7" stroke="#F2B544" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/></svg></div>
        <div><h1>{{E(title)}}</h1><div class="dim">{{E(subtitle)}}</div></div></header>
        {{_b}}
        <footer>{{E(L.T("Отчёт SysWatch. Данные собраны и обработаны на этом компьютере и никуда не отправлялись."))}}</footer>
        </main></body></html>
        """;
}

/// <summary>PDF из HTML через Microsoft Edge в фоновом режиме (--print-to-pdf). Edge есть в любой Windows 10/11.</summary>
internal static class Pdf
{
    public static bool Print(string htmlPath, string pdfPath)
    {
        var edge = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
        }.FirstOrDefault(File.Exists);
        if (edge is null) return false;

        // Отдельный временный профиль: не мешаем открытому Edge пользователя и не трогаем его профиль.
        var profile = Path.Combine(Path.GetTempPath(), "SysWatch-pdf-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var extra in new[] { "", "--no-sandbox " })
            {
                var args = $"--headless=new --disable-gpu --no-first-run --no-default-browser-check {extra}--no-pdf-header-footer " +
                           $"--user-data-dir=\"{profile}\" --print-to-pdf=\"{pdfPath}\" \"{new Uri(htmlPath).AbsoluteUri}\"";
                using var p = Process.Start(new ProcessStartInfo(edge, args) { UseShellExecute = false, CreateNoWindow = true })!;
                if (!p.WaitForExit(90_000)) { try { p.Kill(true); } catch (Exception) { } }
                if (File.Exists(pdfPath) && new FileInfo(pdfPath).Length > 0) return true;
            }
            return false;
        }
        catch (Exception) { return false; }
        finally
        {
            try { Directory.Delete(profile, true); } catch (Exception) { }
        }
    }
}
