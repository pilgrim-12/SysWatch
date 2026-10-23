namespace SysWatch.App.Data;

// Зеркало src/ui/src/api.ts. Сериализуется в camelCase.

public sealed record AgentStatus(bool Running, long? LastWrite, double? Cpu, long? Ram, long? EventsLost);

public sealed record LiveApp(string Name, int Count, double Cpu, long Mem, double IoRead, double IoWrite);

public sealed record Live(
    long Ts, double Cpu, long MemUsed, long MemTotal,
    double DiskRead, double DiskWrite, double NetRx, double NetTx,
    IReadOnlyList<LiveApp> Apps, AgentStatus Agent);

public sealed record AppTotals(
    string Name, double CpuPct, long MemMax, long IoRead, long IoWrite, long NetTx, long NetRx, int Domains);

/// <summary>Из чего складывается расход процессора агентом, % всех ядер в среднем за период.</summary>
public sealed record AgentParts(double Kernel, double Dns, double Sampler, double Writer, double Other);

public sealed record Overview(IReadOnlyList<AppTotals> Apps, IReadOnlyList<double?> AgentCpu, IReadOnlyList<long> Minutes, AgentParts? AgentParts);

public sealed record TimelineApp(string Name, double[] Cpu, double[] Mem, double[] Net);

public sealed record TimelineSystem(double?[] Cpu, double?[] Mem);

public sealed record Timeline(long[] Minutes, TimelineSystem System, IReadOnlyList<TimelineApp> Apps);

public sealed record Flow(string App, string Domain, bool Known, long Tx, long Rx, long Connects, int[] Ports);

public sealed record DomainRow(string Domain, long Tx, long Rx, IReadOnlyList<string> Apps, long FirstSeen, bool NewToday);

public sealed record NetworkTotals(long Tx, long Rx, int Domains, int Apps);

public sealed record Network(IReadOnlyList<Flow> Flows, IReadOnlyList<DomainRow> Domains, NetworkTotals Totals);

public sealed record ProcRow(int Pid, string? Path, string? Cmdline, string? Parent, long Started, long? Exited);

public sealed record AppSeries(long[] Ts, double[] Cpu, double[] Mem, double[] NetTx, double[] NetRx, double[] Io);

public sealed record AppDomain(string Domain, long Tx, long Rx, long Connects);

public sealed record AppDetail(
    string Name, string? Path, string? Signer, string Signature,
    AppSeries Series, IReadOnlyList<ProcRow> Processes, IReadOnlyList<AppDomain> Domains);

public sealed record SysEvent(long Ts, string Kind, string App, int Pid, string? Detail);
