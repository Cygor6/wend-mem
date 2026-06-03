using Microsoft.Extensions.DependencyInjection;
using Wendmem.Storage;

namespace Wendmem.Cli.Commands;

sealed class RoomPatternsCommand
{
    public async Task<int> RunAsync(string[] args, IServiceProvider services, CancellationToken ct)
    {
        var dbFactory = services.GetRequiredService<DuckDbConnectionFactory>();

        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT extension,
                   COUNT(*)            AS file_count,
                   MODE(directory)     AS common_directory,
                   MODE(assigned_room) AS guessed_room
            FROM room_classification_log
            WHERE was_fallback = TRUE
            GROUP BY extension
            ORDER BY file_count DESC
            LIMIT 15
            """;

        Console.WriteLine("Fallback extensions (by frequency) — add to MinerConfig.ExtensionToRoom:");
        using var reader = await cmd.ExecuteReaderAsync(ct);
        bool any = false;
        while (await reader.ReadAsync(ct))
        {
            any = true;
            var ext = reader.GetString(0);
            var count = reader.GetInt64(1);
            var dir = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var room = reader.IsDBNull(3) ? "" : reader.GetString(3);
            Console.WriteLine($"{ext,-6} {count,3} files   common dir: {dir,-20} guessed: {room}");
        }

        if (!any)
            Console.WriteLine("  (no fallback classifications logged yet)");

        return 0;
    }
}
