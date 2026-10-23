using System.Runtime.InteropServices;

namespace SysWatch.App.Disk;

public sealed record CleanupItem(string Path, long Size, long Files, long Modified);

/// <summary>Категория очистки. Mode: delete — навсегда, recycle — в Корзину, none — только показать.</summary>
public sealed record CleanupCategory(
    string Id, string Group, string Title, string Description, string Mode,
    bool DefaultOn, bool ItemsSelectable, string? Hint,
    long Size, long Files, IReadOnlyList<CleanupItem> Items);

public sealed record CleanupResult(long Ts, long Freed, long Deleted, long Recycled, long Skipped);

/// <summary>
/// Поиск того, что можно удалить, и само удаление. Удаляется только внутри заранее известных папок;
/// занятые файлы пропускаются; временные файлы моложе суток не трогаем — ими могут пользоваться прямо сейчас.
/// </summary>
public static class Cleanup
{
    private sealed record Def(
        string Id, string Group, string Title, string Description, string Mode, bool DefaultOn,
        int MinAgeDays, Func<IEnumerable<string>> Roots, Func<string, bool>? FileFilter = null, string? Hint = null);

    private static string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
    private static readonly string Local = Env(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Roaming = Env(Environment.SpecialFolder.ApplicationData);
    private static readonly string Home = Env(Environment.SpecialFolder.UserProfile);
    private static readonly string Win = Env(Environment.SpecialFolder.Windows);
    private static readonly string ProgramData = Env(Environment.SpecialFolder.CommonApplicationData);

    private static readonly EnumerationOptions Recursive = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static IEnumerable<string> Existing(params string[] paths) => paths.Where(Directory.Exists);

    private static IEnumerable<string> ChromiumCaches(string userData)
    {
        if (!Directory.Exists(userData)) yield break;
        foreach (var profile in Directory.EnumerateDirectories(userData))
        {
            var name = Path.GetFileName(profile);
            if (name != "Default" && !name.StartsWith("Profile ", StringComparison.Ordinal)) continue;
            foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage" })
            {
                var p = Path.Combine(profile, sub);
                if (Directory.Exists(p)) yield return p;
            }
        }
    }

    private static IEnumerable<string> FirefoxCaches()
    {
        var profiles = Path.Combine(Local, @"Mozilla\Firefox\Profiles");
        if (!Directory.Exists(profiles)) yield break;
        foreach (var p in Directory.EnumerateDirectories(profiles))
            if (Directory.Exists(Path.Combine(p, "cache2"))) yield return Path.Combine(p, "cache2");
    }

    private static readonly Def[] Defs =
    [
        new("temp", "system", L.K("Временные файлы"), L.K("Папки Temp пользователя и Windows. Файлы моложе суток не трогаем."), "delete", true, 1,
            () => Existing(Path.GetTempPath().TrimEnd('\\'), Path.Combine(Win, "Temp"))),
        new("windows_update", "system", L.K("Загрузки обновлений Windows"), L.K("Уже установленные обновления, которые Windows скачала заранее."), "delete", true, 0,
            () => Existing(Path.Combine(Win, @"SoftwareDistribution\Download"),
                Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"))),
        new("browser_cache", "system", L.K("Кэш браузеров"), L.K("Chrome, Edge, Firefox. Сайты загрузятся заново. Открытые браузеры держат часть файлов — их пропустим."), "delete", true, 0,
            () => ChromiumCaches(Path.Combine(Local, @"Google\Chrome\User Data"))
                .Concat(ChromiumCaches(Path.Combine(Local, @"Microsoft\Edge\User Data")))
                .Concat(ChromiumCaches(Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data")))
                .Concat(FirefoxCaches())),
        new("crash_dumps", "system", L.K("Дампы падений и отчёты об ошибках"), L.K("Снимки памяти упавших программ и отчёты для Microsoft."), "delete", true, 0,
            () => Existing(Path.Combine(Local, "CrashDumps"), Path.Combine(Local, @"Microsoft\Windows\WER"),
                Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue"),
                Path.Combine(Win, "Minidump"), Path.Combine(Win, "LiveKernelReports"))),
        new("thumbnails", "system", L.K("Кэш миниатюр"), L.K("Превью картинок в Проводнике. Пересоздастся сам."), "delete", true, 0,
            () => Existing(Path.Combine(Local, @"Microsoft\Windows\Explorer")),
            f => Path.GetFileName(f).StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase)),

        new("nuget", "dev", L.K("Пакеты NuGet"), L.K("Скачанные .NET-пакеты. При следующей сборке скачаются заново."), "delete", false, 0,
            () => Existing(Path.Combine(Home, @".nuget\packages"), Path.Combine(Local, @"NuGet\v3-cache"), Path.Combine(Local, @"NuGet\http-cache"))),
        new("npm", "dev", L.K("Кэш npm, pnpm и Yarn"), L.K("Скачанные JavaScript-пакеты. При установке скачаются заново."), "delete", false, 0,
            () => Existing(Path.Combine(Local, "npm-cache"), Path.Combine(Roaming, "npm-cache"), Path.Combine(Home, "npm-cache"),
                Path.Combine(Local, @"pnpm\store"), Path.Combine(Local, @"pnpm-cache"), Path.Combine(Local, @"Yarn\Cache"))),
        new("pip", "dev", L.K("Кэш pip"), L.K("Скачанные Python-пакеты."), "delete", false, 0,
            () => Existing(Path.Combine(Local, @"pip\cache"))),
        new("gradle", "dev", L.K("Кэш Gradle"), L.K("Зависимости Android- и Java-проектов. При сборке скачаются заново."), "delete", false, 0,
            () => Existing(Path.Combine(Home, @".gradle\caches"), Path.Combine(Home, @".gradle\wrapper\dists"))),
    ];

    // ── Сканирование ─────────────────────────────────────────────────────

    public static List<CleanupCategory> Scan(Action<string> progress, CancellationToken ct)
    {
        var list = new List<CleanupCategory>();

        foreach (var d in Defs)
        {
            ct.ThrowIfCancellationRequested();
            progress(d.Title);
            var items = new List<CleanupItem>();
            foreach (var root in SafeRoots(d))
            {
                var (size, files, modified) = Measure(root, d.MinAgeDays, d.FileFilter, ct);
                if (files > 0) items.Add(new CleanupItem(root, size, files, modified));
            }
            list.Add(Category(d.Id, d.Group, d.Title, d.Description, d.Mode, d.DefaultOn, false, d.Hint, items));
        }

        progress(L.K("Корзина"));
        var (binSize, binFiles) = RecycleBinSize();
        list.Add(Category("recycle_bin", "system", L.K("Корзина"), L.K("Удалённые файлы, которые ещё можно восстановить. Очистка безвозвратна."), "delete", false, false, null,
            binFiles > 0 ? new List<CleanupItem> { new(L.K("Корзина"), binSize, binFiles, 0) } : new List<CleanupItem>()));

        progress(L.K("Старые сборки в проектах"));
        list.Add(Category("old_builds", "dev", L.K("Старые node_modules, bin, obj, .next"),
            L.K("Папки сборок в проектах, которые не менялись 30+ дней. Уйдут в Корзину; вернуть — npm install или dotnet build."),
            "recycle", false, true, null, OldBuilds(ct)));

        progress(L.K("Загрузки"));
        list.Add(Category("downloads", "review", L.K("Большие старые файлы в «Загрузках»"),
            L.K("Файлы от 50 МБ, которые не открывали 90+ дней. Ничего не отмечено — выбери сам. Уйдут в Корзину."),
            "recycle", false, true, null, OldDownloads(ct)));

        list.Add(Docker());
        list.Add(WindowsOld(ct));
        return list;
    }

    private static CleanupCategory Category(string id, string group, string title, string desc, string mode, bool on, bool selectable, string? hint, List<CleanupItem> items)
    {
        items.Sort((a, b) => b.Size.CompareTo(a.Size));
        return new CleanupCategory(id, group, title, desc, mode, on, selectable, hint,
            items.Sum(i => i.Size), items.Sum(i => i.Files), items.Take(200).ToList());
    }

    private static IEnumerable<string> SafeRoots(Def d)
    {
        try { return d.Roots().Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Exception) { return []; }
    }

    private static (long Size, long Files, long Modified) Measure(string root, int minAgeDays, Func<string, bool>? filter, CancellationToken ct)
    {
        long size = 0, files = 0, modified = 0;
        var cutoff = DateTime.UtcNow.AddDays(-minAgeDays);
        try
        {
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", Recursive))
            {
                if ((files & 1023) == 0) ct.ThrowIfCancellationRequested();
                if (filter is not null && !filter(f.FullName)) continue;
                DateTime w;
                try { w = f.LastWriteTimeUtc; } catch { continue; }
                if (minAgeDays > 0 && w > cutoff) continue;
                try { size += f.Length; } catch { continue; }
                files++;
                modified = Math.Max(modified, new DateTimeOffset(w).ToUnixTimeSeconds());
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* папка исчезла или нет доступа */ }
        return (size, files, modified);
    }

    private static readonly string[] ProjectRoots =
    [
        @"C:\GIT", Path.Combine(Home, "source"), Path.Combine(Home, "Desktop"), Path.Combine(Home, "Documents"),
        Path.Combine(Home, "AndroidStudioProjects"), Path.Combine(Home, "Projects"), Path.Combine(Home, "repos"),
    ];

    private static List<CleanupItem> OldBuilds(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var found = new List<CleanupItem>();
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

        void Walk(string dir, int depth)
        {
            if (depth > 7) return;
            ct.ThrowIfCancellationRequested();
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir, "*", opts).ToList(); }
            catch (Exception) { return; }

            foreach (var s in subs)
            {
                var name = Path.GetFileName(s);
                bool candidate = name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(".next", StringComparison.OrdinalIgnoreCase)
                    || ((name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("obj", StringComparison.OrdinalIgnoreCase)) && HasProjectFile(dir));

                if (candidate)
                {
                    var (size, files, modified) = Measure(s, 0, null, ct);
                    if (files > 0 && DateTimeOffset.FromUnixTimeSeconds(modified).UtcDateTime < cutoff)
                        found.Add(new CleanupItem(s, size, files, modified));
                    continue; // внутрь папок сборки не спускаемся
                }
                if (name.StartsWith('.') && name != ".github") continue; // .git, .vs, .idea
                Walk(s, depth + 1);
            }
        }

        foreach (var root in ProjectRoots.Where(Directory.Exists)) Walk(root, 0);
        return found;
    }

    private static bool HasProjectFile(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.*proj").Any(f =>
                f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { return false; }
    }

    private static List<CleanupItem> OldDownloads(CancellationToken ct)
    {
        var dir = Path.Combine(Home, "Downloads");
        var list = new List<CleanupItem>();
        if (!Directory.Exists(dir)) return list;
        var cutoff = DateTime.UtcNow.AddDays(-90);
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", Recursive))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (f.Length < 50L << 20) continue;
                    var used = f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc;
                    if (used > cutoff) continue;
                    list.Add(new CleanupItem(f.FullName, f.Length, 1, new DateTimeOffset(used).ToUnixTimeSeconds()));
                }
                catch (Exception) { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { }
        return list;
    }

    private static CleanupCategory Docker()
    {
        var candidates = new[]
        {
            Path.Combine(Local, @"Docker\wsl\disk\docker_data.vhdx"),
            Path.Combine(Local, @"Docker\wsl\data\ext4.vhdx"),
            Path.Combine(Local, @"Docker\wsl\main\ext4.vhdx"),
        };
        var items = candidates.Where(File.Exists)
            .Select(p => new FileInfo(p))
            .Select(f => new CleanupItem(f.FullName, f.Length, 1, new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeSeconds()))
            .ToList();
        return Category("docker", "review", "Docker", L.K("Образы, контейнеры и тома живут внутри одного файла-диска. Удалять сам файл нельзя."),
            "none", false, false, L.K("docker system prune -a --volumes   — удалит неиспользуемое; затем в Docker Desktop: Troubleshoot → Clean / Purge data"), items);
    }

    private static CleanupCategory WindowsOld(CancellationToken ct)
    {
        var path = Path.Combine(Path.GetPathRoot(Win)!, "Windows.old");
        var items = new List<CleanupItem>();
        if (Directory.Exists(path))
        {
            var (size, files, modified) = Measure(path, 0, null, ct);
            items.Add(new CleanupItem(path, size, files, modified));
        }
        return Category("windows_old", "review", L.K("Предыдущая версия Windows"), L.K("Остаётся после крупного обновления, нужна только для отката."),
            "none", false, false, L.K("Параметры → Система → Память → Временные файлы → «Предыдущие установки Windows»"), items);
    }

    // ── Удаление ─────────────────────────────────────────────────────────

    public static CleanupResult Clean(IReadOnlyList<CleanupCategory> scanned, IReadOnlyCollection<string> categories,
        IReadOnlyCollection<string> exclude, Action<string> progress, CancellationToken ct)
    {
        var excluded = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
        long freed = 0, deleted = 0, recycled = 0, skipped = 0;

        foreach (var cat in scanned.Where(c => categories.Contains(c.Id) && c.Mode != "none"))
        {
            ct.ThrowIfCancellationRequested();
            progress(cat.Title);

            if (cat.Id == "recycle_bin")
            {
                var (size, files) = RecycleBinSize();
                if (EmptyRecycleBin()) { freed += size; deleted += files; }
                continue;
            }

            var def = Defs.FirstOrDefault(d => d.Id == cat.Id);
            foreach (var item in cat.Items.Where(i => !excluded.Contains(i.Path)))
            {
                ct.ThrowIfCancellationRequested();
                if (!IsAllowed(cat, item.Path)) { skipped += item.Files; continue; }

                if (cat.Mode == "recycle")
                {
                    progress(item.Path);
                    if (SendToRecycleBin(item.Path)) { recycled += item.Files; }
                    else skipped += item.Files;
                }
                else
                {
                    var (bytes, ok, fail) = DeleteContents(item.Path, def?.MinAgeDays ?? 0, def?.FileFilter, ct);
                    freed += bytes; deleted += ok; skipped += fail;
                }
            }
        }

        return new CleanupResult(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), freed, deleted, recycled, skipped);
    }

    /// <summary>
    /// Последняя защита: удаляем только то, что сами нашли при сканировании этой категории,
    /// и никогда — корень диска, Windows, Program Files или профиль целиком.
    /// </summary>
    private static bool IsAllowed(CleanupCategory cat, string path)
    {
        if (!cat.Items.Any(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return false;
        var full = Path.GetFullPath(path).TrimEnd('\\');
        string[] forbidden =
        [
            Path.GetPathRoot(full)!.TrimEnd('\\'), Win, Env(Environment.SpecialFolder.ProgramFiles),
            Env(Environment.SpecialFolder.ProgramFilesX86), Home, Local, Roaming, ProgramData,
        ];
        return !forbidden.Any(f => full.Equals(f.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }

    private static (long Bytes, long Ok, long Fail) DeleteContents(string root, int minAgeDays, Func<string, bool>? filter, CancellationToken ct)
    {
        long bytes = 0, ok = 0, fail = 0;
        var cutoff = DateTime.UtcNow.AddDays(-minAgeDays);
        List<FileInfo> files;
        try { files = new DirectoryInfo(root).EnumerateFiles("*", Recursive).ToList(); }
        catch (Exception) { return (0, 0, 0); }

        foreach (var f in files)
        {
            if ((ok & 1023) == 0) ct.ThrowIfCancellationRequested();
            if (filter is not null && !filter(f.FullName)) continue;
            try
            {
                if (minAgeDays > 0 && f.LastWriteTimeUtc > cutoff) continue;
                long len = f.Length;
                if (f.IsReadOnly) f.IsReadOnly = false;
                f.Delete();
                bytes += len;
                ok++;
            }
            catch (Exception) { fail++; } // файл занят — пропускаем
        }

        // Пустые подпапки убираем, саму корневую папку категории оставляем.
        try
        {
            foreach (var d in Directory.EnumerateDirectories(root, "*", Recursive).OrderByDescending(p => p.Length))
            {
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }
                catch (Exception) { }
            }
        }
        catch (Exception) { }

        return (bytes, ok, fail);
    }

    // ── Корзина и Shell API ──────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)] // x64: обычное выравнивание по 8 байт
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    private const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

    private static (long Size, long Items) RecycleBinSize()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    private static bool EmptyRecycleBin() =>
        SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND) is 0 or unchecked((int)0x8000FFFF);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private const uint FO_DELETE = 0x3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    /// <summary>Переместить файл или папку в Корзину (через Проводник, как обычное удаление).</summary>
    private static bool SendToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }
}
