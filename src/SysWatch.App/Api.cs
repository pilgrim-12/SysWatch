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
using SysWatch.App.Sys;

namespace SysWatch.App;

/// <summary>Маршрутизатор /api/* для интерфейса. Возвращает (код, JSON).</summary>
public sealed class Api(Queries queries, LiveSampler live, AnomalyStore anomalies, DiskService disk, Connections connections,
    Installed programs, Firewall firewall, GeoIp geo, Startup startup, Hardware hardware, Insights insights, DevEnv dev, BrowserHistory history, GpuQueries gpu)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    // История читается по одному запросу за раз: SQLite-файлы общие, а кэши простые.
    private readonly SemaphoreSlim _history = new(1, 1);

    public async Task<(int Status, string Body)> Handle(Uri uri, string method = "GET", string? body = null)
    {
        var q = HttpUtility.ParseQueryString(uri.Query);
        int range = int.TryParse(q["range"], out var r) ? r : 60;

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
                    if (args is null) return (200, Serialize(new ActionResult(false, "Папка не найдена.")));
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", args) { UseShellExecute = false });
                    return (200, Serialize(new ActionResult(true, "Открыто.")));
                }

                case "/api/report/open" when method == "POST":
                    return (200, Serialize(OpenFile(q["path"] ?? "")));

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
                case "/api/disk/cancel" when method == "POST":
                    disk.Cancel();
                    return (200, "true");
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
                    "/api/report" => BuildReport(q["days"]),
                    "/api/report/export" when method == "POST" => Export(q["days"]),
                    "/api/why" => insights.Explain(L(q["from"]), L(q["to"])),
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

    private static long L(string? s) => long.TryParse(s, out var v) ? v : 0;

    private Report BuildReport(string? days)
    {
        var (from, to) = Insights.Period(Math.Clamp(int.TryParse(days, out var d) ? d : 7, 1, 31));
        return insights.Build(from, to);
    }

    private ExportResult Export(string? days)
    {
        var path = ReportHtml.Save(BuildReport(days));
        OpenFile(path);
        return new ExportResult(true, path);
    }

    /// <summary>
    /// Открыть файл программой по умолчанию. Через explorer.exe, а не напрямую: окно SysWatch работает
    /// от администратора, а браузер с отчётом должен открыться с обычными правами пользователя.
    /// </summary>
    private static ActionResult OpenFile(string path)
    {
        var docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SysWatch");
        if (!File.Exists(path) || !Path.GetFullPath(path).StartsWith(docs, StringComparison.OrdinalIgnoreCase))
            return new(false, "Файл отчёта не найден.");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
        return new(true, "Открыто.");
    }

    private sealed record ExportResult(bool Ok, string Path);

    private sealed record CleanRequest(string[]? Categories, string[]? Exclude);

    private static string Serialize(object o) => JsonSerializer.Serialize(o, o.GetType(), Json);
}
