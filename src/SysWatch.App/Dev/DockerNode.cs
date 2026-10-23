using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SysWatch.App.Sampling;

namespace SysWatch.App.Dev;

public sealed record DockerImage(string Id, string Repository, string Tag, long Size, long Unique, int Containers, string Created);

public sealed record DockerContainer(string Id, string Name, string Image, string State, string Status, long Size);

public sealed record DockerVolume(string Name, long Size, int Links);

public sealed record DockerInfo(
    bool Installed, bool Running, string? Version, string? Note,
    IReadOnlyList<DockerImage> Images, IReadOnlyList<DockerContainer> Containers, IReadOnlyList<DockerVolume> Volumes,
    long BuildCache, long? DiskFile);

/// <summary>Проект с node_modules: сколько зависимостей объявлено и сколько места они занимают.</summary>
public sealed record NodeProject(string Path, string Name, int Dependencies, int DevDependencies, long ModulesSize, long LastModified);

/// <summary>
/// Docker через его собственную командную строку (docker.exe): образы, контейнеры, тома, кэш сборки.
/// Работает, только когда запущен Docker Desktop: без него движок недоступен.
/// </summary>
public static class Docker
{
    private static readonly Regex SafeId = new(@"^[A-Za-z0-9_.:/@-]{1,200}$");

    public static string? Exe()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", "docker.exe"),
        };
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null) return found;
        foreach (var dir in (Environment.GetEnvironmentVariable("Path") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var f = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim()), "docker.exe");
                if (File.Exists(f)) return f;
            }
            catch (Exception) { }
        }
        return null;
    }

    public static DockerInfo Info()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var vhd = new[] { Path.Combine(local, "Docker", "wsl", "disk", "docker_data.vhdx"), Path.Combine(local, "Docker", "wsl", "data", "ext4.vhdx") }
            .FirstOrDefault(File.Exists);
        long? disk = vhd is null ? null : new FileInfo(vhd).Length;

        var exe = Exe();
        if (exe is null) return new DockerInfo(false, false, null, null, [], [], [], 0, disk);

        var (ok, version) = Run(exe, "version --format {{.Server.Version}}", 10_000);
        if (!ok || string.IsNullOrWhiteSpace(version))
            return new DockerInfo(true, false, null, "Docker не запущен. Запустите Docker Desktop и нажмите «Проверить заново» — тогда будут видны образы, контейнеры и тома.", [], [], [], 0, disk);

        var images = new List<DockerImage>();
        var containers = new List<DockerContainer>();
        var volumes = new List<DockerVolume>();
        long cache = 0;

        // Одна команда отдаёт всё сразу и с размерами: образы, контейнеры, тома и кэш сборки.
        var (dfOk, df) = Run(exe, "system df -v --format \"{{json .}}\"", 60_000);
        if (dfOk && df.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(df);
                var root = doc.RootElement;
                foreach (var e in Arr(root, "Images"))
                    images.Add(new DockerImage(S(e, "ID"), S(e, "Repository"), S(e, "Tag"), Size(S(e, "Size")), Size(S(e, "UniqueSize")),
                        int.TryParse(S(e, "Containers"), out var n) ? n : 0, S(e, "CreatedSince")));
                foreach (var e in Arr(root, "Containers"))
                    containers.Add(new DockerContainer(S(e, "ID"), S(e, "Names"), S(e, "Image"), S(e, "State"), S(e, "Status"), Size(S(e, "Size").Split(' ')[0])));
                foreach (var e in Arr(root, "Volumes"))
                    volumes.Add(new DockerVolume(S(e, "Name"), Size(S(e, "Size")), int.TryParse(S(e, "Links"), out var l) ? l : 0));
                foreach (var e in Arr(root, "BuildCache"))
                    cache += Size(S(e, "Size"));
            }
            catch (JsonException) { images.Clear(); containers.Clear(); volumes.Clear(); }
        }

        // Старые версии Docker не умеют --format у system df — берём списки без подробных размеров.
        if (images.Count == 0 && containers.Count == 0)
        {
            foreach (var e in Lines(exe, "image ls --format \"{{json .}}\""))
                images.Add(new DockerImage(S(e, "ID"), S(e, "Repository"), S(e, "Tag"), Size(S(e, "Size")), 0, 0, S(e, "CreatedSince")));
            foreach (var e in Lines(exe, "ps -a --format \"{{json .}}\""))
                containers.Add(new DockerContainer(S(e, "ID"), S(e, "Names"), S(e, "Image"), S(e, "State"), S(e, "Status"), 0));
            foreach (var e in Lines(exe, "volume ls --format \"{{json .}}\""))
                volumes.Add(new DockerVolume(S(e, "Name"), 0, 0));
        }

        return new DockerInfo(true, true, version.Trim(), null,
            images.OrderByDescending(i => i.Size).ToList(),
            containers.OrderBy(c => c.State == "running" ? 0 : 1).ThenBy(c => c.Name).ToList(),
            volumes.OrderByDescending(v => v.Size).ToList(), cache, disk);
    }

    /// <summary>Действие над образом, контейнером или томом. Docker сам откажет, если объект занят.</summary>
    public static ActionResult Act(string action, string id)
    {
        var exe = Exe();
        if (exe is null) return new(false, "Docker не установлен.");
        if (action is "rmi" or "rm" or "stop" or "volume-rm" && !SafeId.IsMatch(id)) return new(false, "Некорректный идентификатор.");

        string args = action switch
        {
            "rmi" => $"image rm {id}",
            "rm" => $"container rm {id}",
            "stop" => $"container stop {id}",
            "volume-rm" => $"volume rm {id}",
            "prune-images" => "image prune -f",
            "prune-containers" => "container prune -f",
            "prune-cache" => "builder prune -f",
            _ => "",
        };
        if (args.Length == 0) return new(false, "Неизвестное действие.");

        var (ok, output) = Run(exe, args, 120_000, captureError: true);
        if (ok) return new(true, action.StartsWith("prune") ? Freed(output) : "Готово.");

        var err = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "Docker вернул ошибку.";
        if (err.Contains("is being used") || err.Contains("conflict"))
            err = "Объект используется контейнером — сначала удалите или остановите контейнер.";
        return new(false, err);
    }

    private static string Freed(string output)
    {
        var m = Regex.Match(output, @"reclaimed space:\s*(\S+)", RegexOptions.IgnoreCase);
        return m.Success ? $"Освобождено внутри Docker: {m.Groups[1].Value}. Файл диска Docker на Windows от этого не уменьшается." : "Готово.";
    }

    private static IEnumerable<JsonElement> Arr(JsonElement root, string name) =>
        root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static string S(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";

    private static IEnumerable<JsonElement> Lines(string exe, string args)
    {
        var (ok, text) = Run(exe, args, 30_000);
        if (!ok) yield break;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonElement el;
            try { using var d = JsonDocument.Parse(line); el = d.RootElement.Clone(); }
            catch (JsonException) { continue; }
            yield return el;
        }
    }

    /// <summary>Размер в виде Docker: "1.23GB", "512MB", "0B", "12.5kB" (десятичные единицы).</summary>
    public static long Size(string s)
    {
        var m = Regex.Match(s ?? "", @"([\d.]+)\s*([kKMGT]?)B");
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return 0;
        return (long)(v * (m.Groups[2].Value.ToUpperInvariant() switch { "K" => 1e3, "M" => 1e6, "G" => 1e9, "T" => 1e12, _ => 1 }));
    }

    private static (bool Ok, string Output) Run(string exe, string args, int timeoutMs, bool captureError = false)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
            })!;
            var err = p.StandardError.ReadToEndAsync();
            var outText = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch (Exception) { } return (false, "Docker не ответил вовремя."); }
            var text = outText.Result + (captureError ? "\n" + err.Result : "");
            return (p.ExitCode == 0, p.ExitCode == 0 ? text : outText.Result + "\n" + err.Result);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}

/// <summary>Проекты Node в типичных папках разработки и их node_modules.</summary>
public static class NodeProjects
{
    public static IEnumerable<string> Roots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string>
        {
            Path.Combine(home, "source", "repos"), Path.Combine(home, "Projects"), Path.Combine(home, "projects"),
            Path.Combine(home, "Documents", "GitHub"), Path.Combine(home, "dev"), Path.Combine(home, "code"),
        };
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            foreach (var name in new[] { "GIT", "Git", "git", "Projects", "Dev", "dev", "src", "repos", "Work" })
                list.Add(Path.Combine(d.RootDirectory.FullName, name));
        return list.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static List<NodeProject> Scan()
    {
        var result = new List<NodeProject>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Roots())
            Walk(root, 0, result, seen);
        return result.OrderByDescending(p => p.ModulesSize).ToList();
    }

    /// <summary>Ищем package.json на глубине до 3 папок; внутрь node_modules и скрытых папок не заходим.</summary>
    private static void Walk(string dir, int depth, List<NodeProject> into, HashSet<string> seen)
    {
        if (depth > 3 || !seen.Add(dir)) return;
        string[] subdirs;
        try { subdirs = Directory.GetDirectories(dir); }
        catch (Exception) { return; }

        var pkg = Path.Combine(dir, "package.json");
        var modules = Path.Combine(dir, "node_modules");
        if (File.Exists(pkg) && Directory.Exists(modules))
        {
            int deps = 0, dev = 0;
            string name = Path.GetFileName(dir);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pkg));
                var r = doc.RootElement;
                if (r.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } nn) name = nn;
                if (r.TryGetProperty("dependencies", out var d) && d.ValueKind == JsonValueKind.Object) deps = d.EnumerateObject().Count();
                if (r.TryGetProperty("devDependencies", out var dd) && dd.ValueKind == JsonValueKind.Object) dev = dd.EnumerateObject().Count();
            }
            catch (Exception) { }
            long size = DirSize(modules);
            long modified = new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir)).ToUnixTimeSeconds();
            try { modified = Math.Max(modified, new DateTimeOffset(File.GetLastWriteTimeUtc(pkg)).ToUnixTimeSeconds()); } catch (Exception) { }
            into.Add(new NodeProject(dir, name, deps, dev, size, modified));
        }

        foreach (var s in subdirs)
        {
            var n = Path.GetFileName(s);
            if (n == "node_modules" || n.StartsWith('.') || n is "bin" or "obj" or "dist" or "build") continue;
            Walk(s, depth + 1, into, seen);
        }
    }

    public static ActionResult DeleteModules(string projectPath, IReadOnlyCollection<NodeProject> known)
    {
        if (!known.Any(p => p.Path.Equals(projectPath, StringComparison.OrdinalIgnoreCase)))
            return new(false, "Этого проекта нет в списке.");
        var modules = Path.Combine(projectPath, "node_modules");
        if (!Directory.Exists(modules)) return new(false, "node_modules уже нет.");
        try
        {
            // Удаляем навсегда: node_modules восстанавливается командой npm install.
            Directory.Delete(modules, recursive: true);
            return new(true, $"node_modules удалена. Вернуть: npm install в папке {projectPath}");
        }
        catch (Exception ex)
        {
            return new(false, "Часть файлов занята (открыт редактор или dev-сервер?): " + ex.Message);
        }
    }

    private static long DirSize(string dir)
    {
        try
        {
            var opts = new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return new DirectoryInfo(dir).EnumerateFiles("*", opts).Sum(f => f.Length);
        }
        catch (Exception) { return 0; }
    }
}
