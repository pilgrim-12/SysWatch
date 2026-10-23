using System.Globalization;
using System.Net;
using System.Text;

namespace SysWatch.App.Reports;

/// <summary>Отчёт за период одним HTML-файлом: без скриптов и внешних ссылок, открывается и печатается где угодно.</summary>
public static class ReportHtml
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static readonly Dictionary<string, string> RuleNames = new()
    {
        ["unsigned_net"] = "Программа без подписи выходит в сеть",
        ["temp_exec"] = "Запуск из временной папки или «Загрузок»",
        ["new_program"] = "Новая программа",
        ["new_domain"] = "Новый адрес в сети",
        ["autostart"] = "Новое в автозагрузке",
        ["cpu"] = "Долгая нагрузка на процессор",
        ["memory"] = "Память растёт без снижения",
        ["exposed_port"] = "Порт открыт в сеть",
    };

    /// <summary>Сохранить в «Документы\SysWatch» и вернуть путь.</summary>
    public static string Save(Report r)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SysWatch");
        Directory.CreateDirectory(dir);
        var name = $"Отчёт SysWatch {Local(r.From):yyyy-MM-dd} — {Local(r.To):yyyy-MM-dd HH-mm}.html";
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, Render(r), new UTF8Encoding(true));
        return path;
    }

    private static DateTime Local(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string When(long unix) => Local(unix).ToString("d MMM, HH:mm", Ru);
    private static string Pct(double v) => v.ToString("F0", Ru) + "%";

    private static string Bytes(long b) => b switch
    {
        >= 1L << 40 => (b / (double)(1L << 40)).ToString("F1", Ru) + " ТБ",
        >= 1L << 30 => (b / (double)(1L << 30)).ToString("F1", Ru) + " ГБ",
        >= 1L << 20 => (b / (double)(1L << 20)).ToString("F0", Ru) + " МБ",
        >= 1L << 10 => (b / (double)(1L << 10)).ToString("F0", Ru) + " КБ",
        _ => b + " Б",
    };

    public static string Render(Report r)
    {
        var h = new StringBuilder();
        h.Append($$"""
            <!doctype html><html lang="ru"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Отчёт SysWatch</title>
            <style>
            :root{--bg:#fff;--fg:#14202e;--dim:#5b6878;--line:#e3e8ee;--card:#f6f8fa;--cpu:#c98a12;--mem:#7c5ce0;--disk:#d65a47;--net:#1f8fa6;--bad:#c2410c}
            @media (prefers-color-scheme:dark){:root{--bg:#0f1a2a;--fg:#e7edf4;--dim:#93a1b3;--line:#22324a;--card:#15233a;--cpu:#F2B544;--mem:#A78BFA;--disk:#F27C6B;--net:#4FC3D9;--bad:#fb923c} }
            *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 "Segoe UI",system-ui,sans-serif}
            main{max-width:980px;margin:0 auto;padding:32px 20px 48px}
            h1{font-size:26px;margin:0 0 4px}h2{font-size:18px;margin:32px 0 12px}p{margin:0 0 8px}
            .dim{color:var(--dim)}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:10px;margin-top:20px}
            .card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px 14px}
            .card b{display:block;font-size:22px;font-variant-numeric:tabular-nums}.card span{color:var(--dim);font-size:13px}
            svg{width:100%;height:auto;display:block}.chart{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px}
            .legend{display:flex;gap:16px;font-size:13px;color:var(--dim);margin-top:6px}.legend i{display:inline-block;width:12px;height:3px;border-radius:2px;margin-right:6px;vertical-align:middle}
            .grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:12px}
            table{width:100%;border-collapse:collapse;font-variant-numeric:tabular-nums}th,td{padding:6px 8px;border-bottom:1px solid var(--line);text-align:left}
            th{font-size:12px;color:var(--dim);font-weight:600}td.n,th.n{text-align:right}
            .ep{border:1px solid var(--line);border-left:4px solid var(--cpu);border-radius:8px;padding:12px 14px;margin-bottom:10px;background:var(--card)}
            .ep.mem{border-left-color:var(--mem)}.ep h3{margin:0 0 4px;font-size:15px}.ep ul{margin:6px 0 0;padding-left:18px}
            .tag{display:inline-block;font-size:12px;border:1px solid var(--line);border-radius:20px;padding:0 8px;margin-right:4px;color:var(--dim)}
            .bad{color:var(--bad)}footer{margin-top:40px;color:var(--dim);font-size:12px}
            @media print{body{background:#fff;color:#000}.card,.chart,.ep{break-inside:avoid} }
            </style></head><body><main>
            <h1>Отчёт SysWatch</h1>
            <p class="dim">{{E(When(r.From))}} — {{E(When(r.To))}} · компьютер {{E(Environment.MachineName)}} · данные за {{r.Hours.ToString("F0", Ru)}} ч</p>
            <div class="cards">
            <div class="card"><b style="color:var(--cpu)">{{Pct(r.AvgCpu)}}</b><span>процессор в среднем, пик {{Pct(r.PeakCpu)}}</span></div>
            <div class="card"><b style="color:var(--mem)">{{Pct(r.AvgMem)}}</b><span>память в среднем, пик {{Pct(r.PeakMem)}}</span></div>
            <div class="card"><b>{{r.Episodes.Count}}</b><span>эпизодов перегрузки, всего {{r.SlowMinutes}} мин</span></div>
            <div class="card"><b style="color:var(--net)">{{Bytes(r.NetRx)}}</b><span>получено из сети, отправлено {{Bytes(r.NetTx)}}</span></div>
            <div class="card"><b style="color:var(--disk)">{{Bytes(r.IoRead + r.IoWrite)}}</b><span>прочитано и записано программами</span></div>
            </div>
            """);

        h.Append("<h2>Загрузка за период</h2><div class=\"chart\">").Append(Chart(r.Points))
         .Append("<div class=\"legend\"><span><i style=\"background:var(--cpu)\"></i>процессор</span><span><i style=\"background:var(--mem)\"></i>память</span></div></div>");

        h.Append("<h2>Почему тормозило</h2>");
        if (r.Episodes.Count == 0)
            h.Append("<p>Перегрузок не было: процессор ни разу не держался выше 75%, память — выше 90%.</p>");
        foreach (var w in r.Episodes)
        {
            h.Append($"<div class=\"ep {(w.Kinds.Contains("cpu") ? "" : "mem")}\"><h3>{E(When(w.From))} — {E(Local(w.To).ToString("HH:mm", Ru))}</h3>");
            h.Append($"<p>{E(w.Verdict)}</p>");
            if (w.Details.Count > 0) h.Append("<ul>").Append(string.Concat(w.Details.Select(d => $"<li>{E(d)}</li>"))).Append("</ul>");
            h.Append("</div>");
        }

        h.Append("<h2>Кто больше всего потреблял</h2><div class=\"grid\">");
        h.Append(Top("Процессор", r.TopCpu, a => Pct(a.Cpu)));
        h.Append(Top("Память (пик)", r.TopMem, a => Bytes(a.Mem)));
        h.Append(Top("Чтение и запись", r.TopIo, a => Bytes(a.Io)));
        h.Append(Top("Сеть", r.TopNet, a => Bytes(a.Net)));
        h.Append("</div>");

        h.Append("<h2>Безопасность</h2>");
        if (r.Anomalies.Count == 0) h.Append("<p>Аномалий не найдено.</p>");
        else
        {
            h.Append("<table><tr><th>Что найдено</th><th class=\"n\">Сколько</th></tr>");
            foreach (var a in r.Anomalies)
                h.Append($"<tr><td>{E(RuleNames.GetValueOrDefault(a.Rule, a.Rule))}</td><td class=\"n\">{a.Count}</td></tr>");
            h.Append("</table>");
        }
        if (r.Notable.Count > 0)
        {
            h.Append("<p style=\"margin-top:12px\"><b>Важное:</b></p><ul>");
            foreach (var a in r.Notable) h.Append($"<li><span class=\"{(a.Severity == "high" ? "bad" : "")}\">{E(a.Title)}</span> — {E(a.App)} <span class=\"dim\">{E(When(a.Ts))}</span></li>");
            h.Append("</ul>");
        }
        if (r.NewPrograms.Count > 0)
            h.Append($"<p><b>Впервые запускались:</b> {E(string.Join(", ", r.NewPrograms))}</p>");

        if (r.SystemDrive is { } d)
        {
            h.Append($"<h2>Диск {E(d.Root)}</h2><p>Свободно {Bytes(d.Free)} из {Bytes(d.Total)}.");
            if (r.DiskGrowth is { Since: not null } g)
            {
                h.Append($" С {E(When(g.Since!.Value))} занятое место {(g.DriveDelta >= 0 ? "выросло" : "уменьшилось")} на {Bytes(Math.Abs(g.DriveDelta))}.</p>");
                var items = g.Items.Where(i => i.Delta > 0).Take(8).ToList();
                if (items.Count > 0)
                {
                    h.Append("<table><tr><th>Папка</th><th class=\"n\">Прибавилось</th><th class=\"n\">Размер</th></tr>");
                    foreach (var i in items) h.Append($"<tr><td>{E(i.Path)}</td><td class=\"n\">+{Bytes(i.Delta)}</td><td class=\"n\">{Bytes(i.Size)}</td></tr>");
                    h.Append("</table>");
                }
            }
            else h.Append(" Для сравнения нужен снимок диска недельной давности.</p>");
        }

        h.Append($"<footer>Собрано SysWatch {E(DateTime.Now.ToString("d MMMM yyyy, HH:mm", Ru))}. Данные не покидали компьютер.</footer></main></body></html>");
        return h.ToString();
    }

    private static string Top(string title, IReadOnlyList<ReportApp> rows, Func<ReportApp, string> value)
    {
        var s = new StringBuilder($"<div><table><tr><th>{E(title)}</th><th class=\"n\"></th></tr>");
        if (rows.Count == 0) s.Append("<tr><td class=\"dim\">нет данных</td><td></td></tr>");
        foreach (var a in rows) s.Append($"<tr><td>{E(a.Name)}</td><td class=\"n\">{E(value(a))}</td></tr>");
        return s.Append("</table></div>").ToString();
    }

    /// <summary>Две линии (процессор и память, 0–100%) в SVG, с подписями дней или часов.</summary>
    private static string Chart(IReadOnlyList<ReportPoint> pts)
    {
        const int W = 940, H = 200, L = 36, B = 22;
        if (pts.Count < 2) return "<p class=\"dim\">Мало данных для графика.</p>";
        double X(int i) => L + (W - L - 6) * i / (double)(pts.Count - 1);
        double Y(double v) => 6 + (H - B - 6) * (1 - v / 100);

        string Line(Func<ReportPoint, double?> f, string color)
        {
            var sb = new StringBuilder(); bool pen = false;
            for (int i = 0; i < pts.Count; i++)
            {
                if (f(pts[i]) is not { } v) { pen = false; continue; }
                sb.Append(pen ? " L" : " M").Append(X(i).ToString("F1", CultureInfo.InvariantCulture)).Append(' ').Append(Y(v).ToString("F1", CultureInfo.InvariantCulture));
                pen = true;
            }
            return $"<path d=\"{sb}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\" stroke-linejoin=\"round\"/>";
        }

        var s = new StringBuilder($"<svg viewBox=\"0 0 {W} {H}\" role=\"img\" aria-label=\"Загрузка процессора и памяти\">");
        foreach (var v in new[] { 0, 50, 100 })
            s.Append($"<line x1=\"{L}\" x2=\"{W - 6}\" y1=\"{Y(v):F0}\" y2=\"{Y(v):F0}\" stroke=\"var(--line)\"/><text x=\"{L - 6}\" y=\"{Y(v) + 4:F0}\" text-anchor=\"end\" font-size=\"11\" fill=\"var(--dim)\">{v}%</text>");

        bool week = pts[^1].Ts - pts[0].Ts > 2 * 86400;
        int every = Math.Max(1, pts.Count / 8);
        for (int i = 0; i < pts.Count; i += every)
        {
            var t = Local(pts[i].Ts);
            var label = week ? t.ToString("ddd d", Ru) : t.ToString("HH:mm", Ru);
            s.Append($"<text x=\"{X(i).ToString("F0", CultureInfo.InvariantCulture)}\" y=\"{H - 4}\" text-anchor=\"middle\" font-size=\"11\" fill=\"var(--dim)\">{E(label)}</text>");
        }
        s.Append(Line(p => p.Mem, "var(--mem)")).Append(Line(p => p.Cpu, "var(--cpu)")).Append("</svg>");
        return s.ToString();
    }
}
