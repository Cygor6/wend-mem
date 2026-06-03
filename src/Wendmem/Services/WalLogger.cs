using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Wendmem.Models;
using Wendmem.Serialization;

namespace Wendmem.Services;

sealed class WalLogger
{
    readonly string _walFile;
    readonly Lock _lock = new();

    public WalLogger(IHostEnvironment env)
    {
        var walDir = Path.Combine(env.ContentRootPath, "wal");
        _walFile = Path.Combine(walDir, "write_log.jsonl");
        Directory.CreateDirectory(walDir);
    }

    public void Log(string operation, Dictionary<string, string?> parameters)
    {
        try
        {
            var entry = new WalEntry(
                Timestamp: DateTimeOffset.UtcNow.ToString("O"),
                Operation: operation,
                Params: parameters
            );

            var line = JsonSerializer.Serialize(entry, WendmemJsonContext.Default.WalEntry);

            lock (_lock)
            {
                File.AppendAllText(_walFile, line + "\n");
            }
        }
        catch
        {
        }
    }
}
