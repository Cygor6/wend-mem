using Microsoft.Extensions.DependencyInjection;
using Wendmem.Services;

namespace Wendmem.Cli.Commands;

internal sealed class ActivityCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var wing = ArgvHelpers.GetOption(args, "--wing");
        var limit = ArgvHelpers.GetIntOption(args, "--limit", 20);

        var log = services.GetRequiredService<ActivityLog>();
        var entries = await log.RecentAsync(wing, limit, ct);

        if (entries.Count == 0)
        {
            Console.Out.WriteLine("(no activity)");
            return 0;
        }

        Console.Out.WriteLine($"{"ts",-22} {"wing",-15} {"action",-20} {"target",-30} summary");
        Console.Out.WriteLine(new string('-', 120));
        foreach (var e in entries)
        {
            var ts = e.Ts.ToString("yyyy-MM-dd HH:mm:ss");
            Console.Out.WriteLine($"{ts,-22} {e.Wing ?? "",-15} {e.Action,-20} {e.Target ?? "",-30} {e.Summary ?? ""}");
        }

        Console.Out.WriteLine($"\n{entries.Count} entries");
        return 0;
    }
}
