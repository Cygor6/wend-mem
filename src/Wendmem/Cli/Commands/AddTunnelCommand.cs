using Microsoft.Extensions.DependencyInjection;
using Wendmem.Storage;

namespace Wendmem.Cli.Commands;

internal sealed class AddTunnelCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var topic = ArgvHelpers.GetOption(args, "--topic");
        var wingA = ArgvHelpers.GetOption(args, "--wing-a");
        var roomA = ArgvHelpers.GetOption(args, "--room-a");
        var wingB = ArgvHelpers.GetOption(args, "--wing-b");
        var roomB = ArgvHelpers.GetOption(args, "--room-b");

        if (topic is null || wingA is null || roomA is null || wingB is null || roomB is null)
        {
            Console.Error.WriteLine("Usage: wendmem add-tunnel --topic T --wing-a WA --room-a RA --wing-b WB --room-b RB");
            return 1;
        }

        var kg = services.GetRequiredService<KnowledgeGraph>();
        var id = await kg.AddTunnelAsync(topic, wingA, roomA, wingB, roomB, ct);

        Console.Out.WriteLine($"Tunnel created: {id}");
        return 0;
    }
}
