using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Console;
using Wendmem.Storage;

namespace Wendmem.Http;

internal static class HttpServerHost
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder(args);

        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "Wendmem";
        });

        builder.Logging.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ConsoleFormatter, WendmemConsoleFormatter>());

        var dbPath = builder.Configuration["Palace:DbPath"]
            ?? Environment.GetEnvironmentVariable("WENDMEM_DB")
            ?? "palace.duckdb";

        builder.Services.AddWendmemCore(builder.Configuration, dbPath);

        builder.Services.AddHostedService<PalaceShutdownService>();

        builder.Services
            .AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .AddWendmemTools();

        var app = builder.Build();

        WendmemServices.ValidateStartup(app.Services);

        app.MapGet("/health", () => "ok");
        app.MapMcp("/mcp");

        var port = app.Configuration["Palace:HttpPort"] ?? "5133";
        app.Urls.Add($"http://localhost:{port}");
        Console.Error.WriteLine($"wendmem HTTP MCP server listening on http://localhost:{port}");

        await ((Microsoft.Extensions.Hosting.IHost)app).RunAsync(ct);
        return 0;
    }
}
