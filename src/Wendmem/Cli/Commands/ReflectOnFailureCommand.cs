using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wendmem.Experiences;
using Wendmem.Serialization;

namespace Wendmem.Cli.Commands;

internal sealed class ReflectOnFailureCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var failedPath = ArgvHelpers.GetOption(args, "--failed");
        var successPath = ArgvHelpers.GetOption(args, "--success");
        var wing = ArgvHelpers.GetOption(args, "--wing");

        if (failedPath is null || wing is null)
        {
            Console.Error.WriteLine("Usage: wendmem reflect-on-failure --failed <failed.json> --wing W [--success <success.json>]");
            return 1;
        }

        var failedJson = await File.ReadAllTextAsync(failedPath, ct);
        var failed = JsonSerializer.Deserialize(failedJson, WendmemJsonContext.Default.Trajectory)
            ?? throw new ArgumentException("Invalid failed trajectory JSON");

        Trajectory? success = null;
        if (successPath is not null)
        {
            var successJson = await File.ReadAllTextAsync(successPath, ct);
            success = JsonSerializer.Deserialize(successJson, WendmemJsonContext.Default.Trajectory);
        }

        var refinement = services.GetRequiredService<ExperienceRefinement>();
        var lessons = await refinement.ReflectAsync(failed, success, wing, ct);

        foreach (var m in lessons)
            Console.Out.WriteLine($"  {m.Id}: {m.WhenToUse[..Math.Min(80, m.WhenToUse.Length)]}");

        Console.Out.WriteLine($"Reflected {lessons.Count} lessons");
        return 0;
    }
}
