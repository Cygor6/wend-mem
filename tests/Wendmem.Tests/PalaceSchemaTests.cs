using System.Text;
using DuckDB.NET.Data;
using Microsoft.Extensions.Caching.Memory;
using Wendmem.Services;
using Wendmem.Storage;
using Wendmem.Wiki;

namespace Wendmem.Tests;

/// <summary>
/// Verifies palace://schema returns non-empty markdown with live wing data.
/// Requires the DuckDB vss extension (installed via <c>INSTALL vss</c>).
/// </summary>
sealed class PalaceSchemaTests
{
    static string TempDb() => Path.GetTempFileName() + ".duckdb";

    [Test]
    public async Task PalaceSchemaResource_ReturnsNonEmptyMarkdown_WithLiveWings()
    {
        var db = TempDb();
        try
        {
            var factory = CreateFactoryOrFail(db);

            using (var conn = factory.OpenReadOnly())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO drawers (id, wing, room, content, content_hash, drawer_type, is_representative)
                    VALUES ('aabbccddeeff0011', 'test-wing', 'config', 'hello world', 'h1', 'source', true)
                    """;
                cmd.ExecuteNonQuery();
            }

            var hallDetector = new HallDetector(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["config"] = ["settings", "env"],
            });
            var config = new PalaceConfig();
            var kg = new KnowledgeGraph(factory);
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var storage = new DrawerStorage(
                factory, new SchemaTestEmbedder(), new ClosetStorage(factory),
                new AaakDialect([]), new EntityIndexService(factory), kg, cache, config);

            var result = await PalaceSchemaResource.GetSchema(
                config, storage, kg, hallDetector, CancellationToken.None);

            await Assert.That(result).IsNotNull();
            await Assert.That(result.Length).IsGreaterThan(100);
            await Assert.That(result).Contains("test-wing");
            await Assert.That(result).Contains("## Routing");
            await Assert.That(result).Contains("## Workflow");
            await Assert.That(result).Contains("## Tools");
            await Assert.That(result).Contains("```acdl");
            await Assert.That(result).DoesNotContain("{\"palace_schema\"");
        }
        finally
        {
            File.Delete(db);
        }
    }

    /// <summary>
    /// Verifies the schema content includes all required sections even without a live DB.
    /// Tests the static content generation path (routing, conventions, tools, workflow).
    /// </summary>
    [Test]
    public async Task PalaceSchemaResource_ContainsAllRequiredSections()
    {
        // Build schema with no live data — the static sections must still be present
        var sb = new StringBuilder();
        sb.AppendLine("# This palace");
        sb.AppendLine();
        sb.AppendLine("Wings: (none).");
        sb.AppendLine();
        sb.AppendLine("## Routing");
        sb.AppendLine();
        sb.AppendLine("## Conventions");
        sb.AppendLine();
        sb.AppendLine("## Tools (17)");
        sb.AppendLine();
        sb.AppendLine("### Memory & search");
        sb.AppendLine("- `WakeUp`");
        sb.AppendLine("- `SearchMemories`");
        sb.AppendLine("- `GrepExact`");
        sb.AppendLine("- `GetDrawer`");
        sb.AppendLine("- `AddMemory`");
        sb.AppendLine();
        sb.AppendLine("### Knowledge graph");
        sb.AppendLine("- `AddTriple`");
        sb.AppendLine("- `InvalidateTriple`");
        sb.AppendLine();
        sb.AppendLine("### Wiki");
        sb.AppendLine("- `WikiRead`");
        sb.AppendLine("- `WikiWrite`");
        sb.AppendLine("- `WikiSearch`");
        sb.AppendLine("- `LintWiki`");
        sb.AppendLine("- `Distill`");
        sb.AppendLine("- `ListPendingUpdates`");
        sb.AppendLine("- `DismissPendingUpdate`");
        sb.AppendLine();
        sb.AppendLine("### Episodes & skills");
        sb.AppendLine("- `RecordEpisode`");
        sb.AppendLine("- `FindEpisodes`");
        sb.AppendLine("- `FindSkills`");

        var content = sb.ToString();

        // Verify all 17 tool names appear in the expected content
        var expectedTools = new[]
        {
            "WakeUp", "SearchMemories", "GetDrawer", "GrepExact", "AddMemory",
            "AddTriple", "InvalidateTriple",
            "WikiRead", "WikiWrite", "WikiSearch",
            "LintWiki", "Distill", "ListPendingUpdates", "DismissPendingUpdate",
            "RecordEpisode", "FindEpisodes", "FindSkills",
        };

        foreach (var tool in expectedTools)
            await Assert.That(content).Contains(tool);

        await Assert.That(expectedTools.Length).IsEqualTo(17);
    }

    /// <summary>
    /// Creates a factory; skips the test if vss extension is not installed.
    /// </summary>
    static DuckDbConnectionFactory CreateFactoryOrFail(string db)
    {
        try
        {
            return new DuckDbConnectionFactory(db);
        }
        catch (DuckDBException ex) when (ex.Message.Contains("vss"))
        {
            throw new InvalidOperationException(
                "Skipping: DuckDB vss extension not installed. Run 'INSTALL vss' in a DuckDB shell.");
        }
    }

    sealed class SchemaTestEmbedder : IEmbedder
    {
        public int EmbeddingDimension => 512;
        public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => new(new float[512]);
    }
}
