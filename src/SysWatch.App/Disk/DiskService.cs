namespace SysWatch.App.Disk;

public sealed record DriveState(string Root, long Total, long Free);
public sealed record SnapshotView(long Id, long Ts, long Scanned, long Files, double Seconds, long? Shadow, long Hidden);
public sealed record CleanupView(long ScannedAt, IReadOnlyList<CleanupCategory> Categories);

public sealed record DiskState(
    string Status, string? Progress, DriveState Drive,
    SnapshotView? Snapshot, long? CompareTs, CleanupView? Cleanup, CleanupResult? LastClean, string? Error);

public sealed record TreeItem(string Path, string Name, bool IsDir, long Size, long Files, long? Delta);
public sealed record Tree(string Path, long Size, long? Delta, IReadOnlyList<TreeItem> Items, long Other);

public sealed record GrowthItem(string Path, long Size, long Delta, bool IsNew);
public sealed record Growth(long? Since, IReadOnlyList<GrowthItem> Items, long DriveDelta);

/// <summary>
/// Раздел «Диск»: снимки размеров папок, сравнение снимков и очистка. Тяжёлая работа — по одной за раз,
/// в отдельном фоновом потоке. Раз в сутки снимок делается сам, если компьютер не занят.
/// </summary>
public sealed class DiskService
{
    private readonly DiskStore _store = new();
    private readonly object _gate = new();
    private readonly string _root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;

    private string _status = "idle";
    private string? _progress, _error;
    private CleanupView? _cleanup;
    private CleanupResult? _lastClean;
    private CancellationTokenSource? _cts;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public DiskState State()
    {
        var drive = new DriveInfo(_root);
        var latest = _store.Latest();
        long? compare = latest is null ? null : _store.CompareTo(latest)?.Ts;
        SnapshotView? snap = latest is null ? null : new SnapshotView(
            latest.Id, latest.Ts, latest.Scanned, latest.Files, latest.Seconds, latest.Shadow,
            // То, что занято, но обходом не найдено: служебные файлы NTFS, теневые копии, недоступные папки.
            Math.Max(0, latest.Total - latest.Free - latest.Scanned));

        lock (_gate)
            return new DiskState(_status, _progress, new DriveState(_root, drive.TotalSize, drive.TotalFreeSpace),
                snap, compare, _cleanup, _lastClean, _error);
    }

    /// <summary>Полное сканирование: снимок диска, затем поиск того, что можно очистить.</summary>
    public bool StartScan() => Start("scanning", ct =>
    {
        var started = Now();
        var scanner = new DiskScanner();
        var result = scanner.Scan(_root, p => SetProgress($"Обхожу {p}"), ct);
        SetProgress("Проверяю точки восстановления");
        var drive = new DriveInfo(_root);
        var shadow = DiskScanner.ShadowStorage();
        _store.Save(new SnapshotInfo(0, started, _root, drive.TotalSize, drive.TotalFreeSpace, result.Size, result.Files, result.Seconds, shadow), result.Nodes);

        var cats = Cleanup.Scan(p => SetProgress($"Ищу, что можно очистить: {p}"), ct);
        lock (_gate) _cleanup = new CleanupView(Now(), cats);
    });

    /// <summary>Только поиск того, что можно очистить (быстрее полного снимка).</summary>
    public bool StartCleanupScan() => Start("scanning", ct =>
    {
        var cats = Cleanup.Scan(p => SetProgress($"Ищу, что можно очистить: {p}"), ct);
        lock (_gate) _cleanup = new CleanupView(Now(), cats);
    });

    public bool StartClean(IReadOnlyCollection<string> categories, IReadOnlyCollection<string> exclude)
    {
        CleanupView? view;
        lock (_gate) view = _cleanup;
        if (view is null) return false;

        return Start("cleaning", ct =>
        {
            var before = new DriveInfo(_root).TotalFreeSpace;
            var r = Cleanup.Clean(view.Categories, categories, exclude, p => SetProgress($"Удаляю: {p}"), ct);
            var after = new DriveInfo(_root).TotalFreeSpace;
            // Освобождено по факту диска (точнее суммы размеров, если файлы были жёсткими ссылками).
            lock (_gate) _lastClean = r with { Freed = Math.Max(r.Freed, after - before) };

            var cats = Cleanup.Scan(p => SetProgress($"Пересчитываю: {p}"), ct);
            lock (_gate) _cleanup = new CleanupView(Now(), cats);
        });
    }

    public void Cancel()
    {
        lock (_gate) _cts?.Cancel();
    }

    /// <summary>Вызывается из трея раз в час: снимок раз в сутки, когда процессор почти свободен.</summary>
    public void MaybeAutoScan(double systemCpu)
    {
        if (systemCpu > 25) return;
        var latest = _store.Latest();
        if (latest is not null && Now() - latest.Ts < 23 * 3600) return;
        StartScan();
    }

    private bool Start(string status, Action<CancellationToken> work)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_status != "idle") return false;
            _status = status;
            _progress = null;
            _error = null;
            _cts = cts = new CancellationTokenSource();
        }

        // Отдельный STA-поток: Shell API (Корзина) требует STA, а пул потоков трогать долгой работой не хочется.
        var thread = new Thread(() =>
        {
            try { work(cts.Token); }
            catch (OperationCanceledException) { lock (_gate) _error = "Остановлено."; }
            catch (Exception ex) { lock (_gate) _error = ex.Message; }
            finally
            {
                lock (_gate) { _status = "idle"; _progress = null; _cts = null; }
                cts.Dispose();
            }
        })
        { IsBackground = true, Name = "SysWatch disk", Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return true;
    }

    private void SetProgress(string p)
    {
        lock (_gate) _progress = p;
    }

    // ── Дерево и рост ────────────────────────────────────────────────────

    public Tree? Tree(string? path)
    {
        var latest = _store.Latest();
        if (latest is null) return null;
        var prev = _store.CompareTo(latest);

        path = string.IsNullOrEmpty(path) ? _root : path;
        var self = _store.Node(latest.Id, path);
        var children = _store.Children(latest.Id, path);

        var prevMap = prev is null ? null : _store.Children(prev.Id, path).ToDictionary(n => n.Path, StringComparer.OrdinalIgnoreCase);
        long? Delta(DiskNode n) => prevMap is null ? null : n.Size - (prevMap.TryGetValue(n.Path, out var p) ? p.Size : 0);

        var items = children.Select(n => new TreeItem(n.Path, Path.GetFileName(n.Path.TrimEnd('\\')), n.IsDir, n.Size, n.Files, Delta(n))).ToList();
        long size = self?.Size ?? items.Sum(i => i.Size);
        long? selfDelta = self is null || prev is null ? null : self.Size - (_store.Node(prev.Id, path)?.Size ?? 0);
        return new Tree(path, size, selfDelta, items, Math.Max(0, size - items.Sum(i => i.Size)));
    }

    /// <summary>
    /// Что выросло с прошлого снимка. Показываем самые конкретные папки:
    /// если почти весь рост родителя объясняется одной вложенной папкой, показываем её, а не родителя.
    /// </summary>
    public Growth Growth()
    {
        var latest = _store.Latest();
        if (latest is null) return new Growth(null, [], 0);
        var prev = _store.CompareTo(latest);
        if (prev is null) return new Growth(null, [], 0);

        var now = _store.All(latest.Id);
        var before = _store.All(prev.Id);
        const long Min = 300L << 20; // 300 МБ

        var grown = now.Values
            .Where(n => n.Path.Length > _root.Length)
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
