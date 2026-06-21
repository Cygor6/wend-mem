using Microsoft.Extensions.DependencyInjection;
using Wendmem.Services;

namespace Wendmem.Cli.Commands;

internal sealed class MineConversationCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var path = ArgvHelpers.GetPositional(args, 0);
        if (path is null)
        {
            Console.Error.WriteLine("Usage: wendmem mine-conversation <path> --wing W");
            return 1;
        }

        var config = services.GetRequiredService<PalaceConfig>();
        var wing = ArgvHelpers.GetWing(args, config);

        var miner = services.GetRequiredService<ConversationMiner>();

        int added, skipped;
        if (File.Exists(path))
        {
            (added, skipped) = await miner.MineFileAsync(path, wing, ct);
        }
        else
        {
            (added, skipped) = await miner.MineTextAsync(path, wing, null, ct);
        }

        Console.Out.WriteLine($"Drawers added:   {added}");
        Console.Out.WriteLine($"Drawers skipped: {skipped}");
        return 0;
    }
}
