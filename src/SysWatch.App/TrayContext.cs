using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using SysWatch.Agent.Interop;
using SysWatch.App.Anomalies;
using SysWatch.App.Data;
using SysWatch.App.Disk;
using SysWatch.App.Programs;
using SysWatch.App.Sys;
using SysWatch.App.Sampling;

namespace SysWatch.App;

/// <summary>
/// Жизнь приложения в трее: иконка с подсказкой (процессор, память, агент), меню, открытие окна,
/// раз в минуту — поиск аномалий и уведомления о важных.
/// Пока окно закрыто, раз в 5 секунд снимаются только две системные цифры.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly LiveSampler _live = new();
    private readonly Queries _queries;
    private readonly Api _api;
    private readonly Icon _icon = LoadIcon();
    private readonly Icon _windowIcon = LoadIcon(large: true);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    private readonly System.Windows.Forms.Timer _detectTimer = new() { Interval = 60_000 };
    private readonly AnomalyStore _anomalies = new();
    private readonly Detector _detector;
    private readonly DiskService _disk = new();
    private readonly GeoIp _geo = new();
    private readonly Hardware _hardware = new();
    private readonly System.Windows.Forms.Timer _diskTimer = new() { Interval = 3_600_000 };
    private bool _detecting;
    private double _lastCpu = 100;
    private readonly ToolStripMenuItem _agentItem = new("Агент: проверяю…") { Enabled = false };
    private MainForm? _form;

    private long _idle, _kernel, _user;

    public TrayContext(bool openWindow)
    {
        var files = new DayFiles();
        var signatures = new Signatures();
        _queries = new Queries(files, signatures, _live.Alive);
        var connections = new Connections(_queries.RecentIpDomains, _geo);
        var firewall = new Firewall(_anomalies);
        var installed = new Installed(files, LiveSampler.RunningPaths);
        _api = new Api(_queries, _live, _anomalies, _disk, connections, installed, firewall, _geo,
            new Startup(signatures), _hardware);
        _detector = new Detector(files, signatures, _anomalies);

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Открыть SysWatch", null, (_, _) => ShowWindow());
        open.Font = new Font(open.Font, FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripMenuItem("Аномалии", null, (_, _) => ShowWindow("#anomalies")));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_agentItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => Quit()));

        _tray = new NotifyIcon { Icon = _icon, Text = "SysWatch", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        _tray.BalloonTipClicked += (_, _) => ShowWindow("#anomalies");

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();

        _detectTimer.Tick += async (_, _) => await DetectAsync();
        _detectTimer.Start();

        // Раз в час проверяем, не пора ли сделать суточный снимок диска (только если компьютер не занят).
        _diskTimer.Tick += (_, _) => _disk.MaybeAutoScan(_lastCpu);
        _diskTimer.Start();

        if (openWindow) ShowWindow();
    }

    /// <summary>Открыть окно (при необходимости — сразу на разделе hash) или поднять уже открытое.</summary>
    public void ShowWindow(string? hash = null)
    {
        if (_form is null || _form.IsDisposed)
        {
            _form = new MainForm(_api, _windowIcon, hash);
            _form.FormClosed += (_, _) =>
            {
                _form = null;
                _geo.Unload();
                _hardware.Dispose(); // счётчики частоты процессора нужны только при открытом окне
                GC.Collect(); // освобождаем память окна сразу, а не когда-нибудь потом
            };
            _form.Show();
        }

        else if (hash is not null)
        {
            _form.GoTo(hash);
        }

        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        _form.BringToFront();
    }

    private void Refresh()
    {
        double cpu = 0;
        if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            long dIdle = idle - _idle, dTotal = (kernel - _kernel) + (user - _user);
            if (_kernel != 0 && dTotal > 0) cpu = Math.Clamp((1.0 - (double)dIdle / dTotal) * 100, 0, 100);
            _idle = idle; _kernel = kernel; _user = user;
        }
        _lastCpu = cpu;

        var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref mem);

        var agent = _queries.Agent();
        string agentLine = agent.Running
            ? $"Агент работает, {agent.Cpu:F1}% процессора"
            : "Агент не пишет данные";
        _agentItem.Text = agentLine;

        // Подсказка у иконки в трее ограничена 127 символами.
        var text = $"SysWatch\nПроцессор {cpu:F0}%, память {mem.dwMemoryLoad}%\n{agentLine}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    /// <summary>Поиск аномалий в фоне; о важных (high, medium) — уведомление Windows.</summary>
    private async Task DetectAsync()
    {
        if (_detecting) return;
        _detecting = true;
        try
        {
            var found = await Task.Run(() => _detector.Run());
            var notable = found.Where(a => a.Severity is "high" or "medium").ToList();
            if (notable.Count == 1)
                _tray.ShowBalloonTip(10_000, notable[0].Title, notable[0].Detail, notable[0].Severity == "high" ? ToolTipIcon.Warning : ToolTipIcon.Info);
            else if (notable.Count > 1)
                _tray.ShowBalloonTip(10_000, $"SysWatch: новых аномалий — {notable.Count}",
                    string.Join("\n", notable.Take(3).Select(a => $"{a.Title}: {a.App}")), ToolTipIcon.Warning);
        }
        catch (Exception)
        {
            // Детектор не должен ронять приложение: следующая попытка через минуту.
        }
        finally
        {
            _detecting = false;
        }
    }

    private void Quit()
    {
        _detectTimer.Stop();
        _diskTimer.Stop();
        _disk.Cancel();
        _timer.Stop();
        _form?.Close();
        _tray.Visible = false;
        _tray.Dispose();
        _live.Dispose();
        ExitThread();
    }

    /// <summary>Иконка из syswatch.ico рядом с exe; если файла нет — нарисованная на лету.</summary>
    private static Icon LoadIcon(bool large = false)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "syswatch.ico");
        try { if (File.Exists(path)) return large ? new Icon(path) : new Icon(path, SystemInformation.SmallIconSize); }
        catch (Exception) { /* битый файл — рисуем сами */ }
        return MakeIcon();
    }

    /// <summary>Иконка: импульс янтарного цвета в тёмно-синей рамке, как в окне.</summary>
    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(15, 26, 42));
            using var path = new GraphicsPath();
            const int r = 8;
            path.AddArc(1, 1, r * 2, r * 2, 180, 90);
            path.AddArc(30 - r * 2, 1, r * 2, r * 2, 270, 90);
            path.AddArc(30 - r * 2, 30 - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(1, 30 - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            g.FillPath(bg, path);
            using var border = new Pen(Color.FromArgb(79, 195, 217), 2f);
            g.DrawPath(border, path);
            using var pulse = new Pen(Color.FromArgb(242, 181, 68), 3f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pulse, new[] { new Point(5, 18), new Point(11, 18), new Point(14, 9), new Point(18, 24), new Point(21, 15), new Point(27, 15) });
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
