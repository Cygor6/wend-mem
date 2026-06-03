using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wendmem.Experiences;
using Wendmem.Models;
using Wendmem.Serialization;

namespace Wendmem.Cli.Commands;

internal sealed class ExportTaskMemoryCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var wing = ArgvHelpers.GetOption(args, "--wing");
        var output = ArgvHelpers.GetOption(args, "--output");

        if (wing is null || output is null)
        {
            Console.Error.WriteLine("Usage: wendmem export-task-memory --wing W --output <path.jsonl>");
            return 1;
        }

        var storage = services.GetRequiredService<TaskMemoryStorage>();
        var mems = await storage.ListByWingAsync(wing, int.MaxValue, ct);

        await using var w = new StreamWriter(output);
        foreach (var m in mems)
        {
            var line = JsonSerializer.Serialize(
                new ExportLine(m.Id, wing, m.WhenToUse, m.Content, m.Score, m.Author),
                WendmemJsonContext.Default.ExportLine);
            await w.WriteLineAsync(line);
        }

        Console.Out.WriteLine($"Exported {mems.Count} memories to {output}");
        return 0;
    }
}
