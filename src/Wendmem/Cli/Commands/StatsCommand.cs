using Microsoft.Extensions.DependencyInjection;
using Wendmem.Storage;

namespace Wendmem.Cli.Commands;

internal sealed class StatsCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var factory = services.GetRequiredService<DuckDbConnectionFactory>();

        long drawerCount = await ScalarLongAsync(factory, "SELECT count(*) FROM drawers", ct);
        long wikiCount = await ScalarLongAsync(factory, "SELECT count(*) FROM wiki_pages", ct);
        long entityCount = await ScalarLongAsync(factory, "SELECT count(*) FROM entities", ct);
        long activeTrip = await ScalarLongAsync(factory, "SELECT count(*) FROM triples WHERE valid_to IS NULL", ct);

        Console.Out.WriteLine($"drawers         {drawerCount,10}");
        Console.Out.WriteLine($"wiki pages      {wikiCount,10}");
        Console.Out.WriteLine($"entities        {entityCount,10}");
        Console.Out.WriteLine($"active triples  {activeTrip,10}");
        return 0;
    }

    private static async Task<long> ScalarLongAsync(DuckDbConnectionFactory factory, string sql, CancellationToken ct)
    {
        await using var ro = factory.OpenReadOnly();
        await using var cmd = ro.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(v);
    }
}
