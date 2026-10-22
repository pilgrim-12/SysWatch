using System.Collections.Concurrent;
using System.Net;

namespace SysWatch.Agent.Pipeline;

public sealed class ProcCounters
{
    public long CpuMs;
    public long WsMax;
    public long IoRead;
    public long IoWrite;
    public long NetTx;
    public long NetRx;

    public bool IsIdle => CpuMs == 0 && IoRead == 0 && IoWrite == 0 && NetTx == 0 && NetRx == 0;
}

public enum Proto : byte { Tcp = 6, Udp = 17 }

public readonly record struct ConnKey(ProcInfo Proc, IPAddress Ip, int Port, Proto Proto);

public sealed class ConnCounters
{
    public long Tx;
    public long Rx;
    public long Connects;
}

public sealed record DnsRecord(long Ts, ProcInfo Proc, string Domain, string? Results);

/// <summary>Всё, что накопилось за один интервал сброса (по умолчанию минута).</summary>
public sealed class MinuteBucket
{
    public long StartUnix { get; } = Clock.NowUnix();

    public readonly ConcurrentDictionary<ProcInfo, ProcCounters> Procs = new();
    public readonly ConcurrentDictionary<ConnKey, ConnCounters> Conns = new();
    public readonly ConcurrentQueue<DnsRecord> Dns = new();
    public readonly ConcurrentQueue<ProcInfo> Started = new();
    public readonly ConcurrentQueue<ProcInfo> Stopped = new();

    // Пишет только сэмплер (один поток).
    public double SysCpuSum;
    public double SysMemSum;
    public int SysSamples;
}

/// <summary>
/// Агрегация в памяти. Сырые события на диск не попадают:
/// в базу уходят только суммы за интервал.
/// </summary>
public sealed class Aggregator
{
    private MinuteBucket _current = new();

    /// <summary>Кэш «IP → домен» из DNS-ответов, чтобы подписывать соединения.</summary>
    public ConcurrentDictionary<IPAddress, string> IpToDomain { get; } = new();

    public MinuteBucket Current => Volatile.Read(ref _current);

    public MinuteBucket Swap() => Interlocked.Exchange(ref _current, new MinuteBucket());

    public void ProcStarted(ProcInfo p) => Current.Started.Enqueue(p);

    public void ProcStopped(ProcInfo p) => Current.Stopped.Enqueue(p);

    public void AddNet(ProcInfo p, IPAddress ip, int port, Proto proto, long tx, long rx, bool connect)
    {
        var b = Current;

        var pc = b.Procs.GetOrAdd(p, static _ => new ProcCounters());
        if (tx != 0) Interlocked.Add(ref pc.NetTx, tx);
        if (rx != 0) Interlocked.Add(ref pc.NetRx, rx);

        var cc = b.Conns.GetOrAdd(new ConnKey(p, ip, port, proto), static _ => new ConnCounters());
        if (tx != 0) Interlocked.Add(ref cc.Tx, tx);
        if (rx != 0) Interlocked.Add(ref cc.Rx, rx);
        if (connect) Interlocked.Increment(ref cc.Connects);
    }

    public void AddSample(ProcInfo p, long cpuMs, long workingSet, long ioRead, long ioWrite)
    {
        var pc = Current.Procs.GetOrAdd(p, static _ => new ProcCounters());
        if (cpuMs > 0) Interlocked.Add(ref pc.CpuMs, cpuMs);
        if (ioRead > 0) Interlocked.Add(ref pc.IoRead, ioRead);
        if (ioWrite > 0) Interlocked.Add(ref pc.IoWrite, ioWrite);
        if (workingSet > pc.WsMax) pc.WsMax = workingSet;
    }

    public void AddSystem(double cpuPct, double memLoadPct)
    {
        var b = Current;
        b.SysCpuSum += cpuPct;
        b.SysMemSum += memLoadPct;
        b.SysSamples++;
    }

    public void AddDns(ProcInfo p, string domain, string? results)
    {
        Current.Dns.Enqueue(new DnsRecord(Clock.NowUnix(), p, domain, results));
        if (results is null) return;

        if (IpToDomain.Count > 100_000) IpToDomain.Clear();

        foreach (var part in results.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IPAddress.TryParse(part, out var ip)) continue; // пропускаем "type: 5 cname"
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            IpToDomain[ip] = domain;
        }
    }
}
