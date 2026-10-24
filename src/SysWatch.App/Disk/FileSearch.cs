using System.IO.Enumeration;

namespace SysWatch.App.Disk;

public sealed record SearchQuery(string? Text, string? Root, string? Kind, long MinSize, int Days, bool Folders);
public sealed record SearchHit(string Path, string Name, bool IsDir, long Size, long Modified);
public sealed record SearchState(bool Running, SearchQuery? Query, long Dirs, long Files, string? Current,
    long Started, double Seconds, bool Truncated, string? Error, IReadOnlyList<SearchHit> Hits);

/// <summary>
/// Поиск файлов по всему диску: имя или маска (*.mp4), тип, размер, дата изменения.
/// Идёт в фоне с низким приоритетом, результатов — не больше <see cref="Limit"/>.
/// Ничего не индексирует и не хранит: каждый поиск — свежий обход.
/// </summary>
public sealed class FileSearch
{
    public const int Limit = 1000;

    private static readonly Dictionary<string, string[]> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video"] = ["mp4", "mkv", "avi", "mov", "wmv", "webm", "m4v", "flv", "mpg", "mpeg", "ts"],
        ["image"] = ["jpg", "jpeg", "png", "gif", "bmp", "webp", "heic", "tif", "tiff", "raw", "cr2", "nef", "svg"],
        ["audio"] = ["mp3", "wav", "flac", "m4a", "aac", "ogg", "wma", "opus"],
        ["doc"] = ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "odt", "ods", "csv", "md"],
        ["archive"] = ["zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "img", "cab"],
        ["installer"] = ["exe", "msi", "msix", "appx", "msixbundle"],
        ["disk"] = ["vhd", "vhdx", "vmdk", "vdi", "qcow2"],
        ["code"] = ["cs", "ts", "tsx", "js", "jsx", "py", "java", "cpp", "c", "h", "go", "rs", "sql", "json", "xml", "yml", "yaml"],
    };

    private readonly object _lock = new();
    private readonly List<SearchHit> _hits = new();
    private CancellationTokenSource? _cts;
    private SearchQuery? _query;
    private long _dirs, _files, _started, _ended;
    private string? _current, _error;
    private bool _running, _truncated;

    public SearchState State()
    {
        lock (_lock)
        {
            long end = _running ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : _ended;
            return new SearchState(_running, _query, Interlocked.Read(ref _dirs), Interlocked.Read(ref _files), _current,
                _started / 1000, _started > 0 ? Math.Max(0, end - _started) / 1000.0 : 0, _truncated, _error, _hits.ToList());
        }
    }

    public void Cancel() => _cts?.Cancel();

    public bool Start(SearchQuery q)
    {
        var text = q.Text?.Trim() ?? "";
        if (text.Length == 0 && string.IsNullOrEmpty(q.Kind) && q.MinSize <= 0 && q.Days <= 0) return false;

        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            _cts = cts;
            _query = q with { Text = text };
            _hits.Clear();
            _dirs = _files = 0;
            _current = null; _error = null; _truncated = false;
            _running = true;
            _started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        var thread = new Thread(() => Run(_query!, cts)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "FileSearch" };
        thread.Start();
        return true;
    }

    private static IEnumerable<string> Roots(string? root)
    {
        if (!string.IsNullOrEmpty(root) && root != "all") return [root.EndsWith('\\') ? root : root + "\\"];
        return DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable).Select(d => d.RootDirectory.FullName);
    }

    /// <summary>Сопоставление имени: маска со звёздочкой или вопросом — как в проводнике, иначе — «содержит».</summary>
    public static Func<string, bool> NameMatcher(string text)
    {
        if (text.Length == 0) return _ => true;
        // «.pdf» — то же, что «*.pdf». Несколько масок — любая из них (*.mp4 *.mkv), слова — все сразу.
        var parts = text.Split(new[] { ' ', ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Length > 1 && p[0] == '.' && !p.Contains('*') ? "*" + p : p).ToArray();
        var masks = parts.Where(p => p.Contains('*') || p.Contains('?')).ToArray();
        var words = parts.Except(masks).ToArray();
        return name =>
            (masks.Length == 0 || masks.Any(m => FileSystemName.MatchesSimpleExpression(m, name, ignoreCase: true))) &&
            words.All(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private void Run(SearchQuery q, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var match = NameMatcher(q.Text ?? "");
        HashSet<string>? exts = q.Kind is { Length: > 0 } k && Kinds.TryGetValue(k, out var list) ? new(list, StringComparer.OrdinalIgnoreCase) : null;
        long minTime = q.Days > 0 ? DateTimeOffset.UtcNow.AddDays(-q.Days).ToFileTime() : 0;
        bool folders = q.Folders && exts is null && q.MinSize <= 0;

        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = 0, // OneDrive — reparse point, но не ссылка: его обходим, ссылки отсекаем ниже
            ReturnSpecialDirectories = false,
        };

        try
        {
            foreach (var root in Roots(q.Root))
            {
                if (ct.IsCancellationRequested) break;
                var e = new FileSystemEnumerable<SearchHit>(root,
                    (ref FileSystemEntry x) => new SearchHit(x.ToFullPath(), x.FileName.ToString(), x.IsDirectory,
                        x.IsDirectory ? 0 : x.Length, x.LastWriteTimeUtc.ToUnixTimeSeconds()), opts)
                {
                    ShouldRecursePredicate = (ref FileSystemEntry x) =>
                    {
                        if (ct.IsCancellationRequested) return false;
                        if ((x.Attributes & FileAttributes.ReparsePoint) != 0 && DiskScanner.IsLink(new DirectoryInfo(x.ToFullPath()))) return false;
                        if (Interlocked.Increment(ref _dirs) % 200 == 0) _current = x.ToFullPath();
                        return true;
                    },
                    ShouldIncludePredicate = (ref FileSystemEntry x) =>
                    {
                        if (ct.IsCancellationRequested) return false;
                        if (x.IsDirectory)
                        {
                            if (!folders) return false;
                        }
                        else
                        {
                            Interlocked.Increment(ref _files);
                            if (q.MinSize > 0 && x.Length < q.MinSize) return false;
                            if (exts is not null && !exts.Contains(Path.GetExtension(x.FileName).TrimStart('.').ToString())) return false;
                        }
                        if (minTime > 0 && x.LastWriteTimeUtc.ToFileTime() < minTime) return false;
                        return match(x.FileName.ToString());
                    },
                };

                foreach (var hit in e)
                {
                    lock (_lock)
                    {
                        if (_cts != cts) return; // начат новый поиск
                        if (_hits.Count >= Limit) { _truncated = true; cts.Cancel(); break; }
                        _hits.Add(hit);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_lock) if (_cts == cts) _error = ex.Message;
        }
        finally
        {
            lock (_lock)
            {
                if (_cts == cts)
                {
                    _running = false;
                    _current = null;
                    _ended = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _hits.Sort((a, b) => b.Size.CompareTo(a.Size));
                }
            }
        }
    }
}
