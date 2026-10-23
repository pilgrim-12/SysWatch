using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using SysWatch.App.Sampling;

namespace SysWatch.App.Data;

public sealed record GeoApp(string Name, long Bytes);

/// <summary>Точная связка: программа → адрес (домен или IP) → страна, сколько отправлено и получено.</summary>
public sealed record GeoFlow(string App, string Dest, string Ip, string? Country, long Tx, long Rx, long Connects);

public sealed record GeoCountry(string Code, long Tx, long Rx, long Connects, int Ips,
    IReadOnlyList<GeoApp> Apps, IReadOnlyList<string> Domains);

/// <param name="Home">Страна этого компьютера (ISO): по часовому поясу Windows, иначе из настройки «Страна или регион».</param>
/// <param name="HomeManual">true — выбрана пользователем в SysWatch.</param>
public sealed record GeoView(long From, string? Home, bool HomeManual, bool GeoAvailable, string? GeoStatus,
    IReadOnlyList<GeoCountry> Countries, IReadOnlyList<GeoFlow> Flows, long UnknownBytes, long LanBytes);

/// <summary>
/// Трафик по странам за период: соединения из истории агента (conn_1m), страна — по офлайн-базе IP.
/// Сетевые подробности хранятся 30 дней, поэтому и период — не больше месяца.
/// </summary>
public sealed class GeoTraffic(DayFiles files, GeoIp geo, SysWatch.App.Settings.AppSettings settings)
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetUserDefaultGeoName(StringBuilder geoName, int geoNameCount);

    /// <summary>Windows 10 1709+: двухбуквенный код страны из «Параметры → Время и язык → Регион».</summary>
    public static string? HomeCountry()
    {
        try
        {
            var sb = new StringBuilder(16);
            if (GetUserDefaultGeoName(sb, sb.Capacity) > 0)
            {
                var code = sb.ToString().Trim().ToUpperInvariant();
                if (code.Length == 2 && char.IsLetter(code[0]) && char.IsLetter(code[1])) return code;
            }
        }
        catch (Exception) { /* старая Windows — ниже запасной вариант */ }
        try { return System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName.ToUpperInvariant(); }
        catch (Exception) { return null; }
    }

    private sealed class Acc
    {
        public long Tx, Rx, Connects;
        public readonly HashSet<string> Ips = new();
        public readonly Dictionary<string, long> Apps = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, long> Domains = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Страна этого компьютера: выбранная вручную, иначе по часовому поясу, иначе по региону Windows.</summary>
    public string? Home() => settings.HomeCountry ?? TimeZoneCountry.Current() ?? HomeCountry();

    public GeoView Get(int rangeMinutes)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long from = now - Math.Clamp(rangeMinutes, 1, 60 * 24 * 31) * 60L;
        var byCountry = new Dictionary<string, Acc>(StringComparer.OrdinalIgnoreCase);
        var countryOf = new Dictionary<string, string?>();
        long unknown = 0, lan = 0;
        var flows = new Dictionary<(string App, string Dest), (string Ip, string? Country, long Tx, long Rx, long Connects)>();
        bool geoOk = geo.Available;

        foreach (var (_, path) in files.For(from))
        {
            using var c = DayFiles.Open(path);
            foreach (var r in c.Cmd("""
                SELECT c.remote_ip, p.image, c.domain, SUM(c.bytes_tx), SUM(c.bytes_rx), SUM(c.connects)
                FROM conn_1m c JOIN process p ON p.id = c.proc_id
                WHERE c.ts >= $from GROUP BY c.remote_ip, p.image, c.domain
                """, ("$from", from)).Rows())
            {
                var ipText = r.GetString(0);
                long tx = r.Long0(3), rx = r.Long0(4), connects = r.Long0(5);

                if (!countryOf.TryGetValue(ipText, out var code))
                {
                    code = geoOk && IPAddress.TryParse(ipText, out var ip) ? geo.Country(ip) : null;
                    countryOf[ipText] = code;
                }
                if (code == "LAN") { lan += tx + rx; continue; }

                // Адрес для человека: домен, если программа его спрашивала, иначе IP.
                var appName = r.GetString(1);
                var dest = r.StrOrNull(2) is { Length: > 0 } dn ? dn : ipText;
                var fk = (appName, dest);
                var f = flows.GetValueOrDefault(fk, (ipText, code, 0L, 0L, 0L));
                flows[fk] = (f.Item1, f.Item2 ?? code, f.Item3 + tx, f.Item4 + rx, f.Item5 + connects);

                if (code is null) { unknown += tx + rx; continue; }

                if (!byCountry.TryGetValue(code, out var a)) byCountry[code] = a = new Acc();
                a.Tx += tx; a.Rx += rx; a.Connects += connects;
                a.Ips.Add(ipText);
                var app = r.GetString(1);
                a.Apps[app] = a.Apps.GetValueOrDefault(app) + tx + rx;
                if (r.StrOrNull(2) is { Length: > 0 } dom) a.Domains[dom] = a.Domains.GetValueOrDefault(dom) + tx + rx;
            }
        }

        var countries = byCountry
            .Select(kv => new GeoCountry(kv.Key, kv.Value.Tx, kv.Value.Rx, kv.Value.Connects, kv.Value.Ips.Count,
                kv.Value.Apps.OrderByDescending(x => x.Value).Take(5).Select(x => new GeoApp(x.Key, x.Value)).ToList(),
                kv.Value.Domains.OrderByDescending(x => x.Value).Take(6).Select(x => x.Key).ToList()))
            .OrderByDescending(x => x.Tx + x.Rx)
            .ToList();

        var manual = settings.HomeCountry;
        var flowList = flows
            .Select(kv => new GeoFlow(kv.Key.App, kv.Key.Dest, kv.Value.Ip, kv.Value.Country, kv.Value.Tx, kv.Value.Rx, kv.Value.Connects))
            .OrderByDescending(x => x.Tx + x.Rx)
            .Take(1500)
            .ToList();

        return new GeoView(from, Home(), manual is not null, geoOk, geo.Status, countries, flowList, unknown, lan);
    }
}
