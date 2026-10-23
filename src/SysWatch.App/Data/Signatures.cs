using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;

namespace SysWatch.App.Data;

/// <summary>
/// Кем подписан exe. Проверяется только наличие встроенной подписи и издатель, без проверки цепочки.
/// Системные файлы Windows подписаны через каталоги, а не внутри файла, поэтому для них отдельный статус.
/// </summary>
public sealed class Signatures
{
    private readonly ConcurrentDictionary<string, (string Status, string? Signer)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public (string Status, string? Signer) Check(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return ("unknown", null);
        return _cache.GetOrAdd(path, Compute);
    }

    private static (string, string?) Compute(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // нужен именно Authenticode-сертификат из exe
            using var cert = X509Certificate.CreateFromSignedFile(path);
            using var c2 = new X509Certificate2(cert);
#pragma warning restore SYSLIB0057
            var name = c2.GetNameInfo(X509NameType.SimpleName, false);
            return ("signed", string.IsNullOrWhiteSpace(name) ? c2.Subject : name);
        }
        catch
        {
            if (path.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase)) return ("system", "Microsoft Windows");
            return StorePackage(path) is { } signer ? ("signed", signer) : ("unsigned", null);
        }
    }

    /// <summary>
    /// Приложения из Microsoft Store (MSIX/AppX) подписаны пакетом целиком: подпись лежит в AppxSignature.p7x
    /// в корне пакета, а сами exe внутри без подписи. Папку WindowsApps защищает Windows (пишет только система),
    /// поэтому файл из неё с подписью пакета считаем подписанным издателем пакета.
    /// </summary>
    private static string? StorePackage(string path)
    {
        const string marker = "\\WindowsApps\\";
        int i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        int start = i + marker.Length, end = path.IndexOf('\\', start);
        if (end < 0) return null;
        var root = path[..end];
        var sig = Path.Combine(root, "AppxSignature.p7x");
        if (!File.Exists(sig)) return null;

        try
        {
            // Формат p7x: 4 байта «PKCX», дальше PKCS#7 SignedData.
            var bytes = File.ReadAllBytes(sig);
            if (bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 'C' && bytes[3] == 'X')
            {
                var certs = new X509Certificate2Collection();
#pragma warning disable SYSLIB0057
                certs.Import(bytes.AsSpan(4).ToArray());
#pragma warning restore SYSLIB0057
                // Сертификат издателя — тот, который сам никого не подписал.
                var leaf = certs.Cast<X509Certificate2>()
                    .FirstOrDefault(c => !certs.Cast<X509Certificate2>().Any(o => o != c && o.Issuer == c.Subject));
                var name = leaf?.GetNameInfo(X509NameType.SimpleName, false);
                foreach (var c in certs) c.Dispose();
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        catch (Exception) { /* разберём имя из манифеста */ }

        try
        {
            var manifest = Path.Combine(root, "AppxManifest.xml");
            if (File.Exists(manifest))
            {
                var doc = System.Xml.Linq.XDocument.Load(manifest);
                var display = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "PublisherDisplayName")?.Value;
                if (!string.IsNullOrWhiteSpace(display)) return display.Trim();
            }
        }
        catch (Exception) { }
        return L.T("Пакет Microsoft Store");
    }
}
