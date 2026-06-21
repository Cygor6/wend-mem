using Microsoft.Extensions.DependencyInjection;
using Wendmem.ToolsMemory;

namespace Wendmem.Cli.Commands;

internal sealed class SummarizeToolCallsCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var config = services.GetRequiredService<PalaceConfig>();
        var wing = ArgvHelpers.GetWing(args, config);
        var tool = ArgvHelpers.GetOption(args, "--tool");

        if (tool is null)
        {
            Console.Error.WriteLine("Usage: wendmem summarize-tool-calls --tool T [--wing W] [--limit N]");
            return 1;
        }

        var limit = ArgvHelpers.GetIntOption(args, "--limit", 20);
        var distiller = services.GetRequiredService<ToolMemoryDistiller>();

        var result = await distiller.SummarizeAsync(wing, tool, limit, ct);

        if (result is null)
        {
            Console.Out.WriteLine("(not enough calls to summarize — need at least 3)");
            return 0;
        }

        Console.Out.WriteLine($"Tool:    {result.ToolName}");
        Console.Out.WriteLine($"Score:   {result.Score:F2}");
        Console.Out.WriteLine($"Author:  {result.Author}");
        Console.Out.WriteLine();
        Console.Out.WriteLine(result.Guidelines);
        return 0;
    }
}
