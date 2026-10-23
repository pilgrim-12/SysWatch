using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using SysWatch.Agent.Pipeline;

namespace SysWatch.Agent.Collectors;

/// <summary>
/// ETW ядра: запуск/завершение процессов и TCP/UDP-трафик по процессам.
/// Сетевые события — горячий путь (тысячи в секунду): разбираются без выделения памяти.
/// Шумные провайдеры (FileIO, Registry, ContextSwitch) не включаются.
/// </summary>
public sealed class KernelEtwCollector(
    ProcessRegistry registry,
    Aggregator aggregator,
    EtwStats stats,
    Costs costs,
    ILogger<KernelEtwCollector> log) : BackgroundService
{
    private const string SessionName = "SysWatch-Kernel";

    private const KernelTraceEventParser.Keywords Keywords =
        KernelTraceEventParser.Keywords.Process |
        KernelTraceEventParser.Keywords.NetworkTCPIP;

    private readonly int _selfPid = Environment.ProcessId;
    private TraceEventSession? _session;

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        ct.Register(() => _session?.Dispose());
        return Task.Factory.StartNew(() => Run(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Run(CancellationToken ct)
    {
        try
        {
            costs.KernelThread = Costs.OpenCurrentThread();
            _session = CreateSession();
            stats.KernelSession = _session;
            Subscribe(_session.Source.Kernel);

            log.LogInformation("ETW kernel session '{Name}' started", _session.SessionName);
            _session.Source.Process(); // блокирует поток до Dispose()
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "ETW kernel collector failed");
        }
    }

    private TraceEventSession CreateSession()
    {
        // На Windows 8+ ядро можно включать в именованной сессии (system logger mode),
        // чтобы не конфликтовать с другими инструментами за "NT Kernel Logger".
        TraceEventSession? session = null;
        try
        {
            session = new TraceEventSession(SessionName) { BufferSizeMB = 8, StopOnDispose = true };
            session.EnableKernelProvider(Keywords);
            return session;
        }
        catch (Exception ex)
        {
            session?.Dispose();
            log.LogWarning(ex, "Named kernel session failed, falling back to '{Name}'",
                KernelTraceEventParser.KernelSessionName);

            var fallback = new TraceEventSession(KernelTraceEventParser.KernelSessionName)
            {
                BufferSizeMB = 8,
                StopOnDispose = true,
            };
            fallback.EnableKernelProvider(Keywords);
            return fallback;
        }
    }

    private void Subscribe(KernelTraceEventParser k)
    {
        k.ProcessStart += d => OnProcessStart(d, isNew: true);
        // Rundown: процессы, запущенные до старта агента. Регистрируем, но не считаем «новыми».
        k.ProcessDCStart += d => OnProcessStart(d, isNew: false);
        k.ProcessStop += d =>
        {
            Interlocked.Increment(ref stats.KernelEvents);
            if (registry.OnStop(d.ProcessID) is { } p) aggregator.ProcStopped(p);
        };

        // Сетевые события приходят на каждый вызов send/recv — их десятки тысяч в секунду при загрузке файла.
        // Поэтому адрес читаем прямо из данных события, без создания IPAddress (см. Net ниже).
        k.TcpIpConnect += d => Net(d, d.ProcessID, d.dport, Proto.Tcp, 0, 0, connect: true, v6: false);
        k.TcpIpConnectIPV6 += d => Net(d, d.ProcessID, d.dport, Proto.Tcp, 0, 0, connect: true, v6: true);
        k.TcpIpSend += d => Net(d, d.ProcessID, d.dport, Proto.Tcp, d.size, 0, connect: false, v6: false);
        k.TcpIpSendIPV6 += d => Net(d, d.ProcessID, d.dport, Proto.Tcp, d.size, 0, connect: false, v6: true);
        k.TcpIpRecv += d => Net(d, d.ProcessID, d.dport, Proto.Tcp, 0, d.size, connect: false, v6: false);
        k.TcpIpRecvIPV6 += d => Net(d, d.ProcessID, d.dport, Proto.Tcp, 0, d.size, connect: false, v6: true);
        k.UdpIpSend += d => Net(d, d.ProcessID, d.dport, Proto.Udp, d.size, 0, connect: false, v6: false);
        k.UdpIpSendIPV6 += d => Net(d, d.ProcessID, d.dport, Proto.Udp, d.size, 0, connect: false, v6: true);
        k.UdpIpRecv += d => Net(d, d.ProcessID, d.dport, Proto.Udp, 0, d.size, connect: false, v6: false);
        k.UdpIpRecvIPV6 += d => Net(d, d.ProcessID, d.dport, Proto.Udp, 0, d.size, connect: false, v6: true);
    }

    private void OnProcessStart(ProcessTraceData d, bool isNew)
    {
        Interlocked.Increment(ref stats.KernelEvents);
        if (d.ProcessID == _selfPid) return;

        var p = registry.OnStart(d.ProcessID, d.ParentID, d.ImageFileName, d.CommandLine, isRundown: !isNew);
        if (isNew) aggregator.ProcStarted(p);
    }

    // Быстрый разбор: адрес назначения лежит в данных события со смещения 8 (после PID и size),
    // 4 байта для IPv4 и 16 для IPv6 — так же его читает сама TraceEvent. Первые события каждого вида
    // сверяем с медленным разбором; при любом расхождении навсегда переходим на медленный путь.
    private const int AddrOffset = 8;
    private const int Verify = 64;
    private readonly int[] _verified = new int[8]; // по видам событий: IPv4/IPv6 × TCP/UDP × connect
    private bool _fast = true;

    private void Net(TraceEvent d, int pid, int port, Proto proto, long tx, long rx, bool connect, bool v6)
    {
        Interlocked.Increment(ref stats.KernelEvents);
        if (pid == _selfPid || pid <= 0) return;

        if (_fast && d.EventDataLength >= AddrOffset + (v6 ? 16 : 4))
        {
            ulong hi = 0, lo;
            if (v6)
            {
                hi = (ulong)Marshal.ReadInt64(d.DataStart, AddrOffset);
                lo = (ulong)Marshal.ReadInt64(d.DataStart, AddrOffset + 8);
            }
            else
            {
                lo = (uint)Marshal.ReadInt32(d.DataStart, AddrOffset);
            }

            var key = new NetKey(pid, hi, lo, (ushort)port, proto, v6);
            int kind = (v6 ? 4 : 0) + (proto == Proto.Udp ? 2 : 0) + (connect ? 1 : 0);
            if (_verified[kind] >= Verify || Check(d, key, kind))
            {
                aggregator.AddNetFast(key, tx, rx, connect);
                return;
            }
        }

        aggregator.AddNet(registry.Get(pid), Address(d), port, proto, tx, rx, connect);
    }

    /// <summary>Сверить быстрый разбор с медленным; при расхождении выключить быстрый путь.</summary>
    private bool Check(TraceEvent d, NetKey key, int kind)
    {
        var slow = Address(d);
        var expected = slow.IsIPv4MappedToIPv6 ? slow.MapToIPv4() : slow;
        if (key.ToIp().Equals(expected))
        {
            _verified[kind]++;
            return true;
        }

        _fast = false;
        log.LogWarning("Fast ETW parsing mismatch ({Event}: {Fast} vs {Slow}); using slow path", d.EventName, key.ToIp(), expected);
        return false;
    }

    /// <summary>Медленный, но надёжный разбор адреса средствами TraceEvent.</summary>
    private static IPAddress Address(TraceEvent d) =>
        d.PayloadByName("daddr") as IPAddress ?? IPAddress.None;
}
