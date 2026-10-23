using Microsoft.Win32;
using SysWatch.App.Data;

namespace SysWatch.App.Sys;

public sealed record StartupItem(
    string Id, string Name, string Where, string Command, string? Path,
    bool Enabled, string Signature, string? Signer, bool CanToggle);

/// <summary>
/// Автозагрузка, как во вкладке «Автозагрузка» Диспетчера задач: ключи Run и папки «Автозагрузка».
/// Включение и выключение — тем же способом, что и у Диспетчера (ключи StartupApproved),
/// поэтому выключенное здесь видно выключенным и там, и наоборот. Сама запись не удаляется.
/// </summary>
public sealed class Startup(Signatures signatures)
{
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    private sealed record Source(RegistryKey Hive, string HiveName, string RunPath, string ApprovedSub, string Where);

    private static readonly Source[] RunSources =
    [
        new(Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", L.K("Для этого пользователя")),
        new(Registry.LocalMachine, "HKLM", @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", L.K("Для всех пользователей")),
        new(Registry.LocalMachine, "HKLM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", L.K("Для всех пользователей (32-бит)")),
    ];

    public List<StartupItem> List()
    {
        var list = new List<StartupItem>();

        foreach (var src in RunSources)
        {
            using var run = src.Hive.OpenSubKey(src.RunPath);
            if (run is null) continue;
            foreach (var name in run.GetValueNames())
            {
                var cmd = run.GetValue(name)?.ToString() ?? "";
                var path = ExeOf(cmd);
                var (sig, signer) = signatures.Check(path);
                list.Add(new StartupItem($"run|{src.HiveName}|{src.ApprovedSub}|{name}", name, L.T(src.Where), cmd, path,
                    IsEnabled(src.Hive, src.ApprovedSub, name), sig, signer, true));
            }
        }

        foreach (var (folder, hive) in new[] { (Environment.SpecialFolder.Startup, Registry.CurrentUser), (Environment.SpecialFolder.CommonStartup, Registry.LocalMachine) })
        {
            var dir = Environment.GetFolderPath(folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var file = System.IO.Path.GetFileName(f);
                if (file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(f) : f;
                var (sig, signer) = signatures.Check(target);
                list.Add(new StartupItem($"folder|{(hive == Registry.CurrentUser ? "HKCU" : "HKLM")}|StartupFolder|{file}",
                    System.IO.Path.GetFileNameWithoutExtension(file),
                    hive == Registry.CurrentUser ? L.T("Папка «Автозагрузка»") : L.T("Общая папка «Автозагрузка»"),
                    target ?? f, target, IsEnabled(hive, "StartupFolder", file), sig, signer, true));
            }
        }

        return list.OrderByDescending(i => i.Enabled).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Включить или выключить запуск. Значение: 02… — включено, 03… + время выключения — выключено.</summary>
    public bool Toggle(string id, bool enabled)
    {
        var parts = id.Split('|', 4);
        if (parts.Length != 4) return false;
        var hive = parts[1] == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
        using var key = hive.CreateSubKey($@"{Approved}\{parts[2]}", writable: true);
        if (key is null) return false;

        var data = new byte[12];
        if (enabled)
        {
            data[0] = 0x02;
        }
        else
        {
            data[0] = 0x03;
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        }
        key.SetValue(parts[3], data, RegistryValueKind.Binary);
        return true;
    }

    private static bool IsEnabled(RegistryKey hive, string sub, string name)
    {
        using var key = hive.OpenSubKey($@"{Approved}\{sub}");
        // Нет записи — значит, включено (так считает и Диспетчер задач). Чётный первый байт — включено.
        return key?.GetValue(name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
    }

    /// <summary>Путь к exe из командной строки: "C:\a b\x.exe" --flag или C:\x.exe /min.</summary>
    public static string? ExeOf(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : null;
        }
        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? cmd[..(exe + 4)] : cmd.Split(' ')[0];
    }

    /// <summary>Цель ярлыка .lnk через COM WScript.Shell.</summary>
    private static string? ShortcutTarget(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic sc = shell.CreateShortcut(lnk);
            string target = sc.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch (Exception) { return null; }
    }
}
