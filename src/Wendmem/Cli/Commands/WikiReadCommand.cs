using Microsoft.Extensions.DependencyInjection;
using Wendmem.Wiki;

namespace Wendmem.Cli.Commands;

internal sealed class WikiReadCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var path = ArgvHelpers.GetPositional(args, 0);
        if (path is null)
        {
            Console.Error.WriteLine("Usage: wendmem wiki read <path>");
            return 1;
        }

        var wiki = services.GetRequiredService<WikiStorage>();
        var page = await wiki.ReadAsync(path, ct);
        if (page is null)
        {
            Console.Error.WriteLine($"Wiki page not found: {path}");
            return 1;
        }

        Console.Out.WriteLine($"# {page.Title}");
        Console.Out.WriteLine($"Wing: {page.Wing}  Path: {page.Path}");
        Console.Out.WriteLine($"Updated: {page.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        if (page.Citations.Count > 0)
            Console.Out.WriteLine($"Citations: {string.Join(", ", page.Citations)}");
        if (page.Backlinks.Count > 0)
            Console.Out.WriteLine($"Backlinks: {string.Join(", ", page.Backlinks)}");
        Console.Out.WriteLine();
        Console.Out.WriteLine(page.Content);
        return 0;
    }
}
