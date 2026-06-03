using Microsoft.Extensions.DependencyInjection;
using Wendmem.Storage;

namespace Wendmem.Cli.Commands;

internal sealed class DeleteDrawerCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var id = ArgvHelpers.GetPositional(args, 0);
        if (id is null)
        {
            Console.Error.WriteLine("Usage: wendmem delete-drawer <id>");
            return 1;
        }

        var storage = services.GetRequiredService<DrawerStorage>();
        var deleted = await storage.DeleteDrawerAsync(id, ct);

        if (deleted)
            Console.Out.WriteLine($"Drawer {id} deleted.");
        else
            Console.Out.WriteLine($"Drawer {id} not found.");

        return deleted ? 0 : 1;
    }
}
