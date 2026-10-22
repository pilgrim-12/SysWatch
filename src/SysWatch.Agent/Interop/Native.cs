using System.Runtime.InteropServices;

namespace SysWatch.Agent.Interop;

/// <summary>
/// Прямые вызовы Win32 вместо System.Diagnostics.Process:
/// без исключений на защищённых процессах и почти без аллокаций.
/// </summary>
internal static class Native
{
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_MODE_BACKGROUND_BEGIN = 0x00100000;

    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    public static readonly uint MemCountersSize = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    public static extern bool GetProcessIoCounters(IntPtr handle, out IO_COUNTERS counters);

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
    public static extern bool GetProcessMemoryInfo(IntPtr handle, out PROCESS_MEMORY_COUNTERS counters, uint cb);

    [DllImport("kernel32.dll", EntryPoint = "K32EnumProcesses")]
    public static extern bool EnumProcesses([Out] uint[] ids, uint cb, out uint bytesReturned);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    public static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, [Out] char[] buffer, ref uint size);

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(IntPtr handle, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr handle, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);

    public static bool EnterBackgroundMode() =>
        SetPriorityClass(GetCurrentProcess(), PROCESS_MODE_BACKGROUND_BEGIN);

    public static bool EnableEcoQoS()
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
        };
        return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state,
            (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
    }
}
