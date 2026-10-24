using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SysWatch.App.Data;

namespace SysWatch.App.Sampling;

/// <summary>
/// Загрузка видеокарт для верхней полосы — те же счётчики «GPU Engine», что у агента и Диспетчера задач.
/// Замер — по запросу окна, раз в секунду, пока оно открыто.
/// </summary>
internal sealed class GpuLive : IDisposable
{
    private const string EnginePath = @"\GPU Engine(*)\Utilization Percentage";
    private const string DedicatedPath = @"\GPU Adapter Memory(*)\Dedicated Usage";

    private static readonly Regex LuidRx = new(@"luid_(0x[0-9a-fA-F]+_0x[0-9a-fA-F]+)", RegexOptions.Compiled);
    private static readonly Regex EngRx = new(@"engtype_(.+)$", RegexOptions.Compiled);

    private readonly PdhArray _pdh = new();
    private readonly bool _ok;
    private Dictionary<string, (string Name, uint Vendor, long Mem)> _adapters = new();
    private DateTime _adaptersAt;

    public GpuLive()
    {
        try { _ok = _pdh.Open(EnginePath, DedicatedPath); } catch (Exception) { _ok = false; }
    }

    private void LoadAdapters()
    {
        var d = new Dictionary<string, (string, uint, long)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var dx = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\DirectX");
            if (dx is not null)
                foreach (var sub in dx.GetSubKeyNames())
                {
                    using var k = dx.OpenSubKey(sub);
                    if (k?.GetValue("AdapterLuid") is not long luid || k.GetValue("Description") is not string name) continue;
                    uint vendor = k.GetValue("VendorId") is int v ? (uint)v : 0;
                    long mem = k.GetValue("DedicatedVideoMemory") is long m ? m : k.GetValue("DedicatedVideoMemory") is int mi ? (uint)mi : 0;
                    d[$"0x{(uint)(luid >> 32):X8}_0x{(uint)luid:X8}"] = (name.Trim(), vendor, mem);
                }
        }
        catch (Exception) { /* без имён покажем «Видеокарта» */ }
        _adapters = d;
        _adaptersAt = DateTime.UtcNow;
    }

    private static bool Virtual(string name, uint vendor) =>
        vendor == 0x1414 ||
        name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase) || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase) || name.Contains("Indirect Display", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || name.Contains("Parsec", StringComparison.OrdinalIgnoreCase);

    private static string Norm(string luid) => luid.ToUpperInvariant().Replace("0X", "0x");

    public IReadOnlyList<LiveGpu> Sample()
    {
        if (!_ok || !_pdh.Collect()) return [];
        if (DateTime.UtcNow - _adaptersAt > TimeSpan.FromMinutes(5)) LoadAdapters();

        var byEngine = new Dictionary<(string Adapter, string Eng), double>();
        foreach (var (name, value) in _pdh.Array(EnginePath))
        {
            if (value <= 0) continue;
            var lm = LuidRx.Match(name);
            var em = EngRx.Match(name);
            if (!lm.Success || !em.Success) continue;
            var key = (Norm(lm.Groups[1].Value), em.Groups[1].Value);
            byEngine[key] = byEngine.GetValueOrDefault(key) + value;
        }
        var mem = new Dictionary<string, double>();
        foreach (var (name, value) in _pdh.Array(DedicatedPath))
        {
            var lm = LuidRx.Match(name);
            if (lm.Success) { var k = Norm(lm.Groups[1].Value); mem[k] = mem.GetValueOrDefault(k) + value; }
        }

        // LUID меняется после перезагрузки и бывает двойным у одной карты — объединяем по имени.
        var result = new Dictionary<string, (double Util, long Mem, long Total)>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _adapters.Keys)
        {
            var (name, vendor, total) = _adapters[key];
            if (Virtual(name, vendor)) continue;
            double util = Math.Min(100, byEngine.Where(kv => kv.Key.Adapter == key).Select(kv => kv.Value).DefaultIfEmpty(0).Max());
            long used = (long)mem.GetValueOrDefault(key);
            if (!byEngine.Keys.Any(k => k.Adapter == key) && !mem.ContainsKey(key)) continue; // адаптер не активен в этой загрузке
            var prev = result.GetValueOrDefault(name);
            result[name] = (Math.Max(prev.Util, util), Math.Max(prev.Mem, used), Math.Max(prev.Total, total));
        }
        return result
            .Select(kv => new LiveGpu(kv.Key, Math.Round(kv.Value.Util, 1), kv.Value.Mem, kv.Value.Total))
            .OrderByDescending(g => g.MemTotal)
            .ToList();
    }

    public void Dispose() => _pdh.Dispose();
}

/// <summary>PDH со счётчиками-шаблонами (*): значения всех экземпляров; английские имена работают на любой локали.</summary>
internal sealed class PdhArray : IDisposable
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
        PdhCollectQueryData(_q);
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
            _buf = Marshal.AllocHGlobal((int)_bufSize);
            size = _bufSize;
            status = PdhGetFormattedCounterArray(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, _buf);
        }
        if (status != 0) return list;
        int stride = Marshal.SizeOf<ItemDouble>();
        for (int i = 0; i < count; i++)
        {
            var item = Marshal.PtrToStructure<ItemDouble>(_buf + i * stride);
            if (item.CStatus != 0 && item.CStatus != 1) continue;
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
