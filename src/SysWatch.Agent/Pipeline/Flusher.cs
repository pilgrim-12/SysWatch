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
    Costs costs,
    IOptions<AgentOptions> options,
    ILogger<Flusher> log) : BackgroundService
{
    private readonly Process _self = Process.GetCurrentProcess();
    private TimeSpan _lastCpu;
    private long _lastTicks;
    private long _lastKernelEvents, _lastDnsEvents;
    private long _lastProc, _lastKernelCpu, _lastDnsCpu, _lastSampler, _lastWriter;

    /// <summary>Разбивка CPU агента за интервал, % от всех ядер.</summary>
    private readonly record struct Breakdown(double Kernel, double Dns, double Sampler, double Writer, double Other);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _lastCpu = _self.TotalProcessorTime;
        _lastProc = _lastCpu.Ticks;
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
        var bucket = aggregator.Swap(registry.Get);

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
        // Разбивка за интервал; запись этого интервала попадёт в следующий.
        var breakdown = Measure(cpu, elapsedMs);
        var self = new SelfSample(
            CpuPct: selfPct,
            WorkingSet: _self.WorkingSet64,
            GcHeap: GC.GetTotalMemory(false),
            KernelEvents: kernel - _lastKernelEvents,
            DnsEvents: dns - _lastDnsEvents,
            EventsLost: stats.EventsLost(),
            KernelPct: breakdown.Kernel, DnsPct: breakdown.Dns, SamplerPct: breakdown.Sampler,
            WriterPct: breakdown.Writer, OtherPct: breakdown.Other);
        _lastKernelEvents = kernel;
        _lastDnsEvents = dns;

        var sw = Stopwatch.StartNew();
        long w0 = Costs.CurrentThreadTime();
        try
        {
            store.Write(bucket, self, aggregator.IpToDomain);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Write failed");
        }
        Interlocked.Add(ref costs.Writer, Costs.CurrentThreadTime() - w0);
        sw.Stop();

        registry.Sweep();
        log.LogInformation("{Summary}", Summary(bucket, self, elapsedMs, sw.ElapsedMilliseconds, breakdown));
    }

    private Breakdown Measure(TimeSpan processCpu, double elapsedMs)
    {
        long proc = processCpu.Ticks; // тики TimeSpan = 100 нс
        long kernel = Costs.ThreadTime(costs.KernelThread), dns = Costs.ThreadTime(costs.DnsThread);
        long sampler = Interlocked.Read(ref costs.Sampler), writer = Interlocked.Read(ref costs.Writer);

        long dProc = proc - _lastProc, dK = kernel - _lastKernelCpu, dD = dns - _lastDnsCpu;
        long dS = sampler - _lastSampler, dW = writer - _lastWriter;
        _lastProc = proc; _lastKernelCpu = kernel; _lastDnsCpu = dns; _lastSampler = sampler; _lastWriter = writer;

        double denom = elapsedMs * 10_000 * Environment.ProcessorCount / 100.0; // 100 нс → % всех ядер
        if (denom <= 0) return default;
        return new Breakdown(dK / denom, dD / denom, dS / denom, dW / denom,
            Math.Max(0, dProc - dK - dD - dS - dW) / denom);
    }

    private string Summary(MinuteBucket b, SelfSample self, double elapsedMs, long writeMs, Breakdown c)
    {
        double cores = Environment.ProcessorCount;
        var sb = new StringBuilder();

        sb.AppendLine();
        sb.Append("── SysWatch ").Append(DateTime.Now.ToString("HH:mm:ss")).AppendLine(" ──────────────────────────");
        if (b.SysSamples > 0)
            sb.AppendLine($"Система:   CPU {b.SysCpuSum / b.SysSamples,5:F1}%   RAM {b.SysMemSum / b.SysSamples,4:F0}%");

        // Группируем по имени программы: 20 процессов chrome.exe → одна строка «chrome.exe ×20».
        var apps = b.Procs
            .GroupBy(x => x.Key.Image, StringComparer.OrdinalIgnoreCase)
            .Select(g => (
                Name: g.Key,
                Count: g.Count(),
                Cpu: g.Sum(x => x.Value.CpuMs),
                Ram: g.Sum(x => x.Value.WsMax),
                Tx: g.Sum(x => x.Value.NetTx),
                Rx: g.Sum(x => x.Value.NetRx)))
            .ToList();

        sb.AppendLine("Топ CPU:");
        foreach (var a in apps.OrderByDescending(x => x.Cpu).Take(5))
        {
            if (a.Cpu == 0) break;
            double pct = a.Cpu / (elapsedMs * cores) * 100.0;
            sb.AppendLine($"  {Label(a.Name, a.Count),-28} {pct,6:F2}%   RAM {a.Ram / 1048576,6} MB");
        }

        sb.AppendLine("Топ сеть (↑/↓):");
        foreach (var a in apps.OrderByDescending(x => x.Tx + x.Rx).Take(5))
        {
            if (a.Tx + a.Rx == 0) break;
            sb.AppendLine($"  {Label(a.Name, a.Count),-28} {Kb(a.Tx),9} / {Kb(a.Rx),9}");
        }

        sb.AppendLine($"Соединений: {b.Conns.Count}   DNS-запросов: {b.Dns.Count}   " +
                      $"новых процессов: {b.Started.Count}   завершилось: {b.Stopped.Count}");

        sb.Append($"Агент:     CPU {self.CpuPct:F2}%   RAM {self.WorkingSet / 1048576} MB   " +
                  $"GC {self.GcHeap / 1048576} MB   ETW {(self.KernelEvents + self.DnsEvents) / Math.Max(1, elapsedMs / 1000):F0} соб/с   " +
                  $"потеряно {self.EventsLost}   запись {writeMs} мс");
        sb.AppendLine();
        sb.Append($"  из них:  ETW ядро {c.Kernel:F2}%   DNS {c.Dns:F2}%   опрос процессов {c.Sampler:F2}%   " +
                  $"запись {c.Writer:F2}%   прочее (JIT, GC, таймеры) {c.Other:F2}%   ядер: {Environment.ProcessorCount}");

        return sb.ToString();
    }

    private static string Label(string name, int count)
    {
        var suffix = count > 1 ? $" ×{count}" : "";
        int max = 28 - suffix.Length;
        return (name.Length <= max ? name : name[..(max - 1)] + "…") + suffix;
    }

    private static string Kb(long bytes) => bytes < 1024 * 1024
        ? $"{bytes / 1024.0:F1} KB"
        : $"{bytes / 1048576.0:F1} MB";
}
