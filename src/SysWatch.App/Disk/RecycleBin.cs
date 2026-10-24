using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace SysWatch.App.Disk;

/// <param name="Id">Путь к содержимому в корзине ($R…): по нему восстанавливаем и удаляем.</param>
public sealed record RecycleItem(string Id, string Name, string Original, bool IsDir, long Size, long Deleted, string Drive);
public sealed record RecycleView(long Total, long Count, bool Truncated, IReadOnlyList<RecycleItem> Items);
public sealed record RecycleResult(bool Ok, string Message, int Done, int Failed);

/// <summary>
/// Корзина текущего пользователя на всех дисках. Windows хранит удалённое в X:\$Recycle.Bin\&lt;SID&gt;:
/// $R… — сам файл или папка, $I… — откуда он и когда удалён.
/// </summary>
public static class RecycleBin
{
    public const int Limit = 3000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint flags);

    private const uint NoConfirm = 0x1, NoProgress = 0x2, NoSound = 0x4;

    private static string Sid() => WindowsIdentity.GetCurrent().User?.Value ?? "";

    private static IEnumerable<string> Folders()
    {
        var sid = Sid();
        if (sid.Length == 0) yield break;
        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            var f = Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin", sid);
            if (Directory.Exists(f)) yield return f;
        }
    }

    public static RecycleView List()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        bool queried = false;
        try { queried = SHQueryRecycleBin(null, ref info) == 0; } catch (Exception) { /* ниже посчитаем сами */ }

        var items = new List<RecycleItem>();
        foreach (var folder in Folders())
        {
            string[] meta;
            try { meta = Directory.GetFiles(folder, "$I*"); } catch (Exception) { continue; }
            foreach (var i in meta)
            {
                if (ReadInfo(i) is not { } m) continue;
                var data = Path.Combine(folder, "$R" + Path.GetFileName(i)[2..]);
                bool isDir = Directory.Exists(data);
                if (!isDir && !File.Exists(data)) continue; // осиротевшая запись
                items.Add(new RecycleItem(data, Path.GetFileName(m.Original.TrimEnd('\\')), m.Original, isDir, m.Size, m.Deleted,
                    Path.GetPathRoot(folder) ?? ""));
            }
        }
        items.Sort((a, b) => b.Deleted.CompareTo(a.Deleted));
        long total = queried ? info.i64Size : items.Sum(x => x.Size);
        long count = queried ? info.i64NumItems : items.Count;
        return new RecycleView(total, count, items.Count > Limit, items.Take(Limit).ToList());
    }

    /// <summary>Формат $I: версия 1 (Vista–8.1) — путь фиксированной длины, версия 2 (Windows 10+) — с длиной.</summary>
    private static (string Original, long Size, long Deleted)? ReadInfo(string path)
    {
        try
        {
            var b = File.ReadAllBytes(path);
            if (b.Length < 28) return null;
            long version = BitConverter.ToInt64(b, 0);
            long size = BitConverter.ToInt64(b, 8);
            long ft = BitConverter.ToInt64(b, 16);
            string name;
            if (version >= 2)
            {
                int chars = BitConverter.ToInt32(b, 24);
                int len = Math.Min(chars * 2, b.Length - 28);
                name = Encoding.Unicode.GetString(b, 28, Math.Max(0, len));
            }
            else name = Encoding.Unicode.GetString(b, 24, Math.Min(520, b.Length - 24));
            name = name.TrimEnd('\0');
            int z = name.IndexOf('\0');
            if (z >= 0) name = name[..z];
            long deleted = ft > 0 ? DateTimeOffset.FromFileTime(ft).ToUnixTimeSeconds() : 0;
            return (name, size, deleted);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Разрешаем действия только с элементами корзины этого пользователя.</summary>
    private static bool Own(string id, out string info)
    {
        info = "";
        try
        {
            var full = Path.GetFullPath(id);
            var dir = Path.GetDirectoryName(full);
            var name = Path.GetFileName(full);
            if (dir is null || !name.StartsWith("$R", StringComparison.OrdinalIgnoreCase)) return false;
            if (!Folders().Any(f => string.Equals(Path.GetFullPath(f), dir, StringComparison.OrdinalIgnoreCase))) return false;
            info = Path.Combine(dir, "$I" + name[2..]);
            return true;
        }
        catch (Exception) { return false; }
    }

    public static RecycleResult Restore(IEnumerable<string> ids)
    {
        int ok = 0, fail = 0;
        foreach (var id in ids)
        {
            if (!Own(id, out var info) || ReadInfo(info) is not { } m) { fail++; continue; }
            try
            {
                var target = Free(m.Original);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (Directory.Exists(id)) Directory.Move(id, target); else File.Move(id, target);
                File.Delete(info);
                ok++;
            }
            catch (Exception) { fail++; }
        }
        return new(fail == 0, fail == 0 ? L.T("Восстановлено: {0}.", ok) : L.T("Восстановлено: {0}, не удалось: {1}.", ok, fail), ok, fail);
    }

    /// <summary>Если на старом месте уже есть файл с таким именем — «имя (2).ext».</summary>
    private static string Free(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    public static RecycleResult Delete(IEnumerable<string> ids)
    {
        int ok = 0, fail = 0;
        foreach (var id in ids)
        {
            if (!Own(id, out var info)) { fail++; continue; }
            try
            {
                if (Directory.Exists(id)) Directory.Delete(id, recursive: true);
                else if (File.Exists(id)) File.Delete(id);
                if (File.Exists(info)) File.Delete(info);
                ok++;
            }
            catch (Exception) { fail++; }
        }
        return new(fail == 0, fail == 0 ? L.T("Удалено навсегда: {0}.", ok) : L.T("Удалено: {0}, не удалось: {1} — файлы заняты другой программой.", ok, fail), ok, fail);
    }

    public static RecycleResult Empty()
    {
        var before = List();
        int hr;
        try { hr = SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirm | NoProgress | NoSound); }
        catch (Exception) { hr = -1; }
        // S_OK, либо корзина уже пуста (E_UNEXPECTED на некоторых сборках).
        bool ok = hr == 0 || before.Count == 0;
        return new(ok, ok ? L.T("Корзина очищена, освобождено {0}.", Format(before.Total)) : L.T("Не удалось очистить корзину (код {0}).", $"0x{hr:X8}"),
            (int)before.Count, ok ? 0 : 1);
    }

    private static string Format(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.#} {L.T("ГБ")}" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0.#} {L.T("МБ")}" : $"{b / 1024.0:0} {L.T("КБ")}";
}
