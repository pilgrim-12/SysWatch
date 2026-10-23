namespace SysWatch.App.Settings;

public sealed record AlertMessage(string Kind, string Title, string Text, string Hash);

/// <summary>
/// Уведомления по порогам: процессор или память выше порога N минут подряд, мало места, горячая видеокарта.
/// По каждому виду — не чаще раза в час, чтобы не надоедать.
/// </summary>
public sealed class Alerts(AppSettings settings)
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(1);
    private readonly Dictionary<string, DateTime> _last = new();
    private DateTime? _cpuSince, _memSince;

    /// <summary>Вызывается из трея каждые 5 секунд.</summary>
    public AlertMessage? Observe(double cpu, double mem, Func<string?> topCpuApp, Func<string?> topMemApp)
    {
        var a = settings.Alerts;
        if (!a.Enabled) { _cpuSince = _memSince = null; return null; }
        var now = DateTime.UtcNow;
        var hold = TimeSpan.FromMinutes(a.Minutes);

        _cpuSince = cpu >= a.CpuPct ? _cpuSince ?? now : null;
        _memSince = mem >= a.MemPct ? _memSince ?? now : null;

        if (_cpuSince is { } cs && now - cs >= hold && Due("cpu", now))
        {
            var app = topCpuApp();
            return new AlertMessage("cpu", L.T("Процессор загружен на {0:F0}%", cpu),
                app is null
                    ? L.T("Уже {0} мин подряд выше {1}%.", a.Minutes, a.CpuPct)
                    : L.T("Уже {0} мин подряд выше {1}%. Больше всех грузит {2}.", a.Minutes, a.CpuPct, app), "#now");
        }
        if (_memSince is { } ms && now - ms >= hold && Due("mem", now))
        {
            var app = topMemApp();
            return new AlertMessage("mem", L.T("Память занята на {0:F0}%", mem),
                app is null
                    ? L.T("Уже {0} мин подряд выше {1}%.", a.Minutes, a.MemPct)
                    : L.T("Уже {0} мин подряд выше {1}%. Больше всех занимает {2}.", a.Minutes, a.MemPct, app), "#now");
        }
        return null;
    }

    /// <summary>Место на системном диске (раз в час, вместе с остальными часовыми проверками).</summary>
    public AlertMessage? CheckDisk()
    {
        var a = settings.Alerts;
        if (!a.Enabled) return null;
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var d = new DriveInfo(root);
            if (!d.IsReady || d.TotalSize <= 0) return null;
            double freePct = 100.0 * d.AvailableFreeSpace / d.TotalSize;
            if (freePct >= a.DiskFreePct || !Due("disk", DateTime.UtcNow, TimeSpan.FromHours(12))) return null;
            return new AlertMessage("disk", L.T("Мало места на диске {0}", root.TrimEnd('\\')),
                L.T("Свободно {0:F0}% — меньше порога {1}%. Откройте раздел «Диск», чтобы освободить место.", freePct, a.DiskFreePct), "#disk");
        }
        catch (Exception) { return null; }
    }

    /// <summary>Температура видеокарты за последние минуты.</summary>
    public AlertMessage? CheckGpu(double? peakTemp)
    {
        var a = settings.Alerts;
        if (!a.Enabled || peakTemp is not double t || t < a.GpuTemp || !Due("gpu", DateTime.UtcNow)) return null;
        return new AlertMessage("gpu", L.T("Видеокарта нагрелась до {0:F0} °C", t),
            L.T("Порог — {0} °C. Проверьте охлаждение и что сейчас нагружает видеокарту.", a.GpuTemp), "#gpu");
    }

    private bool Due(string kind, DateTime now, TimeSpan? cooldown = null)
    {
        if (_last.TryGetValue(kind, out var last) && now - last < (cooldown ?? Cooldown)) return false;
        _last[kind] = now;
        return true;
    }
}
