using Microsoft.Data.Sqlite;

namespace SysWatch.App.Data;

/// <summary>
/// Дневные SQLite-файлы агента (YYYY-MM-DD.db). Окно только читает их.
/// Агент в режиме разработки пишет в dev\, служба — в hot\; берём папку со свежими данными.
/// </summary>
public sealed class DayFiles
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SysWatch");

    public string Dir { get; }

    public DayFiles(string? dir = null)
    {
        Dir = dir ?? PickDir();
    }

    private static string PickDir()
    {
        var candidates = new[] { "hot", "dev" }
            .Select(n => Path.Combine(Root, n))
            .Where(Directory.Exists)
            .Select(d => (Dir: d, Last: Directory.EnumerateFiles(d, "*.db").Select(f => File.GetLastWriteTimeUtc(f)).DefaultIfEmpty().Max()))
            .OrderByDescending(x => x.Last)
            .ToList();
        return candidates.Count > 0 ? candidates[0].Dir : Path.Combine(Root, "hot");
    }

    /// <summary>Файлы, которые пересекаются с периодом [fromUnix, сейчас], от старых к новым.</summary>
    public IEnumerable<(DateOnly Day, string Path)> For(long fromUnix)
    {
        var first = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(fromUnix).LocalDateTime);
        var today = DateOnly.FromDateTime(DateTime.Now);
        for (var d = first; d <= today; d = d.AddDays(1))
        {
            var p = Path.Combine(Dir, $"{d:yyyy-MM-dd}.db");
            if (File.Exists(p)) yield return (d, p);
        }
    }

    /// <summary>Все дневные файлы старше указанного дня.</summary>
    public IEnumerable<string> OlderThan(DateOnly day)
    {
        if (!Directory.Exists(Dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(Dir, "*.db"))
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", out var d) && d < day)
                yield return f;
    }

    public static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        c.Open();
        return c;
    }
}

internal static class SqliteExt
{
    public static SqliteCommand Cmd(this SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    public static IEnumerable<SqliteDataReader> Rows(this SqliteCommand cmd)
    {
        using (cmd)
        using (var r = cmd.ExecuteReader())
            while (r.Read()) yield return r;
    }

    public static long? LongOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    public static string? StrOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static long Long0(this SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);
    public static double Dbl0(this SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetDouble(i);

    public static bool HasColumn(this SqliteConnection c, string table, string column)
    {
        foreach (var r in c.Cmd($"PRAGMA table_info({table})").Rows())
            if (r.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Колонка памяти называлась ws_max в первой версии схемы.</summary>
    public static string MemColumn(this SqliteConnection c)
    {
        foreach (var r in c.Cmd("PRAGMA table_info(proc_1m)").Rows())
            if (r.GetString(1) == "mem_private_max") return "mem_private_max";
        return "ws_max";
    }
}
