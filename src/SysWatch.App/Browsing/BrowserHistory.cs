using System.Text.Json;
using System.Web;
using Microsoft.Data.Sqlite;
using SysWatch.App.Anomalies;

namespace SysWatch.App.Browsing;

public sealed record BrowserProfile(string Id, string Browser, string Profile, bool Ok, string? Error, int Visits, int Searches, string Db);

/// <summary>Что удалить: конкретные адреса в конкретном браузере или все страницы сайта.</summary>
public sealed record DeleteTarget(string Source, string Url);

/// <param name="ClearProfiles">Очистить историю этих профилей целиком (или начиная с ClearFrom, unix-время).</param>
public sealed record DeleteRequest(DeleteTarget[]? Items, string? Domain, string[]? Sources, bool CloseBrowsers,
    string[]? ClearProfiles = null, long? ClearFrom = null);

/// <param name="Pending">Браузеры, в которых удаление отложено до их закрытия.</param>
public sealed record DeleteResult(bool Ok, string Message, int Deleted, IReadOnlyList<string> Running, IReadOnlyList<string> Pending);

/// <summary>Отложенное удаление в одном профиле: выполнится, когда браузер будет закрыт.</summary>
public sealed record PendingOp(string Profile, string Browser, string Db, string[] Urls, string? Domain, bool Clear, long? ClearFrom, long At);

public sealed record PendingApplied(int Ops, int Deleted, IReadOnlyList<string> Browsers);

/// <summary>Одна запись: посещение страницы или поисковый запрос (страница результатов поиска).</summary>
public sealed record HistoryItem(long Ts, string Source, string Kind, string Url, string? Title, string Domain, string? Term, string? Engine);

public sealed record DomainStat(string Domain, int Visits, long Last, IReadOnlyList<string> Sources);

public sealed record HistoryResult(
    long From, IReadOnlyList<BrowserProfile> Profiles, IReadOnlyList<HistoryItem> Items, int Total,
    int Searches, int Visits, IReadOnlyList<DomainStat> Domains);

/// <summary>
/// История браузеров текущего пользователя Windows: Chrome, Edge, Brave, Vivaldi, Яндекс, Opera, Firefox, все профили.
/// Базы открыты браузером, поэтому читаем их копию во временной папке и сразу удаляем её. Ничего не сохраняем.
/// Режим инкогнито браузеры не записывают — его здесь нет.
/// </summary>
public sealed class BrowserHistory(AnomalyStore state)
{
    private const string PendingKey = "history_pending";
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private readonly object _gate = new();
    private (long At, int Days, List<HistoryItem> Items, List<BrowserProfile> Profiles)? _cache;

    private sealed record Source(string Browser, string UserData, bool SingleProfile);

    private static IEnumerable<Source> ChromiumSources() =>
    [
        new("Chrome", Path.Combine(Local, "Google", "Chrome", "User Data"), false),
        new("Edge", Path.Combine(Local, "Microsoft", "Edge", "User Data"), false),
        new("Brave", Path.Combine(Local, "BraveSoftware", "Brave-Browser", "User Data"), false),
        new("Vivaldi", Path.Combine(Local, "Vivaldi", "User Data"), false),
        new("Yandex", Path.Combine(Local, "Yandex", "YandexBrowser", "User Data"), false),
        new("Chromium", Path.Combine(Local, "Chromium", "User Data"), false),
        new("Opera", Path.Combine(Roaming, "Opera Software", "Opera Stable"), true),
        new("Opera GX", Path.Combine(Roaming, "Opera Software", "Opera GX Stable"), true),
    ];

    public HistoryResult Query(int days, string? q, string? source, string? kind, int limit = 1500)
    {
        days = Math.Clamp(days, 1, 365);
        List<HistoryItem> all; List<BrowserProfile> profiles;
        lock (_gate)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (_cache is { } c && c.Days >= days && now - c.At < 60)
            {
                long from0 = now - days * 86400L;
                all = c.Items.Where(i => i.Ts >= from0).ToList();
                profiles = c.Profiles;
            }
            else
            {
                (all, profiles) = Load(days);
                _cache = (now, days, all, profiles);
            }
        }

        long from = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - days * 86400L;
        IEnumerable<HistoryItem> f = all;
        var pending = LoadPending();
        if (pending.Count > 0) f = f.Where(i => !pending.Any(op => Hides(op, i))); // отложенное удаление уже не показываем
        if (!string.IsNullOrEmpty(source)) f = f.Where(i => i.Source == source);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            f = f.Where(i => i.Url.Contains(needle, StringComparison.OrdinalIgnoreCase)
                             || (i.Title?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                             || (i.Term?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        var filtered = f.ToList();

        var domains = filtered.Where(i => i.Kind == "visit")
            .GroupBy(i => i.Domain, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DomainStat(g.Key, g.Count(), g.Max(i => i.Ts), g.Select(i => i.Source).Distinct().ToList()))
            .OrderByDescending(d => d.Visits).Take(100).ToList();

        var items = kind is "search" or "visit" ? filtered.Where(i => i.Kind == kind) : filtered;
        // Названия поисковиков и ошибки профилей хранятся по-русски (ключи словаря) — переводим при отдаче.
        var list = items.OrderByDescending(i => i.Ts).Take(limit)
            .Select(i => i.Engine is null ? i : i with { Engine = L.T(i.Engine) }).ToList();
        var shownProfiles = profiles.Select(p => p.Error is null ? p : p with { Error = L.T(p.Error) }).ToList();

        return new HistoryResult(from, shownProfiles, list, filtered.Count,
            filtered.Count(i => i.Kind == "search"), filtered.Count(i => i.Kind == "visit"), domains);
    }

    /// <summary>Забыть прочитанное (при закрытии окна), чтобы история не лежала в памяти.</summary>
    public void Clear()
    {
        lock (_gate) _cache = null;
    }

    private static (List<HistoryItem>, List<BrowserProfile>) Load(int days)
    {
        var items = new List<HistoryItem>();
        var profiles = new List<BrowserProfile>();
        long fromUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - days * 86400L;
        var tmp = Path.Combine(Path.GetTempPath(), "SysWatch-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);

        try
        {
            foreach (var src in ChromiumSources())
            {
                if (!Directory.Exists(src.UserData)) continue;
                var names = ProfileNames(src.UserData);
                var dirs = (src.SingleProfile ? new[] { src.UserData } : Directory.GetDirectories(src.UserData))
                    .Where(d => File.Exists(Path.Combine(d, "History"))).ToArray();

                foreach (var dir in dirs)
                {
                    var folder = Path.GetFileName(dir);
                    var profile = src.SingleProfile ? "" : names.GetValueOrDefault(folder, folder);
                    var label = string.IsNullOrEmpty(profile) || dirs.Length == 1 ? src.Browser : $"{src.Browser} · {profile}";
                    profiles.Add(Read(label, src.Browser, profile, Path.Combine(dir, "History"), tmp, fromUnix, items, Chromium));
                }
            }

            var ff = Path.Combine(Roaming, "Mozilla", "Firefox", "Profiles");
            if (Directory.Exists(ff))
            {
                var dirs = Directory.GetDirectories(ff).Where(d => File.Exists(Path.Combine(d, "places.sqlite"))).ToArray();
                foreach (var dir in dirs)
                {
                    var name = Path.GetFileName(dir);
                    int dot = name.IndexOf('.');
                    var profile = dot >= 0 ? name[(dot + 1)..] : name;
                    var label = dirs.Length == 1 ? "Firefox" : $"Firefox · {profile}";
                    profiles.Add(Read(label, "Firefox", profile, Path.Combine(dir, "places.sqlite"), tmp, fromUnix, items, Firefox));
                }
            }
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (Exception) { /* удалит Windows при очистке TEMP */ }
            SqliteConnection.ClearAllPools();
        }

        return (items, profiles);
    }

    private delegate void Reader(SqliteConnection c, string label, long fromUnix, List<HistoryItem> into);

    private static BrowserProfile Read(string label, string browser, string profile, string db, string tmp, long fromUnix,
        List<HistoryItem> items, Reader reader)
    {
        var copy = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".db");
        try
        {
            CopyShared(db, copy);
            foreach (var ext in new[] { "-wal", "-journal" })
                if (File.Exists(db + ext)) CopyShared(db + ext, copy + ext);

            int before = items.Count;
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            {
                c.Open();
                reader(c, label, fromUnix, items);
            }
            var mine = items.Skip(before).ToList();
            return new BrowserProfile(label, browser, profile, true, null, mine.Count(i => i.Kind == "visit"), mine.Count(i => i.Kind == "search"), db);
        }
        catch (IOException)
        {
            return new BrowserProfile(label, browser, profile, false, L.K("Браузер не даёт прочитать историю. Закройте его и обновите страницу."), 0, 0, db);
        }
        catch (Exception ex)
        {
            return new BrowserProfile(label, browser, profile, false, ex.Message, 0, 0, db);
        }
        finally
        {
            foreach (var f in new[] { copy, copy + "-wal", copy + "-journal", copy + "-shm" })
                try { File.Delete(f); } catch (Exception) { }
        }
    }

    /// <summary>Копия файла, который браузер держит открытым (разрешаем ему писать, пока читаем).</summary>
    private static void CopyShared(string from, string to)
    {
        using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = new FileStream(to, FileMode.CreateNew, FileAccess.Write);
        src.CopyTo(dst);
    }

    private static Dictionary<string, string> ProfileNames(string userData)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(userData, "Local State")));
            if (doc.RootElement.TryGetProperty("profile", out var p) && p.TryGetProperty("info_cache", out var cache))
                foreach (var e in cache.EnumerateObject())
                    if (e.Value.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name) map[e.Name] = name;
        }
        catch (Exception) { }
        return map;
    }

    // Время Chromium — микросекунды от 1601-01-01, Firefox — микросекунды от 1970-01-01.
    private const long ChromiumEpoch = 11_644_473_600L;

    private static void Chromium(SqliteConnection c, string label, long fromUnix, List<HistoryItem> into)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT v.visit_time, u.url, u.title FROM visits v JOIN urls u ON u.id = v.url
            WHERE v.visit_time >= $from ORDER BY v.visit_time
            """;
        cmd.Parameters.AddWithValue("$from", (fromUnix + ChromiumEpoch) * 1_000_000);
        using var r = cmd.ExecuteReader();
        while (r.Read()) Add(into, label, r.GetInt64(0) / 1_000_000 - ChromiumEpoch, r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
    }

    private static void Firefox(SqliteConnection c, string label, long fromUnix, List<HistoryItem> into)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT v.visit_date, p.url, p.title FROM moz_historyvisits v JOIN moz_places p ON p.id = v.place_id
            WHERE v.visit_date >= $from ORDER BY v.visit_date
            """;
        cmd.Parameters.AddWithValue("$from", fromUnix * 1_000_000);
        using var r = cmd.ExecuteReader();
        while (r.Read()) Add(into, label, r.GetInt64(0) / 1_000_000, r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
    }

    // ── Разбор адресов ───────────────────────────────────────────────────

    private static readonly (string Host, string Path, string Param, string Engine)[] Engines =
    [
        ("google.", "/search", "q", "Google"),
        ("bing.com", "/search", "q", "Bing"),
        ("yandex.", "/search", "text", L.K("Яндекс")),
        ("ya.ru", "/search", "text", L.K("Яндекс")),
        ("duckduckgo.com", "/", "q", "DuckDuckGo"),
        ("search.brave.com", "/search", "q", "Brave"),
        ("ecosia.org", "/search", "q", "Ecosia"),
        ("youtube.com", "/results", "search_query", "YouTube"),
        ("github.com", "/search", "q", "GitHub"),
        ("stackoverflow.com", "/search", "q", "Stack Overflow"),
        ("perplexity.ai", "/search", "q", "Perplexity"),
        ("amazon.", "/s", "k", "Amazon"),
        ("aliexpress.", "/w/", "SearchText", "AliExpress"),
        ("wikipedia.org", "/w/index.php", "search", L.K("Википедия")),
        ("nuget.org", "/packages", "q", "NuGet"),
        ("npmjs.com", "/search", "q", "npm"),
    ];

    private static void Add(List<HistoryItem> into, string label, long ts, string url, string? title)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return;
        var host = u.Host.StartsWith("www.") ? u.Host[4..] : u.Host;

        foreach (var (h, p, param, engine) in Engines)
        {
            if (!host.Contains(h, StringComparison.OrdinalIgnoreCase) || !u.AbsolutePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)) continue;
            var term = HttpUtility.ParseQueryString(u.Query)[param];
            if (string.IsNullOrWhiteSpace(term)) break;
            term = term.Trim();

            // Листание страниц результатов и повторные заходы на ту же выдачу — один запрос.
            for (int i = into.Count - 1, seen = 0; i >= 0 && seen < 200; i--, seen++)
            {
                var prev = into[i];
                if (prev.Source != label || prev.Kind != "search") continue;
                if (prev.Term == term && ts - prev.Ts < 600) return;
                break;
            }
            into.Add(new HistoryItem(ts, label, "search", url, title, host, term, engine));
            return;
        }

        into.Add(new HistoryItem(ts, label, "visit", url, string.IsNullOrWhiteSpace(title) ? null : title, host, null, null));
    }

    // ── Выборочное удаление ──────────────────────────────────────────────

    /// <summary>Процессы браузеров: пока браузер работает, он держит историю и перезапишет наши изменения.</summary>
    private static readonly Dictionary<string, (string Process, string? PathPart)> Processes = new()
    {
        ["Chrome"] = ("chrome", @"\Google\Chrome\"), ["Edge"] = ("msedge", null), ["Brave"] = ("brave", null),
        ["Vivaldi"] = ("vivaldi", null), ["Yandex"] = ("browser", @"\Yandex\"), ["Chromium"] = ("chrome", @"\Chromium\"),
        ["Opera"] = ("opera", null), ["Opera GX"] = ("opera", null), ["Firefox"] = ("firefox", null),
    };

    private static List<System.Diagnostics.Process> Running(string browser)
    {
        var list = new List<System.Diagnostics.Process>();
        if (!Processes.TryGetValue(browser, out var p)) return list;
        foreach (var proc in System.Diagnostics.Process.GetProcessesByName(p.Process))
        {
            if (p.PathPart is not null)
            {
                string? path = null;
                try { path = proc.MainModule?.FileName; } catch (Exception) { }
                if (path is not null && !path.Contains(p.PathPart, StringComparison.OrdinalIgnoreCase)) { proc.Dispose(); continue; }
            }
            list.Add(proc);
        }
        return list;
    }

    /// <summary>Закрыть браузер штатно (как кнопкой «крестик»), фоновые процессы без окон — завершить.</summary>
    private static bool Close(string browser)
    {
        var procs = Running(browser);
        foreach (var p in procs.Where(p => p.MainWindowHandle != IntPtr.Zero)) { try { p.CloseMainWindow(); } catch (Exception) { } }
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && procs.Any(p => { try { return !p.HasExited && p.MainWindowHandle != IntPtr.Zero; } catch { return false; } }))
            Thread.Sleep(300);
        foreach (var p in procs) { try { if (!p.HasExited) p.Kill(); } catch (Exception) { } }
        Thread.Sleep(800);
        foreach (var p in procs) p.Dispose();
        return Running(browser).Count == 0;
    }

    public DeleteResult Delete(DeleteRequest req)
    {
        List<BrowserProfile> profiles;
        lock (_gate) profiles = _cache?.Profiles ?? Load(1).Item2;

        // План: по каждому профилю — что удалить.
        var urlsBy = new Dictionary<string, HashSet<string>>();
        foreach (var t in req.Items ?? [])
        {
            if (!urlsBy.TryGetValue(t.Source, out var set)) urlsBy[t.Source] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(t.Url);
        }
        var domain = req.Domain?.Trim().ToLowerInvariant();
        var clear = new HashSet<string>(req.ClearProfiles ?? []);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var ops = new List<PendingOp>();
        foreach (var p in profiles.Where(p => p.Ok))
        {
            bool byDomain = !string.IsNullOrEmpty(domain) && (req.Sources is null || req.Sources.Length == 0 || req.Sources.Contains(p.Id));
            bool isClear = clear.Contains(p.Id);
            urlsBy.TryGetValue(p.Id, out var urls);
            if (!isClear && !byDomain && urls is null) continue;
            ops.Add(new PendingOp(p.Id, p.Browser, p.Db, urls?.ToArray() ?? [], byDomain ? domain : null, isClear, isClear ? req.ClearFrom : null, now));
        }
        if (ops.Count == 0) return new DeleteResult(false, L.T("Нечего удалять."), 0, [], []);

        // Браузер без открытых окон (фоновый режим Edge, «работать в фоне» у Chrome) закрываем молча — вкладок там нет.
        // Открытый браузер не трогаем: удаление выполнится само, как только его закроют.
        int deleted = 0;
        var errors = new List<string>();
        var deferred = new List<string>();
        foreach (var group in ops.GroupBy(o => o.Browser))
        {
            bool canApply = HasNoWindows(group.Key) && CloseBackground(group.Key);
            if (!canApply && req.CloseBrowsers) canApply = Close(group.Key);
            if (!canApply)
            {
                AddPending(group);
                deferred.Add(group.Key);
                continue;
            }
            foreach (var op in group)
            {
                try { deleted += Apply(op); }
                catch (Exception ex) { errors.Add($"{op.Profile}: {ex.Message}"); }
            }
        }

        lock (_gate) _cache = null;
        var parts = new List<string>();
        if (deleted > 0 || deferred.Count < ops.Select(o => o.Browser).Distinct().Count())
            parts.Add(clear.Count > 0 ? L.T("История очищена, удалено посещений: {0}.", deleted) : L.T("Удалено адресов: {0}.", deleted));
        if (deferred.Count > 0)
            parts.Add(deferred.Count > 1
                ? L.T("{0} сейчас открыты — удалю автоматически, как только вы их закроете. Из этого списка записи уже скрыты.", string.Join(", ", deferred))
                : L.T("{0} сейчас открыт — удалю автоматически, как только вы его закроете. Из этого списка записи уже скрыты.", deferred[0]));
        if (errors.Count > 0) parts.Add(L.T("Ошибки: {0}", string.Join("; ", errors)));
        parts.Add(L.T("Перед удалением сохраняется копия истории (.syswatch-backup)."));
        return new DeleteResult(errors.Count == 0, string.Join(" ", parts), deleted, deferred, deferred);
    }

    private static int Apply(PendingOp op)
    {
        var profile = new BrowserProfile(op.Profile, op.Browser, "", true, null, 0, 0, op.Db);
        return op.Clear ? ClearIn(profile, op.ClearFrom, op.At) : DeleteIn(profile, new HashSet<string>(op.Urls, StringComparer.Ordinal), op.Domain);
    }

    /// <summary>
    /// Выполнить отложенные удаления в браузерах, которые уже закрыты. Вызывается из трея раз в 20 секунд,
    /// поэтому сама проверка дешёвая: если очередь пуста — ничего не делаем.
    /// </summary>
    public PendingApplied? ApplyPending()
    {
        var pending = LoadPending();
        if (pending.Count == 0) return null;

        int ops = 0, deleted = 0;
        var done = new List<string>();
        var left = new List<PendingOp>();
        foreach (var group in pending.GroupBy(o => o.Browser))
        {
            if (!(HasNoWindows(group.Key) && CloseBackground(group.Key))) { left.AddRange(group); continue; }
            foreach (var op in group)
            {
                try { deleted += Apply(op); ops++; }
                catch (Exception) when (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - op.At < 7 * 86400) { left.Add(op); } // файл занят — попробуем позже
                catch (Exception) { /* неделю не получается — снимаем задачу */ }
            }
            done.Add(group.Key);
        }
        SavePending(left);
        lock (_gate) _cache = null;
        return ops > 0 ? new PendingApplied(ops, deleted, done) : null;
    }

    /// <summary>«Закрыть и удалить сейчас»: штатно закрыть браузер (вкладки он восстановит) и выполнить его очередь.</summary>
    public PendingApplied? ApplyNow(string browser)
    {
        if (!Close(browser)) return null;
        return ApplyPending();
    }

    public IReadOnlyList<string> PendingBrowsers() => LoadPending().Select(o => o.Browser).Distinct().ToList();

    private static bool Hides(PendingOp op, HistoryItem i)
    {
        if (op.Profile != i.Source) return false;
        if (op.Clear) return (op.ClearFrom is null || i.Ts >= op.ClearFrom) && i.Ts <= op.At;
        if (op.Domain is not null && HostMatches(i.Url, op.Domain)) return true;
        return op.Urls.Contains(i.Url);
    }

    private readonly object _pendingGate = new();

    private List<PendingOp> LoadPending()
    {
        lock (_pendingGate)
        {
            try { return JsonSerializer.Deserialize<List<PendingOp>>(state.Get(PendingKey) ?? "[]") ?? []; }
            catch (Exception) { return []; }
        }
    }

    private void SavePending(List<PendingOp> list)
    {
        lock (_pendingGate) state.Set(PendingKey, JsonSerializer.Serialize(list));
    }

    private void AddPending(IEnumerable<PendingOp> ops)
    {
        lock (_pendingGate)
        {
            var list = LoadPending();
            list.AddRange(ops);
            SavePending(list);
        }
    }

    private static bool HasNoWindows(string browser)
    {
        var procs = Running(browser);
        try { return procs.All(p => { try { return p.MainWindowHandle == IntPtr.Zero; } catch { return true; } }); }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    /// <summary>Завершить процессы браузера без окон (фоновый режим). Окон и вкладок у них нет — пользователь ничего не теряет.</summary>
    private static bool CloseBackground(string browser)
    {
        var procs = Running(browser);
        if (procs.Count == 0) return true;
        foreach (var p in procs) { try { if (p.MainWindowHandle == IntPtr.Zero) p.Kill(); } catch (Exception) { } }
        Thread.Sleep(800);
        foreach (var p in procs) p.Dispose();
        return Running(browser).Count == 0;
    }

    private static bool HostMatches(string url, string domain)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var host = u.Host.ToLowerInvariant();
        return host == domain || host.EndsWith("." + domain);
    }

    private static int DeleteIn(BrowserProfile p, HashSet<string> urls, string? domain)
    {
        // Резервная копия перед любым изменением: вернуть историю можно, переименовав файл обратно.
        File.Copy(p.Db, p.Db + ".syswatch-backup", overwrite: true);

        bool firefox = p.Browser == "Firefox";
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = p.Db, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        c.Open();

        var ids = new List<long>();
        using (var sel = c.CreateCommand())
        {
            sel.CommandText = firefox ? "SELECT id, url FROM moz_places" : "SELECT id, url FROM urls";
            using var r = sel.ExecuteReader();
            while (r.Read())
            {
                var url = r.GetString(1);
                if (urls.Contains(url) || (!string.IsNullOrEmpty(domain) && HostMatches(url, domain))) ids.Add(r.GetInt64(0));
            }
        }
        if (ids.Count == 0) return 0;

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var t = c.CreateCommand())
        {
            t.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using var r = t.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));
        }

        using var tx = c.BeginTransaction();
        void Exec(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        Exec("CREATE TEMP TABLE del_ids(id INTEGER PRIMARY KEY)");
        using (var ins = c.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO del_ids VALUES($id)";
            var prm = ins.Parameters.Add("$id", SqliteType.Integer);
            foreach (var id in ids) { prm.Value = id; ins.ExecuteNonQuery(); }
        }

        if (firefox)
        {
            Exec("DELETE FROM moz_historyvisits WHERE place_id IN (SELECT id FROM del_ids)");
            if (tables.Contains("moz_inputhistory")) Exec("DELETE FROM moz_inputhistory WHERE place_id IN (SELECT id FROM del_ids)");
            // Страницы из закладок оставляем (иначе пропадёт закладка), но обнуляем их посещения.
            Exec("UPDATE moz_places SET visit_count = 0, last_visit_date = NULL WHERE id IN (SELECT id FROM del_ids)");
            Exec("DELETE FROM moz_places WHERE id IN (SELECT id FROM del_ids) AND foreign_count = 0");
        }
        else
        {
            Exec("CREATE TEMP TABLE del_visits(id INTEGER PRIMARY KEY)");
            Exec("INSERT INTO del_visits SELECT id FROM visits WHERE url IN (SELECT id FROM del_ids)");
            foreach (var (table, col) in new[] { ("visit_source", "id"), ("content_annotations", "visit_id"), ("context_annotations", "visit_id"), ("clusters_and_visits", "visit_id") })
                if (tables.Contains(table)) Exec($"DELETE FROM {table} WHERE {col} IN (SELECT id FROM del_visits)");
            Exec("DELETE FROM visits WHERE id IN (SELECT id FROM del_visits)");
            if (tables.Contains("keyword_search_terms")) Exec("DELETE FROM keyword_search_terms WHERE url_id IN (SELECT id FROM del_ids)");
            if (tables.Contains("segments"))
            {
                if (tables.Contains("segment_usage")) Exec("DELETE FROM segment_usage WHERE segment_id IN (SELECT id FROM segments WHERE url_id IN (SELECT id FROM del_ids))");
                Exec("DELETE FROM segments WHERE url_id IN (SELECT id FROM del_ids)");
            }
            Exec("DELETE FROM urls WHERE id IN (SELECT id FROM del_ids)");
        }
        tx.Commit();
        using (var vac = c.CreateCommand()) { vac.CommandText = "VACUUM"; vac.ExecuteNonQuery(); }
        return ids.Count;
    }

    /// <summary>Очистить историю профиля: все посещения (или начиная с from), затем адреса, на которые больше нет посещений.</summary>
    private static int ClearIn(BrowserProfile p, long? from, long until)
    {
        File.Copy(p.Db, p.Db + ".syswatch-backup", overwrite: true);
        bool firefox = p.Browser == "Firefox";
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = p.Db, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        c.Open();

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var t = c.CreateCommand())
        {
            t.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using var r = t.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));
        }

        using var tx = c.BeginTransaction();
        // Удаляем только то, что было до момента запроса: если удаление ждало закрытия браузера,
        // посещения, сделанные после нажатия «Очистить», не трогаем.
        int Exec(string sql, long? arg = null, long? to = null)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            if (arg is not null) cmd.Parameters.AddWithValue("$from", arg.Value);
            if (to is not null) cmd.Parameters.AddWithValue("$to", to.Value);
            return cmd.ExecuteNonQuery();
        }

        int n;
        if (firefox)
        {
            n = Exec("DELETE FROM moz_historyvisits WHERE visit_date >= $from AND visit_date <= $to", (from ?? 0) * 1_000_000, (until + 1) * 1_000_000);
            if (tables.Contains("moz_inputhistory")) Exec("DELETE FROM moz_inputhistory WHERE place_id NOT IN (SELECT place_id FROM moz_historyvisits)");
            Exec("UPDATE moz_places SET visit_count = (SELECT COUNT(*) FROM moz_historyvisits v WHERE v.place_id = moz_places.id), " +
                 "last_visit_date = (SELECT MAX(visit_date) FROM moz_historyvisits v WHERE v.place_id = moz_places.id)");
            Exec("DELETE FROM moz_places WHERE visit_count = 0 AND foreign_count = 0");
        }
        else
        {
            Exec("CREATE TEMP TABLE del_visits(id INTEGER PRIMARY KEY)");
            Exec("INSERT INTO del_visits SELECT id FROM visits WHERE visit_time >= $from AND visit_time <= $to",
                ((from ?? -ChromiumEpoch) + ChromiumEpoch) * 1_000_000, (until + 1 + ChromiumEpoch) * 1_000_000);
            foreach (var (table, col) in new[] { ("visit_source", "id"), ("content_annotations", "visit_id"), ("context_annotations", "visit_id"), ("clusters_and_visits", "visit_id") })
                if (tables.Contains(table)) Exec($"DELETE FROM {table} WHERE {col} IN (SELECT id FROM del_visits)");
            n = Exec("DELETE FROM visits WHERE id IN (SELECT id FROM del_visits)");

            // Адреса без единого посещения больше не нужны — вместе с поисковыми запросами и плитками частых сайтов.
            Exec("CREATE TEMP TABLE del_ids(id INTEGER PRIMARY KEY)");
            Exec("INSERT INTO del_ids SELECT id FROM urls WHERE id NOT IN (SELECT DISTINCT url FROM visits)");
            if (tables.Contains("keyword_search_terms")) Exec("DELETE FROM keyword_search_terms WHERE url_id IN (SELECT id FROM del_ids)");
            if (tables.Contains("segments"))
            {
                if (tables.Contains("segment_usage")) Exec("DELETE FROM segment_usage WHERE segment_id IN (SELECT id FROM segments WHERE url_id IN (SELECT id FROM del_ids))");
                Exec("DELETE FROM segments WHERE url_id IN (SELECT id FROM del_ids)");
            }
            Exec("DELETE FROM urls WHERE id IN (SELECT id FROM del_ids)");
            Exec("UPDATE urls SET visit_count = (SELECT COUNT(*) FROM visits v WHERE v.url = urls.id)");
        }
        tx.Commit();

        using (var vac = c.CreateCommand()) { vac.CommandText = "VACUUM"; vac.ExecuteNonQuery(); } // чтобы удалённое не осталось в свободных страницах файла
        return n;
    }
}
