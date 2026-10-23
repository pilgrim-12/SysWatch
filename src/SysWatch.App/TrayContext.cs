using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using SysWatch.Agent.Interop;
using SysWatch.App.Anomalies;
using SysWatch.App.Data;
using SysWatch.App.Browsing;
using SysWatch.App.Dev;
using SysWatch.App.Disk;
using SysWatch.App.Programs;
using SysWatch.App.Reports;
using SysWatch.App.Sys;
using SysWatch.App.Sampling;
using SysWatch.App.Settings;

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
    private readonly BrowserHistory _history;
    private readonly System.Windows.Forms.Timer _historyTimer = new() { Interval = 20_000 };
    private bool _applyingHistory;
    private readonly System.Windows.Forms.Timer _diskTimer = new() { Interval = 3_600_000 };
    private readonly Insights _insights;
    private readonly ReportCenter _reports;
    private readonly Alerts _alerts;
    private readonly GpuQueries _gpu;
    private string _balloonTarget = "#anomalies";
    private bool _detecting;
    private double _lastCpu = 100;
    private readonly ToolStripMenuItem _agentItem = new("…") { Enabled = false };
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
        _insights = new Insights(files, _anomalies, _disk);
        var startup = new Startup(signatures);
        var dev = new DevEnv(installed);
        var gpuQueries = _gpu = new GpuQueries(files);
        var settings = new AppSettings(files);
        _alerts = new Alerts(settings);
        var health = new Health(_queries, _insights, _anomalies, _disk, startup, dev, gpuQueries);
        _reports = new ReportCenter(health, _insights, _queries, _anomalies, startup, firewall, dev, _disk);
        _history = new BrowserHistory(_anomalies);
        _api = new Api(_queries, _live, _anomalies, _disk, connections, installed, firewall, _geo,
            startup, _hardware, _insights, dev, _history, gpuQueries, health, _reports,
            settings, new Updates(), new GeoTraffic(files, _geo, settings));
        _detector = new Detector(files, signatures, _anomalies);

        L.SetDefault(_anomalies.Get("ui_lang")); // язык, выбранный в окне

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("", null, (_, _) => ShowWindow());
        open.Font = new Font(open.Font, FontStyle.Bold);
        var anomaliesItem = new ToolStripMenuItem("", null, (_, _) => ShowWindow("#anomalies"));
        var reportItem = new ToolStripMenuItem("", null, (_, _) => ShowWindow("#report"));
        var exitItem = new ToolStripMenuItem("", null, (_, _) => Quit());
        menu.Items.Add(open);
        menu.Items.Add(anomaliesItem);
        menu.Items.Add(reportItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_agentItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        void Relabel()
        {
            open.Text = L.T("Открыть SysWatch");
            anomaliesItem.Text = L.T("Аномалии");
            reportItem.Text = L.T("Отчёт за неделю");
            exitItem.Text = L.T("Выход");
        }
        Relabel();
        // Язык меняют в окне (другой поток) — подписи меню обновляем в потоке трея.
        var ui = SynchronizationContext.Current;
        L.DefaultChanged += () => { if (ui is null) Relabel(); else ui.Post(_ => Relabel(), null); };

        _tray = new NotifyIcon { Icon = _icon, Text = "SysWatch", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        _tray.BalloonTipClicked += (_, _) => ShowWindow(_balloonTarget);

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();

        _detectTimer.Tick += async (_, _) => await DetectAsync();
        _detectTimer.Start();

        // Раз в час проверяем, не пора ли сделать суточный снимок диска (только если компьютер не занят).
        _diskTimer.Tick += async (_, _) => { _disk.MaybeAutoScan(_lastCpu); Notify(_alerts.CheckDisk()); await DigestAsync(); await ScheduledReportsAsync(); };
        _diskTimer.Start();

        // Отложенное удаление истории: ждём, пока пользователь сам закроет браузер, и удаляем.
        _historyTimer.Tick += async (_, _) => await ApplyHistoryAsync();
        _historyTimer.Start();

        if (openWindow) ShowWindow();
    }

    /// <summary>Отчёты по расписанию (раз в час проверяем, каким пора) — в «Документы\\SysWatch\\Reports».</summary>
    private async Task ScheduledReportsAsync()
    {
        try
        {
            var files = await Task.Run(() => _reports.RunDue());
            if (files.Count == 0) return;
            _balloonTarget = "#report";
            _tray.ShowBalloonTip(8_000, L.T("SysWatch: отчёт готов"),
                string.Join("\n", files.Select(Path.GetFileName)), ToolTipIcon.Info);
        }
        catch (Exception) { /* попробуем через час */ }
    }

    private async Task ApplyHistoryAsync()
    {
        if (_applyingHistory) return;
        _applyingHistory = true;
        try
        {
            var r = await Task.Run(() => _history.ApplyPending());
            if (r is not null)
            {
                _balloonTarget = "#history";
                _tray.ShowBalloonTip(8_000, L.T("SysWatch: история очищена"),
                    L.T("{0}: удалено записей — {1}. Удаление выполнено после закрытия браузера.", string.Join(", ", r.Browsers), r.Deleted), ToolTipIcon.Info);
            }
        }
        catch (Exception) { /* попробуем через 20 секунд */ }
        finally { _applyingHistory = false; }
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
                _history.Clear(); // прочитанную историю браузеров не держим в памяти
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

        try { Notify(_alerts.Observe(cpu, mem.dwMemoryLoad, () => TopApp(byCpu: true), () => TopApp(byCpu: false))); }
        catch (Exception) { /* оповещения не должны мешать трею */ }

        var agent = _queries.Agent();
        string agentLine = agent.Running
            ? L.T("Агент работает, {0:F1}% процессора", agent.Cpu)
            : L.T("Агент не пишет данные");
        _agentItem.Text = agentLine;

        // Подсказка у иконки в трее ограничена 127 символами.
        var text = "SysWatch\n" + L.T("Процессор {0:F0}%, память {1}%", cpu, mem.dwMemoryLoad) + "\n" + agentLine;
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    private void Notify(AlertMessage? m)
    {
        if (m is null) return;
        _balloonTarget = m.Hash;
        _tray.ShowBalloonTip(10_000, m.Title, m.Text, ToolTipIcon.Warning);
    }

    /// <summary>Кто сейчас больше всех грузит процессор или занимает память (только в момент оповещения).</summary>
    private string? TopApp(bool byCpu)
    {
        try
        {
            var apps = _live.Sample(_queries.Agent()).Apps;
            var top = byCpu ? apps.Where(a => a.Cpu > 0).OrderByDescending(a => a.Cpu).FirstOrDefault() : apps.OrderByDescending(a => a.Mem).FirstOrDefault();
            return top?.Name;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Поиск аномалий в фоне; о важных (high, medium) — уведомление Windows.</summary>
    private async Task DetectAsync()
    {
        if (_detecting) return;
        _detecting = true;
        try
        {
            try
            {
                var temp = await Task.Run(() => _gpu.Get(10).Adapters.Select(a => a.PeakTemp).Max());
                Notify(_alerts.CheckGpu(temp));
            }
            catch (Exception) { /* нет видеокарты или данных — молчим */ }

            var found = await Task.Run(() => _detector.Run());
            var notable = found.Where(a => a.Severity is "high" or "medium").ToList();
            if (notable.Count > 0) _balloonTarget = "#anomalies";
            if (notable.Count == 1)
                _tray.ShowBalloonTip(10_000, notable[0].Title, notable[0].Detail, notable[0].Severity == "high" ? ToolTipIcon.Warning : ToolTipIcon.Info);
            else if (notable.Count > 1)
                _tray.ShowBalloonTip(10_000, L.T("SysWatch: новых аномалий — {0}", notable.Count),
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

    /// <summary>
    /// Раз в неделю — уведомление с итогами. Первая неделя после установки отсчитывается с момента запуска,
    /// показываем днём (9–21), чтобы не будить уведомлением ночью.
    /// </summary>
    private async Task DigestAsync()
    {
        const string key = "digest_last";
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            if (!long.TryParse(_anomalies.Get(key), out var last)) { _anomalies.Set(key, now.ToString()); return; }
            if (now - last < 7 * 86400 || DateTime.Now.Hour is < 9 or >= 21) return;
            _anomalies.Set(key, now.ToString());

            var r = await Task.Run(() => _insights.Build(now - 7 * 86400, now));
            if (r.Hours < 1) return;
            int anomalies = r.Anomalies.Sum(a => a.Count);
            var text = L.T("Процессор в среднем {0:F0}%, память до {1:F0}%.", r.AvgCpu, r.PeakMem) + "\n" +
                       (r.Episodes.Count > 0 ? L.T("Перегрузок: {0}, всего {1} мин.", r.Episodes.Count, r.SlowMinutes) : L.T("Перегрузок не было.")) + "\n" +
                       L.T("Новых программ: {0}, аномалий: {1}.", r.NewPrograms.Count, anomalies);
            _balloonTarget = "#report";
            _tray.ShowBalloonTip(15_000, L.T("SysWatch: итоги недели"), text, ToolTipIcon.Info);
        }
        catch (Exception)
        {
            // Сводка не обязательна: попробуем через час.
        }
    }

    private void Quit()
    {
        _detectTimer.Stop();
        _historyTimer.Stop();
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
