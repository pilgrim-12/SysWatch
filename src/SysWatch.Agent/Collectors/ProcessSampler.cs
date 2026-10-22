using Microsoft.Extensions.Options;
using SysWatch.Agent.Interop;
using SysWatch.Agent.Pipeline;

namespace SysWatch.Agent.Collectors;

/// <summary>
/// Раз в N секунд снимает CPU, память и дисковый I/O по каждому процессу.
/// Буферы переиспользуются, исключения на защищённых процессах не бросаются.
/// </summary>
public sealed class ProcessSampler(
    ProcessRegistry registry,
    Aggregator aggregator,
    IOptions<AgentOptions> options,
    ILogger<ProcessSampler> log) : BackgroundService
{
    private readonly record struct Prev(long Cpu100ns, long IoRead, long IoWrite);

    private readonly uint[] _pids = new uint[8192];
    private readonly char[] _pathBuf = new char[1024];
    private readonly Dictionary<ProcInfo, Prev> _prev = new();
    private readonly HashSet<ProcInfo> _seen = new();
    private readonly List<ProcInfo> _dead = new();
    private readonly int _selfPid = Environment.ProcessId;

    private long _sysIdle, _sysKernel, _sysUser;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.SampleIntervalSeconds));
        using var timer = new PeriodicTimer(interval);
        try
        {
            SampleOnce();
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { SampleOnce(); }
                catch (Exception ex) { log.LogError(ex, "Sampling failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void SampleOnce()
    {
        SampleSystem();

        if (!Native.EnumProcesses(_pids, (uint)(_pids.Length * sizeof(uint)), out var bytes)) return;
        int count = (int)(bytes / sizeof(uint));
        _seen.Clear();

        for (int i = 0; i < count; i++)
        {
            int pid = (int)_pids[i];
            if (pid == 0 || pid == _selfPid) continue;

            var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (h == IntPtr.Zero) continue; // защищённый процесс — пропускаем без исключений

            try
            {
                if (!Native.GetProcessTimes(h, out var created, out _, out var kernel, out var user)) continue;

                var info = registry.ForSample(pid, created);
                if (info.ExePath is null || info.Image.StartsWith("pid ", StringComparison.Ordinal))
                    ResolvePath(h, info);

                long cpu = kernel + user;
                long ws = Native.GetProcessMemoryInfo(h, out var mem, Native.MemCountersSize) ? (long)mem.WorkingSetSize : 0;
                long ioR = 0, ioW = 0;
                if (Native.GetProcessIoCounters(h, out var io))
                {
                    ioR = (long)io.ReadTransferCount;
                    ioW = (long)io.WriteTransferCount;
                }

                _seen.Add(info);
                if (_prev.TryGetValue(info, out var p))
                {
                    aggregator.AddSample(info,
                        cpuMs: Math.Max(0, (cpu - p.Cpu100ns) / 10_000),
                        workingSet: ws,
                        ioRead: Math.Max(0, ioR - p.IoRead),
                        ioWrite: Math.Max(0, ioW - p.IoWrite));
                }
                else
                {
                    aggregator.AddSample(info, 0, ws, 0, 0); // первый замер — база для дельт
                }
                _prev[info] = new Prev(cpu, ioR, ioW);
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }

        // Забыть процессы, которых больше нет
        _dead.Clear();
        foreach (var key in _prev.Keys)
            if (!_seen.Contains(key)) _dead.Add(key);
        foreach (var key in _dead) _prev.Remove(key);
    }

    private void ResolvePath(IntPtr handle, ProcInfo info)
    {
        uint size = (uint)_pathBuf.Length;
        if (!Native.QueryFullProcessImageName(handle, 0, _pathBuf, ref size) || size == 0) return;

        var path = new string(_pathBuf, 0, (int)size);
        info.ExePath = path;
        if (info.Image.StartsWith("pid ", StringComparison.Ordinal))
            info.Image = Path.GetFileName(path);
    }

    private void SampleSystem()
    {
        if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            long dIdle = idle - _sysIdle, dKernel = kernel - _sysKernel, dUser = user - _sysUser;
            bool first = _sysKernel == 0;
            _sysIdle = idle; _sysKernel = kernel; _sysUser = user;

            long total = dKernel + dUser; // kernel включает idle
            double cpuPct = total > 0 ? (1.0 - (double)dIdle / total) * 100.0 : 0;

            var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
            double memPct = Native.GlobalMemoryStatusEx(ref mem) ? mem.dwMemoryLoad : 0;

            if (!first) aggregator.AddSystem(cpuPct, memPct);
        }
    }
}
