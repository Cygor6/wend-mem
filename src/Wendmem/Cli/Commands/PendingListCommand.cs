using Microsoft.Extensions.DependencyInjection;
using Wendmem.Wiki;

namespace Wendmem.Cli.Commands;

internal sealed class PendingListCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var config = services.GetRequiredService<PalaceConfig>();
        var wing = ArgvHelpers.GetWing(args, config);
        var page = ArgvHelpers.GetOption(args, "--page");
        var limit = ArgvHelpers.GetIntOption(args, "--limit", 50);

        var svc = services.GetRequiredService<PendingUpdateService>();
        var updates = await svc.ListPendingAsync(wing, page, limit, ct);

        if (updates.Count == 0)
        {
            Console.Out.WriteLine("(no pending updates)");
            return 0;
        }

        Console.Out.WriteLine($"{"page_path",-40} {"drawer_id",-18} {"sim",6} {"queued_at",-20}");
        Console.Out.WriteLine(new string('-', 84));
        foreach (var u in updates)
            Console.Out.WriteLine($"{u.PagePath,-40} {u.DrawerId,-18} {u.Similarity,6:F3} {u.QueuedAt:yyyy-MM-dd HH:mm:ss}");

        Console.Out.WriteLine($"\n{updates.Count} pending update(s)");
        return 0;
    }
}
