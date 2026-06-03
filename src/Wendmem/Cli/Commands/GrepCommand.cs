using Microsoft.Extensions.DependencyInjection;
using Wendmem.Services;
using Wendmem.Storage;

namespace Wendmem.Cli.Commands;

internal sealed class GrepCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var query = ArgvHelpers.GetPositional(args, 0);
        if (query is null)
        {
            Console.Error.WriteLine("Usage: wendmem grep <query> [--wing W] [--room R] [--context N]");
            return 1;
        }

        var wing = ArgvHelpers.GetOption(args, "--wing");
        var room = ArgvHelpers.GetOption(args, "--room");
        var context = ArgvHelpers.GetIntOption(args, "--context", 3);

        var embedder = services.GetRequiredService<IEmbedder>();
        var storage = services.GetRequiredService<DrawerStorage>();

        var vec = await embedder.EmbedAsync(query, ct);
        var results = await storage.GrepAsync(query, vec, wing, room, context, ct);

        if (results.Count == 0)
        {
            Console.Out.WriteLine("(no results)");
            return 0;
        }

        foreach (var d in results)
        {
            Console.Out.WriteLine($"[{d.Wing}/{d.Room}] {d.Id}");
            Console.Out.WriteLine(d.Content);
            Console.Out.WriteLine();
        }

        Console.Out.WriteLine($"{results.Count} drawers (anchor + context)");
        return 0;
    }
}
