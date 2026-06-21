using Microsoft.Extensions.DependencyInjection;
using Wendmem.Storage;

namespace Wendmem.Cli.Commands;

internal sealed class KgResolveCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var config = services.GetRequiredService<PalaceConfig>();
        var wing = ArgvHelpers.GetWing(args, config);

        var resolver = services.GetRequiredService<KgResolver>();
        var result = await resolver.ResolveAsync(wing, ct);

        Console.Out.WriteLine(
            $"Entities merged: {result.EntitiesMerged}, " +
            $"Triples redirected: {result.TriplesRedirected}, " +
            $"Predicates normalized: {result.PredicatesNormalized}, " +
            $"Confidence updated: {result.ConfidenceUpdated} " +
            $"(range: {result.ConfidenceMin:F2}–{result.ConfidenceMax:F2}, decay half-life: 180 days)");
        return 0;
    }
}
