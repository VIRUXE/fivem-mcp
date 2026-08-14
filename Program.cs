using FiveMMcp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP transport; all logging must go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<WindowManager>();
builder.Services.AddSingleton<InputService>();
builder.Services.AddSingleton<CaptureService>();
builder.Services.AddSingleton<LogService>();
builder.Services.AddSingleton<LauncherService>();
builder.Services.AddSingleton<RconService>();
builder.Services.AddSingleton<DevConService>();
builder.Services.AddSingleton<ConsoleTapService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConsoleTapService>());

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();

// Never leave a movement key stuck down if the server exits mid-action.
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    host.Services.GetRequiredService<InputService>().ReleaseAll();

await host.RunAsync();
