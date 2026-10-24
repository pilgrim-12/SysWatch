using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace SysWatch.App.Sys;

/// <param name="Tone">ok / warn / bad — подсветка значения (например, «BitLocker выключен»).</param>
public sealed record SysKv(string Label, string Value, string? Tone = null);
public sealed record SysTable(string? Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);
public sealed record SysSection(string Id, string Title, IReadOnlyList<SysKv> Items, IReadOnlyList<SysTable> Tables, string? Note = null);
public sealed record SysOverview(long Ts, IReadOnlyList<SysSection> Sections);

public sealed record RestorePoint(int Sequence, string Description, long Ts, string Type);
public sealed record RestoreInfo(bool Available, IReadOnlyList<RestorePoint> Points, string? Note);

/// <summary>
/// «О компьютере»: модель, Windows, учётная запись, компоненты, экраны, порты, безопасность.
/// Всё читается локально (WMI и реестр), WMI медленный — результат держим 5 минут.
/// </summary>
public sealed class SystemInfo
{
    private readonly object _gate = new();
    private (DateTime At, string Lang, SysOverview Data)? _cache;

    public SysOverview Get(bool refresh = false)
    {
        lock (_gate)
        {
            if (!refresh && _cache is { } c && c.Lang == L.Lang && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(5)) return c.Data;
        }
        var sections = new List<SysSection>();
        void Add(Func<SysSection?> f)
        {
            try { if (f() is { } s) sections.Add(s); }
            catch (Exception) { /* одна недоступная часть не должна ломать остальное */ }
        }
        Add(Computer); Add(Windows); Add(Account); Add(Security); Add(Cpu); Add(Memory); Add(Gpu); Add(Displays);
        Add(Storage); Add(Network); Add(Ports); Add(Audio);
        var data = new SysOverview(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), sections);
        lock (_gate) _cache = (DateTime.UtcNow, L.Lang, data);
        return data;
    }

    // ── WMI и реестр ─────────────────────────────────────────────────────

    internal static List<ManagementBaseObject> Wmi(string query, string scope = @"root\cimv2")
    {
        var list = new List<ManagementBaseObject>();
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query)) { Options = { Timeout = TimeSpan.FromSeconds(10) } };
            foreach (var o in s.Get()) list.Add(o);
        }
        catch (Exception) { /* класс отсутствует или нет доступа */ }
        return list;
    }

    internal static string? S(ManagementBaseObject? o, string prop)
    {
        try
        {
            var v = o?[prop];
            var s = v switch { null => null, string str => str, _ => Convert.ToString(v, CultureInfo.InvariantCulture) };
            s = s?.Trim();
            return string.IsNullOrEmpty(s) || s is "To be filled by O.E.M." or "Default string" or "System Product Name" or "None" ? null : s;
        }
        catch (Exception) { return null; }
    }

    internal static long? N(ManagementBaseObject? o, string prop)
    {
        try { var v = o?[prop]; return v is null ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture); }
        catch (Exception) { return null; }
    }

    internal static bool? B(ManagementBaseObject? o, string prop)
    {
        try { var v = o?[prop]; return v is null ? null : Convert.ToBoolean(v, CultureInfo.InvariantCulture); }
        catch (Exception) { return null; }
    }

    internal static object? Reg(string key, string name)
    {
        try { return Registry.GetValue(key, name, null); } catch (Exception) { return null; }
    }

    private static string Date(DateTime? d) => d is { } v ? v.ToString("d MMMM yyyy", L.Culture) : "—";
    internal static string DateTimeText(DateTime? d) => d is { } v ? v.ToString("d MMMM yyyy, HH:mm", L.Culture) : "—";
    internal static DateTime? Dmtf(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); } catch (Exception) { return null; }
    }
    private static string Size(long? bytes)
    {
        if (bytes is not { } b || b <= 0) return "—";
        string[] u = [L.T("Б"), L.T("КБ"), L.T("МБ"), L.T("ГБ"), L.T("ТБ")];
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v.ToString(v >= 100 || i == 0 ? "0" : "0.#", L.Culture)} {u[i]}";
    }
    private static string Yes(bool? v) => v switch { true => L.T("да"), false => L.T("нет"), _ => "—" };
    private static List<SysKv> Kv(params (string Label, string? Value, string? Tone)[] items) =>
        items.Where(x => !string.IsNullOrEmpty(x.Value) && x.Value != "—").Select(x => new SysKv(x.Label, x.Value!, x.Tone)).ToList();

    // ── Разделы ──────────────────────────────────────────────────────────

    private static SysSection Computer()
    {
        var cs = Wmi("SELECT Manufacturer, Model, PCSystemType, SystemFamily, Domain, PartOfDomain, Workgroup FROM Win32_ComputerSystem").FirstOrDefault();
        var prod = Wmi("SELECT Version, IdentifyingNumber FROM Win32_ComputerSystemProduct").FirstOrDefault();
        var bios = Wmi("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SerialNumber FROM Win32_BIOS").FirstOrDefault();
        var board = Wmi("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard").FirstOrDefault();
        var chassis = Wmi("SELECT ChassisTypes FROM Win32_SystemEnclosure").FirstOrDefault();

        // Тип корпуса: 8–14, 30–32 — ноутбуки и планшеты; PCSystemType 2 — мобильный.
        int[] laptop = [8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32];
        bool isLaptop = N(cs, "PCSystemType") == 2 || ((chassis?["ChassisTypes"] as ushort[]) ?? []).Any(t => laptop.Contains(t));
        var model = S(cs, "Model");
        var marketing = S(prod, "Version"); // у Lenovo тут маркетинговое имя: «Legion 5 15ACH6»
        var firmware = Reg(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control", "PEFirmwareType") is int fw ? (fw == 2 ? "UEFI" : "Legacy BIOS") : null;
        bool domain = B(cs, "PartOfDomain") == true;

        return new SysSection("computer", L.T("Компьютер"), Kv(
            (L.T("Производитель"), S(cs, "Manufacturer"), null),
            (L.T("Модель"), marketing is not null && marketing != model && !marketing.StartsWith("ThinkPad", StringComparison.Ordinal) ? $"{marketing} ({model})" : model, null),
            (L.T("Тип"), isLaptop ? L.T("ноутбук") : L.T("настольный компьютер"), null),
            (L.T("Серийный номер"), S(bios, "SerialNumber") ?? S(prod, "IdentifyingNumber"), null),
            (L.T("Материнская плата"), string.Join(" ", new[] { S(board, "Manufacturer"), S(board, "Product") }.Where(x => x is not null)), null),
            (L.T("BIOS"), string.Join(" ", new[] { S(bios, "Manufacturer"), S(bios, "SMBIOSBIOSVersion") }.Where(x => x is not null)), null),
            (L.T("Дата BIOS"), Date(Dmtf(S(bios, "ReleaseDate"))), null),
            (L.T("Режим загрузки"), firmware, null),
            (L.T("Имя компьютера"), Environment.MachineName, null),
            (domain ? L.T("Домен") : L.T("Рабочая группа"), domain ? S(cs, "Domain") : S(cs, "Workgroup"), null)), []);
    }

    private static SysSection Windows()
    {
        var os = Wmi("SELECT Caption, OSArchitecture, InstallDate, LastBootUpTime, RegisteredUser FROM Win32_OperatingSystem").FirstOrDefault();
        const string cv = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var display = Reg(cv, "DisplayVersion") as string;
        var build = Reg(cv, "CurrentBuild") as string;
        var ubr = Reg(cv, "UBR") is int u ? u : (int?)null;
        var boot = Dmtf(S(os, "LastBootUpTime"));
        var up = boot is { } b ? DateTime.Now - b : (TimeSpan?)null;
        var plan = Wmi("SELECT ElementName FROM Win32_PowerPlan WHERE IsActive = TRUE", @"root\cimv2\power").FirstOrDefault();

        return new SysSection("windows", "Windows", Kv(
            (L.T("Система"), S(os, "Caption")?.Replace("Microsoft ", ""), null),
            (L.T("Версия"), display, null),
            (L.T("Сборка"), build is null ? null : ubr is null ? build : $"{build}.{ubr}", null),
            (L.T("Разрядность"), S(os, "OSArchitecture"), null),
            (L.T("Установлена"), Date(Dmtf(S(os, "InstallDate"))), null),
            (L.T("Последняя загрузка"), DateTimeText(boot), null),
            (L.T("Работает без перезагрузки"), up is { } t ? (t.TotalDays >= 1 ? L.T("{0} дн. {1} ч", (int)t.TotalDays, t.Hours) : L.T("{0} ч {1} мин", t.Hours, t.Minutes)) : null,
                up is { TotalDays: > 14 } ? "warn" : null),
            (L.T("Язык системы"), CultureInfo.InstalledUICulture.NativeName, null),
            (L.T("Часовой пояс"), TimeZoneInfo.Local.DisplayName, null),
            (L.T("Схема питания"), S(plan, "ElementName"), null)), []);
    }

    private static SysSection Account()
    {
        var id = WindowsIdentity.GetCurrent();
        var user = Environment.UserName;
        var users = Wmi("SELECT Name, FullName, Disabled, PasswordRequired, LocalAccount FROM Win32_UserAccount WHERE LocalAccount = TRUE");
        var me = users.FirstOrDefault(u => string.Equals(S(u, "Name"), user, StringComparison.OrdinalIgnoreCase));

        // Учётная запись Microsoft: адрес почты лежит в HKCU\…\IdentityCRL\UserExtendedProperties как имя подраздела.
        string? msa = null;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\IdentityCRL\UserExtendedProperties");
            msa = k?.GetSubKeyNames().FirstOrDefault(n => n.Contains('@'));
        }
        catch (Exception) { }

        bool admin;
        try
        {
            // Окно SysWatch запущено с правами администратора, поэтому смотрим членство в группе, а не текущий токен.
            using var grp = Wmi("SELECT * FROM Win32_Group WHERE SID = 'S-1-5-32-544'").FirstOrDefault() as ManagementObject;
            admin = grp is not null && grp.GetRelated("Win32_UserAccount").Cast<ManagementBaseObject>()
                .Any(m => string.Equals(S(m, "Name"), user, StringComparison.OrdinalIgnoreCase));
            if (!admin) admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception) { admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }

        var rows = users.Select(u => new string[] {
            S(u, "Name") ?? "—", S(u, "FullName") ?? "", B(u, "Disabled") == true ? L.T("отключена") : L.T("активна"),
            B(u, "PasswordRequired") == false ? L.T("без пароля") : "",
        }).ToList();

        return new SysSection("account", L.T("Учётная запись"), Kv(
            (L.T("Пользователь"), id.Name, null),
            (L.T("Полное имя"), S(me, "FullName"), null),
            (L.T("Вход"), msa is not null ? L.T("учётная запись Microsoft: {0}", msa) : L.T("локальная учётная запись"), null),
            (L.T("Администратор"), Yes(admin), null),
            (L.T("Папка пользователя"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), null)),
            rows.Count > 0 ? [new SysTable(L.T("Пользователи этого компьютера"), [L.T("Имя"), L.T("Полное имя"), L.T("Состояние"), ""], rows)] : []);
    }

    private static SysSection Security()
    {
        var items = new List<SysKv>();
        var sb = Reg(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        if (sb is int s) items.Add(new SysKv("Secure Boot", s == 1 ? L.T("включён") : L.T("выключен"), s == 1 ? "ok" : "warn"));

        var tpm = Wmi("SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion FROM Win32_Tpm", @"root\cimv2\Security\MicrosoftTpm").FirstOrDefault();
        if (tpm is not null)
        {
            var ver = S(tpm, "SpecVersion")?.Split(',')[0].Trim();
            bool on = B(tpm, "IsEnabled_InitialValue") == true;
            items.Add(new SysKv("TPM", on ? L.T("есть, версия {0}", ver ?? "?") : L.T("выключен"), on ? "ok" : "warn"));
        }
        else items.Add(new SysKv("TPM", L.T("не найден"), "warn"));

        foreach (var v in Wmi("SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume", @"root\cimv2\Security\MicrosoftVolumeEncryption"))
        {
            var letter = S(v, "DriveLetter");
            if (letter is null) continue;
            var st = N(v, "ProtectionStatus");
            items.Add(new SysKv(L.T("BitLocker {0}", letter), st == 1 ? L.T("диск зашифрован") : L.T("не зашифрован"), st == 1 ? "ok" : null));
        }

        var mp = Wmi("SELECT AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureLastUpdated FROM MSFT_MpComputerStatus", @"root\Microsoft\Windows\Defender").FirstOrDefault();
        if (mp is not null)
        {
            bool av = B(mp, "AntivirusEnabled") == true, rt = B(mp, "RealTimeProtectionEnabled") == true;
            items.Add(new SysKv(L.T("Защитник Windows"), av && rt ? L.T("работает") : av ? L.T("защита в реальном времени выключена") : L.T("выключен (возможно, стоит другой антивирус)"), av && rt ? "ok" : "warn"));
            var sig = Dmtf(S(mp, "AntivirusSignatureLastUpdated"));
            if (sig is { } d) items.Add(new SysKv(L.T("Базы Защитника"), DateTimeText(d), DateTime.Now - d > TimeSpan.FromDays(7) ? "warn" : null));
        }
        var av2 = Wmi("SELECT displayName FROM AntiVirusProduct", @"root\SecurityCenter2").Select(o => S(o, "displayName")).OfType<string>().Distinct().ToList();
        if (av2.Count > 0) items.Add(new SysKv(L.T("Антивирус"), string.Join(", ", av2)));

        var uac = Reg(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA");
        if (uac is int ua) items.Add(new SysKv(L.T("Контроль учётных записей (UAC)"), ua == 1 ? L.T("включён") : L.T("выключен"), ua == 1 ? "ok" : "bad"));

        return new SysSection("security", L.T("Безопасность"), items, []);
    }

    private static SysSection Cpu()
    {
        var p = Wmi("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation, VirtualizationFirmwareEnabled FROM Win32_Processor").FirstOrDefault();
        var cs = Wmi("SELECT HypervisorPresent FROM Win32_ComputerSystem").FirstOrDefault();
        bool hyper = B(cs, "HypervisorPresent") == true;
        return new SysSection("cpu", L.T("Процессор"), Kv(
            (L.T("Модель"), S(p, "Name"), null),
            (L.T("Ядер / потоков"), $"{N(p, "NumberOfCores")} / {N(p, "NumberOfLogicalProcessors")}", null),
            (L.T("Базовая частота"), N(p, "MaxClockSpeed") is { } mhz ? $"{mhz / 1000.0:0.0#} GHz" : null, null),
            (L.T("Кэш L2 / L3"), $"{Size(N(p, "L2CacheSize") * 1024)} / {Size(N(p, "L3CacheSize") * 1024)}", null),
            (L.T("Сокет"), S(p, "SocketDesignation"), null),
            // Когда работает гипервизор (Hyper-V, WSL2, Docker), Windows сообщает виртуализацию в прошивке как «нет» — это нормально.
            (L.T("Виртуализация"), hyper ? L.T("используется (Hyper-V / WSL / Docker)") : Yes(B(p, "VirtualizationFirmwareEnabled")), null)), []);
    }

    private static SysSection Memory()
    {
        var cs = Wmi("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault();
        var arr = Wmi("SELECT MemoryDevices, MaxCapacityEx FROM Win32_PhysicalMemoryArray");
        var mods = Wmi("SELECT DeviceLocator, Capacity, SMBIOSMemoryType, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber FROM Win32_PhysicalMemory");
        long slots = arr.Sum(a => N(a, "MemoryDevices") ?? 0);
        long maxKb = arr.Sum(a => N(a, "MaxCapacityEx") ?? 0);
        static string Type(long? t) => t switch { 20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", _ => "" };
        var rows = mods.Select(m => new string[] {
            S(m, "DeviceLocator") ?? "—", Size(N(m, "Capacity")), Type(N(m, "SMBIOSMemoryType")),
            (N(m, "ConfiguredClockSpeed") ?? N(m, "Speed")) is { } mt and > 0 ? $"{mt} MT/s" : "",
            S(m, "Manufacturer") ?? "", S(m, "PartNumber") ?? "",
        }).ToList();
        return new SysSection("memory", L.T("Оперативная память"), Kv(
            (L.T("Всего"), Size(N(cs, "TotalPhysicalMemory")), null),
            (L.T("Слотов занято"), slots > 0 ? $"{mods.Count} / {slots}" : mods.Count.ToString(CultureInfo.InvariantCulture), null),
            (L.T("Максимум по плате"), maxKb > 0 ? Size(maxKb * 1024) : null, null)),
            rows.Count > 0 ? [new SysTable(null, [L.T("Слот"), L.T("Объём"), L.T("Тип"), L.T("Скорость"), L.T("Производитель"), L.T("Модель")], rows)] : []);
    }

    private static SysSection Gpu()
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var v in Wmi("SELECT Name, DriverVersion, DriverDate, AdapterRAM, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, PNPDeviceID FROM Win32_VideoController"))
        {
            var name = S(v, "Name") ?? "—";
            if (name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase) || name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)) continue;
            // AdapterRAM в WMI ограничен 4 ГБ — точный объём берём из реестра драйвера.
            long? vram = VramFromRegistry(name) ?? N(v, "AdapterRAM");
            var mode = N(v, "CurrentHorizontalResolution") is { } w and > 0 ? $"{w}×{N(v, "CurrentVerticalResolution")}, {N(v, "CurrentRefreshRate")} Hz" : "";
            rows.Add(new string[] {name, Size(vram), S(v, "DriverVersion") ?? "", Date(Dmtf(S(v, "DriverDate"))), mode});
        }
        return new SysSection("gpu", L.T("Видеокарты"), [], [new SysTable(null, [L.T("Модель"), L.T("Видеопамять"), L.T("Драйвер"), L.T("Дата драйвера"), L.T("Режим")], rows)]);
    }

    private static long? VramFromRegistry(string name)
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return null;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                if (!string.Equals(k?.GetValue("DriverDesc") as string, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (k!.GetValue("HardwareInformation.qwMemorySize") is long q) return q;
                if (k.GetValue("HardwareInformation.qwMemorySize") is byte[] qb && qb.Length >= 8) return BitConverter.ToInt64(qb, 0);
                if (k.GetValue("HardwareInformation.MemorySize") is int m) return (uint)m;
                if (k.GetValue("HardwareInformation.MemorySize") is byte[] mb && mb.Length >= 4) return BitConverter.ToUInt32(mb, 0);
            }
        }
        catch (Exception) { }
        return null;
    }

    // ── Экраны ──

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    private static string Decode(ManagementBaseObject o, string prop)
    {
        try { return o[prop] is ushort[] a ? new string(a.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim() : ""; }
        catch (Exception) { return ""; }
    }

    private static SysSection Displays()
    {
        const string wmi = @"root\wmi";
        var ids = Wmi("SELECT InstanceName, ManufacturerName, UserFriendlyName, YearOfManufacture FROM WmiMonitorID", wmi);
        var sizes = Wmi("SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams", wmi)
            .ToDictionary(o => S(o, "InstanceName") ?? "", o => (W: N(o, "MaxHorizontalImageSize") ?? 0, H: N(o, "MaxVerticalImageSize") ?? 0));
        var conns = Wmi("SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams", wmi)
            .ToDictionary(o => S(o, "InstanceName") ?? "", o => N(o, "VideoOutputTechnology"));

        static string Conn(long? t) => t switch
        {
            0 => "VGA", 4 => "DVI", 5 => "HDMI", 6 or 11 or 13 => L.T("встроенный экран"), 10 => "DisplayPort", 15 => "Miracast",
            16 => L.T("через адаптер/док-станцию"), 2147483648 => L.T("встроенный экран"), _ => "",
        };

        var rows = new List<IReadOnlyList<string>>();
        foreach (var m in ids)
        {
            var inst = S(m, "InstanceName") ?? "";
            var name = Decode(m, "UserFriendlyName");
            var maker = Decode(m, "ManufacturerName");
            var (w, h) = sizes.GetValueOrDefault(inst);
            string diag = w > 0 && h > 0 ? $"{Math.Sqrt(w * w + h * h) / 2.54:0.#}\"" : "";
            rows.Add(new string[] {string.IsNullOrEmpty(name) ? L.T("экран {0}", maker) : name, maker, diag, Conn(conns.GetValueOrDefault(inst)), N(m, "YearOfManufacture")?.ToString(CultureInfo.InvariantCulture) ?? ""});
        }

        var modes = new List<IReadOnlyList<string>>();
        foreach (var s in Screen.AllScreens)
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            string hz = EnumDisplaySettings(s.DeviceName, -1, ref dm) && dm.dmDisplayFrequency > 1 ? $"{dm.dmDisplayFrequency} Hz" : "";
            string res = dm.dmPelsWidth > 0 ? $"{dm.dmPelsWidth}×{dm.dmPelsHeight}" : $"{s.Bounds.Width}×{s.Bounds.Height}";
            modes.Add(new string[] {s.DeviceName.Replace(@"\\.\", ""), res, hz, s.Primary ? L.T("основной") : ""});
        }

        var tables = new List<SysTable>();
        if (rows.Count > 0) tables.Add(new SysTable(null, [L.T("Модель"), L.T("Производитель"), L.T("Диагональ"), L.T("Подключение"), L.T("Год")], rows));
        if (modes.Count > 0) tables.Add(new SysTable(L.T("Режимы"), [L.T("Экран"), L.T("Разрешение"), L.T("Частота"), ""], modes));
        return new SysSection("displays", L.T("Экраны"), [], tables);
    }

    private static SysSection Storage()
    {
        var disks = Wmi("SELECT Model, Size, InterfaceType, MediaType FROM Win32_DiskDrive").Select(d => new string[] {
            S(d, "Model") ?? "—", Size(N(d, "Size")), S(d, "InterfaceType") ?? "", S(d, "MediaType") is { } mt && mt.Contains("External", StringComparison.OrdinalIgnoreCase) ? L.T("внешний") : L.T("внутренний"),
        }).ToList();
        var vols = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable).Select(d => new string[] {
            d.Name.TrimEnd('\\'), d.VolumeLabel, d.DriveFormat, Size(d.TotalSize), Size(d.AvailableFreeSpace),
        }).ToList();
        return new SysSection("storage", L.T("Накопители"), [], [
            new SysTable(null, [L.T("Модель"), L.T("Объём"), L.T("Интерфейс"), L.T("Расположение")], disks),
            new SysTable(L.T("Разделы"), [L.T("Диск"), L.T("Метка"), L.T("Файловая система"), L.T("Объём"), L.T("Свободно")], vols),
        ], L.T("Здоровье дисков, износ SSD и температура — на вкладке «Здоровье дисков»."));
    }

    private static SysSection Network()
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && n.OperationalStatus != OperationalStatus.Up) continue;
            var ips = string.Join(", ", n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()));
            var mac = string.Join(":", n.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
            string type = n.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi", NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "Ethernet",
                _ => n.NetworkInterfaceType.ToString(),
            };
            string speed = n.OperationalStatus == OperationalStatus.Up && n.Speed > 0
                ? (n.Speed >= 1_000_000_000 ? $"{n.Speed / 1e9:0.#} Gbit/s" : $"{n.Speed / 1e6:0} Mbit/s") : "";
            rows.Add(new string[] {n.Description, type, n.OperationalStatus == OperationalStatus.Up ? L.T("подключён") : L.T("отключён"), speed, ips, mac});
        }
        rows.Sort((a, b) => string.Compare(b[2], a[2], StringComparison.Ordinal));
        return new SysSection("network", L.T("Сетевые адаптеры"), [],
            [new SysTable(null, [L.T("Адаптер"), L.T("Тип"), L.T("Состояние"), L.T("Скорость"), "IPv4", "MAC"], rows)]);
    }

    private static SysSection Ports()
    {
        var ctrls = Wmi("SELECT Name FROM Win32_USBController").Select(o => S(o, "Name")).OfType<string>().Distinct().ToList();
        bool tb = Wmi("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%Thunderbolt%' OR Name LIKE '%USB4%'").Count > 0;
        var connectors = Wmi("SELECT ExternalReferenceDesignator, InternalReferenceDesignator, PortType FROM Win32_PortConnector")
            .Select(o => S(o, "ExternalReferenceDesignator") ?? S(o, "InternalReferenceDesignator")).OfType<string>().Distinct().ToList();

        // Подключённые устройства USB — без служебных концентраторов и составных устройств.
        var skip = new[] { "hub", "концентратор", "composite", "составное", "root", "корнев", "host controller", "контроллер" };
        var devices = Wmi("SELECT Name, PNPClass, Status FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB%' AND Present = TRUE")
            .Select(o => (Name: S(o, "Name"), Cls: S(o, "PNPClass"))).Where(d => d.Name is not null && !skip.Any(s => d.Name!.Contains(s, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(d => d.Name).Select(d => new string[] {d.Name!, d.Cls ?? ""}).ToList();

        var tables = new List<SysTable>();
        if (devices.Count > 0) tables.Add(new SysTable(L.T("Подключено сейчас по USB"), [L.T("Устройство"), L.T("Класс")], devices));
        if (connectors.Count > 0) tables.Add(new SysTable(L.T("Разъёмы по данным BIOS"), [L.T("Разъём")], connectors.Select(c => new string[] {c}).ToList()));
        return new SysSection("ports", L.T("Порты и USB"), Kv(
            (L.T("Контроллеры USB"), string.Join("; ", ctrls), null),
            ("Thunderbolt / USB4", tb ? L.T("есть") : L.T("не найден"), null)), tables,
            L.T("Список разъёмов есть не у всех ноутбуков: многие производители не заполняют его в BIOS."));
    }

    private static SysSection Audio()
    {
        var rows = Wmi("SELECT Name, Manufacturer, Status FROM Win32_SoundDevice").Select(o => new string[] {
            S(o, "Name") ?? "—", S(o, "Manufacturer") ?? "", S(o, "Status") == "OK" ? L.T("работает") : S(o, "Status") ?? "",
        }).ToList();
        return new SysSection("audio", L.T("Звук"), [], [new SysTable(null, [L.T("Устройство"), L.T("Производитель"), L.T("Состояние")], rows)]);
    }

    // ── Точки восстановления ─────────────────────────────────────────────

    public static RestoreInfo RestorePoints()
    {
        var list = new List<RestorePoint>();
        bool ok = true;
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(@"root\default"), new ObjectQuery("SELECT * FROM SystemRestore"));
            foreach (var o in s.Get())
            {
                var ts = Dmtf(S(o, "CreationTime"));
                string type = N(o, "RestorePointType") switch
                {
                    0 => L.T("установка программы"), 1 => L.T("удаление программы"), 10 => L.T("установка драйвера"),
                    12 => L.T("изменение настроек"), 13 => L.T("отменённая операция"), 7 => L.T("обновление Windows"), _ => L.T("другое"),
                };
                list.Add(new RestorePoint((int)(N(o, "SequenceNumber") ?? 0), S(o, "Description") ?? "", ts is { } d ? new DateTimeOffset(d).ToUnixTimeSeconds() : 0, type));
            }
        }
        catch (Exception) { ok = false; }
        list.Sort((a, b) => b.Ts.CompareTo(a.Ts));
        return new RestoreInfo(ok, list, ok && list.Count == 0
            ? L.T("Точек нет. Возможно, «Защита системы» выключена: Пуск → «Создание точки восстановления» → «Настроить».")
            : ok ? null : L.T("Не удалось прочитать точки восстановления."));
    }

    /// <summary>Создать точку восстановления. Windows создаёт не больше одной в сутки — проверяем, появилась ли новая.</summary>
    public static (bool Ok, string Message) CreateRestorePoint()
    {
        int before = RestorePoints().Points.Count;
        try
        {
            using var cls = new ManagementClass(new ManagementScope(@"root\default"), new ManagementPath("SystemRestore"), null);
            var res = cls.InvokeMethod("CreateRestorePoint", [$"SysWatch {DateTime.Now:yyyy-MM-dd HH:mm}", 12, 100]);
            int code = Convert.ToInt32(res, CultureInfo.InvariantCulture);
            if (code == 1058) return (false, L.T("«Защита системы» выключена для диска C:. Включите её: Пуск → «Создание точки восстановления» → «Настроить»."));
            if (code != 0) return (false, L.T("Windows не создала точку восстановления (код {0}).", code));
        }
        catch (Exception ex) { return (false, L.T("Не удалось создать точку восстановления: {0}", ex.Message)); }

        return RestorePoints().Points.Count > before
            ? (true, L.T("Точка восстановления создана."))
            : (false, L.T("Windows не создала новую точку: по умолчанию она делает не больше одной точки в сутки, а последняя была недавно."));
    }
}
