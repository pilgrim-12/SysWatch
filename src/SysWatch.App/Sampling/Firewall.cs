using System.Diagnostics;
using System.Text.Json;
using SysWatch.App.Anomalies;

namespace SysWatch.App.Sampling;

public sealed record BlockedApp(string Path, long Since);

/// <summary>
/// Запрет выхода в сеть для программы через Брандмауэр Windows: два правила (входящее и исходящее)
/// с именем «SysWatch: запрет сети — путь». Снимается удалением этих правил. Список ведём сами,
/// потому что вывод netsh зависит от языка Windows; наличие правила проверяем по коду возврата.
/// </summary>
public sealed class Firewall(AnomalyStore state)
{
    private const string Key = "firewall_blocked";
    private readonly object _gate = new();

    private static string RuleName(string path) => $"SysWatch: запрет сети — {path}";

    public bool IsBlocked(string path) => File.Exists(path) && Netsh($"advfirewall firewall show rule name=\"{RuleName(path)}\"") == 0;

    public ActionResult Block(string path)
    {
        if (!File.Exists(path)) return new(false, L.T("Файл программы не найден."));
        if (path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
            return new(false, L.T("Это системный файл Windows — блокировка может сломать сеть или обновления."));

        lock (_gate)
        {
            Netsh($"advfirewall firewall delete rule name=\"{RuleName(path)}\"");
            int a = Netsh($"advfirewall firewall add rule name=\"{RuleName(path)}\" dir=out action=block program=\"{path}\" enable=yes");
            int b = Netsh($"advfirewall firewall add rule name=\"{RuleName(path)}\" dir=in action=block program=\"{path}\" enable=yes");
            if (a != 0 || b != 0) return new(false, L.T("Брандмауэр Windows не принял правило. Он включён?"));

            var list = Load();
            list.RemoveAll(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            list.Add(new BlockedApp(path, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            Save(list);
        }
        return new(true, L.T("Выход в сеть запрещён. Уже открытые соединения закроются, новые не откроются."));
    }

    public ActionResult Unblock(string path)
    {
        lock (_gate)
        {
            Netsh($"advfirewall firewall delete rule name=\"{RuleName(path)}\"");
            var list = Load();
            list.RemoveAll(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            Save(list);
        }
        return new(true, L.T("Сеть снова разрешена."));
    }

    /// <summary>Заблокированные программы; правила, удалённые вручную в Брандмауэре, из списка убираем.</summary>
    public List<BlockedApp> List()
    {
        lock (_gate)
        {
            var list = Load();
            int before = list.Count;
            list.RemoveAll(x => !IsBlocked(x.Path));
            if (list.Count != before) Save(list);
            return list;
        }
    }

    private List<BlockedApp> Load()
    {
        try { return JsonSerializer.Deserialize<List<BlockedApp>>(state.Get(Key) ?? "[]") ?? []; }
        catch (JsonException) { return []; }
    }

    private void Save(List<BlockedApp> list) => state.Set(Key, JsonSerializer.Serialize(list));

    private static int Netsh(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit(15_000);
        return p.HasExited ? p.ExitCode : -1;
    }
}
