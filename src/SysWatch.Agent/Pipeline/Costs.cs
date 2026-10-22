using System.Runtime.InteropServices;

namespace SysWatch.Agent.Pipeline;

/// <summary>
/// Где агент тратит процессор: время CPU по потокам и участкам кода.
/// Единицы — 100 нс (как у GetThreadTimes).
/// </summary>
public sealed class Costs
{
    public IntPtr KernelThread;
    public IntPtr DnsThread;
    public long Sampler;
    public long Writer;

    private const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool GetThreadTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

    /// <summary>Настоящий хэндл текущего потока, который можно читать из других потоков.</summary>
    public static IntPtr OpenCurrentThread() =>
        OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, GetCurrentThreadId());

    public static long ThreadTime(IntPtr h) =>
        h != IntPtr.Zero && GetThreadTimes(h, out _, out _, out var k, out var u) ? k + u : 0;

    /// <summary>CPU текущего потока — для замера синхронного участка кода.</summary>
    public static long CurrentThreadTime() => ThreadTime(GetCurrentThread());
}
