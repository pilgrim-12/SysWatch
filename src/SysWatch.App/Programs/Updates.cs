using System.Diagnostics;
using System.Text;

namespace SysWatch.App.Programs;

public sealed record UpdateItem(string Name, string Id, string Version, string Available, string Source);

/// <summary>Ход установки: что ставится, что уже готово, что не получилось.</summary>
public sealed record UpdateJob(bool Running, string? Current, IReadOnlyList<string> Done, IReadOnlyList<string> Failed, string? Log);

public sealed record UpdatesView(bool Available, string? Error, long CheckedAt, IReadOnlyList<UpdateItem> Items, UpdateJob Job);

/// <summary>
/// Обновления программ через winget (встроен в Windows 10/11). Список кэшируется на 30 минут:
/// winget опрашивает источники 10–30 секунд.
/// </summary>
public sealed class Updates
{
    private readonly object _gate = new();
    private UpdatesView? _cache;
    private Task? _checking;
    private string? _current;
    private readonly List<string> _done = [], _failed = [];
    private Task? _job;
    private string? _lastLog;

    private UpdateJob Job()
    {
        lock (_gate) return new UpdateJob(_job is { IsCompleted: false }, _current, [.. _done], [.. _failed], _lastLog);
    }

    /// <summary>Отдаёт кэш сразу; если его нет или просили обновить — запускает проверку в фоне.</summary>
    public UpdatesView Get(bool refresh)
    {
        lock (_gate)
        {
            bool stale = _cache is null || refresh
                || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _cache.CheckedAt > 1800;
            if (stale && _checking is not { IsCompleted: false } && _job is not { IsCompleted: false })
                _checking = Task.Run(Check);
            var c = _cache ?? new UpdatesView(true, null, 0, [], Job());
            // Ошибка «winget не найден» хранится ключом словаря — переводим при отдаче.
            return c with { Job = Job(), Error = c.Error is null ? null : L.T(c.Error) };
        }
    }

    public bool Checking { get { lock (_gate) return _checking is { IsCompleted: false }; } }

    private void Check()
    {
        UpdatesView view;
        var (ok, output) = Run("upgrade --include-unknown --accept-source-agreements --disable-interactivity", TimeSpan.FromMinutes(2));
        if (output is null)
            view = new UpdatesView(false, L.K("winget не найден. Установите «Установщик приложений» (App Installer) из Microsoft Store."), Now(), [], Job());
        else
        {
            var items = Parse(output);
            // Пустой список при ошибке — это не «обновлений нет»: показываем, что сказал winget.
            view = new UpdatesView(true, items.Count == 0 && !ok ? Tail(output) : null, Now(), items, Job());
        }
        lock (_gate) _cache = view;
    }

    /// <summary>Поставить обновления по очереди (winget не умеет параллельно).</summary>
    public bool Install(IReadOnlyList<string> ids)
    {
        var list = ids.Where(i => i.Length is > 0 and < 200 && !i.Contains('"')).Distinct().ToList();
        if (list.Count == 0) return false;
        lock (_gate)
        {
            if (_job is { IsCompleted: false }) return false;
            _done.Clear(); _failed.Clear(); _lastLog = null;
            _job = Task.Run(() =>
            {
                foreach (var id in list)
                {
                    lock (_gate) _current = id;
                    var (ok, output) = Run($"upgrade --id \"{id}\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
                        TimeSpan.FromMinutes(20));
                    lock (_gate)
                    {
                        (ok ? _done : _failed).Add(id);
                        if (!ok) _lastLog = output is null ? null : Tail(output);
                        _current = null;
                    }
                }
                lock (_gate) _cache = null; // список изменился — перепроверим при следующем запросе
            });
        }
        return true;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static (bool Ok, string? Output) Run(string args, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo("winget.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return (false, null);
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout))
            {
                try { p.Kill(true); } catch (Exception) { }
                return (false, L.T("winget не ответил за {0} мин.", (int)timeout.TotalMinutes));
            }
            return (p.ExitCode == 0, stdout.Result + stderr.Result);
        }
        catch (System.ComponentModel.Win32Exception) { return (false, null); }
    }

    private static string Tail(string s)
    {
        var lines = Clean(s).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(6);
        return string.Join("\n", lines);
    }

    /// <summary>Убрать анимацию прогресса: winget перерисовывает строку через \r и рисует спиннер «-\|/».</summary>
    private static string Clean(string s) =>
        string.Join("\n", s.Replace("\r\n", "\n").Split('\n').Select(l => { int i = l.LastIndexOf('\r'); return i >= 0 ? l[(i + 1)..] : l; }));

    /// <summary>
    /// Таблица winget: строка заголовков (на языке Windows), под ней строка из «-», дальше строки с выравниванием по колонкам.
    /// Порядок колонок постоянный: имя, Id, версия, доступная версия, источник.
    /// </summary>
    internal static List<UpdateItem> Parse(string output)
    {
        var lines = Clean(output).Split('\n');
        var items = new List<UpdateItem>();
        for (int i = 1; i < lines.Length; i++)
        {
            if (!IsRule(lines[i])) continue;
            var header = lines[i - 1];
            var starts = new List<int>();
            for (int k = 0; k < header.Length; k++)
                if (header[k] != ' ' && (k == 0 || header[k - 1] == ' ')) starts.Add(k);
            if (starts.Count < 4) continue;

            for (int j = i + 1; j < lines.Length; j++)
            {
                var row = lines[j].TrimEnd();
                if (row.Length == 0 || IsRule(row)) break;
                if (row.Length < starts[3]) break; // строка-итог вроде «Доступно обновлений: 5»
                string Col(int c)
                {
                    int from = starts[c], to = c + 1 < starts.Count ? starts[c + 1] : row.Length;
                    if (from >= row.Length) return "";
                    return row[from..Math.Min(to, row.Length)].Trim();
                }
                var id = Col(1);
                if (id.Length == 0 || id.Contains(' ')) continue;
                items.Add(new UpdateItem(Col(0).TrimEnd('…'), id, Col(2), Col(3), starts.Count > 4 ? Col(4) : ""));
            }
        }
        return items;
    }

    private static bool IsRule(string l)
    {
        var t = l.Trim();
        return t.Length >= 10 && t.All(c => c == '-');
    }
}
