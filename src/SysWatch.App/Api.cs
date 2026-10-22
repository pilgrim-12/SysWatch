using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using SysWatch.App.Data;
using SysWatch.App.Sampling;

namespace SysWatch.App;

/// <summary>Маршрутизатор /api/* для интерфейса. Возвращает (код, JSON).</summary>
public sealed class Api(Queries queries, LiveSampler live)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    // История читается по одному запросу за раз: SQLite-файлы общие, а кэши простые.
    private readonly SemaphoreSlim _history = new(1, 1);

    public async Task<(int Status, string Body)> Handle(Uri uri)
    {
        var q = HttpUtility.ParseQueryString(uri.Query);
        int range = int.TryParse(q["range"], out var r) ? r : 60;

        try
        {
            if (uri.AbsolutePath == "/api/live")
                return (200, Serialize(live.Sample(queries.Agent())));

            await _history.WaitAsync();
            try
            {
                object? body = uri.AbsolutePath switch
                {
                    "/api/overview" => queries.Overview(range),
                    "/api/timeline" => queries.Timeline(range),
                    "/api/network" => queries.Network(range),
                    "/api/app" => queries.App(q["name"] ?? "", range),
                    "/api/events" => queries.Events(range),
                    _ => null,
                };
                return body is null ? (404, "\"not found\"") : (200, Serialize(body));
            }
            finally
            {
                _history.Release();
            }
        }
        catch (Exception ex)
        {
            return (500, JsonSerializer.Serialize(ex.Message));
        }
    }

    private static string Serialize(object o) => JsonSerializer.Serialize(o, o.GetType(), Json);
}
