using System.Net;
using Microsoft.Data.Sqlite;
using SysWatch.App.Sampling;

namespace SysWatch.App.Data;

/// <summary>Все запросы окна к истории агента. Период — последние range минут.</summary>
public sealed class Queries(DayFiles files, Signatures signatures, Func<HashSet<(int Pid, long Create)>> aliveProcesses)
{
    private static readonly int Cores = Environment.ProcessorCount;
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static long FromRange(int range) => Now() - Math.Clamp(range, 1, 60 * 24 * 31) * 60L;
    private static long FileTimeToUnix(long ft) => ft / 10_000_000 - 11_644_473_600;

    private sealed class Acc
    {
        public long CpuMs, MemMax, IoR, IoW, Tx, Rx;
        public readonly HashSet<string> Domains = new(StringComparer.OrdinalIgnoreCase);
    }

    // ── Обзор ────────────────────────────────────────────────────────────

    public Overview Overview(int range)
    {
        long from = FromRange(range), first = long.MaxValue;
        var apps = new Dictionary<string, Acc>(StringComparer.OrdinalIgnoreCase);
        var agent = new SortedDictionary<long, (double Sum, int N)>();
        double pk = 0, pd = 0, ps = 0, pw = 0, po = 0; int pn = 0;

        foreach (var (_, path) in files.For(from))
        {
            using var c = DayFiles.Open(path);
            var mem = c.MemColumn();

            foreach (var r in c.Cmd($"""
                SELECT p.image, m.ts, SUM(m.cpu_ms), SUM(m.{mem}), SUM(m.io_read), SUM(m.io_write), SUM(m.net_tx), SUM(m.net_rx)
                FROM proc_1m m JOIN process p ON p.id = m.proc_id
                WHERE m.ts >= $from GROUP BY p.image, m.ts
                """, ("$from", from)).Rows())
            {
                var a = Get(apps, r.GetString(0));
                first = Math.Min(first, r.GetInt64(1));
                a.CpuMs += r.Long0(2);
                a.MemMax = Math.Max(a.MemMax, r.Long0(3));
                a.IoR += r.Long0(4); a.IoW += r.Long0(5);
                a.Tx += r.Long0(6); a.Rx += r.Long0(7);
            }

            foreach (var r in c.Cmd("""
                SELECT p.image, d.domain FROM dns d JOIN process p ON p.id = d.proc_id
                WHERE d.ts >= $from GROUP BY 1, 2
                """, ("$from", from)).Rows())
                Get(apps, r.GetString(0)).Domains.Add(r.GetString(1));

            foreach (var r in c.Cmd("SELECT ts, cpu_pct FROM self_1m WHERE ts >= $from", ("$from", from)).Rows())
            {
                long m = r.GetInt64(0) / 60 * 60;
                agent[m] = agent.TryGetValue(m, out var v) ? (v.Sum + r.Dbl0(1), v.N + 1) : (r.Dbl0(1), 1);
            }

            if (c.HasColumn("self_1m", "kernel_pct"))
            {
                foreach (var r in c.Cmd("""
                    SELECT kernel_pct, dns_pct, sampler_pct, writer_pct, other_pct FROM self_1m
                    WHERE ts >= $from AND kernel_pct IS NOT NULL
                    """, ("$from", from)).Rows())
                {
                    pk += r.Dbl0(0); pd += r.Dbl0(1); ps += r.Dbl0(2); pw += r.Dbl0(3); po += r.Dbl0(4); pn++;
                }
            }
        }

        double seconds = first == long.MaxValue ? 60 : Math.Max(60, Now() - Math.Max(from, first));
        var list = apps.Select(kv => new AppTotals(
                kv.Key,
                kv.Value.CpuMs / (seconds * 1000 * Cores) * 100,
                kv.Value.MemMax, kv.Value.IoR, kv.Value.IoW, kv.Value.Tx, kv.Value.Rx, kv.Value.Domains.Count))
            .OrderByDescending(a => a.CpuPct)
            .Take(200)
            .ToList();

        var parts = pn > 0 ? new AgentParts(pk / pn, pd / pn, ps / pn, pw / pn, po / pn) : null;
        return new Overview(list, agent.Values.Select(v => (double?)(v.Sum / v.N)).ToList(), agent.Keys.ToList(), parts);
    }

    // ── Таймлайн ─────────────────────────────────────────────────────────

    private sealed class Bins
    {
        public readonly long First;
        public readonly int Sec;
        public readonly long[] Starts;

        public Bins(long from, int maxPoints)
        {
            long now = Now();
            int minutes = (int)Math.Ceiling((now - from) / 60.0);
            Sec = 60 * Math.Max(1, (int)Math.Ceiling(minutes / (double)maxPoints));
            First = from - from % Sec;
            int n = (int)((now - First) / Sec) + 1;
            Starts = Enumerable.Range(0, n).Select(i => First + (long)i * Sec).ToArray();
        }

        public int Index(long ts)
        {
            long i = (ts - First) / Sec;
            return i < 0 || i >= Starts.Length ? -1 : (int)i;
        }
    }

    /// <summary>
    /// Сколько секунд каждой корзины реально покрыто записями агента.
    /// Нужно, чтобы средние не занижались в неполных корзинах и до старта агента.
    /// </summary>
    private static double[] Coverage(SqliteConnection c, long from, Bins bins, double[]? into = null)
    {
        var cov = into ?? new double[bins.Starts.Length];
        var ts = c.Cmd("SELECT ts FROM system_1m WHERE ts >= $from ORDER BY ts", ("$from", from)).Rows().Select(r => r.GetInt64(0)).ToList();
        if (ts.Count == 0) return cov;

        var diffs = ts.Zip(ts.Skip(1), (a, b) => b - a).Where(d => d > 0).OrderBy(d => d).ToList();
        double interval = diffs.Count > 0 ? diffs[diffs.Count / 2] : 60;
        foreach (var t in ts)
        {
            int i = bins.Index(t);
            if (i >= 0) cov[i] = Math.Min(bins.Sec, cov[i] + interval);
        }
        return cov;
    }

    /// <summary>Виртуальные адаптеры (базовый драйвер Microsoft, удалённый рабочий стол) — не видеокарты.</summary>
    private static bool VirtualGpu(string name) =>
        name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase) || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase) || name.Contains("Indirect Display", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || name.Contains("Parsec", StringComparison.OrdinalIgnoreCase);

    public Timeline Timeline(int range)
    {
        long from = FromRange(range);
        var bins = new Bins(from, 720);
        int n = bins.Starts.Length;

        var cov = new double[n];
        var sysCpu = new double[n]; var sysMem = new double[n]; var sysN = new int[n];
        var cpu = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        var mem = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        var net = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        double[] Arr(Dictionary<string, double[]> d, string k) => d.TryGetValue(k, out var a) ? a : d[k] = new double[n];
        var gpuSum = new Dictionary<string, (double[] Sum, int[] N)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, path) in files.For(from))
        {
            using var c = DayFiles.Open(path);
            var memCol = c.MemColumn();
            Coverage(c, from, bins, cov);

            foreach (var r in c.Cmd("SELECT ts, cpu_pct, mem_load_pct FROM system_1m WHERE ts >= $from", ("$from", from)).Rows())
            {
                int i = bins.Index(r.GetInt64(0));
                if (i < 0) continue;
                sysCpu[i] += r.Dbl0(1); sysMem[i] += r.Dbl0(2); sysN[i]++;
            }

            // Видеокарты: одна карта может прийти под разными LUID (после перезагрузки) — сводим по имени.
            if (c.HasColumn("gpu_1m", "util"))
                foreach (var r in c.Cmd("SELECT ts, name, MAX(util) FROM gpu_1m WHERE ts >= $from GROUP BY ts, name", ("$from", from)).Rows())
                {
                    int i = bins.Index(r.GetInt64(0));
                    var gname = r.StrOrNull(1);
                    if (i < 0 || gname is null || VirtualGpu(gname)) continue;
                    if (!gpuSum.TryGetValue(gname, out var g)) gpuSum[gname] = g = (new double[n], new int[n]);
                    g.Sum[i] += r.Dbl0(2); g.N[i]++;
                }

            foreach (var r in c.Cmd($"""
                SELECT p.image, m.ts, SUM(m.cpu_ms), SUM(m.{memCol}), SUM(m.net_tx + m.net_rx)
                FROM proc_1m m JOIN process p ON p.id = m.proc_id
                WHERE m.ts >= $from GROUP BY p.image, m.ts
                """, ("$from", from)).Rows())
            {
                int i = bins.Index(r.GetInt64(1));
                if (i < 0) continue;
                var name = r.GetString(0);
                Arr(cpu, name)[i] += r.Long0(2);
                var m = Arr(mem, name);
                m[i] = Math.Max(m[i], r.Long0(3));
                Arr(net, name)[i] += r.Long0(4);
            }
        }

        // Перевод сумм в проценты и «байт за минуту» с учётом покрытия корзины.
        foreach (var a in cpu.Values)
            for (int i = 0; i < n; i++) a[i] = cov[i] > 0 ? a[i] / (cov[i] * 1000 * Cores) * 100 : 0;
        foreach (var a in net.Values)
            for (int i = 0; i < n; i++) a[i] = cov[i] > 0 ? a[i] / (cov[i] / 60) : 0;

        var keep = cpu.Keys.OrderByDescending(k => cpu[k].Sum()).Take(16)
            .Union(mem.Keys.OrderByDescending(k => mem[k].Max()).Take(8), StringComparer.OrdinalIgnoreCase)
            .Union(net.Keys.OrderByDescending(k => net[k].Sum()).Take(8), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var apps = keep.Select(k => new TimelineApp(k, Arr(cpu, k), Arr(mem, k), Arr(net, k))).ToList();
        var system = new TimelineSystem(
            Enumerable.Range(0, n).Select(i => sysN[i] > 0 ? sysCpu[i] / sysN[i] : (double?)null).ToArray(),
            Enumerable.Range(0, n).Select(i => sysN[i] > 0 ? sysMem[i] / sysN[i] : (double?)null).ToArray(),
            gpuSum.OrderByDescending(kv => kv.Value.Sum.Sum())
                .Select(kv => new TimelineGpu(kv.Key, Enumerable.Range(0, n).Select(i => kv.Value.N[i] > 0 ? Math.Round(kv.Value.Sum[i] / kv.Value.N[i], 1) : (double?)null).ToArray()))
                .ToList());

        return new Timeline(bins.Starts, system, apps);
    }

    // ── Сеть ─────────────────────────────────────────────────────────────

    /// <summary>IP → домен по DNS-ответам за сегодня и вчера: подписывать живые соединения.</summary>
    public Dictionary<string, string> RecentIpDomains()
    {
        var map = new Dictionary<string, string>();
        foreach (var (_, path) in files.For(Now() - 86400))
        {
            try
            {
                using var c = DayFiles.Open(path);
                foreach (var (ip, d) in IpToDomain(c)) map[ip] = d;
            }
            catch (SqliteException) { }
        }
        return map;
    }

    private static Dictionary<string, string> IpToDomain(SqliteConnection c)
    {
        var map = new Dictionary<string, string>();
        foreach (var r in c.Cmd("SELECT domain, results FROM dns WHERE results IS NOT NULL ORDER BY ts").Rows())
        {
            var domain = r.GetString(0);
            foreach (var part in r.GetString(1).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!IPAddress.TryParse(part, out var ip)) continue;
                if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                map[ip.ToString()] = domain;
            }
        }
        return map;
    }

    private static bool IsLoopback(string ip) =>
        IPAddress.TryParse(ip, out var a) && IPAddress.IsLoopback(a);

    private sealed class FlowAcc
    {
        public bool Known;
        public long Tx, Rx, Connects, First = long.MaxValue;
        public readonly SortedSet<int> Ports = new();
    }

    private readonly Dictionary<string, (DateTime Mtime, HashSet<string> Domains)> _oldDomains = new();

    private HashSet<string> DomainsBefore(DateOnly day)
    {
        lock (_oldDomains) return DomainsBeforeLocked(day);
    }

    private HashSet<string> DomainsBeforeLocked(DateOnly day)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files.OlderThan(day))
        {
            var mtime = File.GetLastWriteTimeUtc(f);
            if (!_oldDomains.TryGetValue(f, out var cached) || cached.Mtime != mtime)
            {
                using var c = DayFiles.Open(f);
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in c.Cmd("SELECT DISTINCT domain FROM dns UNION SELECT DISTINCT domain FROM conn_1m WHERE domain IS NOT NULL").Rows())
                    set.Add(r.GetString(0));
                cached = (mtime, set);
                _oldDomains[f] = cached;
            }
            all.UnionWith(cached.Domains);
        }
        return all;
    }

    public Network Network(int range)
    {
        long from = FromRange(range);
        var flows = new Dictionary<(string App, string Domain), FlowAcc>();
        var dnsOnly = new Dictionary<(string App, string Domain), long>();

        foreach (var (_, path) in files.For(from))
        {
            using var c = DayFiles.Open(path);
            var ipMap = IpToDomain(c);

            foreach (var r in c.Cmd("""
                SELECT p.image, c.remote_ip, c.domain, c.port, SUM(c.bytes_tx), SUM(c.bytes_rx), SUM(c.connects), MIN(c.ts)
                FROM conn_1m c JOIN process p ON p.id = c.proc_id
                WHERE c.ts >= $from GROUP BY p.image, c.remote_ip, c.port, c.domain
                """, ("$from", from)).Rows())
            {
                var ip = r.GetString(1);
                if (IsLoopback(ip)) continue;
                var domain = r.StrOrNull(2) ?? (ipMap.TryGetValue(ip, out var d) ? d : null);
                var key = (r.GetString(0), domain ?? ip);
                if (!flows.TryGetValue(key, out var f)) flows[key] = f = new FlowAcc();
                f.Known |= domain is not null;
                f.Ports.Add(r.GetInt32(3));
                f.Tx += r.Long0(4); f.Rx += r.Long0(5); f.Connects += r.Long0(6);
                f.First = Math.Min(f.First, r.GetInt64(7));
            }

            foreach (var r in c.Cmd("""
                SELECT p.image, d.domain, MIN(d.ts) FROM dns d JOIN process p ON p.id = d.proc_id
                WHERE d.ts >= $from GROUP BY 1, 2
                """, ("$from", from)).Rows())
            {
                var key = (r.GetString(0), r.GetString(1));
                long t = r.GetInt64(2);
                dnsOnly[key] = dnsOnly.TryGetValue(key, out var old) ? Math.Min(old, t) : t;
            }
        }

        var flowList = flows
            .Select(kv => new Flow(kv.Key.App, kv.Key.Domain, kv.Value.Known, kv.Value.Tx, kv.Value.Rx, kv.Value.Connects, kv.Value.Ports.ToArray()))
            .Where(f => f.Tx + f.Rx > 0 || f.Connects > 0)
            .OrderByDescending(f => f.Tx + f.Rx)
            .ToList();

        // Таблица адресов: потоки + домены, которые только запрашивались через DNS.
        var today = DateOnly.FromDateTime(DateTime.Now);
        long midnight = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue)).ToUnixTimeSeconds();
        var before = DomainsBefore(today);
        bool hasHistory = files.OlderThan(today).Any();

        var rows = new Dictionary<string, (long Tx, long Rx, SortedSet<string> Apps, long First)>(StringComparer.OrdinalIgnoreCase);
        void Add(string domain, string app, long tx, long rx, long first)
        {
            if (!rows.TryGetValue(domain, out var v)) v = (0, 0, new SortedSet<string>(StringComparer.OrdinalIgnoreCase), long.MaxValue);
            v.Apps.Add(app);
            rows[domain] = (v.Tx + tx, v.Rx + rx, v.Apps, Math.Min(v.First, first));
        }
        foreach (var (k, f) in flows) Add(k.Domain, k.App, f.Tx, f.Rx, f.First);
        foreach (var (k, t) in dnsOnly) Add(k.Domain, k.App, 0, 0, t);

        var domains = rows
            .Select(kv => new DomainRow(kv.Key, kv.Value.Tx, kv.Value.Rx, kv.Value.Apps.ToList(), kv.Value.First,
                NewToday: hasHistory && kv.Value.First >= midnight && !before.Contains(kv.Key)))
            .OrderByDescending(d => d.Tx + d.Rx)
            .Take(1000)
            .ToList();

        var totals = new NetworkTotals(
            flowList.Sum(f => f.Tx), flowList.Sum(f => f.Rx), domains.Count,
            flowList.Select(f => f.App).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        return new Network(flowList.Take(300).ToList(), domains, totals);
    }

    // ── Карточка программы ───────────────────────────────────────────────

    public AppDetail App(string name, int range)
    {
        long from = FromRange(range);
        var bins = new Bins(from, 360);
        int n = bins.Starts.Length;
        var cov = new double[n];
        double[] cpu = new double[n], mem = new double[n], tx = new double[n], rx = new double[n], io = new double[n];

        var procs = new Dictionary<(int, long), ProcRow>();
        var domains = new Dictionary<string, (long Tx, long Rx, long Connects)>(StringComparer.OrdinalIgnoreCase);
        string? exePath = null;
        var alive = aliveProcesses();
        var alivePids = alive.Select(a => a.Pid).ToHashSet();

        foreach (var (_, path) in files.For(from))
        {
            using var c = DayFiles.Open(path);
            var memCol = c.MemColumn();
            Coverage(c, from, bins, cov);
            var ipMap = IpToDomain(c);

            foreach (var r in c.Cmd($"""
                SELECT m.ts, SUM(m.cpu_ms), SUM(m.{memCol}), SUM(m.net_tx), SUM(m.net_rx), SUM(m.io_read + m.io_write)
                FROM proc_1m m JOIN process p ON p.id = m.proc_id
                WHERE p.image = $name COLLATE NOCASE AND m.ts >= $from GROUP BY m.ts
                """, ("$name", name), ("$from", from)).Rows())
            {
                int i = bins.Index(r.GetInt64(0));
                if (i < 0) continue;
                cpu[i] += r.Long0(1);
                mem[i] = Math.Max(mem[i], r.Long0(2));
                tx[i] += r.Long0(3); rx[i] += r.Long0(4); io[i] += r.Long0(5);
            }

            foreach (var r in c.Cmd("""
                SELECT p.pid, p.path, p.cmdline, p.create_time, p.first_seen, p.exited,
                       (SELECT q.image FROM process q WHERE q.pid = p.parent_pid AND q.first_seen <= p.first_seen
                        ORDER BY q.first_seen DESC LIMIT 1)
                FROM process p
                WHERE p.image = $name COLLATE NOCASE AND (p.exited IS NULL OR p.exited >= $from)
                ORDER BY p.first_seen DESC LIMIT 300
                """, ("$name", name), ("$from", from)).Rows())
            {
                int pid = r.GetInt32(0);
                long? create = r.LongOrNull(3);
                long started = create is { } ft ? FileTimeToUnix(ft) : r.GetInt64(4);
                long? exited = r.LongOrNull(5);

                // Агент мог не застать завершение (был остановлен) — сверяем с живыми процессами.
                if (exited is null)
                {
                    bool isAlive = create is { } ct ? alive.Contains((pid, ct)) : alivePids.Contains(pid);
                    if (!isAlive) exited = r.GetInt64(4);
                }

                exePath ??= r.StrOrNull(1);
                procs[(pid, started)] = new ProcRow(pid, r.StrOrNull(1), r.StrOrNull(2), r.StrOrNull(6), started, exited);
            }

            foreach (var r in c.Cmd("""
                SELECT c.remote_ip, c.domain, SUM(c.bytes_tx), SUM(c.bytes_rx), SUM(c.connects)
                FROM conn_1m c JOIN process p ON p.id = c.proc_id
                WHERE p.image = $name COLLATE NOCASE AND c.ts >= $from GROUP BY c.remote_ip, c.domain
                """, ("$name", name), ("$from", from)).Rows())
            {
                var ip = r.GetString(0);
                if (IsLoopback(ip)) continue;
                var d = r.StrOrNull(1) ?? (ipMap.TryGetValue(ip, out var x) ? x : ip);
                var v = domains.TryGetValue(d, out var old) ? old : default;
                domains[d] = (v.Tx + r.Long0(2), v.Rx + r.Long0(3), v.Connects + r.Long0(4));
            }
        }

        for (int i = 0; i < n; i++)
        {
            double k = cov[i];
            cpu[i] = k > 0 ? cpu[i] / (k * 1000 * Cores) * 100 : 0;
            tx[i] = k > 0 ? tx[i] / (k / 60) : 0;
            rx[i] = k > 0 ? rx[i] / (k / 60) : 0;
            io[i] = k > 0 ? io[i] / (k / 60) : 0;
        }

        exePath ??= LiveSampler.PathOfRunning(name);
        var (signature, signer) = signatures.Check(exePath);

        return new AppDetail(
            name, exePath, signer, signature,
            new AppSeries(bins.Starts, cpu, mem, tx, rx, io),
            procs.Values.OrderBy(p => p.Exited is null ? 0 : 1).ThenByDescending(p => p.Started).Take(200).ToList(),
            domains.Select(kv => new AppDomain(kv.Key, kv.Value.Tx, kv.Value.Rx, kv.Value.Connects))
                .OrderByDescending(d => d.Tx + d.Rx).Take(200).ToList());
    }

    // ── События ──────────────────────────────────────────────────────────

    public IReadOnlyList<SysEvent> Events(int range)
    {
        long from = FromRange(range);
        var list = new List<SysEvent>();
        var seenStarts = new HashSet<(int, long)>();
        var seenStops = new HashSet<(int, long)>();

        foreach (var (_, path) in files.For(from))
        {
            using var c = DayFiles.Open(path);

            foreach (var r in c.Cmd("""
                SELECT image, pid, create_time, first_seen, exited, COALESCE(cmdline, path)
                FROM process WHERE create_time IS NOT NULL OR exited IS NOT NULL
                """).Rows())
            {
                int pid = r.GetInt32(1);
                long started = r.LongOrNull(2) is { } ft ? FileTimeToUnix(ft) : r.GetInt64(3);
                if (started >= from && seenStarts.Add((pid, started)))
                    list.Add(new SysEvent(started, "start", r.GetString(0), pid, r.StrOrNull(5)));
                if (r.LongOrNull(4) is { } ex && ex >= from && seenStops.Add((pid, started)))
                    list.Add(new SysEvent(ex, "stop", r.GetString(0), pid, null));
            }

            foreach (var r in c.Cmd("""
                SELECT d.ts, p.image, p.pid, d.domain FROM dns d JOIN process p ON p.id = d.proc_id
                WHERE d.ts >= $from ORDER BY d.ts DESC LIMIT 3000
                """, ("$from", from)).Rows())
                list.Add(new SysEvent(r.GetInt64(0), "dns", r.GetString(1), r.GetInt32(2), r.GetString(3)));
        }

        return list.OrderByDescending(e => e.Ts).Take(2000).ToList();
    }

    // ── Статус агента ────────────────────────────────────────────────────

    private (long At, AgentStatus Status)? _agentCache;
    private readonly object _agentLock = new();

    public AgentStatus Agent()
    {
        lock (_agentLock) return AgentLocked();
    }

    private AgentStatus AgentLocked()
    {
        long now = Now();
        if (_agentCache is { } c && now - c.At < 5) return c.Status;

        AgentStatus status = new(false, null, null, null, null);
        var latest = files.For(now - 86400).LastOrDefault();
        if (latest.Path is not null)
        {
            try
            {
                using var conn = DayFiles.Open(latest.Path);
                foreach (var r in conn.Cmd("SELECT ts, cpu_pct, ws_bytes, events_lost FROM self_1m ORDER BY ts DESC LIMIT 1").Rows())
                {
                    long ts = r.GetInt64(0);
                    status = new AgentStatus(now - ts < 180, ts, r.Dbl0(1), r.Long0(2), r.Long0(3));
                }
            }
            catch (SqliteException) { /* файл занят или ещё пустой */ }
        }

        _agentCache = (now, status);
        return status;
    }

    private static Acc Get(Dictionary<string, Acc> d, string k) => d.TryGetValue(k, out var a) ? a : d[k] = new Acc();
}
