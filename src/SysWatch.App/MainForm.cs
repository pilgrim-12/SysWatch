using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SysWatch.App;

/// <summary>
/// Окно: WebView2 с React-интерфейсом. Статика и /api/* отдаются через перехват запросов
/// к https://syswatch.local — без HTTP-сервера и открытых портов.
/// Окно создаётся при открытии из трея и полностью уничтожается при закрытии,
/// вместе со встроенным браузером, — в фоне остаётся только иконка.
/// </summary>
public sealed class MainForm : Form
{
    private const string Host = "https://syswatch.local/";
    private static readonly Color Ink = Color.FromArgb(15, 26, 42);

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Ink };
    private readonly Api _api;
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    private CoreWebView2Environment? _env;

    public MainForm(Api api, Icon icon)
    {
        _api = api;
        Icon = icon;
        Text = "SysWatch";
        BackColor = Ink;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1440, 900);
        MinimumSize = new Size(1000, 640);
        Controls.Add(_web);

        Load += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            await InitCoreAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Не удалось запустить встроенный браузер WebView2.\n\n" + ex.Message +
                "\n\nУстановите WebView2 Runtime: https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                "SysWatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private async Task InitCoreAsync()
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysWatch", "WebView2");
        _env = await CoreWebView2Environment.CreateAsync(null, dataDir);
        await _web.EnsureCoreWebView2Async(_env);

        var core = _web.CoreWebView2;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
#endif
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.AddWebResourceRequestedFilter(Host + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnRequest;
        core.NewWindowRequested += (_, e) => e.Handled = true; // никаких всплывающих окон

        core.Navigate(Host + "index.html");
    }

    private async void OnRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var uri = new Uri(e.Request.Uri);
            if (uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
            {
                var (status, body) = await Task.Run(() => _api.Handle(uri));
                e.Response = Respond(Encoding.UTF8.GetBytes(body), status, "application/json; charset=utf-8");
            }
            else
            {
                e.Response = StaticFile(uri.AbsolutePath);
            }
        }
        catch (Exception ex)
        {
            e.Response = Respond(Encoding.UTF8.GetBytes(ex.Message), 500, "text/plain; charset=utf-8");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private CoreWebView2WebResourceResponse StaticFile(string absPath)
    {
        var rel = Uri.UnescapeDataString(absPath.TrimStart('/'));
        if (rel.Length == 0) rel = "index.html";
        var full = Path.GetFullPath(Path.Combine(_root, rel));

        // Не выпускаем запросы за пределы wwwroot.
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            return Respond(Encoding.UTF8.GetBytes("not found"), 404, "text/plain");

        return Respond(File.ReadAllBytes(full), 200, ContentType(full));
    }

    private CoreWebView2WebResourceResponse Respond(byte[] body, int status, string contentType)
    {
        var headers = $"Content-Type: {contentType}\r\nCache-Control: no-store";
        return _env!.CreateWebResourceResponse(new MemoryStream(body), status, status == 200 ? "OK" : "Error", headers);
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        _ => "application/octet-stream",
    };

    // Тёмный заголовок окна в Windows 10 20H1+ и Windows 11.
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
    }
}
