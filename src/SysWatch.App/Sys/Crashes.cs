using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;

namespace SysWatch.App.Sys;

/// <param name="Kind">gpu, bsod, power, app, hang, disk, hardware, update.</param>
public sealed record Crash(long Ts, string Kind, string Title, string Detail, string? App, string Source, int EventId);

public sealed record CrashList(int Days, IReadOnlyList<Crash> Items, IReadOnlyDictionary<string, int> Counts);

/// <summary>
/// Сбои из журналов Windows: синие экраны, внезапные выключения, сбросы видеодрайвера, падения и зависания программ,
/// ошибки диска и аппаратные ошибки (WHEA). Только чтение журналов «Система» и «Приложение».
/// </summary>
public static class Crashes
{
    private sealed record Rule(string Log, string Provider, int[] Ids, string Kind, Func<EventRecord, Dictionary<string, string>, (string Title, string Detail, string? App)> Describe);

    private static readonly Rule[] Rules =
    [
        new("System", "Microsoft-Windows-WER-SystemErrorReporting", [1001], "bsod",
            (e, d) => (L.T("Синий экран (BSOD)"), L.T("Код остановки {0}. Дамп памяти: {1}", First(d, "param1") ?? Desc(e), First(d, "param2") ?? L.T("не сохранён")), null)),
        new("System", "Microsoft-Windows-Kernel-Power", [41], "power",
            (e, d) => (L.T("Внезапное выключение"),
                long.TryParse(d.GetValueOrDefault("BugcheckCode"), out var bc) && bc != 0
                    ? L.T("Компьютер перезагрузился после синего экрана (код 0x{0:X}).", bc)
                    : L.T("Компьютер выключился без завершения работы: пропало питание, зависание или удерживали кнопку питания."), null)),
        new("System", "EventLog", [6008], "power",
            (e, d) => (L.T("Предыдущее выключение было неожиданным"), Desc(e), null)),
        new("System", "Display", [4101], "gpu",
            (e, d) => (L.T("Видеодрайвер перестал отвечать и был восстановлен"), L.T("Экран мог погаснуть на пару секунд. Частая причина — перегрев, разгон или нестабильный драйвер."), null)),
        new("System", "nvlddmkm", [13, 14, 153, 4101], "gpu",
            (e, d) => (L.T("Ошибка драйвера NVIDIA"), Desc(e), null)),
        new("System", "amdkmdag", [4101, 4097, 4098, 4119], "gpu",
            (e, d) => (L.T("Ошибка драйвера AMD"), Desc(e), null)),
        new("System", "igfx", [4101], "gpu",
            (e, d) => (L.T("Ошибка драйвера Intel Graphics"), Desc(e), null)),
        new("System", "Microsoft-Windows-WHEA-Logger", [1, 17, 18, 19, 20, 46, 47], "hardware",
            (e, d) => (L.T("Аппаратная ошибка (WHEA)"), Desc(e), null)),
        new("System", "disk", [7, 11, 51, 153], "disk",
            (e, d) => (L.T("Ошибка диска"), Desc(e), null)),
        new("System", "Ntfs", [55, 98, 137], "disk",
            (e, d) => (L.T("Ошибка файловой системы"), Desc(e), null)),
        new("System", "Microsoft-Windows-Ntfs", [55, 98, 137], "disk",
            (e, d) => (L.T("Ошибка файловой системы"), Desc(e), null)),
        new("System", "Microsoft-Windows-WindowsUpdateClient", [20], "update",
            (e, d) => (L.T("Обновление Windows не установилось"), First(d, "updateTitle") ?? Desc(e), null)),
        new("Application", "Application Error", [1000], "app",
            (e, d) => (L.T("Программа аварийно завершилась"), L.T("Модуль: {0}, код 0x{1}", d.GetValueOrDefault("param4") ?? "?", (d.GetValueOrDefault("param7") ?? "").TrimStart('0', 'x')), d.GetValueOrDefault("param1"))),
        new("Application", "Application Hang", [1002], "hang",
            (e, d) => (L.T("Программа зависла и была закрыта"), L.T("Окно не отвечало, Windows завершило программу."), d.GetValueOrDefault("param1") ?? First(d, "Data0"))),
        new("Application", ".NET Runtime", [1026], "app",
            (e, d) => (L.T("Программа .NET упала с необработанной ошибкой"), Shorten(Desc(e), 400), AppFromDotnet(Desc(e)))),
        new("Application", "Windows Error Reporting", [1001], "gpu",
            (e, d) => (L.T("Сбой видеодрайвера (LiveKernelEvent)"), L.T("Windows сохранила отчёт о сбросе видеокарты."), null)),
    ];

    public static CrashList List(int days)
    {
        days = Math.Clamp(days, 1, 90);
        var items = new List<Crash>();
        long since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - days * 86400L;
        long ms = days * 86400L * 1000;

        foreach (var group in Rules.GroupBy(r => r.Log))
        {
            // Одним запросом на журнал: только нужные источники и коды за период.
            var providers = string.Join(" or ", group.Select(r => $"@Name='{r.Provider}'").Distinct());
            var ids = string.Join(" or ", group.SelectMany(r => r.Ids).Distinct().Select(i => $"EventID={i}"));
            var xpath = $"*[System[Provider[{providers}] and ({ids}) and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
            try
            {
                using var reader = new EventLogReader(new EventLogQuery(group.Key, PathType.LogName, xpath) { ReverseDirection = true });
                for (int i = 0; i < 2000 && reader.ReadEvent() is { } e; i++)
                {
                    using (e)
                    {
                        var rule = group.FirstOrDefault(r => r.Provider.Equals(e.ProviderName, StringComparison.OrdinalIgnoreCase) && r.Ids.Contains(e.Id));
                        if (rule is null) continue;
                        var data = Data(e);

                        // WER 1001 в «Приложении» — это все отчёты об ошибках; берём только сбои драйверов ядра (LiveKernelEvent).
                        // Коды 141 и 117 — сброс видеокарты (TDR), остальные — другие драйверы.
                        string? lkeCode = null;
                        if (rule.Log == "Application" && rule.Provider == "Windows Error Reporting")
                        {
                            var values = data.Values.ToList();
                            int idx = values.FindIndex(v => v.Equals("LiveKernelEvent", StringComparison.OrdinalIgnoreCase));
                            if (idx < 0) continue;
                            lkeCode = values.ElementAtOrDefault(idx + 3)?.Trim().ToLowerInvariant();
                        }

                        long ts = e.TimeCreated is { } t ? new DateTimeOffset(t).ToUnixTimeSeconds() : 0;
                        if (ts < since) continue;
                        var (title, detail, app) = rule.Describe(e, data);
                        var kind = rule.Kind;
                        if (lkeCode is not null && lkeCode is not ("141" or "117"))
                        {
                            kind = "hardware";
                            title = L.T("Сбой драйвера (LiveKernelEvent {0})", lkeCode);
                            detail = L.T("Windows восстановила драйвер устройства и сохранила отчёт.");
                        }
                        items.Add(new Crash(ts, kind, title, Shorten(detail, 500), app, e.ProviderName ?? rule.Provider, e.Id));
                    }
                }
            }
            catch (EventLogException) { /* журнал недоступен */ }
        }

        // Kernel-Power 41 и EventLog 6008 описывают одно выключение — оставляем одно событие.
        var dedup = new List<Crash>();
        foreach (var c in items.OrderByDescending(c => c.Ts))
        {
            if (c.Kind == "power" && dedup.Any(x => x.Kind == "power" && Math.Abs(x.Ts - c.Ts) < 600)) continue;
            dedup.Add(c);
        }

        var counts = dedup.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.Count());
        return new CrashList(days, dedup, counts);
    }

    private static Dictionary<string, string> Data(EventRecord e)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var x = XDocument.Parse(e.ToXml());
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            int n = 0;
            foreach (var d in x.Descendants(ns + "Data"))
            {
                var name = d.Attribute("Name")?.Value ?? $"Data{n}";
                map[name] = d.Value;
                n++;
            }
        }
        catch (Exception) { }
        return map;
    }

    private static string? First(Dictionary<string, string> d, string key) =>
        d.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static string Desc(EventRecord e)
    {
        try { return (e.FormatDescription() ?? "").Trim(); }
        catch (Exception) { return L.T("Событие {0} от {1}", e.Id, e.ProviderName); }
    }

    private static string Shorten(string s, int max)
    {
        s = string.Join(" ", s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static string? AppFromDotnet(string desc)
    {
        // «Application: Foo.exe» или «Приложение: Foo.exe» в первой строке.
        var line = desc.Split('\n').FirstOrDefault() ?? "";
        int colon = line.IndexOf(':');
        return colon > 0 ? line[(colon + 1)..].Trim() : null;
    }
}
