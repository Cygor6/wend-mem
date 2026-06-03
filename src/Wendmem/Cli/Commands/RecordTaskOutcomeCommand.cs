using Microsoft.Extensions.DependencyInjection;
using Wendmem.Experiences;

namespace Wendmem.Cli.Commands;

internal sealed class RecordTaskOutcomeCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var success = Array.Exists(args, a => a == "--success");
        var ids = args.Where(a => !a.StartsWith("--") && a != "record-outcome").ToArray();

        if (ids.Length == 0)
        {
            Console.Error.WriteLine("Usage: wendmem record-outcome [--success] <id1> [id2 ...]");
            return 1;
        }

        var storage = services.GetRequiredService<TaskMemoryStorage>();
        if (success)
            await storage.RecordUtilityAsync(ids, ct);

        Console.Out.WriteLine($"Recorded {(success ? "success" : "failure")} for {ids.Length} memories");
        return 0;
    }
}
