using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using SysWatch.Agent.Pipeline;

namespace SysWatch.Agent.Collectors;

/// <summary>
/// ETW-провайдер Microsoft-Windows-DNS-Client: какой процесс какой домен разрешил.
/// Ответы попадают в кэш «IP → домен», так соединения подписываются доменами.
/// </summary>
public sealed class DnsEtwCollector(
    ProcessRegistry registry,
    Aggregator aggregator,
    EtwStats stats,
    Costs costs,
    ILogger<DnsEtwCollector> log) : BackgroundService
{
    private const string SessionName = "SysWatch-DNS";
    private const string Provider = "Microsoft-Windows-DNS-Client";
    private const int QueryCompletedEventId = 3008;

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
            costs.DnsThread = Costs.OpenCurrentThread();
            _session = new TraceEventSession(SessionName) { BufferSizeMB = 2, StopOnDispose = true };
            stats.DnsSession = _session;

            _session.Source.Dynamic.All += OnEvent;
            // Нужен только «запрос завершён» (3008). Остальные события клиента DNS (их около десятка на каждый
            // запрос) отсекаются ещё в ядре и не доходят до разбора — это главный расход DNS-сессии.
            _session.EnableProvider(Provider, TraceEventLevel.Informational, ulong.MaxValue,
                new TraceEventProviderOptions { EventIDsToEnable = [QueryCompletedEventId] });

            log.LogInformation("ETW DNS session '{Name}' started", SessionName);
            _session.Source.Process();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "ETW DNS collector failed");
        }
    }

    private void OnEvent(TraceEvent e)
    {
        if ((int)e.ID != QueryCompletedEventId) return;
        Interlocked.Increment(ref stats.DnsEvents);
        if (e.ProcessID == _selfPid) return;

        if (e.PayloadByName("QueryName") is not string name || name.Length == 0) return;
        var domain = name.TrimEnd('.').ToLowerInvariant();
        var results = e.PayloadByName("QueryResults") as string;

        aggregator.AddDns(registry.Get(e.ProcessID), domain, string.IsNullOrEmpty(results) ? null : results);
    }
}
