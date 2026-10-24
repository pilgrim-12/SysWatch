using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using SysWatch.App.Anomalies;
using SysWatch.App.Data;
using SysWatch.App.Browsing;
using SysWatch.App.Dev;
using SysWatch.App.Disk;
using SysWatch.App.Programs;
using SysWatch.App.Reports;
using SysWatch.App.Sampling;
using SysWatch.App.Settings;
using SysWatch.App.Sys;

namespace SysWatch.App;

/// <summary>Маршрутизатор /api/* для интерфейса. Возвращает (код, JSON).</summary>
public sealed class Api(Queries queries, LiveSampler live, AnomalyStore anomalies, DiskService disk, Connections connections,
    Installed programs, Firewall firewall, GeoIp geo, Startup startup, Hardware hardware, Insights insights, DevEnv dev, BrowserHistory history, GpuQueries gpu,
    Health health, ReportCenter reports, AppSettings settings, Updates updates, GeoTraffic geoTraffic, SystemInfo sysInfo)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    // История читается по одному запросу за раз: SQLite-файлы общие, а кэши простые.
    private readonly SemaphoreSlim _history = new(1, 1);
    private readonly FileSearch _search = new();

    public async Task<(int Status, string Body)> Handle(Uri uri, string method = "GET", string? body = null)
    {
        var q = HttpUtility.ParseQueryString(uri.Query);
        int range = int.TryParse(q["range"], out var r) ? r : 60;
        L.UseForRequest(q["lang"]);

        try
        {
            if (uri.AbsolutePath == "/api/live")
                return (200, Serialize(live.Sample(queries.Agent())));

            // Аномалии хранятся в своей базе и не зависят от очереди запросов к истории.
            switch (uri.AbsolutePath)
            {
                case "/api/anomalies" when method == "GET":
                    return (200, Serialize(anomalies.List(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - range * 60L)));
                case "/api/anomalies/seen" when method == "POST":
                    anomalies.MarkSeen();
                    return (200, "true");
                case "/api/anomalies/ignore" when method == "POST":
                    anomalies.Ignore(q["rule"] ?? "", q["app"] ?? "");
                    return (200, "true");

                case "/api/connections" when method == "GET":
                    return (200, Serialize(connections.Sample()));

                case "/api/system/startup" when method == "GET":
                    return (200, Serialize(startup.List()));
                case "/api/system/startup/toggle" when method == "POST":
                    return (200, Serialize(startup.Toggle(q["id"] ?? "", q["enabled"] == "1")));
                case "/api/system/disks" when method == "GET":
                    return (200, Serialize(Hardware.Disks()));
                case "/api/system/battery" when method == "GET":
                    return (200, Serialize(Hardware.Battery()));
                case "/api/system/wakes" when method == "GET":
                    return (200, Serialize(Hardware.Wakes()));
                case "/api/system/crashes" when method == "GET":
                {
                    int cd = int.TryParse(q["days"], out var cdv) ? cdv : 30;
                    return (200, Serialize(await Task.Run(() => Crashes.List(cd))));
                }
                case "/api/system/cpu" when method == "GET":
                    return (200, Serialize(hardware.Cpu()));

                case "/api/history" when method == "GET":
                {
                    int days = int.TryParse(q["days"], out var hd) ? hd : 7;
                    // Копирование баз браузеров может занять секунды — не держим поток окна.
                    var res = await Task.Run(() => history.Query(days, q["q"], q["source"], q["kind"]));
                    return (200, Serialize(res));
                }

                case "/api/history/apply-now" when method == "POST":
                {
                    var b = q["browser"] ?? "";
                    var applied = await Task.Run(() => history.ApplyNow(b));
                    return (200, Serialize(applied is null
                        ? new ActionResult(false, L.T("Не удалось закрыть {0}. Закройте его сами — удаление выполнится автоматически.", b))
                        : new ActionResult(true, L.T("Готово: удалено записей — {0}.", applied.Deleted))));
                }
                case "/api/history/pending" when method == "GET":
                    return (200, Serialize(history.PendingBrowsers()));
                case "/api/history/delete" when method == "POST":
                {
                    var req = JsonSerializer.Deserialize<DeleteRequest>(body ?? "{}", Json) ?? new DeleteRequest(null, null, null, false);
                    return (200, Serialize(await Task.Run(() => history.Delete(req))));
                }

                case "/api/dev" when method == "GET":
                    return (200, Serialize(dev.State(q["refresh"] == "1")));
                case "/api/dev/service" when method == "POST":
                    return (200, Serialize(dev.ServiceAction(q["name"] ?? "", q["action"] ?? "")));
                case "/api/dev/docker" when method == "POST":
                {
                    var (a, id) = (q["action"] ?? "", q["id"] ?? "");
                    return (200, Serialize(await Task.Run(() => dev.DockerAction(a, id))));
                }
                case "/api/dev/node-modules" when method == "POST":
                {
                    var proj = q["path"] ?? "";
                    return (200, Serialize(await Task.Run(() => dev.DeleteNodeModules(proj))));
                }
                case "/api/dev/open" when method == "POST":
                {
                    // Папку открываем, файл (git.exe, диск WSL) — показываем выделенным в его папке.
                    var dir = q["path"] ?? "";
                    string? args = Directory.Exists(dir) ? $"\"{dir}\"" : File.Exists(dir) ? $"/select,\"{dir}\"" : null;
                    if (args is null) return (200, Serialize(new ActionResult(false, L.T("Папка не найдена."))));
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", args) { UseShellExecute = false });
                    return (200, Serialize(new ActionResult(true, L.T("Открыто."))));
                }

                case "/api/settings/lang" when method == "POST":
                    L.SetDefault(q["value"]);
                    anomalies.Set("ui_lang", L.Default);
                    return (200, "true");

                case "/api/settings" when method == "GET":
                    return (200, Serialize(settings.View()));
                case "/api/settings" when method == "POST":
                    try
                    {
                        var upd = JsonSerializer.Deserialize<SettingsUpdate>(body ?? "{}", Json) ?? new SettingsUpdate(null, null, null, null);
                        return (200, Serialize(settings.Save(upd)));
                    }
                    catch (InvalidOperationException ex) { return (400, JsonSerializer.Serialize(ex.Message)); }

                case "/api/updates" when method == "GET":
                {
                    var v = updates.Get(q["refresh"] == "1");
                    return (200, Serialize(new { v.Available, v.Error, v.CheckedAt, v.Items, v.Job, updates.Checking }));
                }
                case "/api/updates/install" when method == "POST":
                {
                    var ids = JsonSerializer.Deserialize<string[]>(body ?? "[]", Json) ?? [];
                    return (200, Serialize(updates.Install(ids)));
                }

                case "/api/report/schedules" when method == "GET":
                    return (200, Serialize(reports.Schedules()));
                case "/api/report/schedules" when method == "POST":
                    reports.SaveSchedules(JsonSerializer.Deserialize<List<ReportSchedule>>(body ?? "[]", Json) ?? []);
                    return (200, Serialize(reports.Schedules()));
                case "/api/report/folder" when method == "POST":
                    Directory.CreateDirectory(ReportCenter.Folder);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{ReportCenter.Folder}\"") { UseShellExecute = false });
                    return (200, "true");

                case "/api/report/open" when method == "POST":
                    return (200, Serialize(OpenFile(q["path"] ?? "")));

                case "/api/system/info" when method == "GET":
                    return (200, Serialize(await Task.Run(() => sysInfo.Get(q["refresh"] == "1"))));
                case "/api/system/restore" when method == "GET":
                    return (200, Serialize(await Task.Run(SystemInfo.RestorePoints)));
                case "/api/system/restore/create" when method == "POST":
                {
                    var (ok, message) = await Task.Run(SystemInfo.CreateRestorePoint);
                    return (200, Serialize(new { ok, message }));
                }
                case "/api/geo/home" when method == "GET":
                    return (200, Serialize(new { home = geoTraffic.Home() }));
                case "/api/geo/download" when method == "POST":
                    return (200, Serialize(await geo.DownloadAsync()));

                case "/api/firewall" when method == "GET":
                    return (200, Serialize(firewall.List()));
                case "/api/firewall/status" when method == "GET":
                    return (200, Serialize(firewall.IsBlocked(q["path"] ?? "")));
                case "/api/firewall/block" when method == "POST":
                    return (200, Serialize(firewall.Block(q["path"] ?? "")));
                case "/api/firewall/unblock" when method == "POST":
                    return (200, Serialize(firewall.Unblock(q["path"] ?? "")));

                case "/api/programs" when method == "GET":
                    return (200, Serialize(programs.List(q["refresh"] == "1")));
                case "/api/programs/uninstall" when method == "POST":
                    return (200, Serialize(programs.Uninstall(q["id"] ?? "")));

                case "/api/process/kill" when method == "POST":
                    return (200, Serialize(ProcessActions.Kill(q["name"] ?? "")));
                case "/api/process/priority" when method == "POST":
                    return (200, Serialize(ProcessActions.Priority(q["name"] ?? "", q["level"] ?? "normal")));
                case "/api/process/reveal" when method == "POST":
                    return (200, Serialize(ProcessActions.Reveal(q["path"] ?? "")));

                case "/api/disk" when method == "GET":
                    return (200, Serialize(disk.State(q["root"])));
                case "/api/disk/tree" when method == "GET":
                    return disk.Tree(q["root"], q["path"]) is { } tree ? (200, Serialize(tree)) : (200, "null");
                case "/api/disk/growth" when method == "GET":
                    return (200, Serialize(disk.Growth(q["root"])));
                case "/api/disk/scan" when method == "POST":
                    return (200, Serialize(disk.StartScan(q["root"])));
                case "/api/disk/cleanup-scan" when method == "POST":
                    return (200, Serialize(disk.StartCleanupScan()));
                case "/api/disk/list" when method == "GET":
                    return (200, Serialize(await Task.Run(() => disk.Listing(q["path"] ?? ""))));
                case "/api/disk/locate" when method == "GET":
                    return (200, Serialize(new { path = disk.Locate(q["path"] ?? "") }));
                case "/api/disk/reveal" when method == "POST":
                    return (200, Serialize(FolderList.Reveal(q["path"] ?? "")
                        ? new ActionResult(true, L.T("Открыто."))
                        : new ActionResult(false, L.T("Файл не найден: возможно, его уже удалили или переместили."))));
                case "/api/search" when method == "GET":
                    return (200, Serialize(_search.State()));
                case "/api/search" when method == "POST":
                {
                    var sq = JsonSerializer.Deserialize<SearchQuery>(body ?? "{}", Json);
                    return (200, Serialize(new { started = sq is not null && _search.Start(sq) }));
                }
                case "/api/search/cancel" when method == "POST":
                    _search.Cancel();
                    return (200, "true");
                case "/api/recycle" when method == "GET":
                    return (200, Serialize(await Task.Run(RecycleBin.List)));
                case "/api/recycle/restore" when method == "POST":
                {
                    var ids = JsonSerializer.Deserialize<string[]>(body ?? "[]", Json) ?? [];
                    return (200, Serialize(await Task.Run(() => RecycleBin.Restore(ids))));
                }
                case "/api/recycle/delete" when method == "POST":
                {
                    var ids = JsonSerializer.Deserialize<string[]>(body ?? "[]", Json) ?? [];
                    return (200, Serialize(await Task.Run(() => RecycleBin.Delete(ids))));
                }
                case "/api/recycle/empty" when method == "POST":
                    return (200, Serialize(await Task.Run(RecycleBin.Empty)));
                case "/api/disk/cancel" when method == "POST":
                    disk.Cancel();
                    return (200, "true");
                case "/api/report/doc":
                case "/api/report/export" when method == "POST":
                {
                    // Сборка отчёта — под общей блокировкой (кэши запросов не потокобезопасны),
                    // а долгие части (обход SDK, печать PDF через Edge) — вне её, чтобы не стояли графики.
                    var template = q["template"] ?? (uri.AbsolutePath == "/api/report/doc" ? "health" : "week");
                    reports.WarmUp(template);
                    ReportDocView view;
                    await _history.WaitAsync();
                    try { view = reports.Build(template, Days(q["days"])); }
                    finally { _history.Release(); }
                    return (200, Serialize(uri.AbsolutePath == "/api/report/doc" ? (object)view : reports.Save(view, q["format"] ?? "html", open: true)));
                }
                case "/api/disk/clean" when method == "POST":
                {
                    var req = JsonSerializer.Deserialize<CleanRequest>(body ?? "{}", Json) ?? new CleanRequest([], []);
                    return (200, Serialize(disk.StartClean(req.Categories ?? [], req.Exclude ?? [])));
                }
            }

            await _history.WaitAsync();
            try
            {
                object? result = uri.AbsolutePath switch
                {
                    "/api/overview" => queries.Overview(range),
                    "/api/timeline" => queries.Timeline(range),
                    "/api/network" => queries.Network(range),
                    "/api/app" => queries.App(q["name"] ?? "", range),
                    "/api/events" => queries.Events(range),
                    "/api/gpu" => gpu.Get(range),
                    "/api/geo" => geoTraffic.Get(range),
                    "/api/report" => BuildReport(q["days"]),
                    "/api/report/templates" => reports.Templates(),
                    "/api/health" => health.Get(q["refresh"] == "1"),
                    "/api/why" => insights.Explain(ToLong(q["from"]), ToLong(q["to"])),
                    "/api/slowdowns" => insights.SlowDowns(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - range * 60L, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                    _ => null,
                };
                return result is null ? (404, "\"not found\"") : (200, Serialize(result));
            }
            finally
            {
                _history.Release();
            }
        }
        catch (Exception ex)
        {
            return (500, JsonSerializer.Serialize(ex.Message));
        }
    }

    private static long ToLong(string? s) => long.TryParse(s, out var v) ? v : 0;

    private Report BuildReport(string? days)
    {
        var (from, to) = Insights.Period(Math.Clamp(int.TryParse(days, out var d) ? d : 7, 1, 31));
        return insights.Build(from, to);
    }

    private static int Days(string? s) => Math.Clamp(int.TryParse(s, out var d) ? d : 7, 1, 31);

    /// <summary>
    /// Открыть файл программой по умолчанию. Через explorer.exe, а не напрямую: окно SysWatch работает
    /// от администратора, а браузер с отчётом должен открыться с обычными правами пользователя.
    /// </summary>
    private static ActionResult OpenFile(string path)
    {
        var docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SysWatch");
        if (!File.Exists(path) || !Path.GetFullPath(path).StartsWith(docs, StringComparison.OrdinalIgnoreCase))
            return new(false, L.T("Файл отчёта не найден."));
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
        return new(true, L.T("Открыто."));
    }

    private sealed record CleanRequest(string[]? Categories, string[]? Exclude);

    private static string Serialize(object o) => JsonSerializer.Serialize(o, o.GetType(), Json);
}
