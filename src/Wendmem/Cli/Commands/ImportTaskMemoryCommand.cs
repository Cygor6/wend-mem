using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wendmem.Experiences;
using Wendmem.Services;

namespace Wendmem.Cli.Commands;

internal sealed class ImportTaskMemoryCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var input = ArgvHelpers.GetPositional(args, 0);
        var wing = ArgvHelpers.GetOption(args, "--wing");

        if (input is null || wing is null)
        {
            Console.Error.WriteLine("Usage: wendmem import-task-memory <path.jsonl> --wing W");
            return 1;
        }

        var storage = services.GetRequiredService<TaskMemoryStorage>();
        var embedder = services.GetRequiredService<IEmbedder>();
        var imported = 0;

        await foreach (var line in File.ReadLinesAsync(input, ct))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            var when = r.GetProperty("when_to_use").GetString()!;
            var content = r.GetProperty("content").GetString()!;
            var emb = await embedder.EmbedDocumentAsync(when, ct);
            var now = DateTimeOffset.UtcNow;
            await storage.AddAsync(new(
                TaskMemoryIds.Compute(when, content), wing, when, content,
                r.TryGetProperty("score", out var s) ? s.GetSingle() : 0.8f,
                r.TryGetProperty("author", out var a) ? a.GetString() ?? "imported" : "imported",
                [], [], TaskMemorySource.Success, 0, 0, emb, now, now, null), ct);
            imported++;
        }

        Console.Out.WriteLine($"Imported {imported} memories into wing '{wing}'");
        return 0;
    }
}
