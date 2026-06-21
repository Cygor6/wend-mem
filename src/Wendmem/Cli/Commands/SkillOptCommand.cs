using Microsoft.Extensions.DependencyInjection;
using Wendmem.Eval;

namespace Wendmem.Cli.Commands;

internal sealed class SkillOptCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var config = services.GetRequiredService<PalaceConfig>();
        var wing = ArgvHelpers.GetWing(args, config);
        var skill = ArgvHelpers.GetOption(args, "--skill");

        if (skill is null)
        {
            Console.Error.WriteLine(
                "Usage: wendmem skill-opt --skill SKILL.md [--wing W] [--epochs N] [--budget N] [--output PATH]");
            return 1;
        }

        var epochs = ArgvHelpers.GetIntOption(args, "--epochs", 3);
        var budget = ArgvHelpers.GetIntOption(args, "--budget", 3);
        var output = ArgvHelpers.GetOption(args, "--output") ?? "SKILL.opt.md";

        var optimizer = services.GetRequiredService<SkillOptimizer>();
        await optimizer.OptimizeAsync(wing, skill, output, epochs, budget, ct);
        return 0;
    }
}
