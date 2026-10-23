using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SysWatch.App.Reports;

namespace SysWatch.App.Settings;

public sealed record AiModels(bool Ok, string Message, IReadOnlyList<string> Models);
public sealed record AiAnswer(bool Ok, string Text, string? Model);

/// <summary>
/// Объяснение состояния компьютера простыми словами через локальную модель (Ollama).
/// Ничего не уходит в интернет: разрешены только адреса этого компьютера, и всё выключено, пока пользователь не включит.
/// </summary>
public sealed class LocalAi(AppSettings settings)
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(3) };

    public static bool IsLocal(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
        && (u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    /// <summary>Какие модели установлены в Ollama (заодно проверка, что она запущена).</summary>
    public async Task<AiModels> Models(string? url = null)
    {
        url = (string.IsNullOrWhiteSpace(url) ? settings.Ai.Url : url).TrimEnd('/');
        if (!IsLocal(url)) return new AiModels(false, L.T("Адрес модели должен указывать на этот компьютер (localhost)."), []);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url + "/api/tags", cts.Token));
            var names = doc.RootElement.TryGetProperty("models", out var m)
                ? m.EnumerateArray().Select(x => x.TryGetProperty("name", out var n) ? n.GetString() : null).OfType<string>().OrderBy(x => x).ToList()
                : [];
            return names.Count == 0
                ? new AiModels(true, L.T("Ollama запущена, но моделей нет. Установите, например: ollama pull llama3.2"), names)
                : new AiModels(true, L.T("Ollama работает, моделей: {0}", names.Count), names);
        }
        catch (Exception)
        {
            return new AiModels(false, L.T("Ollama не отвечает по адресу {0}. Установите её с ollama.com и запустите.", url), []);
        }
    }

    public async Task<AiAnswer> Explain(HealthReport h)
    {
        var ai = settings.Ai;
        if (!ai.Enabled) return new AiAnswer(false, L.T("Локальный ИИ выключен. Его можно включить в настройках."), null);
        if (!IsLocal(ai.Url)) return new AiAnswer(false, L.T("Адрес модели должен указывать на этот компьютер (localhost)."), null);

        var model = ai.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            var list = await Models(ai.Url);
            if (!list.Ok || list.Models.Count == 0) return new AiAnswer(false, list.Message, null);
            model = list.Models[0];
        }

        var prompt = Prompt(h);
        try
        {
            using var resp = await Http.PostAsJsonAsync(ai.Url.TrimEnd('/') + "/api/generate",
                new { model, prompt, stream = false, options = new { temperature = 0.2 } });
            if (!resp.IsSuccessStatusCode)
                return new AiAnswer(false, L.T("Модель вернула ошибку: {0}", (int)resp.StatusCode), model);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var text = doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() ?? "" : "";
            return new AiAnswer(text.Length > 0, text.Trim(), model);
        }
        catch (TaskCanceledException)
        {
            return new AiAnswer(false, L.T("Модель не ответила за 3 минуты. Попробуйте модель поменьше."), model);
        }
        catch (Exception ex)
        {
            return new AiAnswer(false, L.T("Не удалось обратиться к модели: {0}", ex.Message), model);
        }
    }

    /// <summary>Только сводные цифры и найденные проблемы — без истории браузеров, адресов и командных строк.</summary>
    private static string Prompt(HealthReport h)
    {
        var lang = L.Lang switch { "en" => "English", "es" => "Spanish", "fr" => "French", _ => "Russian" };
        var sb = new StringBuilder();
        sb.AppendLine($"You are a friendly PC technician. Answer in {lang}, plainly, for a non-expert, in at most 8 short sentences.");
        sb.AppendLine("Explain the overall state of this Windows computer, what matters most and what to do first. Do not invent facts beyond the data.");
        sb.AppendLine();
        sb.AppendLine($"Health score: {h.Score}/100 ({h.GradeKey}).");
        var f = h.Facts;
        sb.AppendLine($"CPU average over 24h: {f.CpuAvg:F0}%. Memory average {f.MemAvg:F0}%, peak {f.MemPeak:F0}%.");
        if (f.DriveTotal > 0) sb.AppendLine($"System drive free: {f.DriveFree / 1e9:F1} GB of {f.DriveTotal / 1e9:F0} GB.");
        sb.AppendLine($"Uptime: {f.UptimeSeconds / 3600} hours. Overloads in 7 days: {f.Slowdowns7d}. Crashes in 7 days: {f.Crashes7d}. Anomalies in 7 days: {f.Anomalies7d}.");
        if (f.GpuPeakTemp is double g) sb.AppendLine($"GPU peak temperature: {g:F0} °C.");
        sb.AppendLine("Findings:");
        if (h.Findings.Count == 0) sb.AppendLine("- none");
        foreach (var x in h.Findings) sb.AppendLine($"- [{x.Severity}] {x.Title}. {x.Detail}");
        return sb.ToString();
    }
}
