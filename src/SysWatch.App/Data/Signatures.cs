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
            return path.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase)
                ? ("system", "Microsoft Windows")
                : ("unsigned", null);
        }
    }
}
