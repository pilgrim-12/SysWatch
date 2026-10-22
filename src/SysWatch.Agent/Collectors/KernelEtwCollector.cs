using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using SysWatch.Agent.Pipeline;

namespace SysWatch.Agent.Collectors;

/// <summary>
/// ETW ядра: запуск/завершение процессов и TCP/UDP-трафик по процессам.
/// Шумные провайдеры (FileIO, Registry, ContextSwitch) не включаются.
/// </summary>
public sealed class KernelEtwCollector(
    ProcessRegistry registry,
    Aggregator aggregator,
    EtwStats stats,
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
        k.ProcessStart += OnProcessStart;
        k.ProcessDCStart += OnProcessStart; // процессы, запущенные до старта агента
        k.ProcessStop += d =>
        {
            Interlocked.Increment(ref stats.KernelEvents);
            if (registry.OnStop(d.ProcessID) is { } p) aggregator.ProcStopped(p);
        };

        k.TcpIpConnect += d => Net(d.ProcessID, d.daddr, d.dport, Proto.Tcp, 0, 0, connect: true);
        k.TcpIpConnectIPV6 += d => Net(d.ProcessID, d.daddr, d.dport, Proto.Tcp, 0, 0, connect: true);
        k.TcpIpSend += d => Net(d.ProcessID, d.daddr, d.dport, Proto.Tcp, d.size, 0, connect: false);
        k.TcpIpSendIPV6 += d => Net(d.ProcessID, d.daddr, d.dport, Proto.Tcp, d.size, 0, connect: false);
        k.TcpIpRecv += d => Net(d.ProcessID, d.daddr, d.dport, Proto.Tcp, 0, d.size, connect: false);
        k.TcpIpRecvIPV6 += d => Net(d.ProcessID, d.daddr, d.dport, Proto.Tcp, 0, d.size, connect: false);
        k.UdpIpSend += d => Net(d.ProcessID, d.daddr, d.dport, Proto.Udp, d.size, 0, connect: false);
        k.UdpIpSendIPV6 += d => Net(d.ProcessID, d.daddr, d.dport, Proto.Udp, d.size, 0, connect: false);
        k.UdpIpRecv += d => Net(d.ProcessID, d.daddr, d.dport, Proto.Udp, 0, d.size, connect: false);
        k.UdpIpRecvIPV6 += d => Net(d.ProcessID, d.daddr, d.dport, Proto.Udp, 0, d.size, connect: false);
    }

    private void OnProcessStart(ProcessTraceData d)
    {
        Interlocked.Increment(ref stats.KernelEvents);
        if (d.ProcessID == _selfPid) return;

        var p = registry.OnStart(d.ProcessID, d.ParentID, d.ImageFileName, d.CommandLine);
        aggregator.ProcStarted(p);
    }

    private void Net(int pid, IPAddress ip, int port, Proto proto, long tx, long rx, bool connect)
    {
        Interlocked.Increment(ref stats.KernelEvents);
        if (pid == _selfPid || pid <= 0) return;

        aggregator.AddNet(registry.Get(pid), ip, port, proto, tx, rx, connect);
    }
}
