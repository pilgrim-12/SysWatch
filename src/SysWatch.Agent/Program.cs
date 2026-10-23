using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Options;
using SysWatch.Agent;
using SysWatch.Agent.Collectors;
using SysWatch.Agent.Interop;
using SysWatch.Agent.Pipeline;
using SysWatch.Agent.Storage;

if (TraceEventSession.IsElevated() != true)
{
    Console.Error.WriteLine("SysWatch Agent нужно запускать от администратора (ETW ядра требует повышенных прав).");
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(o => o.ServiceName = "SysWatchAgent");
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));

builder.Services.AddSingleton<ProcessRegistry>();
builder.Services.AddSingleton<Aggregator>();
builder.Services.AddSingleton<EtwStats>();
builder.Services.AddSingleton<Costs>();
builder.Services.AddSingleton<SqliteStore>();

builder.Services.AddHostedService<KernelEtwCollector>();
builder.Services.AddHostedService<DnsEtwCollector>();
builder.Services.AddHostedService<ProcessSampler>();
builder.Services.AddHostedService<GpuSampler>();
builder.Services.AddHostedService<Flusher>();

var host = builder.Build();

var opts = host.Services.GetRequiredService<IOptions<AgentOptions>>().Value;
var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SysWatch");

if (opts.BackgroundMode)
    log.LogInformation("Background mode (low CPU/IO/memory priority): {Ok}", Native.EnterBackgroundMode());
if (opts.EcoQoS)
    log.LogInformation("EcoQoS (efficiency cores, Windows 11): {Ok}", Native.EnableEcoQoS());

host.Run();
return 0;
