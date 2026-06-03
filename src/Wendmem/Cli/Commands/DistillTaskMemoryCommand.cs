using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wendmem.Experiences;
using Wendmem.Serialization;

namespace Wendmem.Cli.Commands;

internal sealed class DistillTaskMemoryCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var path = ArgvHelpers.GetPositional(args, 0);
        var wing = ArgvHelpers.GetOption(args, "--wing");

        if (path is null || wing is null)
        {
            Console.Error.WriteLine("Usage: wendmem distill-task-memory <trajectories.json> --wing W");
            return 1;
        }

        var json = await File.ReadAllTextAsync(path, ct);
        var traj = JsonSerializer.Deserialize(json, WendmemJsonContext.Default.ListTrajectory);
        if (traj is null || traj.Count == 0)
        {
            Console.Error.WriteLine("No trajectories found in file.");
            return 1;
        }

        var distiller = services.GetRequiredService<ExperienceDistiller>();
        var produced = await distiller.DistillAsync(traj, wing, ct);

        foreach (var m in produced)
            Console.Out.WriteLine($"  {m.Id}: {m.WhenToUse[..Math.Min(80, m.WhenToUse.Length)]}");

        Console.Out.WriteLine($"Distilled {produced.Count} memories");
        return 0;
    }
}
