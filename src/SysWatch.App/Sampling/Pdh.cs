using System.Runtime.InteropServices;

namespace SysWatch.App.Sampling;

/// <summary>
/// Счётчики производительности через PDH с английскими именами:
/// работают на любой локали Windows (PerformanceCounter требует локализованные имена).
/// </summary>
internal sealed class Pdh : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    private readonly IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = new();

    public Pdh(params string[] paths)
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return;
        foreach (var p in paths)
            if (PdhAddEnglishCounter(_query, p, IntPtr.Zero, out var c) == 0) _counters[p] = c;
        PdhCollectQueryData(_query); // первый замер — база для счётчиков-скоростей
    }

    public void Collect()
    {
        if (_query != IntPtr.Zero) PdhCollectQueryData(_query);
    }

    public double Get(string path)
    {
        if (!_counters.TryGetValue(path, out var c)) return 0;
        return PdhGetFormattedCounterValue(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v) == 0 && v.CStatus == 0
            ? v.DoubleValue
            : 0;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
    }
}
