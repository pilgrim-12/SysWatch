using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Xml.Linq;
using SysWatch.App.Sampling;

namespace SysWatch.App.Sys;

public sealed record DiskHealth(
    string Name, string Media, string Bus, long Size, string Health,
    int? Wear, int? Temperature, int? TemperatureMax, long? PowerOnHours,
    long? ReadErrors, long? WriteErrors, long? StartStops);

public sealed record BatteryInfo(
    bool Present, int? Charge, bool? OnAc, long? DesignCapacity, long? FullCapacity, int? Cycles,
    long? DischargeRate, long? ChargeRate, int? WearPct, int? MinutesLeft);

public sealed record WakeEvent(long Ts, long? SleepTs, string Source, string? SourceType);

public sealed record WakeInfo(IReadOnlyList<WakeEvent> Events, IReadOnlyList<string> ArmedDevices, IReadOnlyList<string> Timers);

public sealed record CpuInfo(
    string Name, int Cores, int Threads, double BaseMhz, double CurrentMhz, double PerformancePct,
    double LimitPct, bool Throttled, IReadOnlyList<ThermalZone> Thermal);

public sealed record ThermalZone(string Name, double Celsius);

/// <summary>Железо: здоровье дисков, батарея, пробуждения, частота и температура процессора.</summary>
public sealed class Hardware : IDisposable
{
    private const string Freq = @"\Processor Information(_Total)\Processor Frequency";
    private const string Perf = @"\Processor Information(_Total)\% Processor Performance";
    private const string Limit = @"\Processor Information(_Total)\% Performance Limit";

    private readonly object _gate = new();
    private Pdh? _pdh;
    private string? _cpuName;
    private (int Cores, int Threads)? _cpuCount;

    // ── Диски ────────────────────────────────────────────────────────────

    public static List<DiskHealth> Disks()
    {
        var list = new List<DiskHealth>();
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        var counters = new Dictionary<string, ManagementBaseObject>();
        try
        {
            using var rel = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_StorageReliabilityCounter"));
            foreach (ManagementObject o in rel.Get()) counters[o["DeviceId"]?.ToString() ?? ""] = o;
        }
        catch (Exception) { /* счётчики надёжности доступны не для всех дисков и не на всех системах */ }

        using var disks = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"));
        foreach (ManagementObject d in disks.Get())
        {
            using (d)
            {
                var id = d["DeviceId"]?.ToString() ?? "";
                counters.TryGetValue(id, out var c);
                list.Add(new DiskHealth(
                    d["FriendlyName"]?.ToString()?.Trim() ?? "Диск",
                    Convert.ToInt32(d["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "диск" },
                    Convert.ToInt32(d["BusType"] ?? 0) switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 3 => "ATA", _ => "" },
                    Convert.ToInt64(d["Size"] ?? 0L),
                    Convert.ToInt32(d["HealthStatus"] ?? 0) switch { 0 => "healthy", 1 => "warning", 2 => "unhealthy", _ => "unknown" },
                    Int(c, "Wear"), Int(c, "Temperature"), Int(c, "TemperatureMax"), Long(c, "PowerOnHours"),
                    Long(c, "ReadErrorsTotal"), Long(c, "WriteErrorsTotal"), Long(c, "StartStopCycleCount")));
            }
        }
        foreach (var c in counters.Values) c.Dispose();
        return list;
    }

    private static int? Int(ManagementBaseObject? o, string prop)
    {
        try { var v = o?[prop]; return v is null ? null : Convert.ToInt32(v); } catch { return null; }
    }

    private static long? Long(ManagementBaseObject? o, string prop)
    {
        try { var v = o?[prop]; return v is null ? null : Convert.ToInt64(v); } catch { return null; }
    }

    // ── Батарея ──────────────────────────────────────────────────────────

    public static BatteryInfo Battery()
    {
        int? charge = null; bool? onAc = null; int? minutes = null;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT EstimatedChargeRemaining, BatteryStatus, EstimatedRunTime FROM Win32_Battery");
            foreach (ManagementObject b in s.Get())
            {
                using (b)
                {
                    charge = Int(b, "EstimatedChargeRemaining");
                    var status = Int(b, "BatteryStatus");
                    onAc = status is 2 or 6 or 7 or 8 or 9 or 11;
                    var run = Int(b, "EstimatedRunTime");
                    minutes = run is > 0 and < 71582788 ? run : null; // 71582788 — «на зарядке»
                }
            }
        }
        catch (Exception) { }
        if (charge is null) return new BatteryInfo(false, null, null, null, null, null, null, null, null, null);

        long? design = WmiFirst("BatteryStaticData", "DesignedCapacity");
        long? full = WmiFirst("BatteryFullChargedCapacity", "FullChargedCapacity");
        long? cycles = WmiFirst("BatteryCycleCount", "CycleCount");
        long? discharge = WmiFirst("BatteryStatus", "DischargeRate");
        long? chargeRate = WmiFirst("BatteryStatus", "ChargeRate");
        int? wear = design is > 0 && full is > 0 ? (int)Math.Round((1 - (double)full.Value / design.Value) * 100) : null;

        return new BatteryInfo(true, charge, onAc, design, full, cycles is > 0 ? (int)cycles : null,
            discharge is > 0 ? discharge : null, chargeRate is > 0 ? chargeRate : null, wear is >= 0 ? wear : 0, minutes);
    }

    private static long? WmiFirst(string cls, string prop)
    {
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\wmi"), new ObjectQuery($"SELECT {prop} FROM {cls}"));
            foreach (ManagementObject o in s.Get()) using (o) return Long(o, prop);
        }
        catch (Exception) { }
        return null;
    }

    // ── Пробуждения ──────────────────────────────────────────────────────

    public static WakeInfo Wakes()
    {
        var events = new List<WakeEvent>();
        try
        {
            var q = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and (EventID=1)]]")
            { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            for (int i = 0; i < 30 && reader.ReadEvent() is { } e; i++)
            {
                using (e)
                {
                    var data = EventData(e);
                    long ts = e.TimeCreated is { } t ? new DateTimeOffset(t).ToUnixTimeSeconds() : 0;
                    long? sleep = data.TryGetValue("SleepTime", out var st) && DateTime.TryParse(st, out var sd)
                        ? new DateTimeOffset(sd.ToUniversalTime()).ToUnixTimeSeconds() : null;
                    var source = data.TryGetValue("WakeSourceText", out var w) && !string.IsNullOrWhiteSpace(w) ? w : "неизвестно";
                    data.TryGetValue("WakeSourceType", out var type);
                    events.Add(new WakeEvent(ts, sleep, source, WakeType(type)));
                }
            }
        }
        catch (Exception) { /* журнал недоступен */ }

        return new WakeInfo(events, Powercfg("-devicequery wake_armed"), Powercfg("-waketimers"));
    }

    private static Dictionary<string, string> EventData(EventRecord e)
    {
        var map = new Dictionary<string, string>();
        try
        {
            var x = XDocument.Parse(e.ToXml());
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            foreach (var d in x.Descendants(ns + "Data"))
                if (d.Attribute("Name")?.Value is { } n) map[n] = d.Value;
        }
        catch (Exception) { }
        return map;
    }

    private static string? WakeType(string? t) => t switch
    {
        "0" => "неизвестно", "1" => "кнопка питания", "2" => "крышка или кнопка", "3" => "таймер пробуждения",
        "4" => "устройство", "5" => "сеть", "6" => "по расписанию", _ => null,
    };

    private static List<string> Powercfg(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            })!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(40).ToList();
        }
        catch (Exception) { return []; }
    }

    // ── Процессор ────────────────────────────────────────────────────────

    public CpuInfo Cpu()
    {
        lock (_gate)
        {
            if (_pdh is null)
            {
                _pdh = new Pdh(Freq, Perf, Limit);
                Thread.Sleep(200); // счётчикам-скоростям нужен второй замер
            }
            _pdh.Collect();
            double baseMhz = _pdh.Get(Freq), perf = _pdh.Get(Perf), limit = _pdh.Get(Limit);

            if (_cpuName is null)
            {
                try
                {
                    using var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
                    int cores = 0, threads = 0;
                    foreach (ManagementObject o in s.Get())
                        using (o) { _cpuName ??= o["Name"]?.ToString()?.Trim(); cores += Int(o, "NumberOfCores") ?? 0; threads += Int(o, "NumberOfLogicalProcessors") ?? 0; }
                    _cpuCount = (cores, threads);
                }
                catch (Exception) { _cpuName = "Процессор"; _cpuCount = (0, Environment.ProcessorCount); }
            }

            return new CpuInfo(_cpuName ?? "Процессор", _cpuCount?.Cores ?? 0, _cpuCount?.Threads ?? Environment.ProcessorCount,
                baseMhz, baseMhz * perf / 100, perf, limit, limit is > 0 and < 99.5, Thermal());
        }
    }

    /// <summary>Температура тепловых зон ACPI. На многих ноутбуках это не датчик ядра, а общая зона платы.</summary>
    private static List<ThermalZone> Thermal()
    {
        var list = new List<ThermalZone>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, HighPrecisionTemperature, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    double k = (Long(o, "HighPrecisionTemperature") ?? 0) / 10.0;
                    if (k <= 0) k = Long(o, "Temperature") ?? 0;
                    if (k > 200) list.Add(new ThermalZone(o["Name"]?.ToString()?.Replace(@"\_TZ.", "") ?? "зона", Math.Round(k - 273.15, 1)));
                }
            }
        }
        catch (Exception) { }
        return list;
    }

    public void Dispose()
    {
        lock (_gate) { _pdh?.Dispose(); _pdh = null; }
    }
}
