using Microsoft.Extensions.DependencyInjection;
using Wendmem.Experiences;

namespace Wendmem.Cli.Commands;

internal sealed class SearchTaskMemoryCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var query = ArgvHelpers.GetPositional(args, 0);
        var wing = ArgvHelpers.GetOption(args, "--wing");

        if (query is null || wing is null)
        {
            Console.Error.WriteLine("Usage: wendmem search-task-memory <query> --wing W [--limit N]");
            return 1;
        }

        var limit = ArgvHelpers.GetIntOption(args, "--limit", 5);
        var retriever = services.GetRequiredService<ExperienceRetriever>();

        var result = await retriever.SearchAsync(query, wing, limit, ct);

        if (result.Memories.Count == 0)
        {
            Console.Out.WriteLine("(no results)");
            return 0;
        }

        foreach (var r in result.Memories)
        {
            var m = r.Memory;
            Console.Out.WriteLine($"[{m.Id}] {m.Source} (score: {r.SimilarityScore:F4})");
            Console.Out.WriteLine($"  When:  {m.WhenToUse}");
            Console.Out.WriteLine($"  What:  {(m.Content.Length > 200 ? m.Content[..200] + "..." : m.Content)}");
            Console.Out.WriteLine();
        }

        Console.Out.WriteLine($"{result.Memories.Count} memories");
        return 0;
    }
}
