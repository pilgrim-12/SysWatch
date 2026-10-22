using Microsoft.Win32;

namespace SysWatch.App.Anomalies;

/// <summary>Одна запись автозапуска: где, как называется и что запускает.</summary>
public sealed record AutostartEntry(string Key, string Where, string Name, string Command, bool Critical);

/// <summary>
/// Снимок всего, что запускается само: ключи Run/RunOnce, папки «Автозагрузка»,
/// задачи планировщика, службы и драйверы с автостартом.
/// </summary>
public static class Autostart
{
    private static readonly (RegistryKey Hive, string HiveName, string Path)[] RunKeys =
    [
        (Registry.LocalMachine, "HKLM", @"Software\Microsoft\Windows\CurrentVersion\Run"),
        (Registry.LocalMachine, "HKLM", @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
        (Registry.LocalMachine, "HKLM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
        (Registry.LocalMachine, "HKLM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce"),
        (Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run"),
        (Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
    ];

    public static List<AutostartEntry> Snapshot()
    {
        var list = new List<AutostartEntry>();

        foreach (var (hive, hiveName, path) in RunKeys)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key is null) continue;
                foreach (var name in key.GetValueNames())
                {
                    var cmd = key.GetValue(name)?.ToString() ?? "";
                    list.Add(new AutostartEntry($"run|{hiveName}\\{path}|{name}|{cmd}", $"Реестр {hiveName}\\…\\{Path.GetFileName(path)}", name, cmd, true));
                }
            }
            catch (Exception) { /* нет доступа — пропускаем */ }
        }

        foreach (var folder in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
        {
            var dir = Environment.GetFolderPath(folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new AutostartEntry($"startup|{f}", "Папка «Автозагрузка»", Path.GetFileName(f), f, true));
            }
        }

        var tasks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks");
        if (Directory.Exists(tasks))
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var f in Directory.EnumerateFiles(tasks, "*", opts))
            {
                var rel = Path.GetRelativePath(tasks, f);
                list.Add(new AutostartEntry($"task|{rel}", "Планировщик заданий", rel, "", true));
            }
        }

        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is not null)
            {
                foreach (var name in services.GetSubKeyNames())
                {
                    using var s = services.OpenSubKey(name);
                    if (s?.GetValue("ImagePath") is not { } image) continue;
                    if (s.GetValue("Start") is not int start || start > 2) continue; // 0–2: загрузка, система, авто
                    int type = s.GetValue("Type") is int t ? t : 0;
                    bool driver = (type & 0x3) != 0;
                    var cmd = image.ToString() ?? "";
                    list.Add(new AutostartEntry($"service|{name}|{cmd}", driver ? "Драйвер" : "Служба", name, cmd, false));
                }
            }
        }
        catch (Exception) { /* нет доступа — пропускаем */ }

        return list;
    }
}
