using System.IO.Compression;
using System.Net;
using System.Net.Sockets;

namespace SysWatch.App.Sampling;

/// <summary>
/// Страна по IP — офлайн, по бесплатной базе DB-IP «IP to Country Lite» (CC BY 4.0, https://db-ip.com).
/// База скачивается только по кнопке в окне и хранится в C:\ProgramData\SysWatch\geo.
/// В память загружается, пока открыто окно, и выгружается при его закрытии.
/// </summary>
public sealed class GeoIp
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SysWatch", "geo");
    private static readonly string FilePath = Path.Combine(Dir, "dbip-country-lite.csv.gz");

    private readonly object _gate = new();
    private uint[]? _v4Start, _v4End;
    private UInt128[]? _v6Start, _v6End;
    private string[]? _v4Code, _v6Code;
    private string? _status;

    public bool Available => File.Exists(FilePath);
    public string? Status { get { lock (_gate) return _status; } }

    public string? Country(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return null;
        if (IsPrivate(ip)) return "LAN";
        lock (_gate)
        {
            if (!EnsureLoaded()) return null;
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                uint v = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
                int i = Find(_v4Start!, v);
                return i >= 0 && v <= _v4End![i] ? _v4Code![i] : null;
            }
            else
            {
                var v = ToUInt128(ip.GetAddressBytes());
                int i = Find(_v6Start!, v);
                return i >= 0 && v <= _v6End![i] ? _v6Code![i] : null;
            }
        }
    }

    /// <summary>Освободить память (окно закрыто).</summary>
    public void Unload()
    {
        lock (_gate) { _v4Start = _v4End = null; _v6Start = _v6End = null; _v4Code = _v6Code = null; }
    }

    public async Task<bool> DownloadAsync()
    {
        lock (_gate) _status = L.T("Скачиваю базу стран…");
        try
        {
            Directory.CreateDirectory(Dir);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SysWatch/1.0");
            // База выходит раз в месяц; в первые дни месяца новой может ещё не быть — пробуем прошлый.
            foreach (var month in new[] { DateTime.UtcNow, DateTime.UtcNow.AddMonths(-1) })
            {
                var url = $"https://download.db-ip.com/free/dbip-country-lite-{month:yyyy-MM}.csv.gz";
                using var resp = await http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) continue;
                var tmp = FilePath + ".tmp";
                await using (var fs = File.Create(tmp)) await resp.Content.CopyToAsync(fs);
                File.Move(tmp, FilePath, overwrite: true);
                lock (_gate) { Unload(); _status = null; }
                return true;
            }
            lock (_gate) _status = L.T("Не удалось скачать базу: сайт db-ip.com не ответил.");
            return false;
        }
        catch (Exception ex)
        {
            lock (_gate) _status = L.T("Не удалось скачать базу: {0}", ex.Message);
            return false;
        }
    }

    private bool EnsureLoaded()
    {
        if (_v4Start is not null) return true;
        if (!File.Exists(FilePath)) return false;

        var s4 = new List<uint>(); var e4 = new List<uint>(); var c4 = new List<string>();
        var s6 = new List<UInt128>(); var e6 = new List<UInt128>(); var c6 = new List<string>();
        var codes = new Dictionary<string, string>(); // одна строка на страну, а не на каждый диапазон

        using var gz = new GZipStream(File.OpenRead(FilePath), CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var parts = line.Split(',');
            if (parts.Length < 3) continue;
            if (!IPAddress.TryParse(parts[0], out var a) || !IPAddress.TryParse(parts[1], out var b)) continue;
            var code = codes.TryGetValue(parts[2], out var c) ? c : codes[parts[2]] = parts[2];
            if (a.AddressFamily == AddressFamily.InterNetwork)
            {
                var x = a.GetAddressBytes(); var y = b.GetAddressBytes();
                s4.Add((uint)(x[0] << 24 | x[1] << 16 | x[2] << 8 | x[3]));
                e4.Add((uint)(y[0] << 24 | y[1] << 16 | y[2] << 8 | y[3]));
                c4.Add(code);
            }
            else
            {
                s6.Add(ToUInt128(a.GetAddressBytes()));
                e6.Add(ToUInt128(b.GetAddressBytes()));
                c6.Add(code);
            }
        }
        _v4Start = s4.ToArray(); _v4End = e4.ToArray(); _v4Code = c4.ToArray();
        _v6Start = s6.ToArray(); _v6End = e6.ToArray(); _v6Code = c6.ToArray();
        return true;
    }

    /// <summary>Индекс последнего диапазона, который начинается не позже v (диапазоны в базе отсортированы).</summary>
    private static int Find<T>(T[] starts, T v) where T : IComparable<T>
    {
        int lo = 0, hi = starts.Length - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (starts[mid].CompareTo(v) <= 0) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    private static UInt128 ToUInt128(byte[] b)
    {
        UInt128 v = 0;
        foreach (var x in b) v = (v << 8) | x;
        return v;
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] == 0;
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.Equals(IPAddress.IPv6Any);
    }
}
