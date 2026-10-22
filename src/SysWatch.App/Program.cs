namespace SysWatch.App;

internal static class Program
{
    private const string MutexName = @"Global\SysWatch.App";
    private const string ShowEventName = @"Global\SysWatch.App.Show";

    /// <summary>
    /// SysWatch.exe          — открыть окно (если уже запущен в трее — поднять его окно).
    /// SysWatch.exe --tray   — запуститься свёрнутым в трей (так стартует при входе в Windows).
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        bool trayOnly = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Уже работает: просим первый экземпляр показать окно и выходим.
            if (!trayOnly && EventWaitHandle.TryOpenExisting(ShowEventName, out var show))
                using (show) show.Set();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var context = new TrayContext(openWindow: !trayOnly);

        // Сигнал «покажи окно» от второго запуска приходит в фоновом потоке — переносим в UI-поток.
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var listener = new Thread(() =>
        {
            while (showEvent.WaitOne()) ui.Post(_ => context.ShowWindow(), null);
        }) { IsBackground = true, Name = "SysWatch show listener" };
        listener.Start();

        Application.Run(context);
    }
}
