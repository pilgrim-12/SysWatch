using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace SysWatch.App;

/// <summary>
/// Перевод текстов, которые формирует программа (вердикты, названия сбоев, сообщения, уведомления трея).
/// Словари — те же JSON, что у интерфейса (src/ui/src/i18n), вшиты в сборку. Ключ — русская строка-шаблон.
/// Язык запроса из окна передаётся параметром lang и действует на время обработки запроса (AsyncLocal);
/// для трея — язык, выбранный в окне (сохраняется в базе состояния).
///
///   L.T("Удалено файлов: {0}", n)     — перевести и подставить (форматы .NET: {0:F0}, {1:N1}…)
///   L.K("…")                          — пометить строку как ключ словаря без перевода (для хранения шаблона в базе)
/// </summary>
public static class L
{
    public static readonly string[] Supported = ["ru", "en", "es", "fr"];

    private static readonly AsyncLocal<string?> Request = new();
    private static readonly Dictionary<string, Dictionary<string, string>> Dicts = new();
    private static readonly object Gate = new();

    /// <summary>Язык трея и всего, что выполняется вне запроса окна.</summary>
    public static string Default { get; private set; } = Detect();

    public static event Action? DefaultChanged;

    public static string Lang => Request.Value ?? Default;

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Lang switch { "en" => "en-US", "es" => "es-ES", "fr" => "fr-FR", _ => "ru-RU" });

    private static string Detect()
    {
        var ui = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Supported.Contains(ui) ? ui : "en";
    }

    public static void SetDefault(string? lang)
    {
        if (lang is null || !Supported.Contains(lang) || lang == Default) return;
        Default = lang;
        DefaultChanged?.Invoke();
    }

    /// <summary>Язык для текущего запроса окна (до конца асинхронной цепочки).</summary>
    public static void UseForRequest(string? lang) => Request.Value = lang is not null && Supported.Contains(lang) ? lang : null;

    public static string K(string ru) => ru;

    public static string T(string ru, params object?[] args)
    {
        var lang = Lang;
        var s = lang == "ru" ? ru : Dict(lang).TryGetValue(ru, out var v) ? v : ru;
        if (args.Length == 0) return s;
        try { return string.Format(Culture, s, args); }
        catch (FormatException) { return string.Format(Culture, ru, args); } // кривой перевод не должен ронять ответ
    }

    /// <summary>Слово в нужной форме числа: ключ «one|few|many» по-русски, значение «one|other».</summary>
    public static string P(long n, string one, string few, string many)
    {
        if (Lang == "ru")
        {
            long a = Math.Abs(n) % 100, b = a % 10;
            return a is > 10 and < 20 ? many : b == 1 ? one : b is >= 2 and <= 4 ? few : many;
        }
        if (!Dict(Lang).TryGetValue($"{one}|{few}|{many}", out var v)) return many;
        var parts = v.Split('|');
        return n == 1 || (Lang == "fr" && n == 0) ? parts[0] : parts.ElementAtOrDefault(1) ?? parts[0];
    }

    private static Dictionary<string, string> Dict(string lang)
    {
        lock (Gate)
        {
            if (Dicts.TryGetValue(lang, out var d)) return d;
            d = new Dictionary<string, string>();
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"i18n.{lang}.json");
                if (s is not null) d = JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? d;
            }
            catch (Exception) { /* без словаря — по-русски */ }
            Dicts[lang] = d;
            return d;
        }
    }
}
