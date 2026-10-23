using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SysWatch.App.Anomalies;

public sealed record Anomaly(long Id, long Ts, string Rule, string Severity, string App, string Title, string Detail, bool Seen);

public sealed record AnomalyList(IReadOnlyList<Anomaly> Items, int Unseen, bool Learning);

/// <summary>
/// Найденные аномалии и состояние детектора: C:\ProgramData\SysWatch\anomalies.db.
/// Одна и та же аномалия (по ключу) записывается один раз.
/// </summary>
public sealed class AnomalyStore
{
    private readonly string _cs;

    public AnomalyStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SysWatch");
        Directory.CreateDirectory(dir);
        _cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "anomalies.db"), DefaultTimeout = 5 }.ToString();

        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS anomaly(
                id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, rule TEXT NOT NULL, severity TEXT NOT NULL,
                app TEXT NOT NULL, title TEXT NOT NULL, detail TEXT NOT NULL,
                key TEXT NOT NULL UNIQUE, seen INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS anomaly_ts ON anomaly(ts);
            CREATE TABLE IF NOT EXISTS ignore(rule TEXT NOT NULL, app TEXT NOT NULL COLLATE NOCASE, PRIMARY KEY(rule, app));
            CREATE TABLE IF NOT EXISTS state(k TEXT PRIMARY KEY, v TEXT);
            """);

        // Заголовок и описание хранятся русским шаблоном + аргументами, переводятся при чтении (колонка args — с версии с языками).
        bool hasArgs = false;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(anomaly)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (r.GetString(1) == "args") hasArgs = true;
        }
        if (!hasArgs) Exec(c, "ALTER TABLE anomaly ADD COLUMN args TEXT");

        // Ложные срабатывания прошлых версий: установщик самого SysWatch (распаковывается во временную папку)
        // и приложения Microsoft Store, подписанные пакетом, а не внутри exe.
        Exec(c, """
            DELETE FROM anomaly
            WHERE (rule IN ('temp_exec', 'new_program') AND app LIKE 'SysWatch-Setup-%')
               OR (rule = 'unsigned_net' AND key LIKE '%\WindowsApps\%')
            """);
    }

    /// <summary>Перевести шаблон на текущий язык. Аргументы тоже пробуем перевести: это могут быть подписи вроде «Служба».</summary>
    private static string Localize(string template, string[]? args)
    {
        if (args is null || args.Length == 0) return L.T(template);
        return L.T(template, args.Select(a => (object?)L.T(a)).ToArray());
    }

    private static string[]? ParseArgs(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<string[]>(json); }
        catch (JsonException) { return null; }
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    private static int Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    /// <summary>Записать аномалию. Возвращает её, если она новая и не в списке игнорируемых.</summary>
    /// <param name="title">Русский шаблон (ключ словаря), помеченный L.K.</param>
    /// <param name="detail">Русский шаблон с {0}, {1}… — аргументы в args.</param>
    public Anomaly? Add(long ts, string rule, string severity, string app, string title, string detail, string key, string[]? args = null)
    {
        using var c = Open();
        if (Scalar(c, "SELECT 1 FROM ignore WHERE rule = $r AND app = $a", ("$r", rule), ("$a", app)) is not null)
            return null;

        int n = Exec(c, """
            INSERT OR IGNORE INTO anomaly(ts, rule, severity, app, title, detail, key, args)
            VALUES($ts, $r, $s, $a, $t, $d, $k, $args)
            """, ("$ts", ts), ("$r", rule), ("$s", severity), ("$a", app), ("$t", title), ("$d", detail), ("$k", key),
            ("$args", args is null ? null : JsonSerializer.Serialize(args)));
        if (n == 0) return null;

        long id = (long)Scalar(c, "SELECT last_insert_rowid()")!;
        return new Anomaly(id, ts, rule, severity, app, Localize(title, null), Localize(detail, args), false);
    }

    public AnomalyList List(long fromUnix)
    {
        using var c = Open();
        var items = new List<Anomaly>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, ts, rule, severity, app, title, detail, seen, args FROM anomaly
                WHERE ts >= $from ORDER BY ts DESC LIMIT 500
                """;
            cmd.Parameters.AddWithValue("$from", fromUnix);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var args = ParseArgs(r.IsDBNull(8) ? null : r.GetString(8));
                items.Add(new Anomaly(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4),
                    Localize(r.GetString(5), null), Localize(r.GetString(6), args), r.GetInt64(7) != 0));
            }
        }

        int unseen = Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM anomaly WHERE seen = 0"));
        bool learning = Get("learning") == "1";
        return new AnomalyList(items, unseen, learning);
    }

    public int Unseen()
    {
        using var c = Open();
        return Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM anomaly WHERE seen = 0"));
    }

    public void MarkSeen()
    {
        using var c = Open();
        Exec(c, "UPDATE anomaly SET seen = 1 WHERE seen = 0");
    }

    /// <summary>Больше не предупреждать по этому правилу для этой программы; уже найденные убрать.</summary>
    public void Ignore(string rule, string app)
    {
        using var c = Open();
        Exec(c, "INSERT OR IGNORE INTO ignore(rule, app) VALUES($r, $a)", ("$r", rule), ("$a", app));
        Exec(c, "DELETE FROM anomaly WHERE rule = $r AND app = $a COLLATE NOCASE", ("$r", rule), ("$a", app));
    }

    public string? Get(string key)
    {
        using var c = Open();
        return Scalar(c, "SELECT v FROM state WHERE k = $k", ("$k", key)) as string;
    }

    public void Set(string key, string value)
    {
        using var c = Open();
        Exec(c, "INSERT INTO state(k, v) VALUES($k, $v) ON CONFLICT(k) DO UPDATE SET v = excluded.v", ("$k", key), ("$v", value));
    }
}
