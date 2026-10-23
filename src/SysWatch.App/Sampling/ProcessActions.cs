using System.Diagnostics;

namespace SysWatch.App.Sampling;

public sealed record ActionResult(bool Ok, string Message);

/// <summary>Действия над процессами программы по имени exe: завершить, сменить приоритет, показать файл.</summary>
public static class ProcessActions
{
    // Без этих процессов Windows падает или выходит из сеанса — их не трогаем.
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe",
        "svchost.exe", "dwm.exe", "fontdrvhost.exe", "LsaIso.exe", "Memory Compression", "Secure System",
        "SysWatch.exe", "SysWatch.Agent.exe", "MsMpEng.exe",
    };

    private static Process[] ByName(string image) =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(image));

    public static ActionResult Kill(string image)
    {
        if (Protected.Contains(image)) return new(false, $"{image} — системный процесс, его завершение нарушит работу Windows.");
        int ok = 0, fail = 0;
        foreach (var p in ByName(image))
        {
            using (p)
            {
                try { p.Kill(entireProcessTree: true); ok++; }
                catch (Exception) { fail++; }
            }
        }
        return ok == 0 && fail == 0
            ? new(false, $"{image} сейчас не запущена.")
            : new(fail == 0, fail == 0 ? $"Завершено процессов: {ok}." : $"Завершено: {ok}, не удалось: {fail}.");
    }

    /// <summary>low — фоновый приоритет (уступает всем), normal — обычный.</summary>
    public static ActionResult Priority(string image, string level)
    {
        if (Protected.Contains(image)) return new(false, $"{image} — системный процесс, приоритет не меняем.");
        var cls = level == "low" ? ProcessPriorityClass.Idle : ProcessPriorityClass.Normal;
        int ok = 0, fail = 0;
        foreach (var p in ByName(image))
        {
            using (p)
            {
                try { p.PriorityClass = cls; ok++; }
                catch (Exception) { fail++; }
            }
        }
        if (ok == 0 && fail == 0) return new(false, $"{image} сейчас не запущена.");
        var what = level == "low" ? "низкий приоритет" : "обычный приоритет";
        return new(fail == 0, $"{what}: {ok} процессов{(fail > 0 ? $", не удалось: {fail}" : "")}. До перезапуска программы.");
    }

    /// <summary>Открыть Проводник с выделенным файлом.</summary>
    public static ActionResult Reveal(string path)
    {
        if (!File.Exists(path)) return new(false, "Файл не найден.");
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        return new(true, "Открыто в Проводнике.");
    }
}
