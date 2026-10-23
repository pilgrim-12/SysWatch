using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SysWatch.App.Programs;
using SysWatch.App.Sampling;

namespace SysWatch.App.Dev;

/// <summary>Один установленный инструмент, SDK или среда выполнения.</summary>
/// <param name="Support">eol — поддержка закончилась, soon — закончится в ближайшие полгода, ok, lts, null — неизвестно.</param>
/// <param name="UninstallId">Запись в «Программах», через которую можно удалить (штатный деинсталлятор).</param>
public sealed record DevItem(
    string Name, string Version, string? Path, long? Size, string? Support, long? Until, string? Note, string? UninstallId);

public sealed record DevGroup(string Id, string Title, string? Hint, IReadOnlyList<DevItem> Items);

/// <summary>Служба разработчика (база, брокер, Docker): состояние, тип запуска, память.</summary>
public sealed record DevService(string Name, string Display, string State, string StartMode, int? Pid, long? Memory, string? Path);

public sealed record PathEntry(string Dir, string Scope, bool Exists, bool Duplicate);

/// <summary>Какой файл реально запустится по команде и какие другие копии он заслоняет.</summary>
public sealed record CommandResolution(string Command, string? Path, string? Version, IReadOnlyList<string> Shadowed, string? Note);

public sealed record DevEnvironment(
    long ScannedAt, double Seconds, IReadOnlyList<DevGroup> Groups, IReadOnlyList<DevService> Services,
    IReadOnlyList<CommandResolution> Commands, IReadOnlyList<PathEntry> Path,
    DockerInfo? Docker, IReadOnlyList<NodeProject> Projects);

public sealed record DevState(bool Busy, DevEnvironment? Data);

/// <summary>
/// Инвентаризация среды разработки: .NET, Node, Python, Java, Go, Rust, Android SDK, Visual Studio, WSL, Docker,
/// службы баз данных и PATH. Только чтение реестра и файловой системы, без сети. Сканирование в фоне, результат в памяти.
/// </summary>
public sealed class DevEnv(Installed installed)
{
    private readonly object _gate = new();
    private DevEnvironment? _data;
    private string? _dataLang; // тексты (заметки, названия групп) формируются на языке сканирования
    private bool _busy;

    private static string Pf => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static string Pf86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public DevState State(bool refresh)
    {
        lock (_gate)
        {
            // Сменили язык окна — пересканируем, чтобы заметки и названия были на новом языке.
            var lang = L.Lang;
            bool stale = _data is null || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _data.ScannedAt > 600 || _dataLang != lang;
            if ((refresh || stale) && !_busy)
            {
                _busy = true;
                var t = new Thread(() =>
                {
                    L.UseForRequest(lang);
                    DevEnvironment? result = null;
                    try { result = Scan(); }
                    catch (Exception) { /* оставим прошлый результат */ }
                    lock (_gate) { if (result is not null) { _data = result; _dataLang = lang; } _busy = false; }
                }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "DevEnv scan" };
                t.Start();
            }
            return new DevState(_busy, _data);
        }
    }

    // ── Сроки поддержки (справочник на осень 2026) ───────────────────────

    private static readonly Dictionary<string, DateOnly> DotnetEol = new()
    {
        ["1.0"] = new(2019, 6, 27), ["1.1"] = new(2019, 6, 27), ["2.0"] = new(2018, 10, 1), ["2.1"] = new(2021, 8, 21),
        ["2.2"] = new(2019, 12, 23), ["3.0"] = new(2020, 3, 3), ["3.1"] = new(2022, 12, 13), ["5.0"] = new(2022, 5, 10),
        ["6.0"] = new(2024, 11, 12), ["7.0"] = new(2024, 5, 14), ["8.0"] = new(2026, 11, 10), ["9.0"] = new(2026, 11, 10),
        ["10.0"] = new(2028, 11, 14),
    };

    private static readonly Dictionary<int, DateOnly> NodeEol = new()
    {
        [10] = new(2021, 4, 30), [12] = new(2022, 4, 30), [14] = new(2023, 4, 30), [16] = new(2023, 9, 11),
        [17] = new(2022, 6, 1), [18] = new(2025, 4, 30), [19] = new(2023, 6, 1), [20] = new(2026, 4, 30),
        [21] = new(2024, 6, 1), [22] = new(2027, 4, 30), [23] = new(2025, 6, 1), [24] = new(2028, 4, 30),
        [25] = new(2026, 6, 1), [26] = new(2029, 4, 30),
    };

    private static readonly Dictionary<string, DateOnly> PythonEol = new()
    {
        ["2.7"] = new(2020, 1, 1), ["3.6"] = new(2021, 12, 23), ["3.7"] = new(2023, 6, 27), ["3.8"] = new(2024, 10, 7),
        ["3.9"] = new(2025, 10, 31), ["3.10"] = new(2026, 10, 31), ["3.11"] = new(2027, 10, 31),
        ["3.12"] = new(2028, 10, 31), ["3.13"] = new(2029, 10, 31), ["3.14"] = new(2030, 10, 31),
    };

    private static (string? Support, long? Until) ByDate(DateOnly? end)
    {
        if (end is not { } e) return (null, null);
        var today = DateOnly.FromDateTime(DateTime.Today);
        long until = new DateTimeOffset(e.ToDateTime(TimeOnly.MinValue)).ToUnixTimeSeconds();
        return (e < today ? "eol" : e < today.AddMonths(6) ? "soon" : "ok", until);
    }

    // ── Сканирование ─────────────────────────────────────────────────────

    private DevEnvironment Scan()
    {
        var sw = Stopwatch.StartNew();
        var programs = installed.List(false).Items;
        var groups = new List<DevGroup>();

        void Add(string id, string title, string? hint, Func<List<DevItem>> f)
        {
            try
            {
                var items = f();
                if (items.Count > 0) groups.Add(new DevGroup(id, title, hint, items));
            }
            catch (Exception) { /* один сломанный источник не мешает остальным */ }
        }

        Add("dotnet", ".NET", L.T("Для каждой основной версии обычно хватает последнего SDK: более старые патчи можно удалить."), () => Dotnet(programs));
        Add("netfx", ".NET Framework", null, NetFramework);
        Add("node", "Node.js", null, () => Node(programs));
        Add("npm", L.T("Глобальные пакеты npm"), null, NpmGlobal);
        Add("python", "Python", null, () => Python(programs));
        Add("java", "Java (JDK)", L.T("Без поддержки — версии не-LTS: на них не выходят исправления безопасности."), () => Java(programs));
        Add("android", "Android SDK", L.T("Нужен для React Native и Android Studio. Старые платформы и build-tools — частый источник лишних гигабайт."), Android);
        Add("langs", L.T("Другие языки"), null, () => OtherLangs(programs));
        Add("ide", L.T("IDE и редакторы"), null, () => Ides(programs));
        Add("winsdk", L.T("Windows SDK и Visual C++"), null, () => WindowsSdk(programs));
        Add("containers", L.T("WSL и Docker"), L.T("Диски WSL и Docker сами не уменьшаются после удаления данных внутри — их нужно сжимать отдельно."), () => Containers(programs));
        Add("tools", L.T("Инструменты командной строки"), null, () => CliTools(programs));
        Add("pm", L.T("Менеджеры пакетов"), null, PackageManagers);

        var services = new List<DevService>();
        try { services = Services(); } catch (Exception) { }

        var (commands, path) = PathInfo();

        DockerInfo? docker = null;
        try { docker = Docker.Info(); } catch (Exception) { }
        var projects = new List<NodeProject>();
        try { projects = NodeProjects.Scan(); } catch (Exception) { }

        return new DevEnvironment(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), sw.Elapsed.TotalSeconds, groups, services, commands, path, docker, projects);
    }

    // ── .NET ─────────────────────────────────────────────────────────────

    private static List<DevItem> Dotnet(IReadOnlyList<ProgramRow> programs)
    {
        var list = new List<DevItem>();
        foreach (var root in new[] { Path.Combine(Pf, "dotnet"), Path.Combine(Pf86, "dotnet") }.Distinct())
        {
            if (!Directory.Exists(root)) continue;
            bool x86 = root.StartsWith(Pf86, StringComparison.OrdinalIgnoreCase) && Pf86 != Pf;
            string arch = x86 ? " (x86)" : "";

            foreach (var dir in Dirs(Path.Combine(root, "sdk")).Where(d => char.IsDigit(Path.GetFileName(d)[0])).OrderByDescending(VersionKey))
            {
                var v = Path.GetFileName(dir);
                var (s, u) = ByDate(DotnetEol.TryGetValue(Major2(v), out var eol) ? eol : null);
                list.Add(new DevItem("SDK" + arch, v, dir, DirSize(dir), s, u, null,
                    Match(programs, p => p.Name.Contains(".NET", StringComparison.OrdinalIgnoreCase) && p.Name.Contains("SDK") && p.Name.Contains(v))));
            }

            foreach (var (fw, label) in new[] { ("Microsoft.NETCore.App", "Runtime"), ("Microsoft.AspNetCore.App", "ASP.NET Core"), ("Microsoft.WindowsDesktop.App", "Desktop Runtime") })
            {
                foreach (var dir in Dirs(Path.Combine(root, "shared", fw)).OrderByDescending(VersionKey))
                {
                    var v = Path.GetFileName(dir);
                    var (s, u) = ByDate(DotnetEol.TryGetValue(Major2(v), out var eol) ? eol : null);
                    var token = fw switch { "Microsoft.NETCore.App" => ".NET Runtime", "Microsoft.AspNetCore.App" => "ASP.NET Core", _ => "Desktop Runtime" };
                    list.Add(new DevItem(label + arch, v, dir, DirSize(dir), s, u, null,
                        Match(programs, p => p.Name.Contains(token, StringComparison.OrdinalIgnoreCase) && p.Name.Contains(v))));
                }
            }
        }

        // Рабочие нагрузки (MAUI, wasm-tools и т. п.) — большие папки packs и workloads.
        var packs = Path.Combine(Pf, "dotnet", "packs");
        if (Directory.Exists(packs))
            list.Add(new DevItem(L.T("Packs и workloads"), "", packs, DirSize(packs), null, null, L.T("MAUI, Android, iOS, WebAssembly и другие нагрузки"), null));
        return list;
    }

    private static List<DevItem> NetFramework()
    {
        var list = new List<DevItem>();
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
        if (k?.GetValue("Release") is int rel)
        {
            var v = rel switch
            {
                >= 533320 => "4.8.1", >= 528040 => "4.8", >= 461808 => "4.7.2", >= 461308 => "4.7.1", >= 460798 => "4.7",
                >= 394802 => "4.6.2", >= 394254 => "4.6.1", >= 393295 => "4.6", >= 379893 => "4.5.2", _ => "4.5",
            };
            bool supported = rel >= 394802;
            list.Add(new DevItem(".NET Framework 4.x", v, k.GetValue("InstallPath") as string, null,
                supported ? "ok" : "eol", null, L.T("Входит в Windows, удалить нельзя"), null));
        }
        using var k35 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5");
        if (k35?.GetValue("Install") is int i && i == 1)
            list.Add(new DevItem(".NET Framework 3.5", (k35.GetValue("Version") as string) ?? "3.5", null, null, "ok", null, L.T("Компонент Windows: отключается в «Компонентах Windows»"), null));
        return list;
    }

    // ── Node.js ──────────────────────────────────────────────────────────

    private static List<DevItem> Node(IReadOnlyList<ProgramRow> programs)
    {
        var list = new List<DevItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // source — «установщик» (официальный) или имя менеджера версий (nvm, Volta, fnm — имена не переводим).
        void AddNode(string exe, string source, bool installer = false)
        {
            if (!File.Exists(exe) || !seen.Add(Path.GetFullPath(exe))) return;
            var v = (FileVersion(exe) ?? "").TrimStart('v');
            int major = int.TryParse(v.Split('.')[0], out var m) ? m : 0;
            var (s, u) = ByDate(NodeEol.TryGetValue(major, out var d) ? d : null);
            if (major is > 0 and < 10) s = "eol";
            var dir = Path.GetDirectoryName(exe)!;
            list.Add(new DevItem("Node.js", v, dir, installer ? null : DirSize(dir), s, u, source,
                installer ? Match(programs, p => p.Name.StartsWith("Node.js", StringComparison.OrdinalIgnoreCase)) : null));
        }

        AddNode(Path.Combine(Pf, "nodejs", "node.exe"), L.T("установщик"), installer: true);

        var nvm = Environment.GetEnvironmentVariable("NVM_HOME", EnvironmentVariableTarget.User)
                  ?? Environment.GetEnvironmentVariable("NVM_HOME", EnvironmentVariableTarget.Machine)
                  ?? Path.Combine(Roaming, "nvm");
        foreach (var d in Dirs(nvm).Where(d => Path.GetFileName(d).StartsWith('v')).OrderByDescending(VersionKey))
            AddNode(Path.Combine(d, "node.exe"), "nvm");

        foreach (var d in Dirs(Path.Combine(Local, "Volta", "tools", "image", "node")))
            AddNode(Path.Combine(d, "node.exe"), "Volta");

        foreach (var d in Dirs(Path.Combine(Roaming, "fnm", "node-versions")))
            AddNode(Path.Combine(d, "installation", "node.exe"), "fnm");

        return list;
    }

    /// <summary>
    /// Глобальные пакеты: у каждого менеджера версий Node своя папка. nvm-windows держит их внутри каждой версии,
    /// обычный установщик — в %APPDATA%\npm (или в prefix из .npmrc), а pnpm и yarn — в своих папках.
    /// </summary>
    private static List<DevItem> NpmGlobal()
    {
        var list = new List<DevItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<(string Dir, string Where)> { (Path.Combine(Roaming, "npm", "node_modules"), "npm") };

        try
        {
            var npmrc = Path.Combine(home, ".npmrc");
            if (File.Exists(npmrc))
                foreach (var line in File.ReadLines(npmrc))
                    if (line.Trim().StartsWith("prefix", StringComparison.OrdinalIgnoreCase) && line.Contains('='))
                        roots.Add((Path.Combine(Environment.ExpandEnvironmentVariables(line[(line.IndexOf('=') + 1)..].Trim()), "node_modules"), L.T("npm (prefix из .npmrc)")));
        }
        catch (Exception) { }

        var nvm = Environment.GetEnvironmentVariable("NVM_HOME", EnvironmentVariableTarget.User)
                  ?? Environment.GetEnvironmentVariable("NVM_HOME", EnvironmentVariableTarget.Machine)
                  ?? Path.Combine(Roaming, "nvm");
        foreach (var d in Dirs(nvm).Where(d => Path.GetFileName(d).StartsWith('v')))
            roots.Add((Path.Combine(d, "node_modules"), "Node " + Path.GetFileName(d)));
        roots.Add((Path.Combine(Pf, "nodejs", "node_modules"), L.T("Node (установщик)")));
        foreach (var d in Dirs(Path.Combine(Local, "pnpm", "global")))
            roots.Add((Path.Combine(d, "node_modules"), "pnpm"));
        roots.Add((Path.Combine(Local, "Yarn", "Data", "global", "node_modules"), "yarn"));

        foreach (var (root, where) in roots)
        {
            if (!seen.Add(root)) continue;
            foreach (var dir in Dirs(root))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.')) continue;
                if (name.StartsWith('@'))
                {
                    foreach (var sub in Dirs(dir)) list.Add(Pkg(sub, $"{name}/{Path.GetFileName(sub)}", where));
                    continue;
                }
                list.Add(Pkg(dir, name, where));
            }
        }
        return list.OrderBy(i => i.Name).ThenBy(i => i.Note).ToList();

        static DevItem Pkg(string dir, string name, string where)
        {
            string v = "";
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "package.json")));
                v = doc.RootElement.TryGetProperty("version", out var e) ? e.GetString() ?? "" : "";
            }
            catch (Exception) { }
            bool bundled = name is "npm" or "corepack";
            return new DevItem(name, v, dir, DirSize(dir), null, null, bundled ? L.T("{0}, входит в Node", where) : where, null);
        }
    }

    // ── Python ───────────────────────────────────────────────────────────

    private static List<DevItem> Python(IReadOnlyList<ProgramRow> programs)
    {
        var list = new List<DevItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddPy(string dir, string? version, string source)
        {
            var exe = Path.Combine(dir, "python.exe");
            if (!File.Exists(exe) || !seen.Add(Path.GetFullPath(dir).TrimEnd('\\'))) return;
            var v = version ?? FileVersion(exe) ?? "";
            var parts = v.Split('.');
            var mm = parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : v;
            var (s, u) = ByDate(PythonEol.TryGetValue(mm, out var d) ? d : null);
            int pkgs = Dirs(Path.Combine(dir, "Lib", "site-packages")).Count(p => p.EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase));
            list.Add(new DevItem("Python", v, dir, DirSize(dir), s, u,
                pkgs > 0 ? L.T("{0}, пакетов: {1}", source, pkgs) : source,
                Match(programs, p => p.Name.StartsWith("Python " + mm, StringComparison.OrdinalIgnoreCase) && !p.Name.Contains("Launcher") && (p.Version?.StartsWith(v) ?? true))));
        }

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var path in new[] { @"Software\Python\PythonCore", @"Software\WOW6432Node\Python\PythonCore" })
            {
                using var core = hive.OpenSubKey(path);
                if (core is null) continue;
                foreach (var tag in core.GetSubKeyNames())
                {
                    using var ip = core.OpenSubKey(tag + @"\InstallPath");
                    if (ip?.GetValue("") is string dir) AddPy(dir, ip.GetValue("Version") as string ?? FileVersion(Path.Combine(dir, "python.exe")), L.T("установщик"));
                }
            }
        }

        foreach (var d in Dirs(Path.Combine(Local, "Programs", "Python"))) AddPy(d, null, L.T("установщик"));
        foreach (var name in new[] { "anaconda3", "miniconda3", "Anaconda3", "Miniconda3" })
        {
            var d = Path.Combine(Home, name);
            AddPy(d, null, "Conda");
            foreach (var env in Dirs(Path.Combine(d, "envs"))) AddPy(env, null, L.T("Conda, окружение {0}", Path.GetFileName(env)));
        }
        foreach (var d in Dirs(Path.Combine(Home, ".pyenv", "pyenv-win", "versions"))) AddPy(d, Path.GetFileName(d), "pyenv");
        foreach (var d in Dirs(Path.Combine(Roaming, "uv", "python"))) AddPy(d, null, "uv");

        return list.OrderByDescending(i => VersionKey(i.Version)).ToList();
    }

    // ── Java ─────────────────────────────────────────────────────────────

    private static readonly int[] JavaLts = [8, 11, 17, 21, 25];

    private static List<DevItem> Java(IReadOnlyList<ProgramRow> programs)
    {
        var list = new List<DevItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();

        foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Eclipse Foundation", "Microsoft", "Zulu", "Amazon Corretto", "BellSoft", "OpenJDK", "Android\\Android Studio" })
        {
            roots.AddRange(Dirs(Path.Combine(Pf, vendor)));
            roots.AddRange(Dirs(Path.Combine(Pf86, vendor)));
        }
        roots.Add(Path.Combine(Pf, "Android", "Android Studio", "jbr"));
        roots.AddRange(Dirs(Path.Combine(Home, ".jdks")));
        if (Environment.GetEnvironmentVariable("JAVA_HOME", EnvironmentVariableTarget.Machine) is { } jh1) roots.Add(jh1);
        if (Environment.GetEnvironmentVariable("JAVA_HOME", EnvironmentVariableTarget.User) is { } jh2) roots.Add(jh2);

        foreach (var dir in roots)
        {
            var release = Path.Combine(dir, "release");
            if (!File.Exists(Path.Combine(dir, "bin", "java.exe")) || !seen.Add(Path.GetFullPath(dir).TrimEnd('\\'))) continue;

            string v = "", vendor = "";
            if (File.Exists(release))
            {
                foreach (var line in File.ReadLines(release))
                {
                    if (line.StartsWith("JAVA_VERSION=")) v = line[13..].Trim('"');
                    else if (line.StartsWith("IMPLEMENTOR=")) vendor = line[12..].Trim('"');
                }
            }
            if (v.Length == 0) v = FileVersion(Path.Combine(dir, "bin", "java.exe")) ?? "";

            int major = v.StartsWith("1.") ? int.TryParse(v.Split('.')[1], out var m1) ? m1 : 0 : int.TryParse(v.Split('.')[0], out var m2) ? m2 : 0;
            bool jdk = File.Exists(Path.Combine(dir, "bin", "javac.exe"));
            string? note = string.Join(", ", new[] { vendor, jdk ? null : L.T("только JRE"), dir.Contains("Android Studio") ? L.T("встроен в Android Studio") : null }.Where(x => !string.IsNullOrEmpty(x)));
            list.Add(new DevItem(jdk ? "JDK" : "JRE", v, dir, DirSize(dir),
                major == 0 ? null : JavaLts.Contains(major) ? "lts" : "eol", null, note.Length > 0 ? note : null,
                Match(programs, p => (p.Name.Contains("JDK") || p.Name.Contains("Java") || p.Name.Contains("OpenJDK")) && (p.Version?.StartsWith(v.Split('+')[0]) ?? false))));
        }
        return list.OrderByDescending(i => VersionKey(i.Version)).ToList();
    }

    // ── Android SDK ──────────────────────────────────────────────────────

    private static List<DevItem> Android()
    {
        var list = new List<DevItem>();
        var root = Environment.GetEnvironmentVariable("ANDROID_HOME", EnvironmentVariableTarget.User)
                   ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT", EnvironmentVariableTarget.User)
                   ?? Environment.GetEnvironmentVariable("ANDROID_HOME", EnvironmentVariableTarget.Machine)
                   ?? Path.Combine(Local, "Android", "Sdk");
        if (!Directory.Exists(root)) return list;

        foreach (var d in Dirs(Path.Combine(root, "platforms")).OrderByDescending(VersionKey))
            list.Add(new DevItem(L.T("Платформа"), Path.GetFileName(d), d, DirSize(d), null, null, null, null));
        foreach (var d in Dirs(Path.Combine(root, "build-tools")).OrderByDescending(VersionKey))
            list.Add(new DevItem("Build-tools", Path.GetFileName(d), d, DirSize(d), null, null, null, null));
        foreach (var d in Dirs(Path.Combine(root, "ndk")).OrderByDescending(VersionKey))
            list.Add(new DevItem("NDK", Path.GetFileName(d), d, DirSize(d), null, null, null, null));
        foreach (var d in Dirs(Path.Combine(root, "system-images")))
            list.Add(new DevItem(L.T("Образы эмулятора"), Path.GetFileName(d), d, DirSize(d), null, null, null, null));

        var avd = Path.Combine(Home, ".android", "avd");
        foreach (var d in Dirs(avd).Where(d => d.EndsWith(".avd")))
            list.Add(new DevItem(L.T("Эмулятор (AVD)"), Path.GetFileNameWithoutExtension(d), d, DirSize(d), null, null, null, null));
        return list;
    }

    // ── Остальные языки ──────────────────────────────────────────────────

    private static List<DevItem> OtherLangs(IReadOnlyList<ProgramRow> programs)
    {
        var list = new List<DevItem>();

        var goRoot = Environment.GetEnvironmentVariable("GOROOT", EnvironmentVariableTarget.Machine) ?? Path.Combine(Pf, "Go");
        var goVer = Path.Combine(goRoot, "VERSION");
        if (File.Exists(goVer))
            list.Add(new DevItem("Go", File.ReadLines(goVer).First().Replace("go", ""), goRoot, DirSize(goRoot), null, null, null,
                Match(programs, p => p.Name.StartsWith("Go Programming Language", StringComparison.OrdinalIgnoreCase))));

        foreach (var d in Dirs(Path.Combine(Home, ".rustup", "toolchains")))
            list.Add(new DevItem("Rust", Path.GetFileName(d), d, DirSize(d), null, null, "rustup", null));

        var cargo = Path.Combine(Home, ".cargo", "registry");
        if (Directory.Exists(cargo)) list.Add(new DevItem(L.T("Кэш Cargo"), "", cargo, DirSize(cargo), null, null, L.T("Можно очистить: cargo cache -a"), null));

        foreach (var exe in new[] { Path.Combine(Pf, "PowerShell", "7", "pwsh.exe"), Path.Combine(Pf, "PowerShell", "7-preview", "pwsh.exe") })
            if (File.Exists(exe)) list.Add(new DevItem("PowerShell", FileVersion(exe) ?? "", Path.GetDirectoryName(exe), null, null, null, null,
                Match(programs, p => p.Name.StartsWith("PowerShell 7", StringComparison.OrdinalIgnoreCase))));

        foreach (var d in Dirs(Path.Combine(Pf, "PostgreSQL")))
            list.Add(new DevItem("PostgreSQL", Path.GetFileName(d), d, DirSize(Path.Combine(d, "bin")), null, null, L.T("Данные баз — в папке data"), Match(programs, p => p.Name.StartsWith("PostgreSQL " + Path.GetFileName(d)))));

        foreach (var p in programs.Where(p => p.Name.StartsWith("Microsoft SQL Server 20", StringComparison.OrdinalIgnoreCase) && !p.Name.Contains("Setup")))
            list.Add(new DevItem("SQL Server", p.Version ?? "", p.Location, null, null, null, p.Name, p.Id));

        return list;
    }

    // ── IDE ──────────────────────────────────────────────────────────────

    private static List<DevItem> Ides(IReadOnlyList<ProgramRow> programs)
    {
        var list = new List<DevItem>();

        var vswhere = Path.Combine(Pf86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(vswhere))
        {
            var json = Run(vswhere, "-all -prerelease -format json -utf8");
            try
            {
                using var doc = JsonDocument.Parse(json ?? "[]");
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    var name = e.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "Visual Studio" : "Visual Studio";
                    var ver = e.TryGetProperty("catalog", out var cat) && cat.TryGetProperty("productDisplayVersion", out var pv) ? pv.GetString() : e.GetProperty("installationVersion").GetString();
                    var path = e.TryGetProperty("installationPath", out var ip) ? ip.GetString() : null;
                    list.Add(new DevItem(name, ver ?? "", path, null, null, null, L.T("Компоненты меняются в Visual Studio Installer"), null));
                }
            }
            catch (Exception) { }
        }

        foreach (var exe in new[] { Path.Combine(Local, "Programs", "Microsoft VS Code", "Code.exe"), Path.Combine(Pf, "Microsoft VS Code", "Code.exe") })
        {
            if (!File.Exists(exe)) continue;
            int ext = Dirs(Path.Combine(Home, ".vscode", "extensions")).Count(d => !Path.GetFileName(d).StartsWith('.'));
            list.Add(new DevItem("Visual Studio Code", FileVersion(exe) ?? "", Path.GetDirectoryName(exe), null, null, null,
                ext > 0 ? L.T("расширений: {0}", ext) : null, Match(programs, p => p.Name.StartsWith("Microsoft Visual Studio Code", StringComparison.OrdinalIgnoreCase))));
        }

        foreach (var p in programs.Where(p =>
                     p.Name.StartsWith("JetBrains", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("Rider", StringComparison.OrdinalIgnoreCase) ||
                     p.Name.StartsWith("IntelliJ", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("PyCharm", StringComparison.OrdinalIgnoreCase) ||
                     p.Name.StartsWith("WebStorm", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("DataGrip", StringComparison.OrdinalIgnoreCase) ||
                     p.Name.StartsWith("Android Studio", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("Cursor", StringComparison.OrdinalIgnoreCase) ||
                     p.Name.StartsWith("SQL Server Management Studio", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("Postman", StringComparison.OrdinalIgnoreCase) ||
                     p.Name.StartsWith("DBeaver", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("pgAdmin", StringComparison.OrdinalIgnoreCase)))
            list.Add(new DevItem(p.Name, p.Version ?? "", p.Location, p.Size > 0 ? p.Size : null, null, null,
                p.LastUsed is { } lu ? L.T("последний запуск {0}", DateTimeOffset.FromUnixTimeSeconds(lu).LocalDateTime.ToString("d MMM yyyy", L.Culture)) : null, p.Id));
        return list;
    }

    // ── Windows SDK, Visual C++ ──────────────────────────────────────────

    private static List<DevItem> WindowsSdk(IReadOnlyList<ProgramRow> programs)
    {
        var list = new List<DevItem>();
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots")
                      ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots");
        if (k?.GetValue("KitsRoot10") is string kits)
            foreach (var d in Dirs(Path.Combine(kits, "Include")).OrderByDescending(VersionKey))
                list.Add(new DevItem("Windows SDK", Path.GetFileName(d), d, null, null, null, null,
                    Match(programs, p => p.Name.StartsWith("Windows Software Development Kit", StringComparison.OrdinalIgnoreCase) && (p.Version?.StartsWith(Path.GetFileName(d)[..Math.Min(10, Path.GetFileName(d).Length)]) ?? false))));

        foreach (var p in programs.Where(p => p.Name.Contains("Visual C++", StringComparison.OrdinalIgnoreCase) && p.Name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(p => p.Name))
        {
            var year = Regex.Match(p.Name, @"20\d\d").Value;
            bool old = int.TryParse(year, out var y) && y < 2015;
            list.Add(new DevItem(p.Name.Replace("Microsoft ", ""), p.Version ?? "", null, null, old ? "eol" : "ok", null,
                L.T("Нужны программам, собранным этой версией. Удалять не стоит, даже старые"), null));
        }
        return list;
    }

    // ── WSL и Docker ─────────────────────────────────────────────────────

    private static List<DevItem> Containers(IReadOnlyList<ProgramRow> programs)
    {
        var list = new List<DevItem>();
        using (var lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss"))
        {
            if (lxss is not null)
            {
                foreach (var id in lxss.GetSubKeyNames())
                {
                    using var d = lxss.OpenSubKey(id);
                    if (d?.GetValue("DistributionName") is not string name) continue;
                    var basePath = (d.GetValue("BasePath") as string)?.Replace(@"\\?\", "");
                    var vhd = basePath is null ? null : Directory.Exists(basePath) ? Directory.EnumerateFiles(basePath, "*.vhdx").FirstOrDefault() : null;
                    int ver = d.GetValue("Version") is int v ? v : 2;
                    list.Add(new DevItem("WSL: " + name, $"WSL {ver}", basePath, vhd is null ? null : new FileInfo(vhd).Length, null, null,
                        vhd is null ? null : L.T("размер файла-диска"), null));
                }
            }
        }

        var dockerExe = Path.Combine(Pf, "Docker", "Docker", "Docker Desktop.exe");
        if (File.Exists(dockerExe))
        {
            var vhd = new[] { Path.Combine(Local, "Docker", "wsl", "disk", "docker_data.vhdx"), Path.Combine(Local, "Docker", "wsl", "data", "ext4.vhdx") }.FirstOrDefault(File.Exists);
            list.Add(new DevItem("Docker Desktop", FileVersion(dockerExe) ?? "", Path.GetDirectoryName(dockerExe), vhd is null ? null : new FileInfo(vhd).Length, null, null,
                vhd is null ? null : L.T("размер диска с образами и томами"), Match(programs, p => p.Name.StartsWith("Docker Desktop", StringComparison.OrdinalIgnoreCase))));
        }
        return list;
    }

    // ── Командная строка и менеджеры пакетов ─────────────────────────────

    private static readonly string[] Tools = ["git", "gh", "kubectl", "helm", "minikube", "terraform", "az", "aws", "gcloud", "ngrok", "ollama", "ffmpeg", "cmake", "nuget", "yarn", "pnpm", "bun", "deno"];

    private static List<DevItem> CliTools(IReadOnlyList<ProgramRow> programs)
    {
        var list = new List<DevItem>();
        foreach (var t in Tools)
        {
            var hit = Which(t).FirstOrDefault();
            if (hit is null) continue;
            list.Add(new DevItem(t, FileVersion(hit) ?? "", hit, null, null, null, null,
                t == "git" ? Match(programs, p => p.Name.StartsWith("Git", StringComparison.OrdinalIgnoreCase) && p.Publisher?.Contains("Git") == true) : null));
        }
        return list;
    }

    private static List<DevItem> PackageManagers()
    {
        var list = new List<DevItem>();
        var winget = Path.Combine(Local, "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(winget)) list.Add(new DevItem("winget", "", winget, null, null, null, L.T("Обновить всё: winget upgrade --all"), null));

        var choco = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey");
        if (Directory.Exists(choco))
        {
            int n = Dirs(Path.Combine(choco, "lib")).Count();
            list.Add(new DevItem("Chocolatey", "", choco, DirSize(Path.Combine(choco, "lib")), null, null, L.T("пакетов: {0}. Обновить: choco upgrade all", n), null));
        }

        var scoop = Environment.GetEnvironmentVariable("SCOOP", EnvironmentVariableTarget.User) ?? Path.Combine(Home, "scoop");
        if (Directory.Exists(Path.Combine(scoop, "apps")))
        {
            var apps = Dirs(Path.Combine(scoop, "apps")).Select(Path.GetFileName).Where(n => n != "scoop").ToList();
            list.Add(new DevItem("Scoop", "", scoop, DirSize(Path.Combine(scoop, "apps")), null, null,
                apps.Count > 0 ? L.T("пакетов: {0} — {1}", apps.Count, string.Join(", ", apps.Take(12)) + (apps.Count > 12 ? "…" : "")) : L.T("пакетов: {0}", apps.Count), null));
        }

        foreach (var (name, dir, how) in new[]
                 {
                     (L.T("Кэш NuGet"), Path.Combine(Home, ".nuget", "packages"), "dotnet nuget locals all --clear"),
                     (L.T("Кэш npm"), Path.Combine(Local, "npm-cache"), "npm cache clean --force"),
                     (L.T("Кэш pip"), Path.Combine(Local, "pip", "Cache"), "pip cache purge"),
                     (L.T("Кэш Gradle"), Path.Combine(Home, ".gradle", "caches"), L.T("удалить папку при закрытой IDE")),
                     (L.T("Репозиторий Maven"), Path.Combine(Home, ".m2", "repository"), L.T("удалить папку — скачается заново")),
                 })
        {
            if (Directory.Exists(dir)) list.Add(new DevItem(name, "", dir, DirSize(dir), null, null, L.T("Очистка: {0}", how), null));
        }
        return list;
    }

    // ── Службы ───────────────────────────────────────────────────────────

    private static readonly Regex ServicePattern = new(
        @"^(MSSQL|SQLAgent|SQLBrowser|SQLWriter|MSSQLFDLauncher|SSIS|ReportServer|postgresql|MySQL|MariaDB|MongoDB|Redis|Memurai|RabbitMQ|" +
        @"com\.docker\.service|docker$|elasticsearch|kibana|logstash|OracleService|OracleOraDB|W3SVC|WAS$|nginx|Apache|Jenkins|kafka|zookeeper|" +
        @"Neo4j|CouchDB|Cassandra|influxdb|grafana|vmcompute|MSMQ|AzureCosmosDB|ollama)",
        RegexOptions.IgnoreCase);

    private static List<DevService> Services()
    {
        var list = new List<DevService>();
        using var s = new ManagementObjectSearcher("SELECT Name, DisplayName, State, StartMode, ProcessId, PathName FROM Win32_Service");
        foreach (ManagementObject o in s.Get())
        {
            using (o)
            {
                var name = o["Name"]?.ToString() ?? "";
                if (!ServicePattern.IsMatch(name)) continue;
                int pid = Convert.ToInt32(o["ProcessId"] ?? 0);
                long? mem = null;
                if (pid > 0)
                {
                    try { using var p = Process.GetProcessById(pid); mem = p.PrivateMemorySize64; } catch (Exception) { }
                }
                list.Add(new DevService(name, o["DisplayName"]?.ToString() ?? name,
                    o["State"]?.ToString() ?? "", o["StartMode"]?.ToString() ?? "", pid > 0 ? pid : null, mem, o["PathName"]?.ToString()));
            }
        }
        return list.OrderByDescending(x => x.State == "Running").ThenBy(x => x.Display).ToList();
    }

    /// <summary>Запустить, остановить или сменить тип запуска службы разработчика.</summary>
    public ActionResult ServiceAction(string name, string action)
    {
        if (!ServicePattern.IsMatch(name) || name.Any(c => !(char.IsLetterOrDigit(c) || c is '$' or '_' or '-' or '.')))
            return new(false, L.T("Эта служба не из списка служб разработчика."));

        string args = action switch
        {
            "start" => $"start \"{name}\"",
            "stop" => $"stop \"{name}\"",
            "auto" => $"config \"{name}\" start= auto",
            "manual" => $"config \"{name}\" start= demand",
            "disabled" => $"config \"{name}\" start= disabled",
            _ => "",
        };
        if (args.Length == 0) return new(false, L.T("Неизвестное действие."));

        var output = Run("sc.exe", args, out int code);
        // sc start/stop возвращает сразу, служба доходит до нужного состояния за несколько секунд.
        lock (_gate) _data = _data is null ? null : _data with { Services = TryServices() };
        return code == 0
            ? new(true, action switch { "start" => L.T("Служба запускается."), "stop" => L.T("Служба останавливается."), _ => L.T("Тип запуска изменён.") })
            : new(false, (output ?? "").Trim().Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? L.T("Ошибка {0}", code));
    }

    /// <summary>Действие с Docker; после него перечитываем только Docker, а не всё окружение.</summary>
    public ActionResult DockerAction(string action, string id)
    {
        var r = Docker.Act(action, id);
        DockerInfo? fresh = null;
        try { fresh = Docker.Info(); } catch (Exception) { }
        lock (_gate) if (_data is not null && fresh is not null) _data = _data with { Docker = fresh };
        return r;
    }

    public ActionResult DeleteNodeModules(string project)
    {
        IReadOnlyList<NodeProject> known;
        lock (_gate) known = _data?.Projects ?? [];
        var r = NodeProjects.DeleteModules(project, known.ToList());
        if (r.Ok)
            lock (_gate) if (_data is not null) _data = _data with { Projects = _data.Projects.Where(p => !p.Path.Equals(project, StringComparison.OrdinalIgnoreCase)).ToList() };
        return r;
    }

    public IReadOnlyList<DevService> TryServices()
    {
        try { return Services(); } catch (Exception) { return []; }
    }

    // ── PATH ─────────────────────────────────────────────────────────────

    private static readonly string[] Commands = ["dotnet", "node", "npm", "python", "python3", "py", "pip", "java", "javac", "go", "cargo", "git", "docker", "pwsh", "code", "adb"];

    private static IEnumerable<string> PathDirs()
    {
        foreach (var target in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
            foreach (var d in (Environment.GetEnvironmentVariable("Path", target) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return Environment.ExpandEnvironmentVariables(d);
    }

    /// <summary>Все копии команды по порядку поиска в PATH, как их найдёт новая консоль (сначала системный PATH, потом пользовательский).</summary>
    private static List<string> Which(string cmd)
    {
        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in PathDirs())
        {
            foreach (var ext in exts)
            {
                try
                {
                    var f = Path.Combine(dir, cmd + ext.ToLowerInvariant());
                    if (File.Exists(f) && seen.Add(Path.GetFullPath(f))) { hits.Add(f); break; }
                }
                catch (Exception) { /* кривой элемент PATH */ }
            }
        }
        return hits;
    }

    private static (List<CommandResolution>, List<PathEntry>) PathInfo()
    {
        var commands = new List<CommandResolution>();
        foreach (var c in Commands)
        {
            var hits = Which(c);
            if (hits.Count == 0) continue;
            string? note = null;
            var first = hits[0];
            if (first.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) && c.StartsWith("python"))
                note = L.T("Это заглушка Microsoft Store, а не Python: она откроет Store. Отключите её в «Параметры → Приложения → Псевдонимы выполнения» или поставьте нужный Python выше в PATH.");
            else if (hits.Count > 1)
                note = L.T("Найдено копий: {0}. Запускается первая, остальные заслонены.", hits.Count);
            commands.Add(new CommandResolution(c, first, FileVersion(first), hits.Skip(1).ToList(), note));
        }

        var entries = new List<PathEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
        {
            foreach (var raw in (Environment.GetEnvironmentVariable("Path", target) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var d = Environment.ExpandEnvironmentVariables(raw).TrimEnd('\\');
                bool exists;
                try { exists = Directory.Exists(d); } catch (Exception) { exists = false; }
                entries.Add(new PathEntry(raw, target == EnvironmentVariableTarget.Machine ? "system" : "user", exists, !seen.Add(d)));
            }
        }
        return (commands, entries);
    }

    // ── Утилиты ──────────────────────────────────────────────────────────

    private static IEnumerable<string> Dirs(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path) : []; }
        catch (Exception) { return []; }
    }

    private static string Major2(string v)
    {
        var p = v.Split('.');
        return p.Length >= 2 ? $"{p[0]}.{p[1]}" : v;
    }

    private static Version VersionKey(string pathOrVersion)
    {
        var name = Path.GetFileName(pathOrVersion.TrimEnd('\\')).TrimStart('v', 'V');
        var m = Regex.Match(name, @"\d+(\.\d+){0,3}");
        return m.Success && Version.TryParse(m.Value.Contains('.') ? m.Value : m.Value + ".0", out var v) ? v : new Version(0, 0);
    }

    private static string? FileVersion(string exe)
    {
        try
        {
            var fi = FileVersionInfo.GetVersionInfo(exe);
            var v = fi.ProductVersion ?? fi.FileVersion;
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Split(' ', '+')[0].Trim();
            return v.Length > 0 ? v : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Размер папки. Ссылки (junction, symlink) не обходим, чтобы не считать одно и то же дважды.</summary>
    private static long? DirSize(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        try
        {
            long total = 0;
            var opts = new System.IO.EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opts)) total += f.Length;
            return total;
        }
        catch (Exception) { return null; }
    }

    private static string? Match(IReadOnlyList<ProgramRow> programs, Func<ProgramRow, bool> pick) =>
        programs.FirstOrDefault(p => p.CanUninstall && pick(p))?.Id;

    private static string? Run(string exe, string args) => Run(exe, args, out _);

    private static string? Run(string exe, string args, out int code)
    {
        code = -1;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            })!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(20_000);
            code = p.HasExited ? p.ExitCode : -1;
            return text;
        }
        catch (Exception) { return null; }
    }
}
