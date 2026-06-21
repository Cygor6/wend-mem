using Microsoft.Extensions.DependencyInjection;
using Wendmem.Services;

namespace Wendmem.Cli.Commands;

internal sealed class SweepCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var path = ArgvHelpers.GetPositional(args, 0);
        var config = services.GetRequiredService<PalaceConfig>();
        var wing = ArgvHelpers.GetWing(args, config);

        if (path is null)
        {
            Console.Error.WriteLine("Usage: wendmem sweep <path> [--wing W] [--fix]");
            return 1;
        }

        var fix = Array.Exists(args, a => a == "--fix");
        var sweeper = services.GetRequiredService<Sweeper>();

        var report = await sweeper.SweepAsync(path, wing, fix, ct);

        Console.Out.WriteLine($"Missing: {report.MissingCount}");
        Console.Out.WriteLine($"Stale:   {report.StaleCount}");
        Console.Out.WriteLine($"OK:      {report.OkCount}");

        if (fix)
            Console.Out.WriteLine($"Fixed:   {report.Fixed}");

        if (report.MissingFiles.Count > 0)
        {
            Console.Out.WriteLine("\nMissing files:");
            foreach (var f in report.MissingFiles)
                Console.Out.WriteLine($"  {f}");
        }
        if (report.StaleFiles.Count > 0)
        {
            Console.Out.WriteLine("\nStale files:");
            foreach (var f in report.StaleFiles)
                Console.Out.WriteLine($"  {f}");
        }

        return 0;
    }
}
