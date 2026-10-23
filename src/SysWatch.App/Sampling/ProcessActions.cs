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
        if (Protected.Contains(image)) return new(false, L.T("{0} — системный процесс, его завершение нарушит работу Windows.", image));
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
            ? new(false, L.T("{0} сейчас не запущена.", image))
            : new(fail == 0, fail == 0 ? L.T("Завершено процессов: {0}.", ok) : L.T("Завершено: {0}, не удалось: {1}.", ok, fail));
    }

    /// <summary>low — фоновый приоритет (уступает всем), normal — обычный.</summary>
    public static ActionResult Priority(string image, string level)
    {
        if (Protected.Contains(image)) return new(false, L.T("{0} — системный процесс, приоритет не меняем.", image));
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
        if (ok == 0 && fail == 0) return new(false, L.T("{0} сейчас не запущена.", image));
        var what = level == "low" ? L.T("низкий приоритет") : L.T("обычный приоритет");
        return new(fail == 0, fail > 0
            ? L.T("{0}: {1} процессов, не удалось: {2}. До перезапуска программы.", what, ok, fail)
            : L.T("{0}: {1} процессов. До перезапуска программы.", what, ok));
    }

    /// <summary>Открыть Проводник с выделенным файлом.</summary>
    public static ActionResult Reveal(string path)
    {
        if (!File.Exists(path)) return new(false, L.T("Файл не найден."));
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        return new(true, L.T("Открыто в Проводнике."));
    }
}
