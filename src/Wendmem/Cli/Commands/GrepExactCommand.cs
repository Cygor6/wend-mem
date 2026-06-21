using Microsoft.Extensions.DependencyInjection;
using Wendmem.Storage;

namespace Wendmem.Cli.Commands;

internal sealed class GrepExactCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var pattern = ArgvHelpers.GetPositional(args, 0);
        if (pattern is null)
        {
            Console.Error.WriteLine("Usage: wendmem grep-exact <pattern> [--wing W] [--room R] [--limit N]");
            return 1;
        }

        var config = services.GetRequiredService<PalaceConfig>();
        var wing = ArgvHelpers.GetWing(args, config);
        var room = ArgvHelpers.GetOption(args, "--room");
        var limit = ArgvHelpers.GetIntOption(args, "--limit", 20);

        var storage = services.GetRequiredService<DrawerStorage>();

        try
        {
            _ = new System.Text.RegularExpressions.Regex(pattern);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Invalid regex: {ex.Message}");
            return 1;
        }

        var results = await storage.GrepExactAsync(pattern, wing, room, limit, ct);

        if (results.Count == 0)
        {
            Console.Out.WriteLine("(no results)");
            return 0;
        }

        foreach (var d in results)
        {
            var src = d.SourceFile ?? "(no source)";
            var snippet = d.Snippet.Replace("\r", "").Replace("\n", " ");
            Console.Out.WriteLine($"[{d.Wing}/{d.Room}] {src}:  {snippet}");
        }

        return 0;
    }
}
