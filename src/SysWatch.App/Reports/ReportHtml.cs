using System.Globalization;
using System.Net;
using System.Text;

namespace SysWatch.App.Reports;

/// <summary>Отчёт за период одним HTML-файлом: без скриптов и внешних ссылок, открывается и печатается где угодно.</summary>
public static class ReportHtml
{
    // Язык и форматы — те, что выбраны в окне (или язык трея).
    private static CultureInfo Ru => L.Culture;

    public static readonly Dictionary<string, string> RuleNames = new()
    {
        ["unsigned_net"] = L.K("Программа без подписи выходит в сеть"),
        ["temp_exec"] = L.K("Запуск из временной папки или «Загрузок»"),
        ["new_program"] = L.K("Новая программа"),
        ["new_domain"] = L.K("Новый адрес в сети"),
        ["autostart"] = L.K("Новое в автозагрузке"),
        ["cpu"] = L.K("Долгая нагрузка на процессор"),
        ["memory"] = L.K("Память растёт без снижения"),
        ["exposed_port"] = L.K("Порт открыт в сеть"),
    };

    public static string RuleName(string rule) => RuleNames.TryGetValue(rule, out var rn) ? L.T(rn) : rule;

    /// <summary>Сохранить в «Документы\SysWatch» и вернуть путь.</summary>
    public static string Save(Report r)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SysWatch");
        Directory.CreateDirectory(dir);
        var name = L.T("Отчёт SysWatch {0:yyyy-MM-dd} — {1:yyyy-MM-dd HH-mm}.html", Local(r.From), Local(r.To));
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
        >= 1L << 40 => (b / (double)(1L << 40)).ToString("F1", Ru) + " " + L.T("ТБ"),
        >= 1L << 30 => (b / (double)(1L << 30)).ToString("F1", Ru) + " " + L.T("ГБ"),
        >= 1L << 20 => (b / (double)(1L << 20)).ToString("F0", Ru) + " " + L.T("МБ"),
        >= 1L << 10 => (b / (double)(1L << 10)).ToString("F0", Ru) + " " + L.T("КБ"),
        _ => b + " " + L.T("Б"),
    };

    public static string Render(Report r)
    {
        var h = new StringBuilder();
        h.Append($$"""
            <!doctype html><html lang="{{L.Lang}}"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{E(L.T("Отчёт SysWatch"))}}</title>
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
            <h1>{{E(L.T("Отчёт SysWatch"))}}</h1>
            <p class="dim">{{E(When(r.From))}} — {{E(When(r.To))}} · {{E(L.T("компьютер {0} · данные за {1:F0} ч", Environment.MachineName, r.Hours))}}</p>
            <div class="cards">
            <div class="card"><b style="color:var(--cpu)">{{Pct(r.AvgCpu)}}</b><span>{{E(L.T("процессор в среднем, пик {0}", Pct(r.PeakCpu)))}}</span></div>
            <div class="card"><b style="color:var(--mem)">{{Pct(r.AvgMem)}}</b><span>{{E(L.T("память в среднем, пик {0}", Pct(r.PeakMem)))}}</span></div>
            <div class="card"><b>{{r.Episodes.Count}}</b><span>{{E(L.T("эпизодов перегрузки, всего {0} мин", r.SlowMinutes))}}</span></div>
            <div class="card"><b style="color:var(--net)">{{Bytes(r.NetRx)}}</b><span>{{E(L.T("получено из сети, отправлено {0}", Bytes(r.NetTx)))}}</span></div>
            <div class="card"><b style="color:var(--disk)">{{Bytes(r.IoRead + r.IoWrite)}}</b><span>{{E(L.T("прочитано и записано программами"))}}</span></div>
            </div>
            """);

        h.Append("<h2>").Append(E(L.T("Загрузка за период"))).Append("</h2><div class=\"chart\">").Append(Chart(r.Points))
         .Append("<div class=\"legend\"><span><i style=\"background:var(--cpu)\"></i>").Append(E(L.T("процессор")))
         .Append("</span><span><i style=\"background:var(--mem)\"></i>").Append(E(L.T("память"))).Append("</span></div></div>");

        h.Append("<h2>").Append(E(L.T("Почему тормозило"))).Append("</h2>");
        if (r.Episodes.Count == 0)
            h.Append("<p>").Append(E(L.T("Перегрузок не было: процессор ни разу не держался выше 75%, память — выше 90%."))).Append("</p>");
        foreach (var w in r.Episodes)
        {
            h.Append($"<div class=\"ep {(w.Kinds.Contains("cpu") ? "" : "mem")}\"><h3>{E(When(w.From))} — {E(Local(w.To).ToString("HH:mm", Ru))}</h3>");
            h.Append($"<p>{E(w.Verdict)}</p>");
            if (w.Details.Count > 0) h.Append("<ul>").Append(string.Concat(w.Details.Select(d => $"<li>{E(d)}</li>"))).Append("</ul>");
            h.Append("</div>");
        }

        h.Append("<h2>").Append(E(L.T("Кто больше всего потреблял"))).Append("</h2><div class=\"grid\">");
        h.Append(Top(L.T("Процессор"), r.TopCpu, a => Pct(a.Cpu)));
        h.Append(Top(L.T("Память (пик)"), r.TopMem, a => Bytes(a.Mem)));
        h.Append(Top(L.T("Чтение и запись"), r.TopIo, a => Bytes(a.Io)));
        h.Append(Top(L.T("Сеть"), r.TopNet, a => Bytes(a.Net)));
        h.Append("</div>");

        h.Append("<h2>").Append(E(L.T("Безопасность"))).Append("</h2>");
        if (r.Anomalies.Count == 0) h.Append("<p>").Append(E(L.T("Аномалий не найдено."))).Append("</p>");
        else
        {
            h.Append("<table><tr><th>").Append(E(L.T("Что найдено"))).Append("</th><th class=\"n\">").Append(E(L.T("Сколько"))).Append("</th></tr>");
            foreach (var a in r.Anomalies)
                h.Append($"<tr><td>{E(RuleName(a.Rule))}</td><td class=\"n\">{a.Count}</td></tr>");
            h.Append("</table>");
        }
        if (r.Notable.Count > 0)
        {
            h.Append("<p style=\"margin-top:12px\"><b>").Append(E(L.T("Важное:"))).Append("</b></p><ul>");
            foreach (var a in r.Notable) h.Append($"<li><span class=\"{(a.Severity == "high" ? "bad" : "")}\">{E(a.Title)}</span> — {E(a.App)} <span class=\"dim\">{E(When(a.Ts))}</span></li>");
            h.Append("</ul>");
        }
        if (r.NewPrograms.Count > 0)
            h.Append("<p><b>").Append(E(L.T("Впервые запускались:"))).Append("</b> ").Append(E(string.Join(", ", r.NewPrograms))).Append("</p>");

        if (r.SystemDrive is { } d)
        {
            h.Append("<h2>").Append(E(L.T("Диск {0}", d.Root))).Append("</h2><p>").Append(E(L.T("Свободно {0} из {1}.", Bytes(d.Free), Bytes(d.Total))));
            if (r.DiskGrowth is { Since: not null } g)
            {
                h.Append(" ").Append(E(g.DriveDelta >= 0
                    ? L.T("С {0} занятое место выросло на {1}.", When(g.Since!.Value), Bytes(Math.Abs(g.DriveDelta)))
                    : L.T("С {0} занятое место уменьшилось на {1}.", When(g.Since!.Value), Bytes(Math.Abs(g.DriveDelta))))).Append("</p>");
                var items = g.Items.Where(i => i.Delta > 0).Take(8).ToList();
                if (items.Count > 0)
                {
                    h.Append("<table><tr><th>").Append(E(L.T("Папка"))).Append("</th><th class=\"n\">").Append(E(L.T("Прибавилось"))).Append("</th><th class=\"n\">").Append(E(L.T("Размер"))).Append("</th></tr>");
                    foreach (var i in items) h.Append($"<tr><td>{E(i.Path)}</td><td class=\"n\">+{Bytes(i.Delta)}</td><td class=\"n\">{Bytes(i.Size)}</td></tr>");
                    h.Append("</table>");
                }
            }
            else h.Append(" ").Append(E(L.T("Для сравнения нужен снимок диска недельной давности."))).Append("</p>");
        }

        h.Append("<footer>").Append(E(L.T("Собрано SysWatch {0}. Данные не покидали компьютер.", DateTime.Now.ToString("d MMMM yyyy, HH:mm", Ru)))).Append("</footer></main></body></html>");
        return h.ToString();
    }

    private static string Top(string title, IReadOnlyList<ReportApp> rows, Func<ReportApp, string> value)
    {
        var s = new StringBuilder($"<div><table><tr><th>{E(title)}</th><th class=\"n\"></th></tr>");
        if (rows.Count == 0) s.Append("<tr><td class=\"dim\">").Append(E(L.T("нет данных"))).Append("</td><td></td></tr>");
        foreach (var a in rows) s.Append($"<tr><td>{E(a.Name)}</td><td class=\"n\">{E(value(a))}</td></tr>");
        return s.Append("</table></div>").ToString();
    }

    /// <summary>Две линии (процессор и память, 0–100%) в SVG, с подписями дней или часов.</summary>
    private static string Chart(IReadOnlyList<ReportPoint> pts)
    {
        const int W = 940, H = 200, Left = 36, B = 22;
        if (pts.Count < 2) return "<p class=\"dim\">" + E(L.T("Мало данных для графика.")) + "</p>";
        double X(int i) => Left + (W - Left - 6) * i / (double)(pts.Count - 1);
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

        var s = new StringBuilder($"<svg viewBox=\"0 0 {W} {H}\" role=\"img\" aria-label=\"{E(L.T("Загрузка процессора и памяти"))}\">");
        foreach (var v in new[] { 0, 50, 100 })
            s.Append($"<line x1=\"{Left}\" x2=\"{W - 6}\" y1=\"{Y(v):F0}\" y2=\"{Y(v):F0}\" stroke=\"var(--line)\"/><text x=\"{Left - 6}\" y=\"{Y(v) + 4:F0}\" text-anchor=\"end\" font-size=\"11\" fill=\"var(--dim)\">{v}%</text>");

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
