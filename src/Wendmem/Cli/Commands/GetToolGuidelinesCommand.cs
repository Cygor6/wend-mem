using Microsoft.Extensions.DependencyInjection;
using Wendmem.ToolsMemory;

namespace Wendmem.Cli.Commands;

internal sealed class GetToolGuidelinesCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var config = services.GetRequiredService<PalaceConfig>();
        var wing = ArgvHelpers.GetWing(args, config);
        var tool = ArgvHelpers.GetOption(args, "--tool");

        if (tool is null)
        {
            Console.Error.WriteLine("Usage: wendmem get-tool-guidelines --tool T [--wing W]");
            return 1;
        }

        var storage = services.GetRequiredService<ToolMemoryStorage>();
        var guidelines = await storage.GetGuidelinesAsync(wing, tool, ct);

        if (guidelines is null)
        {
            Console.Out.WriteLine("(no guidelines found)");
            return 0;
        }

        Console.Out.WriteLine($"Tool:    {guidelines.ToolName}");
        Console.Out.WriteLine($"Score:   {guidelines.Score:F2}");
        Console.Out.WriteLine($"Author:  {guidelines.Author}");
        Console.Out.WriteLine($"Updated: {guidelines.TimeModified:yyyy-MM-dd}");
        Console.Out.WriteLine();
        Console.Out.WriteLine(guidelines.Guidelines);
        return 0;
    }
}
