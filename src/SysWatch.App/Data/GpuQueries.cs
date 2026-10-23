using Microsoft.Data.Sqlite;

namespace SysWatch.App.Data;

public sealed record GpuAdapterView(
    string Key, string Name, long MemTotal,
    double? Util, double? Temp, double? Power, int? Fan, long? MemUsed, long? LastTs,
    double AvgUtil, double PeakUtil, double? PeakTemp, double? AvgTemp);

public sealed record GpuSeries(string Adapter, double?[] Util, double?[] Util3D, double?[] Video, double?[] Compute, double?[] Mem, double?[] Temp);

public sealed record GpuApp(string Name, string Adapter, double AvgUtil, double PeakUtil, long MemMax, int Minutes);

public sealed record GpuView(bool HasData, long[] Ts, IReadOnlyList<GpuAdapterView> Adapters, IReadOnlyList<GpuSeries> Series, IReadOnlyList<GpuApp> Apps);

/// <summary>История видеокарты из дневных файлов агента (таблицы gpu_1m и gpu_proc_1m).</summary>
public sealed class GpuQueries(DayFiles files)
{
    public GpuView Get(int rangeMinutes)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long from = now - Math.Clamp(rangeMinutes, 5, 60 * 24 * 31) * 60L;
        int step = 60 * Math.Max(1, (int)Math.Ceiling(rangeMinutes / 720.0)); // не больше ~720 точек на графике
        long first = from - from % step;
        int n = (int)((now - first) / step) + 1;
        var ts = Enumerable.Range(0, n).Select(i => first + (long)i * step).ToArray();

        var rows = new List<(long Ts, string Adapter, string? Name, double Util, double UtilMax, double U3D, double Video, double Compute, long Mem, long MemTotal, double? Temp, double? TempMax, double? Power, int? Fan)>();
        var apps = new Dictionary<(string Name, string Adapter), (double Sum, double Peak, long Mem, int N)>();

        foreach (var (_, path) in files.For(from))
        {
            try
            {
                using var c = DayFiles.Open(path);
                if (!c.HasColumn("gpu_1m", "util")) continue; // файл от старой версии агента — без видеокарты

                foreach (var r in c.Cmd("""
                    SELECT ts, adapter, name, util, util_max, util_3d, util_video, util_compute, mem_dedicated, mem_total, temp, temp_max, power, fan, mem_shared
                    FROM gpu_1m WHERE ts >= $f ORDER BY ts
                    """, ("$f", from)).Rows())
                {
                    // У встроенной графики «своей» памяти почти нет (128 МБ), она работает в общей памяти —
                    // для неё показываем выделенную + общую и не показываем объём «из N».
                    long total = r.Long0(9);
                    bool integrated = total < 1L << 30;
                    long mem = integrated ? r.Long0(8) + r.Long0(14) : r.Long0(8);
                    rows.Add((r.GetInt64(0), r.GetString(1), r.StrOrNull(2), r.Dbl0(3), r.Dbl0(4), r.Dbl0(5), r.Dbl0(6), r.Dbl0(7), mem, integrated ? 0 : total,
                        r.IsDBNull(10) ? null : r.GetDouble(10), r.IsDBNull(11) ? null : r.GetDouble(11),
                        r.IsDBNull(12) ? null : r.GetDouble(12), r.IsDBNull(13) ? null : r.GetInt32(13)));
                }

                foreach (var r in c.Cmd("""
                    SELECT p.image, g.adapter, SUM(g.util), MAX(g.util), MAX(g.mem), COUNT(*)
                    FROM gpu_proc_1m g JOIN process p ON p.id = g.proc_id
                    WHERE g.ts >= $f GROUP BY p.image, g.adapter
                    """, ("$f", from)).Rows())
                {
                    var key = (r.GetString(0), r.GetString(1));
                    var v = apps.GetValueOrDefault(key);
                    apps[key] = (v.Sum + r.Dbl0(2), Math.Max(v.Peak, r.Dbl0(3)), Math.Max(v.Mem, r.Long0(4)), v.N + r.GetInt32(5));
                }
            }
            catch (SqliteException) { /* файл занят агентом */ }
        }

        if (rows.Count == 0) return new GpuView(false, ts, [], [], []);

        var adapters = new List<GpuAdapterView>();
        var series = new List<GpuSeries>();
        int minutesTotal = Math.Max(1, rows.Select(r => r.Ts).Distinct().Count());

        foreach (var g in rows.GroupBy(r => r.Adapter))
        {
            var list = g.ToList();
            var last = list[^1];
            var temps = list.Where(r => r.Temp is not null).ToList();
            adapters.Add(new GpuAdapterView(g.Key, list.LastOrDefault(r => r.Name is not null).Name ?? "Видеоадаптер", last.MemTotal,
                last.Util, last.Temp, last.Power, last.Fan, last.Mem, last.Ts,
                list.Average(r => r.Util), list.Max(r => r.UtilMax),
                temps.Count > 0 ? temps.Max(r => r.TempMax ?? r.Temp) : null,
                temps.Count > 0 ? temps.Average(r => r.Temp!.Value) : null));

            double?[] Bin(Func<(long Ts, string Adapter, string? Name, double Util, double UtilMax, double U3D, double Video, double Compute, long Mem, long MemTotal, double? Temp, double? TempMax, double? Power, int? Fan), double?> f, bool max = false)
            {
                var sum = new double[n]; var cnt = new int[n]; var mx = new double?[n];
                foreach (var r in list)
                {
                    int i = (int)((r.Ts - first) / step);
                    if (i < 0 || i >= n || f(r) is not { } v) continue;
                    sum[i] += v; cnt[i]++; mx[i] = Math.Max(mx[i] ?? double.MinValue, v);
                }
                return Enumerable.Range(0, n).Select(i => cnt[i] == 0 ? (double?)null : max ? mx[i] : sum[i] / cnt[i]).ToArray();
            }

            series.Add(new GpuSeries(g.Key, Bin(r => r.Util), Bin(r => r.U3D), Bin(r => r.Video), Bin(r => r.Compute), Bin(r => r.Mem, max: true), Bin(r => r.Temp)));
        }

        var appList = apps.Select(kv => new GpuApp(kv.Key.Name, kv.Key.Adapter, kv.Value.Sum / minutesTotal, kv.Value.Peak, kv.Value.Mem, kv.Value.N))
            .Where(a => a.AvgUtil >= 0.05 || a.MemMax > 0)
            .OrderByDescending(a => a.AvgUtil).ThenByDescending(a => a.MemMax).Take(40).ToList();

        // Сначала дискретная (больше видеопамяти), потом встроенная.
        var order = adapters.OrderByDescending(a => a.MemTotal).Select(a => a.Key).ToList();
        return new GpuView(true, ts, adapters.OrderBy(a => order.IndexOf(a.Key)).ToList(),
            series.OrderBy(s => order.IndexOf(s.Adapter)).ToList(), appList);
    }
}
