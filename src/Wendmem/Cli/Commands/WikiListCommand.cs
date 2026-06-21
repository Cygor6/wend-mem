using Microsoft.Extensions.DependencyInjection;
using Wendmem.Wiki;

namespace Wendmem.Cli.Commands;

internal sealed class WikiListCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var wiki = services.GetRequiredService<WikiStorage>();
        var config = services.GetRequiredService<PalaceConfig>();
        var wing = ArgvHelpers.GetWing(args, config);
        var headers = await wiki.IndexAsync(wing, ct);

        if (headers.Count == 0)
        {
            Console.Out.WriteLine("(no wiki pages)");
            return 0;
        }

        Console.Out.WriteLine($"{"path",-40} {"wing",-20} {"title",-30} {"cites",5}");
        Console.Out.WriteLine(new string('-', 95));
        foreach (var h in headers)
            Console.Out.WriteLine($"{h.Path,-40} {h.Wing,-20} {h.Title,-30} {h.CitationCount,5}");

        Console.Out.WriteLine($"\n{headers.Count} pages");
        return 0;
    }
}
