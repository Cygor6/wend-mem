using Microsoft.Extensions.DependencyInjection;
using Wendmem.ToolsMemory;

namespace Wendmem.Cli.Commands;

internal sealed class RecordToolCallCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var wing = ArgvHelpers.GetOption(args, "--wing");
        var tool = ArgvHelpers.GetOption(args, "--tool");
        var inputJson = ArgvHelpers.GetOption(args, "--input") ?? "{}";
        var outputJson = ArgvHelpers.GetOption(args, "--output") ?? "{}";

        if (wing is null || tool is null)
        {
            Console.Error.WriteLine("Usage: wendmem record-tool-call --wing W --tool T [--input JSON] [--output JSON]");
            return 1;
        }

        var success = !Array.Exists(args, a => a == "--fail");
        var storage = services.GetRequiredService<ToolMemoryStorage>();

        var call = new ToolCallResult(
            ToolCallIds.Compute(tool, inputJson, DateTimeOffset.UtcNow),
            wing, tool, inputJson, outputJson, success,
            success ? 1.0f : 0.0f, null, 0, 0.0, false, DateTimeOffset.UtcNow);

        await storage.RecordCallAsync(call, ct);
        Console.Out.WriteLine($"Recorded tool call: {call.Id}");
        return 0;
    }
}
