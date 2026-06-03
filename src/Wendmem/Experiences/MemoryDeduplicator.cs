using System.Numerics.Tensors;
using Wendmem.Services;

namespace Wendmem.Experiences;

public sealed class MemoryDeduplicator
{
    readonly TaskMemoryStorage _storage;
    readonly IEmbedder _embedder;
    readonly float _threshold;

    public MemoryDeduplicator(TaskMemoryStorage storage, IEmbedder embedder, float threshold = 0.92f)
    {
        _storage = storage;
        _embedder = embedder;
        _threshold = threshold;
    }

    public async Task<IReadOnlyList<(Extractors.ExtractedMemory mem, float[] embedding)>> FilterAsync(
        IReadOnlyList<Extractors.ExtractedMemory> candidates, string wing, CancellationToken ct)
    {
        if (candidates.Count == 0)
            return [];

        var embeddings = new List<float[]>(candidates.Count);
        foreach (var c in candidates)
        {
            var emb = await _embedder.EmbedDocumentAsync(c.WhenToUse, ct);
            embeddings.Add(emb);
        }

        var kept = new List<(Extractors.ExtractedMemory, float[])>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var cand = candidates[i];
            var emb = embeddings[i];

            var dbDups = await _storage.FindNearDuplicatesAsync(wing, emb, _threshold, ct);
            if (dbDups.Count > 0)
                continue;

            var batchDup = kept.Any(k => CosineSim(k.Item2, emb) >= _threshold);
            if (batchDup)
                continue;

            kept.Add((cand, emb));
        }
        return kept;
    }

    static float CosineSim(float[] a, float[] b)
    {
        float dot = TensorPrimitives.Dot(a, b);
        float normA = TensorPrimitives.Norm(a);
        float normB = TensorPrimitives.Norm(b);
        float denom = normA * normB;
        return denom < 1e-9f ? 0f : dot / denom;
    }
}
