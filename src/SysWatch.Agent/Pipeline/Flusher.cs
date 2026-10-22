using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using SysWatch.Agent.Collectors;
using SysWatch.Agent.Storage;

namespace SysWatch.Agent.Pipeline;

/// <summary>
/// Раз в интервал: забирает накопленный bucket, пишет его одной транзакцией,
/// измеряет собственную стоимость агента и печатает краткую сводку.
/// </summary>
public sealed class Flusher(
    Aggregator aggregator,
    ProcessRegistry registry,
    SqliteStore store,
    EtwStats stats,
    IOptions<AgentOptions> options,
    ILogger<Flusher> log) : BackgroundService
{
    private readonly Process _self = Process.GetCurrentProcess();
    private TimeSpan _lastCpu;
    private long _lastTicks;
    private long _lastKernelEvents, _lastDnsEvents;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _lastCpu = _self.TotalProcessorTime;
        _lastTicks = Stopwatch.GetTimestamp();

        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.FlushIntervalSeconds));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) FlushOnce();
        }
        catch (OperationCanceledException) { }

        FlushOnce(); // последний сброс при остановке
    }

    private void FlushOnce()
    {
        var bucket = aggregator.Swap();

        _self.Refresh();
        var cpu = _self.TotalProcessorTime;
        var now = Stopwatch.GetTimestamp();
        double elapsedMs = Stopwatch.GetElapsedTime(_lastTicks, now).TotalMilliseconds;
        double selfPct = elapsedMs > 0
            ? (cpu - _lastCpu).TotalMilliseconds / (elapsedMs * Environment.ProcessorCount) * 100.0
            : 0;
        _lastCpu = cpu;
        _lastTicks = now;

        long kernel = Interlocked.Read(ref stats.KernelEvents);
        long dns = Interlocked.Read(ref stats.DnsEvents);
        var self = new SelfSample(
            CpuPct: selfPct,
            WorkingSet: _self.WorkingSet64,
            GcHeap: GC.GetTotalMemory(false),
            KernelEvents: kernel - _lastKernelEvents,
            DnsEvents: dns - _lastDnsEvents,
            EventsLost: stats.EventsLost());
        _lastKernelEvents = kernel;
        _lastDnsEvents = dns;

        var sw = Stopwatch.StartNew();
        try
        {
            store.Write(bucket, self, aggregator.IpToDomain);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Write failed");
        }
        sw.Stop();

        registry.Sweep();
        log.LogInformation("{Summary}", Summary(bucket, self, elapsedMs, sw.ElapsedMilliseconds));
    }

    private string Summary(MinuteBucket b, SelfSample self, double elapsedMs, long writeMs)
    {
        double cores = Environment.ProcessorCount;
        var sb = new StringBuilder();

        sb.AppendLine();
        sb.Append("── SysWatch ").Append(DateTime.Now.ToString("HH:mm:ss")).AppendLine(" ──────────────────────────");
        if (b.SysSamples > 0)
            sb.AppendLine($"Система:   CPU {b.SysCpuSum / b.SysSamples,5:F1}%   RAM {b.SysMemSum / b.SysSamples,4:F0}%");

        sb.AppendLine("Топ CPU:");
        foreach (var (p, c) in b.Procs.OrderByDescending(x => x.Value.CpuMs).Take(5))
        {
            if (c.CpuMs == 0) break;
            double pct = c.CpuMs / (elapsedMs * cores) * 100.0;
            sb.AppendLine($"  {Trim(p.Image),-28} {pct,6:F2}%   RAM {c.WsMax / 1048576,6} MB");
        }

        sb.AppendLine("Топ сеть (↑/↓):");
        foreach (var (p, c) in b.Procs.OrderByDescending(x => x.Value.NetTx + x.Value.NetRx).Take(5))
        {
            if (c.NetTx + c.NetRx == 0) break;
            sb.AppendLine($"  {Trim(p.Image),-28} {Kb(c.NetTx),9} / {Kb(c.NetRx),9}");
        }

        sb.AppendLine($"Соединений: {b.Conns.Count}   DNS-запросов: {b.Dns.Count}   " +
                      $"новых процессов: {b.Started.Count}   завершилось: {b.Stopped.Count}");

        sb.Append($"Агент:     CPU {self.CpuPct:F2}%   RAM {self.WorkingSet / 1048576} MB   " +
                  $"GC {self.GcHeap / 1048576} MB   ETW {(self.KernelEvents + self.DnsEvents) / Math.Max(1, elapsedMs / 1000):F0} соб/с   " +
                  $"потеряно {self.EventsLost}   запись {writeMs} мс");

        return sb.ToString();
    }

    private static string Trim(string s) => s.Length <= 28 ? s : s[..27] + "…";

    private static string Kb(long bytes) => bytes < 1024 * 1024
        ? $"{bytes / 1024.0:F1} KB"
        : $"{bytes / 1048576.0:F1} MB";
}
