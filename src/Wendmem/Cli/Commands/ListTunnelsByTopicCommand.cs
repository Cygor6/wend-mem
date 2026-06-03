using Microsoft.Extensions.DependencyInjection;
using Wendmem.Storage;

namespace Wendmem.Cli.Commands;

internal sealed class ListTunnelsByTopicCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var topic = ArgvHelpers.GetPositional(args, 0);
        if (topic is null)
        {
            Console.Error.WriteLine("Usage: wendmem list-tunnels-by-topic <topic>");
            return 1;
        }

        var kg = services.GetRequiredService<KnowledgeGraph>();
        var tunnels = await kg.GetTunnelsByTopicAsync(topic, ct);

        if (tunnels.Count == 0)
        {
            Console.Out.WriteLine("(no tunnels)");
            return 0;
        }

        foreach (var (wa, ra, wb, rb) in tunnels)
            Console.Out.WriteLine($"{wa}/{ra} <-> {wb}/{rb}");

        Console.Out.WriteLine($"{tunnels.Count} tunnel(s)");
        return 0;
    }
}
