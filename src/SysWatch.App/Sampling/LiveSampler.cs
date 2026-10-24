using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using SysWatch.Agent.Interop;
using SysWatch.App.Data;

namespace SysWatch.App.Sampling;

/// <summary>
/// Живые данные для верхней полосы и экрана «Сейчас». Замер делается по запросу интерфейса
/// (раз в секунду), только пока окно открыто; агент в этом не участвует.
/// </summary>
public sealed class LiveSampler : IDisposable
{
    private const string DiskRead = @"\PhysicalDisk(_Total)\Disk Read Bytes/sec";
    private const string DiskWrite = @"\PhysicalDisk(_Total)\Disk Write Bytes/sec";

    private readonly object _gate = new();
    private readonly Pdh _pdh = new(DiskRead, DiskWrite);
    private readonly GpuLive _gpu = new();
    private readonly uint[] _pids = new uint[8192];
    private readonly char[] _buf = new char[1024];
    private readonly Dictionary<(int Pid, long Create), Prev> _prev = new();
    private readonly Dictionary<(int Pid, long Create), string> _names = new();
    private readonly int _selfPid = Environment.ProcessId;
    private static readonly int Cores = Environment.ProcessorCount;

    private long _lastTicks;
    private long _sysIdle, _sysKernel, _sysUser;
    private long _netRx, _netTx;
    private HashSet<(int, long)> _alive = new();

    private readonly record struct Prev(long Cpu, long IoR, long IoW);

    public Data.Live Sample(AgentStatus agent)
    {
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            double dt = _lastTicks == 0 ? 1 : Math.Max(0.05, Stopwatch.GetElapsedTime(_lastTicks, now).TotalSeconds);
            bool first = _lastTicks == 0;
            _lastTicks = now;

            _pdh.Collect();
            var (cpu, memUsed, memTotal) = SystemNumbers();
            var (rx, tx) = NetRates(dt, first);
            var apps = Processes(dt);

            return new Data.Live(
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                cpu, memUsed, memTotal,
                _pdh.Get(DiskRead), _pdh.Get(DiskWrite),
                rx, tx, apps, agent, Gpus());
        }
    }

    /// <summary>Пары (PID, время создания) живых процессов — чтобы отличать завершённые в истории.</summary>
    public HashSet<(int Pid, long Create)> Alive()
    {
        lock (_gate)
        {
            if (_alive.Count == 0) Processes(1);
            return new HashSet<(int, long)>(_alive);
        }
    }

    private (double Cpu, long Used, long Total) SystemNumbers()
    {
        double cpu = 0;
        if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            long dIdle = idle - _sysIdle, dTotal = (kernel - _sysKernel) + (user - _sysUser);
            if (_sysKernel != 0 && dTotal > 0) cpu = Math.Clamp((1.0 - (double)dIdle / dTotal) * 100, 0, 100);
            _sysIdle = idle; _sysKernel = kernel; _sysUser = user;
        }

        var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref mem)) return (cpu, 0, 1);
        return (cpu, (long)(mem.ullTotalPhys - mem.ullAvailPhys), (long)mem.ullTotalPhys);
    }

    private (double Rx, double Tx) NetRates(double dt, bool first)
    {
        long rx = 0, tx = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                var s = ni.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
            catch (NetworkInformationException) { }
        }

        double r = first || _netRx == 0 ? 0 : Math.Max(0, rx - _netRx) / dt;
        double t = first || _netTx == 0 ? 0 : Math.Max(0, tx - _netTx) / dt;
        _netRx = rx; _netTx = tx;
        return (r, t);
    }

    private List<LiveApp> Processes(double dt)
    {
        var groups = new Dictionary<string, (int Count, double Cpu, long Mem, double IoR, double IoW)>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<(int, long)>();

        if (Native.EnumProcesses(_pids, (uint)(_pids.Length * sizeof(uint)), out var bytes))
        {
            int count = (int)(bytes / sizeof(uint));
            for (int i = 0; i < count; i++)
            {
                int pid = (int)_pids[i];
                if (pid == 0) continue;

                var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
                if (h == IntPtr.Zero) continue;
                try
                {
                    if (!Native.GetProcessTimes(h, out var created, out _, out var k, out var u)) continue;
                    var key = (pid, created);
                    seen.Add(key);

                    if (!_names.TryGetValue(key, out var name))
                    {
                        name = pid == 4 ? "System" : ImageName(h) ?? $"pid {pid}";
                        _names[key] = name;
                    }

                    long cpu = k + u;
                    long mem = Native.GetProcessMemoryInfo(h, out var mc, Native.MemCountersSize) ? (long)mc.PagefileUsage : 0;
                    long ior = 0, iow = 0;
                    if (Native.GetProcessIoCounters(h, out var io)) { ior = (long)io.ReadTransferCount; iow = (long)io.WriteTransferCount; }

                    double cpuPct = 0, r = 0, w = 0;
                    if (_prev.TryGetValue(key, out var p))
                    {
                        cpuPct = Math.Max(0, cpu - p.Cpu) / 10_000.0 / (dt * 1000 * Cores) * 100;
                        r = Math.Max(0, ior - p.IoR) / dt;
                        w = Math.Max(0, iow - p.IoW) / dt;
                    }
                    _prev[key] = new Prev(cpu, ior, iow);

                    if (pid == _selfPid) continue; // само окно в списке не показываем
                    var g = groups.TryGetValue(name, out var v) ? v : default;
                    groups[name] = (g.Count + 1, g.Cpu + cpuPct, g.Mem + mem, g.IoR + r, g.IoW + w);
                }
                finally
                {
                    Native.CloseHandle(h);
                }
            }
        }

        foreach (var key in _prev.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _prev.Remove(key);
            _names.Remove(key);
        }
        _alive = seen;

        return groups
            .Select(kv => new LiveApp(kv.Key, kv.Value.Count, kv.Value.Cpu, kv.Value.Mem, kv.Value.IoR, kv.Value.IoW))
            .OrderByDescending(a => a.Cpu)
            .ToList();
    }

    private string? ImageName(IntPtr h)
    {
        uint size = (uint)_buf.Length;
        return Native.QueryFullProcessImageName(h, 0, _buf, ref size) && size > 0
            ? Path.GetFileName(new string(_buf, 0, (int)size))
            : null;
    }

    /// <summary>Пути exe всех запущенных сейчас процессов (для «последнего запуска» программ).</summary>
    public static HashSet<string> RunningPaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new uint[8192];
        var buf = new char[1024];
        if (!Native.EnumProcesses(ids, (uint)(ids.Length * sizeof(uint)), out var bytes)) return set;
        for (int i = 0; i < bytes / sizeof(uint); i++)
        {
            var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, ids[i]);
            if (h == IntPtr.Zero) continue;
            try
            {
                uint size = (uint)buf.Length;
                if (Native.QueryFullProcessImageName(h, 0, buf, ref size) && size > 0) set.Add(new string(buf, 0, (int)size));
            }
            finally { Native.CloseHandle(h); }
        }
        return set;
    }

    /// <summary>Путь к exe запущенной программы по имени (для карточки, если в истории пути нет).</summary>
    public static string? PathOfRunning(string image)
    {
        var name = Path.GetFileNameWithoutExtension(image);
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                try { return p.MainModule?.FileName; } catch { /* защищённый процесс */ }
            }
        }
        return null;
    }

    private IReadOnlyList<LiveGpu> Gpus()
    {
        try { return _gpu.Sample(); } catch (Exception) { return []; }
    }

    public void Dispose() { _pdh.Dispose(); _gpu.Dispose(); }
}
