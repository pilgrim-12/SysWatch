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
/// Один SQLite-файл на день (hot\2026-10-22.db). Запись — одной транзакцией за интервал.
/// Старые дни сначала облегчаются (без сетевых подробностей), потом удаляются целиком.
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

        CREATE TABLE IF NOT EXISTS gpu_1m(
            ts INTEGER NOT NULL, adapter TEXT NOT NULL, name TEXT,
            util REAL, util_max REAL, util_3d REAL, util_video REAL, util_compute REAL,
            mem_dedicated INTEGER, mem_shared INTEGER, mem_total INTEGER,
            temp REAL, temp_max REAL, power REAL, fan INTEGER,
            PRIMARY KEY(ts, adapter)) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS gpu_proc_1m(
            ts INTEGER NOT NULL, proc_id INTEGER NOT NULL, adapter TEXT NOT NULL, util REAL, mem INTEGER,
            PRIMARY KEY(ts, proc_id, adapter)) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS self_1m(
            ts INTEGER PRIMARY KEY, cpu_pct REAL, ws_bytes INTEGER, gc_heap INTEGER,
            kernel_events INTEGER, dns_events INTEGER, events_lost INTEGER);
        """;

    private readonly Dictionary<ProcInfo, long> _ids = new();
    private readonly List<SqliteCommand> _cmds = new();
    private SqliteConnection? _conn;
    private DateOnly _day;

    private SqliteCommand _insProc = null!, _updExit = null!, _insProcMin = null!,
        _insConn = null!, _insDns = null!, _insSys = null!, _insSelf = null!, _insGpu = null!, _insGpuProc = null!;

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

        foreach (var (adapter, g) in b.Gpu)
        {
            if (g.Samples == 0) continue;
            Exec(_insGpu, b.StartUnix, adapter, g.Name, g.Util / g.Samples, g.UtilMax, g.Util3D / g.Samples, g.UtilVideo / g.Samples,
                g.UtilCompute / g.Samples, g.MemDedicated, g.MemShared, g.MemTotal,
                g.TempN > 0 ? g.TempSum / g.TempN : null, g.TempN > 0 ? g.TempMax : null,
                g.PowerN > 0 ? g.PowerSum / g.PowerN : null, g.Fan > 0 ? g.Fan : null);
        }
        foreach (var ((proc, adapter), g) in b.GpuProcs)
        {
            // Загрузка — средняя за все замеры минуты по адаптеру (процесс мог не попадать в каждый замер).
            int samples = b.Gpu.TryGetValue(adapter, out var ga) ? Math.Max(ga.Samples, g.Samples) : Math.Max(1, g.Samples);
            Exec(_insGpuProc, b.StartUnix, GetId(proc, storeCmd), adapter, g.UtilSum / samples, g.MemMax);
        }

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

    private DateTime _lastMaintenance = DateTime.MinValue;

    private void EnsureOpen()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_conn is not null && today == _day)
        {
            // Срок хранения меняют в окне SysWatch — подхватываем раз в час, не дожидаясь смены дня.
            if (DateTime.UtcNow - _lastMaintenance > TimeSpan.FromHours(1)) ApplyRetention(options.Value.DataDir, today);
            return;
        }

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
        _insGpu = Cmd("INSERT OR REPLACE INTO gpu_1m VALUES($p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12,$p13,$p14,$p15)", 15);
        _insGpuProc = Cmd("INSERT OR REPLACE INTO gpu_proc_1m VALUES($p1,$p2,$p3,$p4,$p5)", 5);
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

    /// <summary>
    /// Срок хранения: из настроек окна (C:\ProgramData\SysWatch\settings.json), иначе из appsettings.
    /// retentionDays — сколько дней хранить вообще; detailDays — сколько дней хранить сетевые подробности
    /// (соединения, DNS, видеокарта по процессам). Старше detailDays файл «облегчается»: остаются поминутные
    /// данные процессов и системы, а это основная часть отчётов и графиков.
    /// </summary>
    private (int Retention, int Detail) Limits()
    {
        int retention = options.Value.RetentionDays, detail = options.Value.DetailDays;
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SysWatch", "settings.json");
            if (File.Exists(file))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("retentionDays", out var r) && r.TryGetInt32(out var rv)) retention = rv;
                if (doc.RootElement.TryGetProperty("detailDays", out var d) && d.TryGetInt32(out var dv)) detail = dv;
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "Retention: cannot read settings.json"); }
        retention = Math.Clamp(retention, 1, 3650);
        return (retention, Math.Clamp(detail, 1, retention));
    }

    private void ApplyRetention(string dir, DateOnly today)
    {
        _lastMaintenance = DateTime.UtcNow;
        var (retention, detail) = Limits();
        var cutoff = today.AddDays(-retention);
        var thinBefore = today.AddDays(-detail);
        foreach (var file in Directory.EnumerateFiles(dir, "*.db"))
        {
            if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", out var d) || d >= thinBefore)
                continue;
            if (d < cutoff)
            {
                try
                {
                    File.Delete(file);
                    File.Delete(file + "-wal");
                    File.Delete(file + "-shm");
                    log.LogInformation("Retention: removed {File}", Path.GetFileName(file));
                }
                catch (Exception ex) { log.LogWarning(ex, "Retention: cannot remove {File}", file); }
            }
            else Thin(file);
        }
    }

    /// <summary>Убрать из старого дня сетевые подробности и сжать файл. Помечается user_version=1, повторно не трогается.</summary>
    private void Thin(string file)
    {
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = file, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 5,
            }.ToString());
            c.Open();
            using (var check = c.CreateCommand())
            {
                check.CommandText = "PRAGMA user_version";
                if (Convert.ToInt64(check.ExecuteScalar()) >= 1) return;
            }
            long before = new FileInfo(file).Length;
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var list = c.CreateCommand())
            {
                list.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                using var r = list.ExecuteReader();
                while (r.Read()) tables.Add(r.GetString(0));
            }
            using (var tx = c.BeginTransaction())
            {
                // Файлы старых версий агента могут не иметь части таблиц (например, gpu_proc_1m).
                foreach (var t in new[] { "conn_1m", "dns", "gpu_proc_1m" })
                {
                    if (!tables.Contains(t)) continue;
                    using var del = c.CreateCommand();
                    del.Transaction = tx;
                    del.CommandText = $"DELETE FROM {t}";
                    del.ExecuteNonQuery();
                }
                if (tables.Contains("process"))
                {
                    using var upd = c.CreateCommand();
                    upd.Transaction = tx;
                    upd.CommandText = "UPDATE process SET cmdline = NULL";
                    upd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            using (var mark = c.CreateCommand())
            {
                mark.CommandText = "PRAGMA user_version = 1";
                mark.ExecuteNonQuery();
            }
            using (var vac = c.CreateCommand())
            {
                vac.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM;";
                vac.ExecuteNonQuery();
            }
            c.Close();
            log.LogInformation("Retention: thinned {File} {Before} -> {After} bytes", Path.GetFileName(file), before, new FileInfo(file).Length);
        }
        catch (Exception ex) { log.LogWarning(ex, "Retention: cannot thin {File}", file); }
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
