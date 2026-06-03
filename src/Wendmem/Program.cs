using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Wendmem;
using Wendmem.Storage;

var dbPath = Environment.GetEnvironmentVariable("WENDMEM_DB") ?? "palace.duckdb";

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "Wendmem";
});

builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.Services.TryAddEnumerable(
    ServiceDescriptor.Singleton<ConsoleFormatter, WendmemConsoleFormatter>());

builder.Services.AddWendmemCore(builder.Configuration, dbPath);

builder.Services.AddHostedService<PalaceShutdownService>();

// CLI-only eval services (not needed by HTTP transport)
builder.Services
    .AddSingleton<Wendmem.Eval.KgEvaluator>()
    .AddSingleton<Wendmem.Eval.SkillOptimizer>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .AddWendmemTools();

var host = builder.Build();

WendmemServices.ValidateStartup(host.Services);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

int dispatchResult = await Wendmem.Cli.CliDispatcher.TryDispatchAsync(args, host.Services, cts.Token);

if (dispatchResult == -1)
{
    await host.RunAsync(cts.Token);
    return 0;
}

if (dispatchResult == -2)
{
    return await Wendmem.Http.HttpServerHost.RunAsync(args, cts.Token);
}

return dispatchResult;
