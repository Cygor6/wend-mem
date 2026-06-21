using Microsoft.Extensions.DependencyInjection;
using Wendmem.Experiences;

namespace Wendmem.Cli.Commands;

internal sealed class PruneTaskMemoryCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var config = services.GetRequiredService<PalaceConfig>();
        var wing = ArgvHelpers.GetWing(args, config);

        var refinement = services.GetRequiredService<ExperienceRefinement>();
        var deleted = await refinement.PruneAsync(wing, ct);

        Console.Out.WriteLine($"Pruned {deleted} low-utility memories from wing '{wing}'");
        return 0;
    }
}
