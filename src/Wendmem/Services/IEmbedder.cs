namespace Wendmem.Services;

public interface IEmbedder
{

    /// <summary>
    /// Whether the embedding model is loaded and ready for use.
    /// When false, embedding methods will throw or return zero vectors.
    /// </summary>
    bool IsAvailable => true;
    ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// Embed text as a document (for indexing/storage). Implementations may
    /// use a task-specific prefix optimized for retrieval quality.
    /// Default: falls back to <see cref="EmbedAsync"/>.
    /// </summary>
    ValueTask<float[]> EmbedDocumentAsync(string text, CancellationToken ct = default)
        => EmbedAsync(text, ct);

    /// <summary>
    /// Embed text as a search query (for retrieval). Implementations may use
    /// a task-specific prefix optimized for retrieval quality.
    /// Default: falls back to <see cref="EmbedAsync"/>.
    /// </summary>
    ValueTask<float[]> EmbedQueryAsync(string text, CancellationToken ct = default)
        => EmbedAsync(text, ct);

    /// <summary>
    /// Count the number of tokens in text using the embedder's tokenizer.
    /// Used for token-aware chunking and budgeting.
    /// </summary>
    int CountTokens(string text) => text.Length / 4; // default: rough estimate

    /// <summary>
    /// Embed multiple texts in a single batch. Default implementation
    /// calls EmbedDocumentAsync sequentially. Implementations may override for
    /// true batched ONNX inference.
    /// </summary>
    async ValueTask<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var results = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++)
            results[i] = await EmbedDocumentAsync(texts[i], ct);
        return results;
    }

    /// <summary>
    /// Embed multiple documents in a single batch. Default implementation
    /// calls EmbedDocumentAsync sequentially. Implementations may override for
    /// true batched ONNX inference.
    /// </summary>
    async ValueTask<IReadOnlyList<float[]>> EmbedDocumentBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var results = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++)
            results[i] = await EmbedDocumentAsync(texts[i], ct);
        return results;
    }

    /// <summary>
    /// Embed multiple queries in a single batch. Default implementation
    /// calls EmbedQueryAsync sequentially. Implementations may override for
    /// true batched ONNX inference.
    /// </summary>
    async ValueTask<IReadOnlyList<float[]>> EmbedQueryBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var results = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++)
            results[i] = await EmbedQueryAsync(texts[i], ct);
        return results;
    }

    /// <summary>
    /// The embedding dimension (vector length) produced by this embedder.
    /// </summary>
    int EmbeddingDimension { get; }
}
