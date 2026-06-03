using Microsoft.Extensions.DependencyInjection;
using Wendmem.Experiences;

namespace Wendmem.Cli.Commands;

internal sealed class PruneTaskMemoryCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var wing = ArgvHelpers.GetOption(args, "--wing");
        if (wing is null)
        {
            Console.Error.WriteLine("Usage: wendmem prune-task-memory --wing W");
            return 1;
        }

        var refinement = services.GetRequiredService<ExperienceRefinement>();
        var deleted = await refinement.PruneAsync(wing, ct);

        Console.Out.WriteLine($"Pruned {deleted} low-utility memories from wing '{wing}'");
        return 0;
    }
}
