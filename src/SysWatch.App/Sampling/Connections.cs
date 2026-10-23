using System.Net;
using System.Runtime.InteropServices;
using SysWatch.Agent.Interop;

namespace SysWatch.App.Sampling;

public sealed record ConnRow(
    string Key, string Proto, string Dir, int Pid, string App, string? Path,
    string Local, int LocalPort, string Remote, int RemotePort, string? Domain,
    string State, long FirstSeen, long? ClosedAt, bool Exposed, string? Country);

public sealed record ConnSnapshot(long Ts, IReadOnlyList<ConnRow> Rows, int Out, int In, int Listen, int Exposed,
    bool GeoAvailable, string? GeoStatus);

/// <summary>
/// Живые соединения из таблиц TCP/UDP Windows (GetExtendedTcpTable / GetExtendedUdpTable).
/// Снимается по запросу окна раз в секунду; агент в этом не участвует.
/// Направление: если локальный порт соединения слушает эта же программа — входящее, иначе исходящее.
/// </summary>
public sealed class Connections(Func<Dictionary<string, string>> ipToDomain, GeoIp geo)
{
    private const int AF_INET = 2, AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5, UDP_TABLE_OWNER_PID = 1;
    private const int KeepClosedSeconds = 10;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    private static readonly string[] States =
        ["?", L.K("закрыто"), L.K("слушает"), L.K("подключается"), L.K("принимает"), L.K("установлено"), L.K("закрывается"), L.K("закрывается"),
         L.K("закрывается"), L.K("закрывается"), L.K("закрывается"), L.K("ожидание"), L.K("удаляется")];

    private readonly object _gate = new();
    private readonly Dictionary<string, ConnRow> _known = new();
    private readonly Dictionary<int, (string Name, string? Path)> _procs = new();
    private readonly char[] _buf = new char[1024];
    private Dictionary<string, string> _domains = new();
    private long _domainsAt;

    private readonly record struct Raw(string Proto, int Pid, IPAddress LocalIp, int LocalPort, IPAddress RemoteIp, int RemotePort, int State);

    public ConnSnapshot Sample()
    {
        lock (_gate)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now - _domainsAt > 20)
            {
                try { _domains = ipToDomain(); } catch (Exception) { }
                _domainsAt = now;
            }

            var raw = new List<Raw>();
            ReadTcp(AF_INET, raw);
            ReadTcp(AF_INET6, raw);
            ReadUdp(AF_INET, raw);
            ReadUdp(AF_INET6, raw);

            // Порты, которые слушает каждая программа: по ним определяем входящие соединения.
            var listening = raw.Where(r => r.State == 2 || r.Proto == "UDP").Select(r => (r.Pid, r.LocalPort)).ToHashSet();
            var alive = new HashSet<string>();

            foreach (var r in raw)
            {
                if (r.Pid <= 4 && r.State != 2) continue;      // TIME_WAIT и системные хвосты без владельца
                if (r.State == 11) continue;                    // TIME_WAIT
                string dir = r.State == 2 || r.Proto == "UDP" ? "listen"
                    : listening.Contains((r.Pid, r.LocalPort)) ? "in" : "out";

                var key = $"{r.Proto}|{r.Pid}|{r.LocalIp}|{r.LocalPort}|{r.RemoteIp}|{r.RemotePort}";
                alive.Add(key);
                var (name, path) = Proc(r.Pid);
                var remote = r.RemoteIp.ToString();
                _domains.TryGetValue(remote, out var domain);
                if (domain is null && IPAddress.IsLoopback(r.RemoteIp)) domain = "localhost";

                var state = r.Proto == "UDP" ? L.T("слушает") : r.State < States.Length ? L.T(States[r.State]) : "?";
                long first = _known.TryGetValue(key, out var old) && old.ClosedAt is null ? old.FirstSeen : now;

                // Доступен из сети: слушает не только на 127.0.0.1/::1, а на всех или внешних адресах.
                bool exposed = dir != "out" && !IPAddress.IsLoopback(r.LocalIp);
                string? country = dir == "listen" ? null
                    : old is not null && old.Remote == remote && old.Country is not null ? old.Country
                    : geo.Country(r.RemoteIp);
                _known[key] = new ConnRow(key, r.Proto, dir, r.Pid, name, path, r.LocalIp.ToString(), r.LocalPort,
                    remote, r.RemotePort, domain, state, first, null, exposed, country);
            }

            // Закрытые держим ещё немного, чтобы в интерфейсе было видно, что соединение исчезло.
            foreach (var key in _known.Keys.ToList())
            {
                if (alive.Contains(key)) continue;
                var row = _known[key];
                if (row.ClosedAt is null) _known[key] = row with { ClosedAt = now, State = L.T("закрыто") };
                else if (now - row.ClosedAt > KeepClosedSeconds) _known.Remove(key);
            }

            var pids = raw.Select(r => r.Pid).ToHashSet();
            foreach (var pid in _procs.Keys.Where(p => !pids.Contains(p)).ToList()) _procs.Remove(pid);

            var rows = _known.Values
                .OrderBy(r => r.ClosedAt is null ? 0 : 1)
                .ThenByDescending(r => r.FirstSeen)
                .Take(2000)
                .ToList();
            return new ConnSnapshot(now, rows,
                rows.Count(r => r.Dir == "out" && r.ClosedAt is null),
                rows.Count(r => r.Dir == "in" && r.ClosedAt is null),
                rows.Count(r => r.Dir == "listen" && r.ClosedAt is null),
                rows.Where(r => r.Dir == "listen" && r.Proto == "TCP" && r.Exposed && r.ClosedAt is null).Select(r => (r.Pid, r.LocalPort)).Distinct().Count(),
                geo.Available, geo.Status);
        }
    }

    /// <summary>Порты, открытые в сеть (не только для самого компьютера): для правила аномалий.</summary>
    public static List<(int Pid, string Proto, int Port)> ExposedListeners()
    {
        var raw = new List<Raw>();
        ReadTcp(AF_INET, raw);
        ReadTcp(AF_INET6, raw);
        ReadUdp(AF_INET, raw);
        ReadUdp(AF_INET6, raw);
        return raw.Where(r => (r.State == 2 || r.Proto == "UDP") && r.Pid > 4 && !IPAddress.IsLoopback(r.LocalIp))
            .Select(r => (r.Pid, r.Proto, r.LocalPort)).Distinct().ToList();
    }

    private (string Name, string? Path) Proc(int pid)
    {
        if (_procs.TryGetValue(pid, out var p)) return p;
        string name = pid == 4 ? "System" : $"pid {pid}";
        string? path = null;
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                uint size = (uint)_buf.Length;
                if (Native.QueryFullProcessImageName(h, 0, _buf, ref size) && size > 0)
                {
                    path = new string(_buf, 0, (int)size);
                    name = System.IO.Path.GetFileName(path);
                }
            }
            finally { Native.CloseHandle(h); }
        }
        return _procs[pid] = (name, path);
    }

    private static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    private delegate uint TableCall(IntPtr table, ref int size);

    /// <summary>Таблица в неуправляемой памяти. Первый вызов сообщает размер; таблица может вырасти — тогда повторяем.</summary>
    private static IntPtr Fetch(TableCall call)
    {
        int size = 0;
        call(IntPtr.Zero, ref size);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            size += 4096;
            var buf = Marshal.AllocHGlobal(size);
            int s = size;
            if (call(buf, ref s) == 0) return buf;
            Marshal.FreeHGlobal(buf);
            size = Math.Max(s, size * 2);
        }
        return IntPtr.Zero;
    }

    private static void ReadTcp(int af, List<Raw> list)
    {
        var buf = Fetch((IntPtr p, ref int s) => GetExtendedTcpTable(p, ref s, false, af, TCP_TABLE_OWNER_PID_ALL, 0));
        if (buf == IntPtr.Zero) return;
        try
        {
            int n = Marshal.ReadInt32(buf);
            if (af == AF_INET)
            {
                const int rowSize = 24; // state, localAddr, localPort, remoteAddr, remotePort, pid
                for (int i = 0; i < n; i++)
                {
                    var row = buf + 4 + i * rowSize;
                    list.Add(new Raw("TCP",
                        Marshal.ReadInt32(row, 20),
                        new IPAddress((uint)Marshal.ReadInt32(row, 4)), Port((uint)Marshal.ReadInt32(row, 8)),
                        new IPAddress((uint)Marshal.ReadInt32(row, 12)), Port((uint)Marshal.ReadInt32(row, 16)),
                        Marshal.ReadInt32(row, 0)));
                }
            }
            else
            {
                const int rowSize = 56; // local[16], scope, port, remote[16], scope, port, state, pid
                for (int i = 0; i < n; i++)
                {
                    var row = buf + 4 + i * rowSize;
                    list.Add(new Raw("TCP",
                        Marshal.ReadInt32(row, 52),
                        Ip6(row, 0), Port((uint)Marshal.ReadInt32(row, 20)),
                        Ip6(row, 24), Port((uint)Marshal.ReadInt32(row, 44)),
                        Marshal.ReadInt32(row, 48)));
                }
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static void ReadUdp(int af, List<Raw> list)
    {
        var buf = Fetch((IntPtr p, ref int s) => GetExtendedUdpTable(p, ref s, false, af, UDP_TABLE_OWNER_PID, 0));
        if (buf == IntPtr.Zero) return;
        try
        {
            int n = Marshal.ReadInt32(buf);
            var any = af == AF_INET ? IPAddress.Any : IPAddress.IPv6Any;
            for (int i = 0; i < n; i++)
            {
                if (af == AF_INET)
                {
                    var row = buf + 4 + i * 12; // localAddr, localPort, pid
                    list.Add(new Raw("UDP", Marshal.ReadInt32(row, 8),
                        new IPAddress((uint)Marshal.ReadInt32(row, 0)), Port((uint)Marshal.ReadInt32(row, 4)), any, 0, 2));
                }
                else
                {
                    var row = buf + 4 + i * 28; // local[16], scope, port, pid
                    list.Add(new Raw("UDP", Marshal.ReadInt32(row, 24), Ip6(row, 0), Port((uint)Marshal.ReadInt32(row, 20)), any, 0, 2));
                }
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static IPAddress Ip6(IntPtr row, int offset)
    {
        var bytes = new byte[16];
        Marshal.Copy(row + offset, bytes, 0, 16);
        var ip = new IPAddress(bytes);
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }
}
