using Microsoft.Extensions.DependencyInjection;
using Wendmem.ToolsMemory;

namespace Wendmem.Cli.Commands;

internal sealed class ListToolCallsCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var config = services.GetRequiredService<PalaceConfig>();
        var wing = ArgvHelpers.GetWing(args, config);
        var tool = ArgvHelpers.GetOption(args, "--tool");

        if (tool is null)
        {
            Console.Error.WriteLine("Usage: wendmem list-tool-calls --tool T [--wing W] [--limit N]");
            return 1;
        }

        var limit = ArgvHelpers.GetIntOption(args, "--limit", 20);
        var storage = services.GetRequiredService<ToolMemoryStorage>();
        var calls = await storage.GetRecentCallsAsync(wing, tool, limit, ct);

        if (calls.Count == 0)
        {
            Console.Out.WriteLine("(no calls)");
            return 0;
        }

        foreach (var c in calls)
        {
            Console.Out.WriteLine($"[{c.Id}] {(c.Success ? "OK" : "FAIL")} {c.CalledAt:yyyy-MM-dd HH:mm:ss}");
            Console.Out.WriteLine($"  Input:  {Truncate(c.InputJson, 120)}");
            Console.Out.WriteLine($"  Output: {Truncate(c.OutputJson, 120)}");
            if (c.Summary is not null)
                Console.Out.WriteLine($"  Note:   {c.Summary}");
            Console.Out.WriteLine();
        }

        Console.Out.WriteLine($"{calls.Count} call(s)");
        return 0;
    }

    static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "...";
}
