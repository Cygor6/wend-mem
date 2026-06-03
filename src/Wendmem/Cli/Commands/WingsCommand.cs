using Microsoft.Extensions.DependencyInjection;
using Wendmem.Storage;

namespace Wendmem.Cli.Commands;

internal sealed class WingsCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var storage = services.GetRequiredService<DrawerStorage>();
        var pairs = await storage.ListWingsRoomsAsync(ct);

        Console.Out.WriteLine($"{"wing",-30} {"room",-30}");
        Console.Out.WriteLine(new string('-', 60));
        foreach (var p in pairs)
            Console.Out.WriteLine($"{p.Wing,-30} {p.Room,-30}");

        Console.Out.WriteLine($"\n{pairs.Count} wing/room pairs");
        return 0;
    }
}
