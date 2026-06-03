using Microsoft.Extensions.DependencyInjection;
using Wendmem.Storage;

namespace Wendmem.Cli.Commands;

internal sealed class ListTunnelsCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var wing = ArgvHelpers.GetOption(args, "--wing");
        var room = ArgvHelpers.GetOption(args, "--room");

        if (wing is null || room is null)
        {
            Console.Error.WriteLine("Usage: wendmem list-tunnels --wing W --room R");
            return 1;
        }

        var kg = services.GetRequiredService<KnowledgeGraph>();
        var tunnels = await kg.GetTunnelsAsync(wing, room, ct);

        if (tunnels.Count == 0)
        {
            Console.Out.WriteLine("(no tunnels)");
            return 0;
        }

        foreach (var (topic, otherWing, otherRoom) in tunnels)
            Console.Out.WriteLine($"{topic} -> {otherWing}/{otherRoom}");

        Console.Out.WriteLine($"{tunnels.Count} tunnel(s)");
        return 0;
    }
}
