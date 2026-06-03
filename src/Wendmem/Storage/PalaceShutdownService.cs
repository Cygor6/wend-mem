using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wendmem.Storage;

sealed class PalaceShutdownService(
    DuckDbConnectionFactory dbFactory,
    IHostApplicationLifetime lifetime,
    ILogger<PalaceShutdownService> log) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        lifetime.ApplicationStopping.Register(Checkpoint);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private void Checkpoint()
    {
        try
        {
            log.LogInformation("Checkpointing DuckDB before shutdown...");
            dbFactory.ExecuteWriteAsync(async db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "CHECKPOINT";
                await cmd.ExecuteNonQueryAsync();
            }).GetAwaiter().GetResult();
            log.LogInformation("Checkpoint complete.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Checkpoint failed — WAL will be replayed on next start.");
        }
        finally
        {
            dbFactory.Dispose();
        }
    }
}
