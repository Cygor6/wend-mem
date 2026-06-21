using Microsoft.Extensions.DependencyInjection;
using Wendmem.Storage;

namespace Wendmem.Cli.Commands;

internal sealed class SaveSessionCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var id = ArgvHelpers.GetOption(args, "--id");
        var config = services.GetRequiredService<PalaceConfig>();
        var wing = ArgvHelpers.GetWing(args, config);
        var content = ArgvHelpers.GetPositional(args, 0);

        if (content is null)
        {
            Console.Error.WriteLine("Usage: wendmem save-session <content> [--wing W] [--room R] [--id ID]");
            return 1;
        }

        var room = ArgvHelpers.GetOption(args, "--room") ?? "session";
        if (id is null)
            id = Guid.NewGuid().ToString("N")[..16];

        var storage = services.GetRequiredService<DrawerStorage>();
        await storage.UpsertDrawerAsync(id, wing, room, content, source: null, sourceMtime: null, drawerType: "synthesis", ct);

        Console.Out.WriteLine($"Session state saved: {id}");
        return 0;
    }
}
