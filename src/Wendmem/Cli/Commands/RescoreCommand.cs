using Microsoft.Extensions.DependencyInjection;
using Wendmem.Services;

namespace Wendmem.Cli.Commands;

internal sealed class RescoreCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var wing = ArgvHelpers.GetOption(args, "--wing");
        if (wing is null)
        {
            Console.Error.WriteLine("Usage: wendmem rescore --wing <wing> [--llm] [--limit <n>]");
            return 1;
        }

        var useLlm = ArgvHelpers.HasFlag(args, "--llm");
        var limit = ArgvHelpers.GetIntOption(args, "--limit", 0);
        int? limitValue = limit > 0 ? limit : null;

        var scorer = services.GetRequiredService<ImportanceScorer>();

        int updated;
        if (useLlm)
            updated = await scorer.RescoreWingWithLlmAsync(wing, limitValue, ct);
        else
            updated = await scorer.RescoreWingAsync(wing, limitValue, ct);

        Console.Out.WriteLine($"Rescored {updated} drawers in wing '{wing}' (mode: {(useLlm ? "llm" : "heuristic")})");
        return 0;
    }
}
