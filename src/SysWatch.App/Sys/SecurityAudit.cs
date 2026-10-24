using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Win32;
using SysWatch.App.Anomalies;
using SysWatch.App.Programs;
using SysWatch.App.Sampling;

namespace SysWatch.App.Sys;

/// <param name="Status">ok — хорошо, warn — стоит поправить, bad — опасно, info — для сведения, skip — не удалось проверить.</param>
/// <param name="Fix">«#раздел» — открыть раздел SysWatch, иначе ключ из <see cref="SecurityAudit.Targets"/> — открыть настройки Windows.</param>
public sealed record SecCheck(string Id, string Group, string Title, string Status, string Detail, string? Why, string? Fix, int Weight);
public sealed record SecChange(string Id, string Title, string From, string To);
public sealed record SecReport(long Ts, int Score, double Seconds, IReadOnlyList<SecCheck> Checks, long? PrevTs, int? PrevScore, IReadOnlyList<SecChange> Changes);

/// <summary>
/// Проверка безопасности одним нажатием: настройки защиты Windows, обновления, учётные записи, сеть, программы.
/// Всё читается локально (реестр, WMI, счётчики), ничего не меняется и никуда не отправляется.
/// Последний результат хранится в C:\ProgramData\SysWatch\security.json — чтобы показать, что изменилось.
/// </summary>
public sealed class SecurityAudit(Startup startup, Installed programs, Updates updates, AnomalyStore anomalies)
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SysWatch", "security.json");

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Что открывает кнопка «Исправить». Только этот список — из интерфейса нельзя запустить произвольную команду.</summary>
    public static readonly IReadOnlyDictionary<string, (string File, string? Args)> Targets = new Dictionary<string, (string, string?)>
    {
        ["defender"] = ("windowsdefender:", null),
        ["firewall"] = ("control.exe", "firewall.cpl"),
        ["update"] = ("ms-settings:windowsupdate", null),
        ["uac"] = ("UserAccountControlSettings.exe", null),
        ["smartscreen"] = ("windowsdefender://appbrowser", null),
        ["coreiso"] = ("windowsdefender://coreisolation", null),
        ["bitlocker"] = ("control.exe", "/name Microsoft.BitLockerDriveEncryption"),
        ["restore"] = ("SystemPropertiesProtection.exe", null),
        ["accounts"] = ("ms-settings:otherusers", null),
        ["signin"] = ("ms-settings:signinoptions", null),
        ["rdp"] = ("ms-settings:remotedesktop", null),
        ["features"] = ("optionalfeatures.exe", null),
        ["shares"] = ("fsmgmt.msc", null),
        ["wifi"] = ("ms-settings:network-wifi", null),
        ["proxy"] = ("ms-settings:network-proxy", null),
        ["hosts"] = ("notepad.exe", Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts")),
    };

    public static bool Open(string id)
    {
        if (!Targets.TryGetValue(id, out var t)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(t.File, t.Args ?? "") { UseShellExecute = true });
            return true;
        }
        catch (Exception) { return false; }
    }

    public SecReport? Last()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<SecReport>(File.ReadAllText(FilePath), Json) : null; }
        catch (Exception) { return null; }
    }

    public async Task<SecReport> Run()
    {
        await _gate.WaitAsync();
        try { return await Task.Run(RunCore); }
        finally { _gate.Release(); }
    }

    private SecReport RunCore()
    {
        var sw = Stopwatch.StartNew();
        var list = new List<SecCheck>();
        void Safe(string id, string group, string title, Action<List<SecCheck>> check)
        {
            try { check(list); }
            catch (Exception) { list.Add(new SecCheck(id, group, title, "skip", L.T("Не удалось проверить."), null, null, 0)); }
        }

        string gProtect = L.T("Защита Windows"), gUpdate = L.T("Обновления"), gBoot = L.T("Шифрование и загрузка"),
            gUsers = L.T("Учётные записи"), gNet = L.T("Сеть"), gApps = L.T("Программы");

        Safe("antivirus", gProtect, L.T("Антивирус"), Antivirus);
        Safe("firewall", gProtect, L.T("Брандмауэр"), Firewall);
        Safe("uac", gProtect, L.T("Контроль учётных записей (UAC)"), Uac);
        Safe("smartscreen", gProtect, "SmartScreen", SmartScreen);
        Safe("coreiso", gProtect, L.T("Изоляция ядра"), CoreIsolation);

        Safe("winver", gUpdate, L.T("Поддержка Windows"), WindowsSupport);
        Safe("lastupdate", gUpdate, L.T("Последнее обновление Windows"), LastUpdate);
        Safe("apps-updates", gUpdate, L.T("Обновления программ"), AppUpdates);

        Safe("secureboot", gBoot, "Secure Boot", SecureBoot);
        Safe("tpm", gBoot, "TPM", Tpm);
        Safe("bitlocker", gBoot, "BitLocker", BitLocker);
        Safe("restore", gBoot, L.T("Точки восстановления"), Restore);

        Safe("admin", gUsers, L.T("Ваша учётная запись"), Admin);
        Safe("builtin", gUsers, L.T("Встроенные учётные записи"), BuiltIn);
        Safe("autologon", gUsers, L.T("Автоматический вход"), AutoLogon);

        Safe("rdp", gNet, L.T("Удалённый рабочий стол"), Rdp);
        Safe("smb1", gNet, "SMBv1", Smb1);
        Safe("shares", gNet, L.T("Общие папки"), Shares);
        Safe("ports", gNet, L.T("Открытые порты"), Ports);
        Safe("wifi", gNet, "Wi-Fi", Wifi);
        Safe("hosts", gNet, L.T("Файл hosts"), Hosts);
        Safe("proxy", gNet, L.T("Прокси"), Proxy);
        Safe("dns", gNet, L.T("DNS-серверы"), Dns);

        Safe("startup", gApps, L.T("Автозагрузка"), StartupUnsigned);
        Safe("anomalies", gApps, L.T("Аномалии за неделю"), RecentAnomalies);
        Safe("remote", gApps, L.T("Программы удалённого доступа"), RemoteTools);

        // Оценка: «опасно» снимает вес целиком, «стоит поправить» — половину.
        int penalty = list.Sum(c => c.Status switch { "bad" => c.Weight, "warn" => (c.Weight + 1) / 2, _ => 0 });
        int score = Math.Clamp(100 - penalty, 0, 100);

        var prev = Last();
        var changes = new List<SecChange>();
        if (prev is not null)
        {
            var before = prev.Checks.ToDictionary(c => c.Id, c => c.Status);
            foreach (var c in list)
                if (before.TryGetValue(c.Id, out var was) && was != c.Status && was != "skip" && c.Status != "skip")
                    changes.Add(new SecChange(c.Id, c.Title, was, c.Status));
        }

        var report = new SecReport(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), score, Math.Round(sw.Elapsed.TotalSeconds, 1),
            list, prev?.Ts, prev?.Score, changes);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(report, Json));
        }
        catch (Exception) { /* не сохранили — сравнения в следующий раз не будет, не страшно */ }
        return report;
    }

    // ── Помощники ────────────────────────────────────────────────────────

    private static object? Reg(string key, string name) => SystemInfo.Reg(key, name);
    private static int? RegInt(string key, string name) => Reg(key, name) is int i ? i : null;
    private static string? RegStr(string key, string name) => Reg(key, name) as string;
    private const string HKLM = @"HKEY_LOCAL_MACHINE\";

    private static SecCheck C(string id, string group, string title, string status, string detail, string? why, string? fix, int weight) =>
        new(id, group, title, status, detail, why, fix, weight);

    // ── Защита Windows ───────────────────────────────────────────────────

    private void Antivirus(List<SecCheck> list)
    {
        string g = L.T("Защита Windows"), title = L.T("Антивирус");
        var products = SystemInfo.Wmi("SELECT displayName, productState FROM AntiVirusProduct", @"root\SecurityCenter2")
            .Select(o => (Name: SystemInfo.S(o, "displayName") ?? "?", State: SystemInfo.N(o, "productState") ?? 0)).ToList();
        // productState: второй байт — включён ли (0x10), младший — устарели ли базы (0x10).
        var on = products.Where(p => ((p.State >> 8) & 0x10) != 0).ToList();
        if (on.Count > 0)
        {
            var stale = on.Where(p => (p.State & 0xFF) == 0x10).Select(p => p.Name).ToList();
            list.Add(stale.Count > 0
                ? C("antivirus", g, title, "warn", L.T("Работает {0}, но базы устарели.", string.Join(", ", on.Select(p => p.Name))),
                    L.T("Без свежих баз антивирус не узнаёт новые угрозы."), "defender", 16)
                : C("antivirus", g, title, "ok", L.T("Работает: {0}.", string.Join(", ", on.Select(p => p.Name))), null, null, 25));
        }
        else if (products.Count > 0)
            list.Add(C("antivirus", g, title, "bad", L.T("Установлен, но выключен: {0}.", string.Join(", ", products.Select(p => p.Name))),
                L.T("Компьютер без защиты от вирусов."), "defender", 25));
        else
            list.Add(C("antivirus", g, title, "skip", L.T("Windows не сообщает о состоянии антивируса (так бывает на серверных версиях)."), null, "defender", 0));

        // Возраст баз Защитника — если он основной антивирус.
        var mp = SystemInfo.Wmi("SELECT AntivirusEnabled, AntivirusSignatureLastUpdated, QuickScanEndTime FROM MSFT_MpComputerStatus", @"root\Microsoft\Windows\Defender").FirstOrDefault();
        if (mp is not null && SystemInfo.B(mp, "AntivirusEnabled") == true && SystemInfo.Dmtf(SystemInfo.S(mp, "AntivirusSignatureLastUpdated")) is { } sig)
        {
            var age = DateTime.Now - sig;
            list.Add(age > TimeSpan.FromDays(7)
                ? C("defender-sig", g, L.T("Базы Защитника"), "warn", L.T("Обновлялись {0} — больше недели назад.", SystemInfo.DateTimeText(sig)),
                    L.T("Обычно базы обновляются несколько раз в день. Проверьте Центр обновления."), "defender", 8)
                : C("defender-sig", g, L.T("Базы Защитника"), "ok", L.T("Обновлены {0}.", SystemInfo.DateTimeText(sig)), null, null, 8));
        }
    }

    private void Firewall(List<SecCheck> list)
    {
        string g = L.T("Защита Windows"), title = L.T("Брандмауэр");
        const string Std = HKLM + @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\";
        const string Pol = HKLM + @"SOFTWARE\Policies\Microsoft\WindowsFirewall\";
        int? Profile(string name) => RegInt(Pol + name, "EnableFirewall") ?? RegInt(Std + name, "EnableFirewall");
        var profiles = new (string Key, string Label)[] { ("DomainProfile", L.T("доменная")), ("StandardProfile", L.T("частная")), ("PublicProfile", L.T("общедоступная")) };
        var off = profiles.Where(p => Profile(p.Key) == 0).Select(p => p.Label).ToList();
        var third = SystemInfo.Wmi("SELECT displayName, productState FROM FirewallProduct", @"root\SecurityCenter2")
            .Where(o => ((SystemInfo.N(o, "productState") ?? 0) >> 8 & 0x10) != 0).Select(o => SystemInfo.S(o, "displayName")).OfType<string>().ToList();

        if (off.Count == 0)
            list.Add(C("firewall", g, title, "ok", L.T("Включён во всех сетях."), null, null, 20));
        else if (third.Count > 0)
            list.Add(C("firewall", g, title, "ok", L.T("Брандмауэр Windows выключен ({0}), работает {1}.", string.Join(", ", off), string.Join(", ", third)), null, null, 20));
        else
            list.Add(C("firewall", g, title, off.Count == 1 && off[0] == L.T("доменная") ? "warn" : "bad",
                L.T("Выключен для сетей: {0}.", string.Join(", ", off)),
                L.T("Без брандмауэра программы на компьютере доступны всем в сети — в кафе, гостинице, аэропорту."), "firewall", 20));
    }

    private void Uac(List<SecCheck> list)
    {
        string g = L.T("Защита Windows"), title = L.T("Контроль учётных записей (UAC)");
        const string K = HKLM + @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        if (RegInt(K, "EnableLUA") == 0)
            list.Add(C("uac", g, title, "bad", L.T("Выключен."), L.T("Любая программа получает права администратора без вопроса."), "uac", 15));
        else if (RegInt(K, "ConsentPromptBehaviorAdmin") == 0)
            list.Add(C("uac", g, title, "warn", L.T("Включён, но не спрашивает подтверждения."), L.T("Программы повышают права молча — как будто UAC выключен."), "uac", 12));
        else
            list.Add(C("uac", g, title, "ok", L.T("Спрашивает подтверждение перед изменениями системы."), null, null, 15));
    }

    private void SmartScreen(List<SecCheck> list)
    {
        string g = L.T("Защита Windows");
        bool off = RegInt(HKLM + @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen") == 0
            || string.Equals(RegStr(HKLM + @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "SmartScreenEnabled"), "Off", StringComparison.OrdinalIgnoreCase);
        list.Add(off
            ? C("smartscreen", g, "SmartScreen", "warn", L.T("Проверка скачанных программ выключена."), L.T("SmartScreen предупреждает о незнакомых и опасных файлах из интернета."), "smartscreen", 6)
            : C("smartscreen", g, "SmartScreen", "ok", L.T("Проверяет скачанные программы."), null, null, 6));
    }

    private void CoreIsolation(List<SecCheck> list)
    {
        string g = L.T("Защита Windows"), title = L.T("Изоляция ядра");
        bool on = RegInt(HKLM + @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled") == 1;
        list.Add(on
            ? C("coreiso", g, title, "ok", L.T("Целостность памяти включена."), null, null, 4)
            : C("coreiso", g, title, "warn", L.T("Целостность памяти выключена."),
                L.T("Защищает ядро от вредоносных драйверов. Иногда мешает старым драйверам — тогда Windows сама скажет, каким."), "coreiso", 4));
    }

    // ── Обновления ───────────────────────────────────────────────────────

    /// <summary>Окончание поддержки по графику Microsoft: (сборка, Home/Pro, Enterprise/Education).</summary>
    private static readonly (int Build, string Name, DateTime Consumer, DateTime Enterprise)[] Support =
    [
        (19045, "Windows 10 22H2", new DateTime(2025, 10, 14), new DateTime(2025, 10, 14)),
        (22000, "Windows 11 21H2", new DateTime(2023, 10, 10), new DateTime(2024, 10, 8)),
        (22621, "Windows 11 22H2", new DateTime(2024, 10, 8), new DateTime(2025, 10, 14)),
        (22631, "Windows 11 23H2", new DateTime(2025, 11, 11), new DateTime(2026, 11, 10)),
        (26100, "Windows 11 24H2", new DateTime(2026, 10, 13), new DateTime(2027, 10, 12)),
        (26200, "Windows 11 25H2", new DateTime(2027, 10, 12), new DateTime(2028, 10, 10)),
    ];

    private void WindowsSupport(List<SecCheck> list)
    {
        string g = L.T("Обновления"), title = L.T("Поддержка Windows");
        const string K = HKLM + @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        int build = int.TryParse(RegStr(K, "CurrentBuild") ?? RegStr(K, "CurrentBuildNumber"), out var b) ? b : 0;
        string edition = RegStr(K, "EditionID") ?? "";
        string shown = $"{RegStr(K, "DisplayVersion") ?? ""} ({build})".Trim();
        if (edition.Contains("EnterpriseS", StringComparison.OrdinalIgnoreCase) || edition.Contains("IoT", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(C("winver", g, title, "info", L.T("Выпуск LTSC/IoT {0}: у него свой длинный срок поддержки.", shown), null, null, 0));
            return;
        }
        bool ent = edition.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) || edition.Contains("Education", StringComparison.OrdinalIgnoreCase);
        (int, string, DateTime, DateTime)? row = null;
        foreach (var x in Support)
            if (build >= x.Build && build < x.Build + 100) row = (x.Build, x.Name, x.Consumer, x.Enterprise);
        if (row is null && build > 0 && build < 19045) row = (build, "Windows 10", new DateTime(2025, 10, 14), new DateTime(2025, 10, 14));
        if (row is not { } r)
        {
            list.Add(C("winver", g, title, "ok", L.T("Сборка {0}: новее известных версий.", shown), null, null, 20));
            return;
        }
        var end = ent ? r.Item4 : r.Item3;
        var left = end - DateTime.Today;
        if (left.TotalDays < 0)
            list.Add(C("winver", g, title, "bad", L.T("{0}: поддержка закончилась {1}.", r.Item2, end.ToString("d MMMM yyyy", L.Culture)),
                r.Item1 < 22000
                    ? L.T("Windows 10 больше не получает исправлений безопасности (кроме платной программы ESU). Обновитесь до Windows 11, если компьютер подходит.")
                    : L.T("Эта версия больше не получает исправлений безопасности. Установите новую версию в Центре обновления."), "update", 20));
        else if (left.TotalDays < 60)
            list.Add(C("winver", g, title, "warn", L.T("{0}: поддержка закончится {1}.", r.Item2, end.ToString("d MMMM yyyy", L.Culture)),
                L.T("Установите новую версию в Центре обновления до этой даты."), "update", 10));
        else
            list.Add(C("winver", g, title, "ok", L.T("{0}: поддерживается до {1}.", r.Item2, end.ToString("d MMMM yyyy", L.Culture)), null, null, 20));
    }

    private void LastUpdate(List<SecCheck> list)
    {
        string g = L.T("Обновления"), title = L.T("Последнее обновление Windows");
        DateTime? last = null;
        foreach (var o in SystemInfo.Wmi("SELECT InstalledOn FROM Win32_QuickFixEngineering"))
        {
            var s = SystemInfo.S(o, "InstalledOn");
            if (s is null) continue;
            if (DateTime.TryParseExact(s, new[] { "M/d/yyyy", "MM/dd/yyyy", "yyyyMMdd", "d.M.yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                && d <= DateTime.Today.AddDays(1) && (last is null || d > last)) last = d;
        }
        bool reboot;
        using (var rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            reboot = rk is not null;

        if (last is not { } l)
            list.Add(C("lastupdate", g, title, "skip", L.T("Windows не сообщает дату установки обновлений."), null, "update", 0));
        else
        {
            int days = (int)(DateTime.Today - l).TotalDays;
            string when = L.T("{0} ({1} дн. назад)", l.ToString("d MMMM yyyy", L.Culture), days);
            list.Add(days > 90
                ? C("lastupdate", g, title, "bad", when, L.T("Три месяца без исправлений безопасности. Откройте Центр обновления."), "update", 15)
                : days > 45
                    ? C("lastupdate", g, title, "warn", when, L.T("Обновления Windows выходят каждый месяц — похоже, они не ставятся."), "update", 15)
                    : C("lastupdate", g, title, "ok", when, null, null, 15));
        }
        if (reboot)
            list.Add(C("reboot", g, L.T("Перезагрузка"), "warn", L.T("Обновления ждут перезагрузки."), L.T("Пока компьютер не перезагружен, исправления не действуют."), "update", 4));
    }

    private void AppUpdates(List<SecCheck> list)
    {
        string g = L.T("Обновления"), title = L.T("Обновления программ");
        var v = updates.Get(false); // берём то, что уже проверено; если не проверялось — проверка пойдёт в фоне
        if (!v.Available || v.CheckedAt == 0)
        {
            list.Add(C("apps-updates", g, title, "skip", v.Available ? L.T("winget ещё проверяет — повторите проверку через минуту.") : L.T("winget не найден."), null, "#programs", 0));
            return;
        }
        int n = v.Items.Count;
        list.Add(n == 0
            ? C("apps-updates", g, title, "ok", L.T("Все программы, которые знает winget, обновлены."), null, null, 8)
            : C("apps-updates", g, title, n > 5 ? "warn" : "info", L.T("Можно обновить: {0}.", string.Join(", ", v.Items.Take(6).Select(i => i.Name)) + (n > 6 ? "…" : "")),
                L.T("Старые версии браузеров, мессенджеров и плееров — частый путь заражения."), "#programs", 8));
    }

    // ── Шифрование и загрузка ────────────────────────────────────────────

    private void SecureBoot(List<SecCheck> list)
    {
        string g = L.T("Шифрование и загрузка");
        var v = RegInt(HKLM + @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        list.Add(v switch
        {
            1 => C("secureboot", g, "Secure Boot", "ok", L.T("Включён: при загрузке запускается только подписанный загрузчик."), null, null, 6),
            0 => C("secureboot", g, "Secure Boot", "warn", L.T("Выключен в настройках UEFI."), L.T("Защищает от вирусов, которые запускаются раньше Windows. Включается в BIOS/UEFI."), null, 6),
            _ => C("secureboot", g, "Secure Boot", "info", L.T("Компьютер загружается в режиме BIOS (Legacy) — Secure Boot недоступен."), null, null, 0),
        });
    }

    private void Tpm(List<SecCheck> list)
    {
        string g = L.T("Шифрование и загрузка");
        var tpm = SystemInfo.Wmi("SELECT IsEnabled_InitialValue, SpecVersion FROM Win32_Tpm", @"root\cimv2\Security\MicrosoftTpm").FirstOrDefault();
        if (tpm is null)
            list.Add(C("tpm", g, "TPM", "info", L.T("Модуль TPM не найден или выключен в BIOS."), L.T("Нужен для BitLocker и Windows Hello."), null, 0));
        else
        {
            var ver = SystemInfo.S(tpm, "SpecVersion")?.Split(',')[0].Trim();
            list.Add(SystemInfo.B(tpm, "IsEnabled_InitialValue") == true
                ? C("tpm", g, "TPM", "ok", L.T("Есть, версия {0}.", ver ?? "?"), null, null, 3)
                : C("tpm", g, "TPM", "warn", L.T("Есть, но выключен."), L.T("Включается в BIOS/UEFI."), null, 3));
        }
    }

    private void BitLocker(List<SecCheck> list)
    {
        string g = L.T("Шифрование и загрузка");
        var sys = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        var vols = SystemInfo.Wmi("SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume", @"root\cimv2\Security\MicrosoftVolumeEncryption");
        var v = vols.FirstOrDefault(o => string.Equals(SystemInfo.S(o, "DriveLetter"), sys, StringComparison.OrdinalIgnoreCase));
        if (v is null)
        {
            list.Add(C("bitlocker", g, "BitLocker", "info", L.T("Недоступен: в Windows Home его нет (бывает «Шифрование устройства» в Параметрах)."), null, null, 0));
            return;
        }
        bool laptop = SystemInfo.Wmi("SELECT BatteryStatus FROM Win32_Battery").Count > 0;
        list.Add(SystemInfo.N(v, "ProtectionStatus") == 1
            ? C("bitlocker", g, "BitLocker", "ok", L.T("Системный диск {0} зашифрован.", sys), null, null, 8)
            : C("bitlocker", g, "BitLocker", laptop ? "warn" : "info", L.T("Системный диск {0} не зашифрован.", sys),
                laptop ? L.T("Если ноутбук украдут, файлы можно прочитать, вынув диск. Сохраните ключ восстановления перед включением.") : null, "bitlocker", laptop ? 8 : 0));
    }

    private void Restore(List<SecCheck> list)
    {
        string g = L.T("Шифрование и загрузка"), title = L.T("Точки восстановления");
        var r = SystemInfo.RestorePoints();
        if (!r.Available)
            list.Add(C("restore", g, title, "skip", r.Note ?? L.T("Не удалось прочитать."), null, "restore", 0));
        else if (r.Points.Count == 0)
            list.Add(C("restore", g, title, "warn", L.T("Ни одной точки восстановления."),
                L.T("После неудачного драйвера или обновления систему не откатить. Включите защиту системы для диска C:."), "restore", 5));
        else
        {
            var last = DateTimeOffset.FromUnixTimeSeconds(r.Points.Max(p => p.Ts)).LocalDateTime;
            list.Add(C("restore", g, title, "ok", L.T("Точек: {0}, последняя {1}.", r.Points.Count, SystemInfo.DateTimeText(last)), null, null, 5));
        }
    }

    // ── Учётные записи ───────────────────────────────────────────────────

    private void Admin(List<SecCheck> list)
    {
        string g = L.T("Учётные записи"), title = L.T("Ваша учётная запись");
        var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        // Членство в Администраторах видно и в отфильтрованном токене UAC — как «deny only» SID.
        bool admin = id.Claims.Any(c => c.Value == "S-1-5-32-544");
        list.Add(admin
            ? C("admin", g, title, "info", L.T("{0} — администратор.", id.Name),
                L.T("Обычная ситуация дома. Безопаснее работать под обычной записью, а администратора держать отдельно."), "accounts", 0)
            : C("admin", g, title, "ok", L.T("{0} — обычный пользователь.", id.Name), null, null, 0));
    }

    private void BuiltIn(List<SecCheck> list)
    {
        string g = L.T("Учётные записи"), title = L.T("Встроенные учётные записи");
        var users = SystemInfo.Wmi("SELECT Name, SID, Disabled FROM Win32_UserAccount WHERE LocalAccount = True");
        var on = users.Where(u => SystemInfo.B(u, "Disabled") == false)
            .Select(u => (Name: SystemInfo.S(u, "Name") ?? "?", Sid: SystemInfo.S(u, "SID") ?? ""))
            .Where(u => u.Sid.EndsWith("-500") || u.Sid.EndsWith("-501")).Select(u => u.Name).ToList();
        list.Add(on.Count == 0
            ? C("builtin", g, title, "ok", L.T("Встроенный «Администратор» и «Гость» отключены."), null, null, 8)
            : C("builtin", g, title, "warn", L.T("Включены: {0}.", string.Join(", ", on)),
                L.T("Эти записи знает любой злоумышленник — их подбирают первыми. Отключите, если не пользуетесь."), "accounts", 8));
    }

    private void AutoLogon(List<SecCheck> list)
    {
        string g = L.T("Учётные записи"), title = L.T("Автоматический вход");
        const string K = HKLM + @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        bool on = RegStr(K, "AutoAdminLogon") == "1";
        bool plain = !string.IsNullOrEmpty(RegStr(K, "DefaultPassword"));
        list.Add(!on
            ? C("autologon", g, title, "ok", L.T("Выключен: при включении компьютер спрашивает пароль."), null, null, 10)
            : plain
                ? C("autologon", g, title, "bad", L.T("Включён, пароль хранится в реестре открытым текстом."), L.T("Любая программа может прочитать ваш пароль."), "signin", 12)
                : C("autologon", g, title, "warn", L.T("Включён: компьютер входит без пароля."), L.T("Кто угодно, кто включит компьютер, получит доступ к вашим файлам."), "signin", 6));
    }

    // ── Сеть ─────────────────────────────────────────────────────────────

    private void Rdp(List<SecCheck> list)
    {
        string g = L.T("Сеть"), title = L.T("Удалённый рабочий стол");
        bool on = RegInt(HKLM + @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections") == 0;
        bool nla = RegInt(HKLM + @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", "UserAuthentication") != 0;
        list.Add(!on
            ? C("rdp", g, title, "ok", L.T("Выключен."), null, null, 12)
            : !nla
                ? C("rdp", g, title, "bad", L.T("Включён без проверки подлинности на уровне сети (NLA)."), L.T("К экрану входа может подключиться кто угодно — частая цель атак."), "rdp", 12)
                : C("rdp", g, title, "warn", L.T("Включён (с NLA)."), L.T("Если не подключаетесь к этому компьютеру удалённо — выключите."), "rdp", 4));
    }

    private void Smb1(List<SecCheck> list)
    {
        string g = L.T("Сеть");
        bool server = RegInt(HKLM + @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "SMB1") == 1;
        var client = RegInt(HKLM + @"SYSTEM\CurrentControlSet\Services\mrxsmb10", "Start");
        bool on = server || client is int s && s != 4;
        list.Add(on
            ? C("smb1", g, "SMBv1", "bad", L.T("Устаревший протокол SMBv1 включён."), L.T("Через него распространялись WannaCry и NotPetya. Отключите в «Компонентах Windows»."), "features", 10)
            : C("smb1", g, "SMBv1", "ok", L.T("Отключён."), null, null, 10));
    }

    private void Shares(List<SecCheck> list)
    {
        string g = L.T("Сеть"), title = L.T("Общие папки");
        var shares = SystemInfo.Wmi("SELECT Name, Path, Type FROM Win32_Share")
            .Where(o => SystemInfo.N(o, "Type") == 0 && !(SystemInfo.S(o, "Name") ?? "$").EndsWith('$'))
            .Select(o => $"{SystemInfo.S(o, "Name")} ({SystemInfo.S(o, "Path")})").ToList();
        list.Add(shares.Count == 0
            ? C("shares", g, title, "ok", L.T("Нет открытых в сеть папок."), null, null, 4)
            : C("shares", g, title, "warn", L.T("Открыты в сеть: {0}.", string.Join(", ", shares)), L.T("Их видят все в вашей сети. Закройте лишние."), "shares", 4));
    }

    private void Ports(List<SecCheck> list)
    {
        string g = L.T("Сеть"), title = L.T("Открытые порты");
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var third = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        int total = 0;
        foreach (var grp in Connections.ExposedListeners().Where(x => x.Proto == "TCP").GroupBy(x => x.Pid))
        {
            total += grp.Count();
            string? path = null, name = null;
            try { using var p = Process.GetProcessById(grp.Key); name = p.ProcessName; path = p.MainModule?.FileName; }
            catch (Exception) { /* процесс завершился или нет доступа */ }
            if (path is not null && path.StartsWith(windir, StringComparison.OrdinalIgnoreCase)) continue;
            third.Add($"{name ?? $"pid {grp.Key}"} ({string.Join(", ", grp.Select(x => x.Port).Distinct().OrderBy(x => x).Take(4))})");
        }
        list.Add(third.Count == 0
            ? C("ports", g, title, "ok", total == 0 ? L.T("Ничего не принимает соединения из сети.") : L.T("Слушают только службы Windows."), null, null, 6)
            : C("ports", g, title, "warn", L.T("Принимают соединения из сети: {0}.", string.Join(", ", third.Take(6))),
                L.T("Убедитесь, что это нужные программы (сервер разработки, игра, NAS). Подробнее — в «Соединениях»."), "#connections", 6));
    }

    private void Wifi(List<SecCheck> list)
    {
        string g = L.T("Сеть");
        string output;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh", "wlan show interfaces")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
        }
        catch (Exception) { return; }
        string? ssid = null, auth = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            int i = line.IndexOf(':');
            if (i < 0) continue;
            var key = line[..i].Trim();
            var val = line[(i + 1)..].Trim();
            if (key.Equals("SSID", StringComparison.OrdinalIgnoreCase)) ssid = val;
            else if (key.Contains("Authentication", StringComparison.OrdinalIgnoreCase) || key.Contains("подлинности", StringComparison.OrdinalIgnoreCase)) auth = val;
        }
        if (ssid is null || auth is null) return; // не по Wi-Fi — проверку не показываем
        bool open = auth.Contains("Open", StringComparison.OrdinalIgnoreCase) || auth.Contains("Откр", StringComparison.OrdinalIgnoreCase);
        bool wep = auth.Contains("WEP", StringComparison.OrdinalIgnoreCase) || auth.Equals("WPA-Personal", StringComparison.OrdinalIgnoreCase);
        list.Add(open || wep
            ? C("wifi", g, "Wi-Fi", "bad", L.T("Сеть «{0}»: {1}.", ssid, open ? L.T("без пароля") : auth),
                L.T("Трафик в такой сети могут перехватить соседи. Не входите в банк и почту без VPN."), "wifi", 10)
            : C("wifi", g, "Wi-Fi", "ok", L.T("Сеть «{0}»: {1}.", ssid, auth), null, null, 10));
    }

    private static readonly string[] Sensitive = ["microsoft", "windowsupdate", "windows.com", "google", "apple", "bank", "paypal", "kaspersky", "eset", "avast", "norton", "mcafee", "malwarebytes", "bitdefender", "defender"];

    private void Hosts(List<SecCheck> list)
    {
        string g = L.T("Сеть"), title = L.T("Файл hosts");
        var path = Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");
        if (!File.Exists(path)) { list.Add(C("hosts", g, title, "ok", L.T("Файла нет — подмен адресов нет."), null, null, 10)); return; }
        var entries = File.ReadAllLines(path)
            .Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0)
            .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length >= 2)
            .SelectMany(p => p.Skip(1)).Where(h => !h.Equals("localhost", StringComparison.OrdinalIgnoreCase)).ToList();
        var bad = entries.Where(h => Sensitive.Any(s => h.Contains(s, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
        list.Add(entries.Count == 0
            ? C("hosts", g, title, "ok", L.T("Без изменений."), null, null, 10)
            : bad.Count > 0
                ? C("hosts", g, title, "bad", L.T("Подменены адреса: {0}.", string.Join(", ", bad.Take(5))),
                    L.T("Так вирусы блокируют обновления антивируса или подсовывают поддельные сайты. Если это сделали не вы — удалите эти строки."), "hosts", 10)
                : C("hosts", g, title, "info", L.T("Своих записей: {0} (например, {1}).", entries.Count, string.Join(", ", entries.Take(3))),
                    L.T("Обычно это делают программы разработки или блокировщики рекламы."), "hosts", 0));
    }

    private void Proxy(List<SecCheck> list)
    {
        string g = L.T("Сеть"), title = L.T("Прокси");
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        bool on = k?.GetValue("ProxyEnable") is int e && e == 1;
        var server = k?.GetValue("ProxyServer") as string;
        var pac = k?.GetValue("AutoConfigURL") as string;
        list.Add(!on && string.IsNullOrEmpty(pac)
            ? C("proxy", g, title, "ok", L.T("Не задан: программы выходят в интернет напрямую."), null, null, 4)
            : C("proxy", g, title, "warn", L.T("Весь трафик браузеров идёт через {0}.", on ? server ?? "?" : pac!),
                L.T("Если вы не настраивали прокси (VPN, работа) — его мог поставить вирус, чтобы читать ваш трафик."), "proxy", 4));
    }

    private void Dns(List<SecCheck> list)
    {
        string g = L.T("Сеть");
        var dns = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().DnsAddresses).Select(a => a.ToString()).Distinct().Take(6).ToList();
        if (dns.Count > 0)
            list.Add(C("dns", g, L.T("DNS-серверы"), "info", string.Join(", ", dns),
                L.T("Через них компьютер узнаёт адреса сайтов. Незнакомые серверы, которые вы не ставили, — повод проверить роутер."), null, 0));
    }

    // ── Программы ────────────────────────────────────────────────────────

    private void StartupUnsigned(List<SecCheck> list)
    {
        string g = L.T("Программы"), title = L.T("Автозагрузка");
        var items = startup.List().Where(i => i.Enabled).ToList();
        var unsigned = items.Where(i => i.Signature == "unsigned").Select(i => i.Name).ToList();
        list.Add(unsigned.Count == 0
            ? C("startup", g, title, "ok", L.T("Все программы в автозагрузке подписаны ({0}).", items.Count), null, null, 8)
            : C("startup", g, title, "warn", L.T("Без цифровой подписи: {0}.", string.Join(", ", unsigned.Take(6))),
                L.T("Неподписанная программа в автозагрузке — не обязательно вирус, но стоит знать, что это."), "#system", 8));
    }

    private void RecentAnomalies(List<SecCheck> list)
    {
        string g = L.T("Программы"), title = L.T("Аномалии за неделю");
        var a = anomalies.List(DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds()).Items;
        int high = a.Count(x => x.Severity == "high");
        list.Add(high == 0
            ? C("anomalies", g, title, "ok", L.T("Важных нет (всего записей: {0}).", a.Count), null, null, 10)
            : C("anomalies", g, title, "warn", L.T("Важных: {0}, например: {1}.", high, string.Join(", ", a.Where(x => x.Severity == "high").Select(x => x.App).Distinct().Take(4))),
                L.T("Неподписанные программы в сети, запуск из временных папок. Разберите в «Аномалиях»."), "#anomalies", 10));
    }

    private static readonly string[] RemoteNames = ["TeamViewer", "AnyDesk", "RustDesk", "UltraVNC", "TightVNC", "RealVNC", "VNC Server", "Splashtop", "ScreenConnect", "Chrome Remote Desktop", "Radmin", "Ammyy", "LogMeIn", "Supremo", "RemotePC", "Parsec"];

    private void RemoteTools(List<SecCheck> list)
    {
        string g = L.T("Программы"), title = L.T("Программы удалённого доступа");
        var found = programs.List(false).Items.Select(p => p.Name)
            .Where(n => RemoteNames.Any(r => n.Contains(r, StringComparison.OrdinalIgnoreCase))).Distinct().Take(6).ToList();
        list.Add(found.Count == 0
            ? C("remote", g, title, "ok", L.T("Не найдены."), null, null, 0)
            : C("remote", g, title, "info", L.T("Установлены: {0}.", string.Join(", ", found)),
                L.T("Удобно для поддержки, но через них же действуют мошенники («служба поддержки банка»). Удалите те, которыми не пользуетесь."), "#programs", 0));
    }
}
