using Microsoft.Extensions.DependencyInjection;
using Wendmem.Services;

namespace Wendmem.Cli.Commands;

internal sealed class WakeUpFullCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var wing = ArgvHelpers.GetOption(args, "--wing");
        var seedQuery = ArgvHelpers.GetOption(args, "--seed");

        var searcher = services.GetRequiredService<PalaceSearcher>();
        var result = await searcher.WakeUpFullAsync(wing, seedQuery, ct);

        Console.Out.WriteLine(result);
        return 0;
    }
}
