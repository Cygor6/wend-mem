using Microsoft.Extensions.Logging.Abstractions;
using Wendmem.Services;

namespace Wendmem.Tests;

public sealed class ChunkerDebugTests
{
    [Test]
    public async Task Debug_Chunker_Splits_50K_Code()
    {
        var sb = new System.Text.StringBuilder();
        string[] templates =
        [
            "The quick brown fox jumps over the lazy dog. This is a test sentence. Another sentence here. Yet another one.",
            "Machine learning is a subset of artificial intelligence. It focuses on building systems that learn from data. Deep learning uses neural networks with many layers.",
            "Database normalization reduces data redundancy. First normal form eliminates repeating groups. Second normal form removes partial dependencies. Third normal form eliminates transitive dependencies.",
            "REST APIs use HTTP methods like GET, POST, PUT, DELETE. They are stateless and cacheable. Authentication is typically handled via tokens or API keys.",
            "Version control systems track changes to files over time. Git is the most popular distributed version control system. Branching allows parallel development streams.",
        ];

        int idx = 0;
        while (sb.Length < 50_000)
        {
            sb.AppendLine(string.Format(templates[idx % templates.Length], idx));
            sb.AppendLine();
            idx++;
        }
        var code = sb.ToString();

        var embedder = new InstrumentedEmbedder();
        var config = new PalaceConfig
        {
            TopicShiftChunkingEnabled = true,
            TopicShiftThreshold = 0.60f,
            MinChunkTokens = 80,
            TargetChunkTokens = 800,
            MaxChunkTokens = 1800,
        };

        var chunker = new Chunkers.TopicShiftChunker(embedder, config, NullLogger<Chunkers.TopicShiftChunker>.Instance);
        var chunks = await chunker.ChunkAsync(code, CancellationToken.None);

        await Assert.That(chunks.Count).IsGreaterThan(1);

        foreach (var (chunk, i) in chunks.Select((c, i) => (c, i)))
        {
            int tokens = embedder.CountTokens(chunk);
            await Assert.That(tokens).IsLessThanOrEqualTo(config.MaxChunkTokens)
                .Because($"Chunk {i} has {tokens} tokens");
        }
    }

    sealed class InstrumentedEmbedder(int dim = 512) : IEmbedder
    {
        public int EmbeddingDimension => dim;
        public int MaxSequenceTokens => 2048;
        public int BatchCallCount { get; private set; }

        public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            var v = new float[dim];
            v[0] = 1.0f;
            return ValueTask.FromResult(v);
        }

        public ValueTask<IReadOnlyList<float[]>> EmbedDocumentBatchAsync(
            IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            BatchCallCount++;
            var results = new float[texts.Count][];
            for (int i = 0; i < texts.Count; i++)
            {
                results[i] = new float[dim];
                results[i][0] = 1.0f;
            }
            return ValueTask.FromResult<IReadOnlyList<float[]>>(results);
        }

        public int CountTokens(string text) =>
            string.IsNullOrWhiteSpace(text) ? 0 : Math.Max(1, text.Length / 4);
    }
}
