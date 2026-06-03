using Microsoft.Extensions.Logging.Abstractions;
using Wendmem.Services;

namespace Wendmem.Tests;

/// <summary>
/// Verifies that the token-aware chunker never emits chunks exceeding MaxChunkTokens.
/// </summary>
public sealed class TokenCapChunkerTests
{
    static string GenerateCodeFile(int targetBytes = 50_000)
    {
        var sb = new System.Text.StringBuilder();
        var rng = new Random(42);
        string[] templates =
        [
            "public class Service{0} {{ private readonly IRepo _repo; public Service{0}(IRepo repo) {{ _repo = repo; }} public async Task<Result> ProcessAsync(string input) {{ var data = await _repo.FindByIdAsync(input); if (data is null) return Result.NotFound; return Result.Ok(data); }} }}",
            "public interface IRepo {{ Task<Data?> FindByIdAsync(string id); Task<List<Data>> GetAllAsync(); Task UpsertAsync(Data d); }}",
            "// Section {0}: Helper methods for processing batch operations with configurable parallelism and retry logic",
            "private static readonly string[] _knownPatterns = [\"alpha\", \"beta\", \"gamma\", \"delta\", \"epsilon\", \"zeta\", \"eta\", \"theta\"];",
            "for (int i = 0; i < items.Count; i++) {{ var batch = items.Skip(i * batchSize).Take(batchSize).ToList(); results.AddRange(await ProcessBatchAsync(batch, ct)); }}",
        ];

        int idx = 0;
        while (sb.Length < targetBytes)
        {
            sb.AppendLine(string.Format(templates[idx % templates.Length], idx));
            sb.AppendLine();
            idx++;
        }

        return sb.ToString();
    }

    /// Constant embedder: returns the same unit vector for all inputs.
    /// This prevents topic-shift detection, forcing the chunker to split
    /// only on the hard token cap — which is exactly what we want to test.
    sealed class ConstantEmbedder(int dim = 512) : IEmbedder
    {
        public int EmbeddingDimension => dim;
        public int MaxSequenceTokens => 2048;

        public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => ValueTask.FromResult(UnitVector(dim));

        public int CountTokens(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;
            return Math.Max(1, text.Length / 4);
        }

        static float[] UnitVector(int dim)
        {
            var v = new float[dim];
            v[0] = 1.0f;
            return v;
        }
    }

    [Test]
    public async Task No_Chunk_Exceeds_MaxChunkTokens()
    {
        var embedder = new ConstantEmbedder();
        var config = new PalaceConfig
        {
            TopicShiftChunkingEnabled = true,
            TopicShiftThreshold = 0.60f,
            MinChunkTokens = 80,
            TargetChunkTokens = 800,
            MaxChunkTokens = 1800,
            ChunkOverlapSentences = 0,
        };

        var chunker = new Chunkers.TopicShiftChunker(embedder, config, NullLogger<Chunkers.TopicShiftChunker>.Instance);
        var code = GenerateCodeFile();

        int totalTokens = embedder.CountTokens(code);
        await Assert.That(totalTokens).IsGreaterThan(config.MaxChunkTokens);

        var chunks = await chunker.ChunkAsync(code, CancellationToken.None);
        await Assert.That(chunks.Count).IsGreaterThan(1);

        foreach (var (chunk, i) in chunks.Select((c, i) => (c, i)))
        {
            int tokens = embedder.CountTokens(chunk);
            await Assert.That(tokens)
                .IsLessThanOrEqualTo(config.MaxChunkTokens)
                .Because($"Chunk {i} has {tokens} tokens, exceeding MaxChunkTokens={config.MaxChunkTokens}");
        }
    }

    [Test]
    public async Task No_Chunk_Exceeds_MaxChunkTokens_FixedWindow()
    {
        var embedder = new ConstantEmbedder();
        var config = new PalaceConfig
        {
            TopicShiftChunkingEnabled = false,
            TopicShiftThreshold = 0.60f,
            MinChunkTokens = 80,
            TargetChunkTokens = 800,
            MaxChunkTokens = 1800,
            ChunkOverlapSentences = 0,
        };

        var chunker = new Chunkers.TopicShiftChunker(embedder, config, NullLogger<Chunkers.TopicShiftChunker>.Instance);
        var code = GenerateCodeFile();

        var chunks = await chunker.ChunkAsync(code, CancellationToken.None);
        await Assert.That(chunks.Count).IsGreaterThan(1);

        foreach (var (chunk, i) in chunks.Select((c, i) => (c, i)))
        {
            int tokens = embedder.CountTokens(chunk);
            await Assert.That(tokens)
                .IsLessThanOrEqualTo(config.MaxChunkTokens)
                .Because($"Chunk {i} has {tokens} tokens, exceeding MaxChunkTokens={config.MaxChunkTokens}");
        }
    }

    [Test]
    public async Task Short_Text_Returns_Single_Chunk()
    {
        var embedder = new ConstantEmbedder();
        var config = new PalaceConfig
        {
            TopicShiftChunkingEnabled = true,
            TopicShiftThreshold = 0.60f,
            MinChunkTokens = 80,
            TargetChunkTokens = 800,
            MaxChunkTokens = 1800,
        };

        var chunker = new Chunkers.TopicShiftChunker(embedder, config, NullLogger<Chunkers.TopicShiftChunker>.Instance);
        var shortText = "Hello world. This is a short test.";

        var chunks = await chunker.ChunkAsync(shortText, CancellationToken.None);

        await Assert.That(chunks.Count).IsEqualTo(1);
        await Assert.That(chunks[0]).IsEqualTo(shortText);
    }

    [Test]
    public async Task Overlap_Does_Not_Exceed_MaxChunkTokens()
    {
        var embedder = new ConstantEmbedder();
        var config = new PalaceConfig
        {
            TopicShiftChunkingEnabled = true,
            TopicShiftThreshold = 0.60f,
            MinChunkTokens = 80,
            TargetChunkTokens = 800,
            MaxChunkTokens = 1800,
            ChunkOverlapSentences = 2,
        };

        var chunker = new Chunkers.TopicShiftChunker(embedder, config, NullLogger<Chunkers.TopicShiftChunker>.Instance);
        var code = GenerateCodeFile();

        var chunks = await chunker.ChunkAsync(code, CancellationToken.None);
        await Assert.That(chunks.Count).IsGreaterThan(1);

        foreach (var (chunk, i) in chunks.Select((c, i) => (c, i)))
        {
            int tokens = embedder.CountTokens(chunk);
            await Assert.That(tokens)
                .IsLessThanOrEqualTo(config.MaxChunkTokens)
                .Because($"Chunk {i} with overlap has {tokens} tokens, exceeding MaxChunkTokens={config.MaxChunkTokens}");
        }
    }
}
