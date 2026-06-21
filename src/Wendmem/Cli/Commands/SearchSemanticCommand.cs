using Microsoft.Extensions.DependencyInjection;
using Wendmem.Services;
using Wendmem.Storage;

namespace Wendmem.Cli.Commands;

internal sealed class SearchSemanticCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var query = ArgvHelpers.GetPositional(args, 0);
        if (query is null)
        {
            Console.Error.WriteLine("Usage: wendmem search-semantic <query> [--wing W] [--limit N]");
            return 1;
        }

        var config = services.GetRequiredService<PalaceConfig>();
        var wing = ArgvHelpers.GetWing(args, config);
        var limit = ArgvHelpers.GetIntOption(args, "--limit", 10);

        var embedder = services.GetRequiredService<IEmbedder>();
        var storage = services.GetRequiredService<DrawerStorage>();

        var vec = await embedder.EmbedAsync(query, ct);
        var results = await storage.CosinSearchAsync(vec, wing, limit, ct);

        if (results.Count == 0)
        {
            Console.Out.WriteLine("(no results)");
            return 0;
        }

        foreach (var r in results)
        {
            Console.Out.WriteLine($"[{r.Drawer.Wing}/{r.Drawer.Room}] {r.Drawer.Id} (score: {r.Score:F4})");
            var preview = r.Drawer.Content.Length > 200
                ? r.Drawer.Content[..200] + "..."
                : r.Drawer.Content;
            Console.Out.WriteLine($"  {preview}");
            Console.Out.WriteLine();
        }

        Console.Out.WriteLine($"{results.Count} results");
        return 0;
    }
}
