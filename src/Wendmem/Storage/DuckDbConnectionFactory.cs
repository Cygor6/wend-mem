using DuckDB.NET.Data;

namespace Wendmem.Storage;

/// <summary>
/// Provides DuckDB connections: a single read-write connection for writes,
/// and read-only connections that can be opened concurrently by multiple agents.
/// DuckDB allows multiple concurrent readers but only one writer at a time.
/// </summary>
public sealed class DuckDbConnectionFactory : IDisposable, IAsyncDisposable
{
    readonly string _dbPath;
    readonly DuckDBConnection _writeConnection;
    readonly SemaphoreSlim _writeLock = new(1, 1);

    public DuckDbConnectionFactory(string dbPath)
    {
        _dbPath = dbPath;
        _writeConnection = new DuckDBConnection($"Data Source={dbPath}");
        _writeConnection.Open();

        // Resolve temp dir relative to the database file
        var dbDir = System.IO.Path.GetDirectoryName(dbPath)!;
        var tempDir = System.IO.Path.Combine(dbDir, "palace-temp");
        System.IO.Directory.CreateDirectory(tempDir);

        using var cmd = _writeConnection.CreateCommand();
        cmd.CommandText = $"""
            LOAD fts;
            LOAD vss;
            SET hnsw_enable_experimental_persistence = true;
            SET temp_directory = '{tempDir.Replace("\\", "\\\\")}';
            SET memory_limit = '2GB';
            """;
        cmd.ExecuteNonQuery();
        DbBootstrap.Initialize(_writeConnection);
    }

    /// <summary>
    /// Serializes all write operations onto the single write connection.
    /// Use for INSERT / UPDATE / DELETE / DDL.
    /// </summary>
    public async Task<T> ExecuteWriteAsync<T>(
        Func<DuckDBConnection, Task<T>> action,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        { return await action(_writeConnection); }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Serializes all write operations onto the single write connection.
    /// Use for INSERT / UPDATE / DELETE / DDL.
    /// </summary>
    public async Task ExecuteWriteAsync(
        Func<DuckDBConnection, Task> action,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        { await action(_writeConnection); }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Opens a new read-only connection per call. Caller must dispose (use <c>await using</c>).
    /// Multiple read-only connections can run concurrently - no lock needed.
    /// </summary>
    public DuckDBConnection OpenReadOnly()
    {
        var conn = new DuckDBConnection(
            $"Data Source={_dbPath};ACCESS_MODE=READ_ONLY");
        conn.Open();
        return conn;
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        _writeConnection.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        await _writeConnection.DisposeAsync();
    }
}
