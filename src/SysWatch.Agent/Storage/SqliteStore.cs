using System.Collections.Concurrent;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SysWatch.Agent.Pipeline;

namespace SysWatch.Agent.Storage;

public sealed record SelfSample(
    double CpuPct, long WorkingSet, long GcHeap, long KernelEvents, long DnsEvents, long EventsLost,
    double KernelPct, double DnsPct, double SamplerPct, double WriterPct, double OtherPct);

/// <summary>
/// Один SQLite-файл на день (hot\2026-10-22.db). Удаление старых данных —
/// это удаление файла, без DELETE и VACUUM. Запись — одной транзакцией за интервал.
/// </summary>
public sealed class SqliteStore(IOptions<AgentOptions> options, ILogger<SqliteStore> log) : IDisposable
{
    private const string Schema = """
        PRAGMA journal_mode=WAL;
        PRAGMA synchronous=NORMAL;
        PRAGMA cache_size=-2000;
        PRAGMA temp_store=MEMORY;

        CREATE TABLE IF NOT EXISTS process(
            id INTEGER PRIMARY KEY,
            pid INTEGER NOT NULL,
            create_time INTEGER,
            first_seen INTEGER NOT NULL,
            exited INTEGER,
            image TEXT NOT NULL,
            path TEXT,
            cmdline TEXT,
            parent_pid INTEGER);

        CREATE TABLE IF NOT EXISTS proc_1m(
            ts INTEGER NOT NULL, proc_id INTEGER NOT NULL,
            cpu_ms INTEGER, mem_private_max INTEGER, io_read INTEGER, io_write INTEGER,
            net_tx INTEGER, net_rx INTEGER,
            PRIMARY KEY(ts, proc_id)) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS conn_1m(
            ts INTEGER NOT NULL, proc_id INTEGER NOT NULL,
            remote_ip TEXT NOT NULL, port INTEGER NOT NULL, proto INTEGER NOT NULL,
            domain TEXT, bytes_tx INTEGER, bytes_rx INTEGER, connects INTEGER,
            PRIMARY KEY(ts, proc_id, remote_ip, port, proto)) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS dns(
            ts INTEGER NOT NULL, proc_id INTEGER NOT NULL, domain TEXT NOT NULL, results TEXT);

        CREATE TABLE IF NOT EXISTS system_1m(
            ts INTEGER PRIMARY KEY, cpu_pct REAL, mem_load_pct REAL);

        CREATE TABLE IF NOT EXISTS self_1m(
            ts INTEGER PRIMARY KEY, cpu_pct REAL, ws_bytes INTEGER, gc_heap INTEGER,
            kernel_events INTEGER, dns_events INTEGER, events_lost INTEGER);
        """;

    private readonly Dictionary<ProcInfo, long> _ids = new();
    private readonly List<SqliteCommand> _cmds = new();
    private SqliteConnection? _conn;
    private DateOnly _day;

    private SqliteCommand _insProc = null!, _updExit = null!, _insProcMin = null!,
        _insConn = null!, _insDns = null!, _insSys = null!, _insSelf = null!;

    public void Write(MinuteBucket b, SelfSample self, ConcurrentDictionary<IPAddress, string> ipToDomain)
    {
        EnsureOpen();
        bool storeCmd = options.Value.StoreCommandLines;

        using var tx = _conn!.BeginTransaction();
        foreach (var c in _cmds) c.Transaction = tx;

        foreach (var p in b.Started) GetId(p, storeCmd);

        foreach (var (p, c) in b.Procs)
        {
            if (c.IsIdle) continue; // простаивающие процессы не пишем — экономия места
            Exec(_insProcMin, b.StartUnix, GetId(p, storeCmd), c.CpuMs, c.WsMax, c.IoRead, c.IoWrite, c.NetTx, c.NetRx);
        }

        foreach (var (k, c) in b.Conns)
        {
            ipToDomain.TryGetValue(k.Ip, out var domain);
            Exec(_insConn, b.StartUnix, GetId(k.Proc, storeCmd), k.Ip.ToString(), k.Port, (int)k.Proto,
                domain, c.Tx, c.Rx, c.Connects);
        }

        foreach (var d in b.Dns)
            Exec(_insDns, d.Ts, GetId(d.Proc, storeCmd), d.Domain, d.Results);

        foreach (var p in b.Stopped)
            if (_ids.TryGetValue(p, out var id)) Exec(_updExit, p.ExitedUnix, id);

        if (b.SysSamples > 0)
            Exec(_insSys, b.StartUnix, b.SysCpuSum / b.SysSamples, b.SysMemSum / b.SysSamples);

        Exec(_insSelf, b.StartUnix, self.CpuPct, self.WorkingSet, self.GcHeap,
            self.KernelEvents, self.DnsEvents, self.EventsLost,
            self.KernelPct, self.DnsPct, self.SamplerPct, self.WriterPct, self.OtherPct);

        tx.Commit();
    }

    private long GetId(ProcInfo p, bool storeCmd)
    {
        if (_ids.TryGetValue(p, out var id)) return id;

        var r = Scalar(_insProc, p.Pid,
            p.CreateTime == 0 ? (object?)null : p.CreateTime,
            p.FirstSeenUnix,
            p.ExitedUnix == 0 ? (object?)null : p.ExitedUnix,
            p.Image, p.ExePath,
            storeCmd ? p.CommandLine : null, p.ParentPid);
        id = Convert.ToInt64(r);
        _ids[p] = id;
        return id;
    }

    private void EnsureOpen()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_conn is not null && today == _day) return;

        CloseConnection();
        var dir = options.Value.DataDir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{today:yyyy-MM-dd}.db");

        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _conn.Open();

        using (var init = _conn.CreateCommand())
        {
            init.CommandText = Schema;
            init.ExecuteNonQuery();
        }
        AddMissingColumns();

        _insProc = Cmd("INSERT INTO process(pid,create_time,first_seen,exited,image,path,cmdline,parent_pid) " +
                       "VALUES($p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8) RETURNING id", 8);
        _updExit = Cmd("UPDATE process SET exited=$p1 WHERE id=$p2", 2);
        _insProcMin = Cmd("INSERT OR REPLACE INTO proc_1m VALUES($p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8)", 8);
        _insConn = Cmd("INSERT OR REPLACE INTO conn_1m VALUES($p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9)", 9);
        _insDns = Cmd("INSERT INTO dns VALUES($p1,$p2,$p3,$p4)", 4);
        _insSys = Cmd("INSERT OR REPLACE INTO system_1m VALUES($p1,$p2,$p3)", 3);
        _insSelf = Cmd("INSERT OR REPLACE INTO self_1m(ts, cpu_pct, ws_bytes, gc_heap, kernel_events, dns_events, events_lost, " +
                       "kernel_pct, dns_pct, sampler_pct, writer_pct, other_pct) " +
                       "VALUES($p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12)", 12);

        _day = today;
        log.LogInformation("Storage: {Path}", path);
        ApplyRetention(dir, today);
    }

    /// <summary>Файл дня мог быть создан прошлой версией агента — добавляем новые колонки разбивки расхода.</summary>
    private void AddMissingColumns()
    {
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _conn!.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(self_1m)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) have.Add(r.GetString(1));
        }
        foreach (var col in new[] { "kernel_pct", "dns_pct", "sampler_pct", "writer_pct", "other_pct" })
        {
            if (have.Contains(col)) continue;
            using var alter = _conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE self_1m ADD COLUMN {col} REAL";
            alter.ExecuteNonQuery();
        }
    }

    private void ApplyRetention(string dir, DateOnly today)
    {
        var cutoff = today.AddDays(-Math.Max(1, options.Value.RetentionDays));
        foreach (var file in Directory.EnumerateFiles(dir, "*.db"))
        {
            if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", out var d) || d >= cutoff)
                continue;
            try
            {
                File.Delete(file);
                File.Delete(file + "-wal");
                File.Delete(file + "-shm");
                log.LogInformation("Retention: removed {File}", Path.GetFileName(file));
            }
            catch (Exception ex) { log.LogWarning(ex, "Retention: cannot remove {File}", file); }
        }
    }

    private SqliteCommand Cmd(string sql, int paramCount)
    {
        var c = _conn!.CreateCommand();
        c.CommandText = sql;
        for (int i = 1; i <= paramCount; i++)
            c.Parameters.Add(new SqliteParameter { ParameterName = "$p" + i });
        _cmds.Add(c);
        return c;
    }

    private static void Bind(SqliteCommand c, object?[] values)
    {
        for (int i = 0; i < values.Length; i++)
            c.Parameters[i].Value = values[i] ?? DBNull.Value;
    }

    private static void Exec(SqliteCommand c, params object?[] values)
    {
        Bind(c, values);
        c.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteCommand c, params object?[] values)
    {
        Bind(c, values);
        return c.ExecuteScalar();
    }

    private void CloseConnection()
    {
        foreach (var c in _cmds) c.Dispose();
        _cmds.Clear();
        _ids.Clear();
        _conn?.Dispose();
        _conn = null;
    }

    public void Dispose() => CloseConnection();
}
