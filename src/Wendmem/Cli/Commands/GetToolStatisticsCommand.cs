using Microsoft.Extensions.DependencyInjection;
using Wendmem.ToolsMemory;

namespace Wendmem.Cli.Commands;

internal sealed class GetToolStatisticsCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var wing = ArgvHelpers.GetOption(args, "--wing");
        var tool = ArgvHelpers.GetOption(args, "--tool");

        if (wing is null || tool is null)
        {
            Console.Error.WriteLine("Usage: wendmem get-tool-statistics --wing W --tool T [--limit N]");
            return 1;
        }

        var limit = ArgvHelpers.GetIntOption(args, "--limit", 100);
        var storage = services.GetRequiredService<ToolMemoryStorage>();
        var stats = await storage.ComputeStatisticsAsync(wing, tool, limit, ct);

        Console.Out.WriteLine($"Tool:        {stats.ToolName}");
        Console.Out.WriteLine($"Total calls: {stats.TotalCalls}");
        Console.Out.WriteLine($"Successes:   {stats.Successes}");
        Console.Out.WriteLine($"Success rate:{stats.SuccessRate:P0}");
        Console.Out.WriteLine($"Avg time:    {stats.AvgTimeSeconds:F2}s");
        Console.Out.WriteLine($"Avg tokens:  {stats.AvgTokenCost}");
        return 0;
    }
}
