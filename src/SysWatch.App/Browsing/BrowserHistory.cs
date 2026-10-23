using System.Text.Json;
using System.Web;
using Microsoft.Data.Sqlite;

namespace SysWatch.App.Browsing;

public sealed record BrowserProfile(string Id, string Browser, string Profile, bool Ok, string? Error, int Visits, int Searches);

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
public sealed class BrowserHistory
{
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
        new("Яндекс", Path.Combine(Local, "Yandex", "YandexBrowser", "User Data"), false),
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
        var list = items.OrderByDescending(i => i.Ts).Take(limit).ToList();

        return new HistoryResult(from, profiles, list, filtered.Count,
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
                    var profile = src.SingleProfile ? "" : names.GetValueOrDefault(folder, folder == "Default" ? "Основной" : folder);
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
            return new BrowserProfile(label, browser, profile, true, null, mine.Count(i => i.Kind == "visit"), mine.Count(i => i.Kind == "search"));
        }
        catch (IOException)
        {
            return new BrowserProfile(label, browser, profile, false, "Браузер не даёт прочитать историю. Закройте его и обновите страницу.", 0, 0);
        }
        catch (Exception ex)
        {
            return new BrowserProfile(label, browser, profile, false, ex.Message, 0, 0);
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
        ("yandex.", "/search", "text", "Яндекс"),
        ("ya.ru", "/search", "text", "Яндекс"),
        ("duckduckgo.com", "/", "q", "DuckDuckGo"),
        ("search.brave.com", "/search", "q", "Brave"),
        ("ecosia.org", "/search", "q", "Ecosia"),
        ("youtube.com", "/results", "search_query", "YouTube"),
        ("github.com", "/search", "q", "GitHub"),
        ("stackoverflow.com", "/search", "q", "Stack Overflow"),
        ("perplexity.ai", "/search", "q", "Perplexity"),
        ("amazon.", "/s", "k", "Amazon"),
        ("aliexpress.", "/w/", "SearchText", "AliExpress"),
        ("wikipedia.org", "/w/index.php", "search", "Википедия"),
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
}
