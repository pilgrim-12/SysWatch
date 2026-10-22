using System.Collections.Concurrent;

namespace SysWatch.Agent.Pipeline;

public static class Clock
{
    public static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

/// <summary>
/// Экземпляр процесса. Идентичность — ссылка на объект, а не PID:
/// Windows переиспользует PID, поэтому новый процесс с тем же PID получает новый ProcInfo.
/// </summary>
public sealed class ProcInfo(int pid, string image)
{
    public int Pid { get; } = pid;
    public string Image { get; set; } = image;
    public string? ExePath { get; set; }
    public string? CommandLine { get; set; }
    public int ParentPid { get; set; }

    /// <summary>FILETIME создания процесса; 0 — ещё неизвестно.</summary>
    public long CreateTime { get; set; }

    public long FirstSeenUnix { get; } = Clock.NowUnix();
    public long ExitedUnix { get; set; }

    public override string ToString() => $"{Image} ({Pid})";
}

public sealed class ProcessRegistry
{
    private readonly ConcurrentDictionary<int, ProcInfo> _byPid = new();

    /// <summary>Процесс стартовал (ETW ProcessStart) или уже был запущен (ProcessDCStart).</summary>
    public ProcInfo OnStart(int pid, int parentPid, string image, string? commandLine)
    {
        var info = new ProcInfo(pid, string.IsNullOrEmpty(image) ? $"pid {pid}" : image)
        {
            ParentPid = parentPid,
            CommandLine = commandLine,
        };
        _byPid[pid] = info;
        return info;
    }

    public ProcInfo? OnStop(int pid)
    {
        if (!_byPid.TryGetValue(pid, out var info)) return null;
        info.ExitedUnix = Clock.NowUnix();
        return info;
    }

    /// <summary>Для сетевых событий: вернуть известный процесс или завести заглушку.</summary>
    public ProcInfo Get(int pid) =>
        _byPid.GetOrAdd(pid, static id => new ProcInfo(id, $"pid {id}"));

    /// <summary>
    /// Для сэмплера: сверить время создания, чтобы поймать переиспользование PID,
    /// которое ETW мог пропустить (например, до старта агента).
    /// </summary>
    public ProcInfo ForSample(int pid, long createTime)
    {
        if (_byPid.TryGetValue(pid, out var info))
        {
            if (info.CreateTime == 0)
            {
                info.CreateTime = createTime;
                return info;
            }
            if (info.CreateTime == createTime) return info;
        }

        // Неизвестный процесс или PID переиспользован: имя сэмплер возьмёт из пути к exe.
        var fresh = new ProcInfo(pid, $"pid {pid}") { CreateTime = createTime };
        _byPid[pid] = fresh;
        return fresh;
    }

    /// <summary>Убрать завершившиеся процессы старше 2 минут.</summary>
    public void Sweep()
    {
        var now = Clock.NowUnix();
        foreach (var kv in _byPid)
        {
            if (kv.Value.ExitedUnix != 0 && now - kv.Value.ExitedUnix > 120)
                _byPid.TryRemove(kv);
        }
    }

    public int Count => _byPid.Count;
}
