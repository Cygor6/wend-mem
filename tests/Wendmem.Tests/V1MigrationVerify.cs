using DuckDB.NET.Data;
using Wendmem.Storage;

namespace Wendmem.Tests;

file static class Db
{
    public static string Temp() => Path.GetTempFileName() + ".duckdb";
}

sealed class V1MigrationVerify
{
    [Test]
    public async Task FreshDb_InitializesSuccessfully()
    {
        var db = Db.Temp();
        try
        {
            using var conn = new DuckDBConnection($"DataSource={db}");
            conn.Open();
            DbBootstrap.Initialize(conn);

            // Verify the columns exist by querying information_schema
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT count(*) FROM information_schema.columns
                WHERE table_name = 'drawers'
                  AND column_name IN ('access_count', 'last_accessed_at', 'cluster_id',
                                      'cluster_d_eff', 'drawer_type')
                """;
            var count = (long)cmd.ExecuteScalar()!;
            await Assert.That(count).IsEqualTo(5L);
        }
        finally { File.Delete(db); }
    }

    [Test]
    public async Task DoubleInit_IsIdempotent()
    {
        var db = Db.Temp();
        try
        {
            // First initialization
            using var conn1 = new DuckDBConnection($"DataSource={db}");
            conn1.Open();
            DbBootstrap.Initialize(conn1);

            // Insert a row to prove data survives
            using var insertCmd = conn1.CreateCommand();
            insertCmd.CommandText = """
                INSERT INTO drawers (id, wing, room, content, content_hash)
                VALUES ('test-id', 'w', 'r', 'hello', 'hash1')
                """;
            insertCmd.ExecuteNonQuery();

            // Second initialization on same connection
            DbBootstrap.Initialize(conn1);

            // Data still present
            using var verifyCmd = conn1.CreateCommand();
            verifyCmd.CommandText = "SELECT count(*) FROM drawers WHERE id = 'test-id'";
            var count = (long)verifyCmd.ExecuteScalar()!;
            await Assert.That(count).IsEqualTo(1L);

            // Third initialization on a fresh connection
            using var conn2 = new DuckDBConnection($"DataSource={db}");
            conn2.Open();
            DbBootstrap.Initialize(conn2);

            using var verifyCmd2 = conn2.CreateCommand();
            verifyCmd2.CommandText = "SELECT count(*) FROM drawers WHERE id = 'test-id'";
            var count2 = (long)verifyCmd2.ExecuteScalar()!;
            await Assert.That(count2).IsEqualTo(1L);
        }
        finally { File.Delete(db); }
    }
}
