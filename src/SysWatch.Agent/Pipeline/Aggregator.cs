using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;

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

/// <summary>
/// Ключ сетевого счётчика без выделения памяти: PID, адрес (IPv4 — в Lo, IPv6 — Hi+Lo), порт, протокол.
/// Нужен для горячего пути ETW: на каждое событие TCP/UDP не создаём ни IPAddress, ни других объектов.
/// </summary>
public readonly record struct NetKey(int Pid, ulong Hi, ulong Lo, ushort Port, Proto Proto, bool V6)
{
    public IPAddress ToIp()
    {
        if (!V6) return new IPAddress((long)(uint)Lo);
        Span<byte> b = stackalloc byte[16];
        BitConverter.TryWriteBytes(b, Hi);
        BitConverter.TryWriteBytes(b[8..], Lo);
        var ip = new IPAddress(b);
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }
}

public struct NetVal
{
    public long Tx, Rx, Connects;
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

    // Видеокарта: по адаптерам и по процессам. Пишет только GpuSampler.
    public readonly ConcurrentDictionary<string, Collectors.GpuAcc> Gpu = new();
    public readonly ConcurrentDictionary<(ProcInfo Proc, string Adapter), Collectors.GpuProcAcc> GpuProcs = new();

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

    // Сетевые события копятся в обычном словаре под одной блокировкой (пишет один поток ETW),
    // а в объекты процессов и адресов превращаются только раз в минуту при сбросе.
    private readonly object _netGate = new();
    private Dictionary<NetKey, NetVal> _net = new(4096);
    private Dictionary<NetKey, NetVal> _netSpare = new(4096);

    public MinuteBucket Current => Volatile.Read(ref _current);

    /// <summary>Забрать накопленное за интервал. procOf — процесс по PID на момент сброса.</summary>
    public MinuteBucket Swap(Func<int, ProcInfo> procOf)
    {
        var old = Interlocked.Exchange(ref _current, new MinuteBucket());

        Dictionary<NetKey, NetVal> net;
        lock (_netGate)
        {
            net = _net;
            _net = _netSpare;
        }

        foreach (var (k, v) in net)
            AddNetTo(old, procOf(k.Pid), k.ToIp(), k.Port, k.Proto, v.Tx, v.Rx, v.Connects);

        net.Clear();
        if (net.EnsureCapacity(0) > 65536) net = new Dictionary<NetKey, NetVal>(4096); // не держим огромный словарь после всплеска
        lock (_netGate) _netSpare = net;
        return old;
    }

    /// <summary>Горячий путь: одно событие TCP/UDP. Без выделений памяти.</summary>
    public void AddNetFast(in NetKey key, long tx, long rx, bool connect)
    {
        lock (_netGate)
        {
            ref var v = ref CollectionsMarshal.GetValueRefOrAddDefault(_net, key, out _);
            v.Tx += tx;
            v.Rx += rx;
            if (connect) v.Connects++;
        }
    }

    private static void AddNetTo(MinuteBucket b, ProcInfo p, IPAddress ip, int port, Proto proto, long tx, long rx, long connects)
    {
        var pc = b.Procs.GetOrAdd(p, static _ => new ProcCounters());
        if (tx != 0) Interlocked.Add(ref pc.NetTx, tx);
        if (rx != 0) Interlocked.Add(ref pc.NetRx, rx);

        var cc = b.Conns.GetOrAdd(new ConnKey(p, ip, port, proto), static _ => new ConnCounters());
        if (tx != 0) Interlocked.Add(ref cc.Tx, tx);
        if (rx != 0) Interlocked.Add(ref cc.Rx, rx);
        if (connects != 0) Interlocked.Add(ref cc.Connects, connects);
    }

    public void ProcStarted(ProcInfo p) => Current.Started.Enqueue(p);

    public void ProcStopped(ProcInfo p) => Current.Stopped.Enqueue(p);

    /// <summary>Медленный путь (запасной, если быстрый разбор события недоступен).</summary>
    public void AddNet(ProcInfo p, IPAddress ip, int port, Proto proto, long tx, long rx, bool connect) =>
        AddNetTo(Current, p, ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip, port, proto, tx, rx, connect ? 1 : 0);

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
