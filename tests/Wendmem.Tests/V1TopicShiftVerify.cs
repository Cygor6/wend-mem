using Microsoft.Extensions.Logging.Abstractions;
using Wendmem.Services;

namespace Wendmem.Tests;

/// <summary>
/// Verify that topic-shift chunking produces ~3 chunks for a 5KB text
/// with three clearly distinct topics (not ~6 from fixed-window).
/// </summary>
public class V1TopicShiftVerify
{
    /// <summary>
    /// Embedder that assigns topic vectors based on which third of the
    /// batch the sentence falls in, simulating three distinct semantic regions.
    /// </summary>
    sealed class TopicAwareEmbedder : IEmbedder
    {
        public int EmbeddingDimension => 4;

        static readonly float[][] Centers = [
            [1f, 0f, 0f, 0f],
            [0f, 1f, 0f, 0f],
            [0f, 0f, 1f, 0f],
        ];

        public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => new(Centers[0]);

        public ValueTask<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            int count = texts.Count;
            var results = new float[count][];
            for (int i = 0; i < count; i++)
            {
                // Assign topic based on position: first third, middle third, last third
                int topicIdx = count <= 1 ? 0 : (int)((float)i / count * 3) switch
                {
                    0 => 0,
                    1 => 1,
                    _ => 2
                };
                var c = Centers[topicIdx];
                results[i] = [c[0], c[1], c[2], c[3]];
            }
            return new ValueTask<IReadOnlyList<float[]>>(results);
        }
    }

    [Test]
    public async Task ShortInput_ReturnsSingleChunk_NoEmbedding()
    {
        // 100-char input — well under MinChunkChars * 1.5 (300)
        var text = new string('x', 50) + ". " + new string('y', 45) + ".";

        // Use an embedder that throws if called, proving no embedding happens
        var embedder = new ThrowingEmbedder();
        var config = new PalaceConfig
        {
            TopicShiftChunkingEnabled = true,
        };

        var chunker = new Chunkers.TopicShiftChunker(
            embedder, config,
            NullLogger<Chunkers.TopicShiftChunker>.Instance);

        var chunks = await chunker.ChunkAsync(text, CancellationToken.None);

        await Assert.That(chunks.Count).IsEqualTo(1);
        await Assert.That(chunks[0]).IsEqualTo(text);
    }

    [Test]
    public async Task ThreeTopics_ProducesThreeChunks()
    {
        // ~5KB: three ~1700-char blocks separated by double newline
        var topic1 = string.Join(" ", Enumerable.Range(0, 20).Select(_ =>
            "Machine learning models require careful feature engineering and hyperparameter tuning to achieve optimal performance."));
        var topic2 = string.Join(" ", Enumerable.Range(0, 20).Select(_ =>
            "The French Revolution fundamentally transformed European political structures and led to the rise of democratic governance."));
        var topic3 = string.Join(" ", Enumerable.Range(0, 20).Select(_ =>
            "Quantum computing leverages superposition and entanglement to solve problems that classical computers cannot handle efficiently."));

        var text = $"{topic1}\n\n{topic2}\n\n{topic3}";

        var config = new PalaceConfig
        {
            TopicShiftChunkingEnabled = true,
            TopicShiftThreshold = 0.60f,
        };

        var chunker = new Chunkers.TopicShiftChunker(
            new TopicAwareEmbedder(), config,
            NullLogger<Chunkers.TopicShiftChunker>.Instance);

        var chunks = await chunker.ChunkAsync(text, CancellationToken.None);

        // Topic-shift should produce ~3 chunks (one per topic), not ~6 from fixed-window
        await Assert.That(chunks.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(chunks.Count).IsLessThanOrEqualTo(4);
    }

    /// <summary>
    /// Embedder that throws if called — proves the chunker short-circuits
    /// without embedding for short inputs.
    /// </summary>
    sealed class ThrowingEmbedder : IEmbedder
    {
        public int EmbeddingDimension => 512;
        public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => throw new InvalidOperationException("EmbedAsync should not be called for short input");

        public ValueTask<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts, CancellationToken ct = default)
            => throw new InvalidOperationException("EmbedBatchAsync should not be called for short input");
    }
}
