using Microsoft.Extensions.DependencyInjection;
using Wendmem.Services;

namespace Wendmem.Cli.Commands;

internal sealed class MineCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var path = ArgvHelpers.GetPositional(args, 0);
        if (path is null)
        {
            Console.Error.WriteLine("Usage: wendmem mine <path> --wing W [--room R]");
            return 1;
        }

        var wing = ArgvHelpers.GetOption(args, "--wing");
        if (wing is null)
        {
            Console.Error.WriteLine("Error: --wing is required.");
            return 1;
        }

        var room = ArgvHelpers.GetOption(args, "--room");
        var miner = services.GetRequiredService<FileMiner>();

        int files, drawers, skipped;
        if (Directory.Exists(path))
        {
            var r = await miner.MineDirectoryAsync(path, wing, room, ct);
            files = r.FilesProcessed;
            drawers = r.DrawersAdded;
            skipped = r.FilesSkipped;
        }
        else
        {
            var r = await miner.MineFileAsync(path, wing, room, ct);
            files = r.FilesProcessed;
            drawers = r.DrawersAdded;
            skipped = r.FilesSkipped;
        }

        Console.Out.WriteLine($"Files processed: {files}");
        Console.Out.WriteLine($"Drawers added:   {drawers}");
        Console.Out.WriteLine($"Files skipped:   {skipped}");
        return 0;
    }
}
