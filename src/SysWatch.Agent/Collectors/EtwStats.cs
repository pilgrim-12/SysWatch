using Microsoft.Diagnostics.Tracing.Session;

namespace SysWatch.Agent.Collectors;

/// <summary>Счётчики самих ETW-сессий: сколько событий пришло и сколько потеряно.</summary>
public sealed class EtwStats
{
    public long KernelEvents;
    public long DnsEvents;

    public TraceEventSession? KernelSession { get; set; }
    public TraceEventSession? DnsSession { get; set; }

    public long EventsLost()
    {
        long lost = 0;
        try { lost += KernelSession?.EventsLost ?? 0; } catch { /* сессия закрыта */ }
        try { lost += DnsSession?.EventsLost ?? 0; } catch { /* сессия закрыта */ }
        return lost;
    }
}
