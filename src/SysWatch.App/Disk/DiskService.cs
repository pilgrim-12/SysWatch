namespace SysWatch.App.Disk;

public sealed record DriveInfoView(string Root, string Label, string Format, string Kind, long Total, long Free, bool IsSystem, long? LastScan);
public sealed record SnapshotView(long Id, long Ts, long Scanned, long Files, double Seconds, long? Shadow, long Hidden);
public sealed record CleanupView(long ScannedAt, IReadOnlyList<CleanupCategory> Categories);

/// <summary>Прогресс текущей операции. Target — сколько всего занято на диске (к этому стремится Bytes).</summary>
public sealed record OpProgress(string Phase, long Bytes, long Target, long Files, long Dirs, long Started, string? Current, int Step, int Steps);

public sealed record DiskState(
    string Status, string? Progress, OpProgress? Op, string? BusyRoot, IReadOnlyList<DriveInfoView> Drives, string Root,
    SnapshotView? Snapshot, long? CompareTs, CleanupView? Cleanup, CleanupResult? LastClean, string? Error);

public sealed record TreeChild(string Name, bool IsDir, long Size);
public sealed record TreeItem(string Path, string Name, bool IsDir, long Size, long Files, long? Delta, IReadOnlyList<TreeChild> Children, long Other);
public sealed record Tree(string Path, long Size, long? Delta, IReadOnlyList<TreeItem> Items, long Other);

public sealed record GrowthItem(string Path, long Size, long Delta, bool IsNew);
public sealed record Growth(long? Since, IReadOnlyList<GrowthItem> Items, long DriveDelta);

/// <summary>
/// Раздел «Диск»: снимки размеров папок по каждому диску, сравнение снимков и очистка системного диска.
/// Тяжёлая работа — по одной за раз, в отдельном фоновом потоке. Системный диск раз в сутки сканируется сам.
/// </summary>
public sealed class DiskService
{
    private readonly DiskStore _store = new();
    private readonly object _gate = new();
    private readonly string _system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;

    private string _status = "idle";
    // Текст прогресса — русский шаблон и аргумент: переводится при каждом запросе состояния на язык окна.
    private string? _progTpl, _progArg, _progRaw, _error, _busyRoot;
    private OpProgress? _op;
    private CleanupView? _cleanup;
    private CleanupResult? _lastClean;
    private CancellationTokenSource? _cts;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Локальные диски, которые готовы к чтению: встроенные и подключённые по USB.</summary>
    public List<DriveInfoView> Drives()
    {
        var scans = _store.LastScans();
        var list = new List<DriveInfoView>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            try
            {
                if (!d.IsReady) continue;
                list.Add(new DriveInfoView(
                    d.RootDirectory.FullName,
                    string.IsNullOrWhiteSpace(d.VolumeLabel) ? (d.DriveType == DriveType.Removable ? L.T("Съёмный диск") : L.T("Локальный диск")) : d.VolumeLabel,
                    d.DriveFormat,
                    d.DriveType == DriveType.Removable ? "removable" : "fixed",
                    d.TotalSize, d.TotalFreeSpace,
                    d.RootDirectory.FullName.Equals(_system, StringComparison.OrdinalIgnoreCase),
                    scans.TryGetValue(d.RootDirectory.FullName, out var ts) ? ts : null));
            }
            catch (IOException) { /* диск отключили во время опроса */ }
        }
        return list;
    }

    private string Normalize(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return _system;
        var r = Path.GetPathRoot(root);
        return string.IsNullOrEmpty(r) ? _system : r.ToUpperInvariant();
    }

    public DiskState State(string? root)
    {
        root = Normalize(root);
        var drives = Drives();
        var drive = drives.FirstOrDefault(d => d.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
        var latest = _store.Latest(root);
        long? compare = latest is null ? null : _store.CompareTo(latest)?.Ts;
        SnapshotView? snap = latest is null ? null : new SnapshotView(
            latest.Id, latest.Ts, latest.Scanned, latest.Files, latest.Seconds, latest.Shadow,
            // Занято, но обходом не найдено: служебные файлы NTFS, теневые копии, недоступные папки.
            Math.Max(0, (drive?.Total ?? latest.Total) - (drive?.Free ?? latest.Free) - latest.Scanned));

        bool isSystem = root.Equals(_system, StringComparison.OrdinalIgnoreCase);
        lock (_gate)
        {
            string? progress = Progress();
            var op = _op is not null && _progRaw is not null && _op.Current == _progRaw ? _op with { Current = progress } : _op;
            return new DiskState(_status, progress, op, _busyRoot, drives, root, snap, compare,
                isSystem ? Localize(_cleanup) : null, isSystem ? _lastClean : null, _error is null ? null : L.T(_error));
        }
    }

    /// <summary>Снимок диска; для системного диска — ещё и поиск того, что можно очистить.</summary>
    public bool StartScan(string? root)
    {
        root = Normalize(root);
        var target = root;
        return Start("scanning", target, ct =>
        {
            var started = Now();
            var d0 = new DriveInfo(target);
            long used = d0.TotalSize - d0.TotalFreeSpace;
            bool system = target.Equals(_system, StringComparison.OrdinalIgnoreCase);
            int steps = system ? 2 : 1;
            var result = new DiskScanner().Scan(target, p =>
            {
                lock (_gate)
                {
                    (_progTpl, _progArg, _progRaw) = (L.K("Обхожу {0}"), p.Current, null);
                    _op = new OpProgress("disk", p.Bytes, used, p.Files, p.Dirs, started, p.Current, 1, steps);
                }
            }, ct);
            SetProgress(L.K("Проверяю точки восстановления"));
            var drive = new DriveInfo(target);
            var shadow = DiskScanner.ShadowStorage(target);
            _store.Save(new SnapshotInfo(0, started, target, drive.TotalSize, drive.TotalFreeSpace, result.Size, result.Files, result.Seconds, shadow), result.Nodes);

            if (system)
            {
                lock (_gate) _op = new OpProgress("cleanup", result.Size, used, result.Files, result.Dirs, started, null, 2, steps);
                var cats = Cleanup.Scan(p => SetProgress(L.K("Ищу, что можно очистить: {0}"), p), ct);
                lock (_gate) _cleanup = new CleanupView(Now(), cats);
            }
        });
    }

    public bool StartCleanupScan() => Start("scanning", _system, ct =>
    {
        var cats = Cleanup.Scan(p => SetProgress(L.K("Ищу, что можно очистить: {0}"), p), ct);
        lock (_gate) _cleanup = new CleanupView(Now(), cats);
    });

    public bool StartClean(IReadOnlyCollection<string> categories, IReadOnlyCollection<string> exclude)
    {
        CleanupView? view;
        lock (_gate) view = _cleanup;
        if (view is null) return false;

        return Start("cleaning", _system, ct =>
        {
            var before = new DriveInfo(_system).TotalFreeSpace;
            var r = Cleanup.Clean(view.Categories, categories, exclude, p => SetProgress(L.K("Удаляю: {0}"), p), ct);
            var after = new DriveInfo(_system).TotalFreeSpace;
            // Освобождено по факту диска — точнее суммы размеров, если файлы были жёсткими ссылками.
            lock (_gate) _lastClean = r with { Freed = Math.Max(r.Freed, after - before) };

            var cats = Cleanup.Scan(p => SetProgress(L.K("Пересчитываю: {0}"), p), ct);
            lock (_gate) _cleanup = new CleanupView(Now(), cats);
        });
    }

    public void Cancel()
    {
        lock (_gate) _cts?.Cancel();
    }

    /// <summary>Вызывается из трея раз в час: системный диск раз в сутки, когда процессор почти свободен.</summary>
    public void MaybeAutoScan(double systemCpu)
    {
        if (systemCpu > 25) return;
        var latest = _store.Latest(_system);
        if (latest is not null && Now() - latest.Ts < 23 * 3600) return;
        StartScan(_system);
    }

    private bool Start(string status, string root, Action<CancellationToken> work)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_status != "idle") return false;
            _status = status;
            _busyRoot = root;
            _progTpl = null; _progArg = null; _progRaw = null;
            _op = new OpProgress(status == "cleaning" ? "clean" : "start", 0, 0, 0, 0, Now(), null, 1, 1);
            _error = null;
            _cts = cts = new CancellationTokenSource();
        }

        // Отдельный STA-поток: Shell API (Корзина) требует STA, а пул потоков долгой работой не занимаем.
        var thread = new Thread(() =>
        {
            try { work(cts.Token); }
            catch (OperationCanceledException) { lock (_gate) _error = L.K("Остановлено."); }
            catch (Exception ex) { lock (_gate) _error = ex.Message; }
            finally
            {
                lock (_gate) { _status = "idle"; _progTpl = null; _progArg = null; _progRaw = null; _op = null; _busyRoot = null; _cts = null; }
                cts.Dispose();
            }
        })
        { IsBackground = true, Name = "SysWatch disk", Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return true;
    }

    /// <param name="tpl">Русский шаблон (L.K) с {0}; arg — подстановка (путь или название категории очистки).</param>
    private void SetProgress(string tpl, string? arg = null)
    {
        lock (_gate)
        {
            _progTpl = tpl;
            _progArg = arg;
            _progRaw = arg is null ? tpl : string.Format(tpl, arg);
            if (_op is not null) _op = _op with { Current = _progRaw };
        }
    }

    /// <summary>Прогресс на языке текущего запроса. Аргумент тоже пробуем перевести — это может быть название категории.</summary>
    private string? Progress() =>
        _progTpl is null ? null : _progArg is null ? L.T(_progTpl) : L.T(_progTpl, L.T(_progArg));

    /// <summary>Категории очистки хранятся по-русски (ключи словаря), переводим при отдаче в окно.</summary>
    private static CleanupView? Localize(CleanupView? v) => v is null ? null : v with
    {
        Categories = v.Categories.Select(c => c with
        {
            Title = L.T(c.Title),
            Description = L.T(c.Description),
            Hint = c.Hint is null ? null : L.T(c.Hint),
            // У Корзины вместо пути — подпись; настоящие пути не переводим.
            Items = c.Items.Select(i => i.Path.Contains('\\') ? i : i with { Path = L.T(i.Path) }).ToList(),
        }).ToList(),
    };

    // ── Дерево и рост ────────────────────────────────────────────────────

    private static string NameOf(string path) => Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path;

    /// <summary>Содержимое папки на два уровня вглубь: для списка и для карты диска.</summary>
    public Tree? Tree(string? root, string? path)
    {
        root = Normalize(root);
        var latest = _store.Latest(root);
        if (latest is null) return null;
        var prev = _store.CompareTo(latest);

        path = string.IsNullOrEmpty(path) ? root : path;
        var self = _store.Node(latest.Id, path);
        var children = _store.Children(latest.Id, path);

        var prevMap = prev is null ? null : _store.Children(prev.Id, path).ToDictionary(n => n.Path, StringComparer.OrdinalIgnoreCase);
        long? Delta(DiskNode n) => prevMap is null ? null : n.Size - (prevMap.TryGetValue(n.Path, out var p) ? p.Size : 0);

        var items = children.Select(n =>
        {
            var grand = n.IsDir ? _store.Children(latest.Id, n.Path) : new List<DiskNode>();
            var sub = grand.Take(40).Select(g => new TreeChild(NameOf(g.Path), g.IsDir, g.Size)).ToList();
            return new TreeItem(n.Path, NameOf(n.Path), n.IsDir, n.Size, n.Files, Delta(n), sub, Math.Max(0, n.Size - sub.Sum(x => x.Size)));
        }).ToList();

        long size = self?.Size ?? items.Sum(i => i.Size);
        long? selfDelta = self is null || prev is null ? null : self.Size - (_store.Node(prev.Id, path)?.Size ?? 0);
        return new Tree(path, size, selfDelta, items, Math.Max(0, size - items.Sum(i => i.Size)));
    }

    /// <summary>
    /// Что выросло с прошлого снимка. Показываем самые конкретные папки:
    /// если почти весь рост родителя объясняется одной вложенной папкой, показываем её, а не родителя.
    /// </summary>
    public Growth Growth(string? root)
    {
        root = Normalize(root);
        var latest = _store.Latest(root);
        if (latest is null) return new Growth(null, [], 0);
        var prev = _store.CompareTo(latest);
        if (prev is null) return new Growth(null, [], 0);

        var now = _store.All(latest.Id);
        var before = _store.All(prev.Id);
        const long Min = 300L << 20; // 300 МБ

        var grown = now.Values
            .Where(n => n.Path.Length > root.Length)
            .Select(n => (Node: n, Delta: n.Size - (before.TryGetValue(n.Path, out var b) ? b.Size : 0), IsNew: !before.ContainsKey(n.Path)))
            .Where(x => x.Delta >= Min)
            .ToList();

        var byParent = grown.Where(g => g.Node.Parent is not null).ToLookup(g => g.Node.Parent!, StringComparer.OrdinalIgnoreCase);
        var specific = grown
            .Where(g => !byParent[g.Node.Path].Any(c => c.Delta >= g.Delta * 0.6))
            .OrderByDescending(g => g.Delta)
            .Take(15)
            .Select(g => new GrowthItem(g.Node.Path, g.Node.Size, g.Delta, g.IsNew))
            .ToList();

        long driveDelta = (latest.Total - latest.Free) - (prev.Total - prev.Free);
        return new Growth(prev.Ts, specific, driveDelta);
    }
}
