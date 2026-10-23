using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using SysWatch.Agent.Pipeline;

namespace SysWatch.Agent.Collectors;

/// <summary>Видеоадаптер: имя и объём памяти — из реестра DirectX, ключ — LUID, как в счётчиках «GPU Engine».</summary>
public sealed record GpuAdapter(string Key, string Name, uint VendorId, long DedicatedMemory);

/// <summary>
/// Раз в несколько секунд: загрузка видеокарты по типам движков (3D, видео, вычисления), занятая видеопамять,
/// загрузка и память по процессам — из счётчиков Windows «GPU Engine» (те же, что у Диспетчера задач).
/// Температура, мощность и вентилятор — от драйвера через D3DKMT, для NVIDIA при необходимости через nvidia-smi.
/// </summary>
public sealed class GpuSampler(
    ProcessRegistry registry,
    Aggregator aggregator,
    IOptions<AgentOptions> options,
    Costs costs,
    ILogger<GpuSampler> log) : BackgroundService
{
    private const string EnginePath = @"\GPU Engine(*)\Utilization Percentage";
    private const string AdapterDedicatedPath = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string AdapterSharedPath = @"\GPU Adapter Memory(*)\Shared Usage";
    private const string ProcessMemPath = @"\GPU Process Memory(*)\Dedicated Usage";

    private static readonly Regex LuidRx = new(@"luid_(0x[0-9a-fA-F]+_0x[0-9a-fA-F]+)", RegexOptions.Compiled);
    private static readonly Regex PidRx = new(@"^pid_(\d+)_", RegexOptions.Compiled);
    private static readonly Regex EngRx = new(@"engtype_(.+)$", RegexOptions.Compiled);

    private readonly Dictionary<string, GpuAdapter> _adapters = new();
    private readonly Dictionary<string, int> _perfType = new();    // найденный тип запроса D3DKMT для каждого адаптера
    private readonly HashSet<string> _noPerf = new();
    private int _tick;
    private (DateTime At, List<(double Temp, double Power, int Fan)> Rows)? _smi;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.Gpu) return;
        var interval = TimeSpan.FromSeconds(Math.Max(2, options.Value.GpuIntervalSeconds));

        using var pdh = new PdhQuery();
        if (!pdh.Open(EnginePath, AdapterDedicatedPath, AdapterSharedPath, ProcessMemPath))
        {
            log.LogInformation("GPU counters are not available on this system");
            return;
        }
        LoadAdapters();

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                long t0 = Costs.CurrentThreadTime();
                try { Sample(pdh); }
                catch (Exception ex) { log.LogWarning(ex, "GPU sampling failed"); }
                finally { Interlocked.Add(ref costs.Sampler, Costs.CurrentThreadTime() - t0); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void LoadAdapters()
    {
        try
        {
            using var dx = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\DirectX");
            if (dx is null) return;
            foreach (var sub in dx.GetSubKeyNames())
            {
                using var k = dx.OpenSubKey(sub);
                if (k?.GetValue("AdapterLuid") is not long luid || k.GetValue("Description") is not string name) continue;
                uint vendor = k.GetValue("VendorId") is int v ? (uint)v : 0;
                long mem = k.GetValue("DedicatedVideoMemory") is long m ? m : k.GetValue("DedicatedVideoMemory") is int mi ? (uint)mi : 0;
                var key = $"0x{(uint)(luid >> 32):X8}_0x{(uint)luid:X8}";
                _adapters[key] = new GpuAdapter(key, name.Trim(), vendor, mem);
            }
        }
        catch (Exception ex) { log.LogDebug(ex, "DirectX adapters registry"); }
    }

    private void Sample(PdhQuery pdh)
    {
        if (!pdh.Collect()) return;

        // Сумма по процессам для каждого (адаптер, тип движка) — так считает Диспетчер задач.
        var byEngine = new Dictionary<(string Adapter, string Eng), double>();
        var byPid = new Dictionary<(int Pid, string Adapter), Dictionary<string, double>>();
        foreach (var (name, value) in pdh.Array(EnginePath))
        {
            if (value <= 0) continue;
            var lm = LuidRx.Match(name);
            var em = EngRx.Match(name);
            var pm = PidRx.Match(name);
            if (!lm.Success || !em.Success) continue;
            var adapter = Norm(lm.Groups[1].Value);
            var eng = em.Groups[1].Value;
            byEngine[(adapter, eng)] = byEngine.GetValueOrDefault((adapter, eng)) + value;
            if (pm.Success && int.TryParse(pm.Groups[1].Value, out var pid))
            {
                if (!byPid.TryGetValue((pid, adapter), out var engs)) byPid[(pid, adapter)] = engs = new Dictionary<string, double>();
                engs[eng] = engs.GetValueOrDefault(eng) + value;
            }
        }

        var dedicated = Sum(pdh.Array(AdapterDedicatedPath));
        var shared = Sum(pdh.Array(AdapterSharedPath));
        var procMem = new Dictionary<(int, string), double>();
        foreach (var (name, value) in pdh.Array(ProcessMemPath))
        {
            var lm = LuidRx.Match(name);
            var pm = PidRx.Match(name);
            if (lm.Success && pm.Success && int.TryParse(pm.Groups[1].Value, out var pid) && value > 0)
                procMem[(pid, Norm(lm.Groups[1].Value))] = value;
        }

        var keys = dedicated.Keys.Concat(byEngine.Keys.Select(k => k.Adapter)).Distinct().ToList();
        var bucket = aggregator.Current;
        _tick++;

        foreach (var key in keys)
        {
            if (!_adapters.TryGetValue(key, out var a))
            {
                LoadAdapters();
                a = _adapters.GetValueOrDefault(key) ?? new GpuAdapter(key, "Видеоадаптер", 0, 0);
                _adapters[key] = a;
            }
            if (a.VendorId == 0x1414) continue; // Microsoft Basic Render Driver — программный, не видеокарта

            double Eng(Func<string, bool> pick) => Math.Min(100, byEngine.Where(kv => kv.Key.Adapter == key && pick(kv.Key.Eng)).Select(kv => kv.Value).DefaultIfEmpty(0).Max());
            double util = Eng(_ => true);
            double util3d = Eng(e => e.Equals("3D", StringComparison.OrdinalIgnoreCase) || e.StartsWith("Graphics", StringComparison.OrdinalIgnoreCase));
            double video = Eng(e => e.Contains("Video", StringComparison.OrdinalIgnoreCase) || e.Contains("Codec", StringComparison.OrdinalIgnoreCase));
            double compute = Eng(e => e.StartsWith("Compute", StringComparison.OrdinalIgnoreCase) || e.Equals("Cuda", StringComparison.OrdinalIgnoreCase));

            var (temp, power, fan) = Sensors(a);

            var acc = bucket.Gpu.GetOrAdd(key, _ => new GpuAcc { Name = a.Name, MemTotal = a.DedicatedMemory });
            acc.Samples++;
            acc.Util += util; acc.UtilMax = Math.Max(acc.UtilMax, util);
            acc.Util3D += util3d; acc.UtilVideo += video; acc.UtilCompute += compute;
            acc.MemDedicated = Math.Max(acc.MemDedicated, (long)dedicated.GetValueOrDefault(key));
            acc.MemShared = Math.Max(acc.MemShared, (long)shared.GetValueOrDefault(key));
            if (temp is { } t) { acc.TempSum += t; acc.TempN++; acc.TempMax = Math.Max(acc.TempMax, t); }
            if (power is { } p) { acc.PowerSum += p; acc.PowerN++; }
            if (fan is { } f) acc.Fan = Math.Max(acc.Fan, f);
        }

        // Процессы: загрузка = максимум по типам движков (как столбец «ГП» в Диспетчере задач).
        foreach (var ((pid, adapter), engs) in byPid)
        {
            double u = Math.Min(100, engs.Values.Max());
            if (u < 0.05) continue;
            var proc = registry.Get(pid);
            var pacc = bucket.GpuProcs.GetOrAdd((proc, adapter), _ => new GpuProcAcc());
            pacc.UtilSum += u; pacc.Samples++;
        }
        foreach (var ((pid, adapter), mem) in procMem)
        {
            if (mem < 16 * 1048576) continue; // меньше 16 МБ видеопамяти — шум
            var pacc = bucket.GpuProcs.GetOrAdd((registry.Get(pid), adapter), _ => new GpuProcAcc());
            pacc.MemMax = Math.Max(pacc.MemMax, (long)mem);
        }
    }

    private static string Norm(string luid) => luid.ToUpperInvariant().Replace("0X", "0x");

    private static Dictionary<string, double> Sum(IEnumerable<(string Name, double Value)> rows)
    {
        var d = new Dictionary<string, double>();
        foreach (var (name, value) in rows)
        {
            var m = LuidRx.Match(name);
            if (m.Success) { var k = Norm(m.Groups[1].Value); d[k] = d.GetValueOrDefault(k) + value; }
        }
        return d;
    }

    // ── Датчики ──────────────────────────────────────────────────────────

    private (double? Temp, double? Power, int? Fan) Sensors(GpuAdapter a)
    {
        if (!_noPerf.Contains(a.Key) && D3dkmt.Query(a.Key, _perfType, out var perf))
        {
            double? temp = perf.Temperature is > 0 and < 1500 ? perf.Temperature / 10.0 : null;
            double? power = perf.Power is > 0 and <= 2000 ? perf.Power / 10.0 : null;
            int? fan = perf.FanRPM is > 0 and < 20000 ? (int)perf.FanRPM : null;
            if (temp is not null) return (temp, power, fan);
        }
        else if (!_perfType.ContainsKey(a.Key))
        {
            _noPerf.Add(a.Key); // драйвер не отдаёт данные — больше не спрашиваем
        }

        // NVIDIA без данных от драйвера через D3DKMT: nvidia-smi, не чаще раза в 30 секунд.
        if (a.VendorId == 0x10DE)
        {
            if (_smi is null || DateTime.UtcNow - _smi.Value.At > TimeSpan.FromSeconds(30)) _smi = (DateTime.UtcNow, NvidiaSmi());
            var nvidia = _adapters.Values.Where(x => x.VendorId == 0x10DE).Select(x => x.Key).OrderBy(x => x).ToList();
            int idx = nvidia.IndexOf(a.Key);
            if (_smi.Value.Rows.Count > Math.Max(0, idx))
            {
                var r = _smi.Value.Rows[Math.Max(0, idx)];
                return (r.Temp > 0 ? r.Temp : null, null, r.Fan > 0 ? r.Fan : null);
            }
        }
        return (null, null, null);
    }

    private List<(double, double, int)> NvidiaSmi()
    {
        var list = new List<(double, double, int)>();
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (!File.Exists(exe)) return list;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "--query-gpu=temperature.gpu,power.draw,fan.speed --format=csv,noheader,nounits")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            })!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',').Select(x => x.Trim()).ToArray();
                double.TryParse(parts.ElementAtOrDefault(0), NumberStyles.Float, CultureInfo.InvariantCulture, out var t);
                double.TryParse(parts.ElementAtOrDefault(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var w);
                int.TryParse(parts.ElementAtOrDefault(2), out var fanPct);
                list.Add((t, w, fanPct > 0 ? fanPct : 0));
            }
        }
        catch (Exception ex) { log.LogDebug(ex, "nvidia-smi"); }
        return list;
    }
}

/// <summary>Накопитель за минуту по одному адаптеру. Пишет только поток GpuSampler.</summary>
public sealed class GpuAcc
{
    public string Name = "";
    public long MemTotal;
    public int Samples;
    public double Util, UtilMax, Util3D, UtilVideo, UtilCompute;
    public long MemDedicated, MemShared;
    public double TempSum, TempMax, PowerSum;
    public int TempN, PowerN, Fan;
}

public sealed class GpuProcAcc
{
    public double UtilSum;
    public int Samples;
    public long MemMax;
}

/// <summary>
/// Данные драйвера о видеокарте (температура в десятых °C, мощность в десятых % TDP, обороты вентилятора) —
/// тот же источник, что у Диспетчера задач. Номер типа запроса в заголовках WDK не задокументирован публично,
/// поэтому один раз подбираем его и проверяем, что значения правдоподобны.
/// </summary>
internal static class D3dkmt
{
    [StructLayout(LayoutKind.Sequential)]
    private struct OpenFromLuid { public uint LowPart; public int HighPart; public uint hAdapter; }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryInfo { public uint hAdapter; public uint Type; public IntPtr pData; public uint Size; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapter { public uint hAdapter; }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct PerfData
    {
        [FieldOffset(0)] public uint PhysicalAdapterIndex;
        [FieldOffset(8)] public ulong MemoryFrequency;
        [FieldOffset(16)] public ulong MaxMemoryFrequency;
        [FieldOffset(24)] public ulong MaxMemoryFrequencyOC;
        [FieldOffset(32)] public ulong MemoryBandwidth;
        [FieldOffset(40)] public ulong PCIEBandwidth;
        [FieldOffset(48)] public uint FanRPM;
        [FieldOffset(52)] public uint Power;
        [FieldOffset(56)] public uint Temperature;
        [FieldOffset(60)] public byte PowerStateOverride;
    }

    [DllImport("gdi32.dll")] private static extern int D3DKMTOpenAdapterFromLuid(ref OpenFromLuid p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryInfo p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CloseAdapter p);

    private static readonly int[] Candidates = Enumerable.Range(55, 40).ToArray();

    /// <param name="key">LUID в виде 0xHIGH_0xLOW.</param>
    public static bool Query(string key, Dictionary<string, int> types, out PerfData data)
    {
        data = default;
        var parts = key.Split('_');
        if (parts.Length != 2) return false;
        var open = new OpenFromLuid
        {
            HighPart = (int)Convert.ToUInt32(parts[0], 16),
            LowPart = Convert.ToUInt32(parts[1], 16),
        };
        try
        {
            if (D3DKMTOpenAdapterFromLuid(ref open) != 0) return false;
        }
        catch (Exception) { return false; } // старая Windows без этих функций

        try
        {
            if (types.TryGetValue(key, out var known)) return Call(open.hAdapter, known, out data) && Plausible(data);

            foreach (var t in Candidates)
            {
                if (Call(open.hAdapter, t, out var d) && Plausible(d) && (d.Temperature > 0 || d.MemoryFrequency > 0))
                {
                    types[key] = t;
                    data = d;
                    return true;
                }
            }
            return false;
        }
        finally
        {
            var close = new CloseAdapter { hAdapter = open.hAdapter };
            D3DKMTCloseAdapter(ref close);
        }
    }

    private static bool Call(uint adapter, int type, out PerfData data)
    {
        data = default;
        var buf = Marshal.AllocHGlobal(64);
        try
        {
            for (int i = 0; i < 64; i++) Marshal.WriteByte(buf, i, 0);
            var q = new QueryInfo { hAdapter = adapter, Type = (uint)type, pData = buf, Size = 64 };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return false;
            data = Marshal.PtrToStructure<PerfData>(buf);
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static bool Plausible(PerfData d) =>
        d.PhysicalAdapterIndex == 0 && d.Temperature < 1500 && d.Power <= 2000 && d.FanRPM < 20000 && d.PowerStateOverride <= 1 &&
        (d.MemoryFrequency == 0 || d.MemoryFrequency is > 50_000_000 and < 50_000_000_000) &&
        (d.MaxMemoryFrequency == 0 || d.MaxMemoryFrequency >= d.MemoryFrequency);
}

/// <summary>PDH со счётчиками-шаблонами (*): значения всех экземпляров, английские имена работают на любой локали.</summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x00000200, PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    private struct ItemDouble { public IntPtr Name; public uint CStatus; public uint Pad; public double Value; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? src, IntPtr user, out IntPtr q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")] private static extern uint PdhAddEnglishCounter(IntPtr q, string path, IntPtr user, out IntPtr c);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(IntPtr c, uint fmt, ref uint size, out uint count, IntPtr items);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr q);

    private IntPtr _q;
    private readonly Dictionary<string, IntPtr> _c = new();
    private IntPtr _buf;
    private uint _bufSize;

    public bool Open(params string[] paths)
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _q) != 0) return false;
        foreach (var p in paths)
            if (PdhAddEnglishCounter(_q, p, IntPtr.Zero, out var c) == 0) _c[p] = c;
        if (_c.Count == 0) return false;
        PdhCollectQueryData(_q); // база для счётчиков-скоростей
        return true;
    }

    public bool Collect() => _q != IntPtr.Zero && PdhCollectQueryData(_q) == 0;

    public List<(string Name, double Value)> Array(string path)
    {
        var list = new List<(string, double)>();
        if (!_c.TryGetValue(path, out var c)) return list;

        uint size = _bufSize;
        uint status = PdhGetFormattedCounterArray(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out var count, _buf);
        if (status == PDH_MORE_DATA)
        {
            if (_buf != IntPtr.Zero) Marshal.FreeHGlobal(_buf);
            _bufSize = size + 4096;
            _buf = Marshal.AllocHGlobal((int)_bufSize); // буфер переиспользуем, растёт только при росте числа экземпляров
            size = _bufSize;
            status = PdhGetFormattedCounterArray(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, _buf);
        }
        if (status != 0) return list;

        int stride = Marshal.SizeOf<ItemDouble>();
        for (int i = 0; i < count; i++)
        {
            var item = Marshal.PtrToStructure<ItemDouble>(_buf + i * stride);
            if (item.CStatus != 0 && item.CStatus != 1) continue; // 0 — новые данные, 1 — валидные
            list.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value));
        }
        return list;
    }

    public void Dispose()
    {
        if (_q != IntPtr.Zero) PdhCloseQuery(_q);
        if (_buf != IntPtr.Zero) Marshal.FreeHGlobal(_buf);
        _q = IntPtr.Zero; _buf = IntPtr.Zero;
    }
}
