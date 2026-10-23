using Microsoft.Data.Sqlite;

namespace SysWatch.App.Disk;

public sealed record DiskNode(string Path, string? Parent, bool IsDir, long Size, long Files);

public sealed record SnapshotInfo(long Id, long Ts, string Root, long Total, long Free, long Scanned, long Files, double Seconds, long? Shadow);

/// <summary>
/// Снимки диска: C:\ProgramData\SysWatch\disk.db. В снимок попадают папки и файлы от 100 МБ
/// и всё содержимое корня — этого хватает, чтобы найти, куда ушло место, и сравнить снимки между собой.
/// </summary>
public sealed class DiskStore
{
    private const int Keep = 60;
    private readonly string _cs;

    public DiskStore()
    {
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SysWatch");
        Directory.CreateDirectory(dir);
        _cs = new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(dir, "disk.db"), DefaultTimeout = 10 }.ToString();

        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS snapshot(
                id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, root TEXT NOT NULL,
                total INTEGER, free INTEGER, scanned INTEGER, files INTEGER, seconds REAL, shadow INTEGER);
            CREATE TABLE IF NOT EXISTS node(
                snap INTEGER NOT NULL, path TEXT NOT NULL, parent TEXT, is_dir INTEGER NOT NULL,
                size INTEGER NOT NULL, files INTEGER NOT NULL,
                PRIMARY KEY(snap, path)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS node_parent ON node(snap, parent);
            """);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public long Save(SnapshotInfo info, IReadOnlyList<DiskNode> nodes)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        long id;
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO snapshot(ts, root, total, free, scanned, files, seconds, shadow)
                VALUES($ts, $root, $total, $free, $scanned, $files, $seconds, $shadow) RETURNING id
                """;
            cmd.Parameters.AddWithValue("$ts", info.Ts);
            cmd.Parameters.AddWithValue("$root", info.Root);
            cmd.Parameters.AddWithValue("$total", info.Total);
            cmd.Parameters.AddWithValue("$free", info.Free);
            cmd.Parameters.AddWithValue("$scanned", info.Scanned);
            cmd.Parameters.AddWithValue("$files", info.Files);
            cmd.Parameters.AddWithValue("$seconds", info.Seconds);
            cmd.Parameters.AddWithValue("$shadow", (object?)info.Shadow ?? DBNull.Value);
            id = (long)cmd.ExecuteScalar()!;
        }

        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO node VALUES($s, $p, $parent, $d, $size, $files)";
            var ps = new[] { "$s", "$p", "$parent", "$d", "$size", "$files" }.Select(n => cmd.Parameters.Add(new SqliteParameter { ParameterName = n })).ToArray();
            ps[0].Value = id;
            foreach (var n in nodes)
            {
                ps[1].Value = n.Path;
                ps[2].Value = (object?)n.Parent ?? DBNull.Value;
                ps[3].Value = n.IsDir ? 1 : 0;
                ps[4].Value = n.Size;
                ps[5].Value = n.Files;
                cmd.ExecuteNonQuery();
            }
        }

        // Старые снимки удаляем, чтобы база не росла сама по себе.
        const string Old = "SELECT id FROM snapshot WHERE root = $root COLLATE NOCASE ORDER BY ts DESC LIMIT -1 OFFSET $k";
        Exec(c, $"DELETE FROM node WHERE snap IN ({Old})", ("$root", info.Root), ("$k", Keep));
        Exec(c, $"DELETE FROM snapshot WHERE id IN ({Old})", ("$root", info.Root), ("$k", Keep));
        tx.Commit();
        return id;
    }

    private static SnapshotInfo ReadInfo(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetDouble(7),
        r.IsDBNull(8) ? null : r.GetInt64(8));

    private const string InfoCols = "id, ts, root, total, free, scanned, files, seconds, shadow";

    public SnapshotInfo? Latest(string root)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {InfoCols} FROM snapshot WHERE root = $root COLLATE NOCASE ORDER BY ts DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$root", root);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadInfo(r) : null;
    }

    /// <summary>С чем сравнивать: снимок примерно недельной давности, иначе самый старый из имеющихся.</summary>
    public SnapshotInfo? CompareTo(SnapshotInfo latest)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT {InfoCols} FROM snapshot WHERE root = $root COLLATE NOCASE AND id <> $id AND ts < $ts
            ORDER BY CASE WHEN ts <= $week THEN 0 ELSE 1 END, CASE WHEN ts <= $week THEN -ts ELSE ts END
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", latest.Id);
        cmd.Parameters.AddWithValue("$root", latest.Root);
        cmd.Parameters.AddWithValue("$ts", latest.Ts);
        cmd.Parameters.AddWithValue("$week", latest.Ts - 6 * 86400);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadInfo(r) : null;
    }

    public List<DiskNode> Children(long snap, string parent)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path, parent, is_dir, size, files FROM node WHERE snap = $s AND parent = $p ORDER BY size DESC";
        cmd.Parameters.AddWithValue("$s", snap);
        cmd.Parameters.AddWithValue("$p", parent);
        return Read(cmd);
    }

    /// <summary>Время последнего снимка по каждому диску.</summary>
    public Dictionary<string, long> LastScans()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT root, MAX(ts) FROM snapshot GROUP BY root";
        var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read()) d[r.GetString(0)] = r.GetInt64(1);
        return d;
    }

    public DiskNode? Node(long snap, string path)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path, parent, is_dir, size, files FROM node WHERE snap = $s AND path = $p";
        cmd.Parameters.AddWithValue("$s", snap);
        cmd.Parameters.AddWithValue("$p", path);
        return Read(cmd).FirstOrDefault();
    }

    public Dictionary<string, DiskNode> All(long snap)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path, parent, is_dir, size, files FROM node WHERE snap = $s";
        cmd.Parameters.AddWithValue("$s", snap);
        return Read(cmd).ToDictionary(n => n.Path, StringComparer.OrdinalIgnoreCase);
    }

    private static List<DiskNode> Read(SqliteCommand cmd)
    {
        var list = new List<DiskNode>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new DiskNode(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2) != 0, r.GetInt64(3), r.GetInt64(4)));
        return list;
    }
}
