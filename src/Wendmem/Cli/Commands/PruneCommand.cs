using Microsoft.Extensions.DependencyInjection;
using Wendmem.Storage;

namespace Wendmem.Cli.Commands;

internal sealed class PruneCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var wing = ArgvHelpers.GetOption(args, "--wing");
        if (wing is null)
        {
            Console.Error.WriteLine("Usage: wendmem prune --wing W [--threshold 0.97]");
            return 1;
        }

        var threshold = ArgvHelpers.GetFloatOption(args, "--threshold", 0.97f);

        var storage = services.GetRequiredService<DrawerStorage>();
        var report = await storage.PruneAsync(wing, threshold, ct);

        Console.Out.WriteLine($"Pruned wing '{wing}': {report.Clusters} clusters, {report.Retired} retired, {report.Kept} kept");
        return 0;
    }
}
