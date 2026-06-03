using Microsoft.Extensions.DependencyInjection;
using Wendmem.Wiki;

namespace Wendmem.Cli.Commands;

internal sealed class PendingDismissCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var page = ArgvHelpers.GetOption(args, "--page");
        var drawer = ArgvHelpers.GetOption(args, "--drawer");
        if (page is null || drawer is null)
        {
            Console.Error.WriteLine("Usage: wendmem pending dismiss --page <path> --drawer <id>");
            return 1;
        }

        var svc = services.GetRequiredService<PendingUpdateService>();
        await svc.ResolveAsync(page, drawer, "dismissed", ct);
        Console.Out.WriteLine($"Dismissed pending update: {page} / {drawer}");
        return 0;
    }
}
