using System.Numerics.Tensors;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Wendmem.Services;

/// <summary>
/// EmbeddingGemma-300M embedder. Uses SentencePiece tokenizer,
/// Gemma3 decoder-only transformer with bidirectional attention.
/// Prefers pre-pooled sentence_embedding output; falls back to mean pooling.
/// </summary>
public sealed class GemmaEmbedder : IEmbedder, IDisposable
{
    readonly int _maxSeqLen;
    readonly int _embeddingDim;
    readonly int _modelOutputDim;
    readonly InferenceSession _session;
    readonly Tokenizer _tokenizer;
    readonly bool _modelWantsTokenTypeIds;
    readonly bool _modelWantsPositionIds;

    public bool IsAvailable => true;
    public int EmbeddingDimension => _embeddingDim;
    public int MaxSequenceTokens => _maxSeqLen;

    /// <summary>
    /// Embedding dimension (vector length) produced after Matryoshka truncation.
    /// </summary>
    public const int Dimensions = 512;

    public GemmaEmbedder(
        string modelPath, string tokenizerPath,
        int maxSeqLen = 2048, int embeddingDim = Dimensions,
        int? intraOpThreads = null,
        int modelOutputDim = 768)
    {
        _maxSeqLen = maxSeqLen;
        _embeddingDim = embeddingDim;
        _modelOutputDim = modelOutputDim;

        var opts = BuildSessionOptions(intraOpThreads);
        _session = new InferenceSession(modelPath, opts);

        using var tokStream = File.OpenRead(tokenizerPath);
        _tokenizer = SentencePieceTokenizer.Create(tokStream, addBeginningOfSentence: true, addEndOfSentence: false, specialTokens: null);

        _modelWantsTokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");
        _modelWantsPositionIds = _session.InputMetadata.ContainsKey("position_ids");
    }

    /// <summary>
    /// Embed text as a document (for indexing/storage). Prepends "Document: " prefix.
    /// </summary>
    public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => EmbedWithPrefixAsync("", text, ct);

    /// <summary>
    /// Embed text as a document (for indexing/storage).
    /// Prefixes with "title: none | text: " per EmbeddingGemma training format.
    /// </summary>
    public ValueTask<float[]> EmbedDocumentAsync(string text, CancellationToken ct = default)
        => EmbedWithPrefixAsync("title: none | text: ", text, ct);

    /// <summary>
    /// Embed text as a query (for search/retrieval).
    /// Prefixes with "task: search result | query: " per EmbeddingGemma training format.
    /// </summary>
    public ValueTask<float[]> EmbedQueryAsync(string text, CancellationToken ct = default)
        => EmbedWithPrefixAsync("task: search result | query: ", text, ct);

    /// <summary>
    /// Embed multiple documents in a single batch.
    /// </summary>
    public ValueTask<IReadOnlyList<float[]>> EmbedDocumentBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
        => EmbedBatchInternalAsync(texts, "title: none | text: ", ct);

    /// <summary>
    /// Embed multiple queries in a single batch.
    /// </summary>
    public ValueTask<IReadOnlyList<float[]>> EmbedQueryBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
        => EmbedBatchInternalAsync(texts, "task: search result | query: ", ct);

    const int BatchSize = 8;

    async ValueTask<IReadOnlyList<float[]>> EmbedBatchInternalAsync(
        IReadOnlyList<string> texts, string prefix, CancellationToken ct)
    {
        var results = new float[texts.Count][];

        for (int chunkStart = 0; chunkStart < texts.Count; chunkStart += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            int chunkLen = Math.Min(BatchSize, texts.Count - chunkStart);

            // Encode all texts in chunk, find max token length
            var encoded = new IReadOnlyList<int>[chunkLen];
            int maxLen = 0;

            for (int i = 0; i < chunkLen; i++)
            {
                var text = texts[chunkStart + i];
                if (string.IsNullOrWhiteSpace(text))
                {
                    encoded[i] = Array.Empty<int>();
                    continue;
                }

                var prefixed = $"{prefix}{text}";
                string? normalizedText = null;
                int charsConsumed = 0;
                var ids = _tokenizer.EncodeToIds(
                    prefixed, maxTokenCount: _maxSeqLen - 1,
                    out normalizedText, out charsConsumed);
                encoded[i] = ids;
                if (ids.Count > maxLen)
                    maxLen = ids.Count;
            }

            // Handle all-empty edge case
            if (maxLen == 0)
            {
                for (int i = 0; i < chunkLen; i++)
                    results[chunkStart + i] = new float[_embeddingDim];
                continue;
            }

            // Build stacked tensors: [chunkLen, maxLen]
            var inputIds = new DenseTensor<long>(new long[chunkLen * maxLen], new[] { chunkLen, maxLen });
            var attentionMask = new DenseTensor<long>(new long[chunkLen * maxLen], new[] { chunkLen, maxLen });

            for (int i = 0; i < chunkLen; i++)
            {
                var ids = encoded[i];
                for (int j = 0; j < ids.Count; j++)
                {
                    inputIds[i, j] = ids[j];
                    attentionMask[i, j] = 1;
                }
                // remaining positions stay 0 (padding)
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            };

            if (_modelWantsTokenTypeIds)
            {
                var tokenTypeIds = new DenseTensor<long>(new long[chunkLen * maxLen], new[] { chunkLen, maxLen });
                inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds));
            }

            if (_modelWantsPositionIds)
            {
                var positionIds = new DenseTensor<long>(new long[chunkLen * maxLen], new[] { chunkLen, maxLen });
                for (int i = 0; i < chunkLen; i++)
                    for (int j = 0; j < maxLen; j++)
                        positionIds[i, j] = j;
                inputs.Add(NamedOnnxValue.CreateFromTensor("position_ids", positionIds));
            }

            using var outputs = _session.Run(inputs);

            ExtractBatchEmbeddings(outputs, chunkLen, maxLen, results, chunkStart);
        }

        return results;
    }

    void ExtractBatchEmbeddings(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        int batchSize, int seqLen,
        float[][] results, int resultsOffset)
    {
        // Scan outputs for pre-pooled [batch, dim] or token-level [batch, seq, dim]
        float[]? prePooled = null;
        float[]? tokenLevel = null;
        int prePooledDim = 0;
        bool prePooledBatch = false;

        foreach (var output in outputs)
        {
            var t = output.AsTensor<float>();
            if (t is null)
                continue;
            var dims = t.Dimensions.ToArray();
            var values = t.ToArray();

            // Pre-pooled: [batch, dim] where dim == _modelOutputDim
            if (dims.Length == 2 && dims[0] == batchSize && dims[1] == _modelOutputDim)
            {
                prePooled = values;
                prePooledDim = _modelOutputDim;
                prePooledBatch = true;
                continue;
            }

            // Single-row pre-pooled (batch=1 fallback)
            if (dims.Length == 2 && dims[0] == 1 && dims[1] == _modelOutputDim && batchSize == 1)
            {
                prePooled = values;
                prePooledDim = _modelOutputDim;
                prePooledBatch = false;
                continue;
            }

            // Token-level: [batch, seq, dim] or [1, seq, dim]
            if (dims.Length == 3 && dims[2] == _modelOutputDim)
            {
                tokenLevel = values;
            }
        }

        for (int i = 0; i < batchSize; i++)
        {
            float[] full;

            if (prePooled is not null)
            {
                full = new float[_modelOutputDim];
                int srcOffset = prePooledBatch ? i * _modelOutputDim : 0;
                Array.Copy(prePooled, srcOffset, full, 0, _modelOutputDim);
            }
            else if (tokenLevel is not null)
            {
                // Mean pooling over token positions
                int rowOffset = i * seqLen * _modelOutputDim;
                full = new float[_modelOutputDim];
                for (int s = 0; s < seqLen; s++)
                {
                    int tokOffset = rowOffset + s * _modelOutputDim;
                    for (int d = 0; d < _modelOutputDim; d++)
                        full[d] += tokenLevel[tokOffset + d];
                }
                for (int d = 0; d < _modelOutputDim; d++)
                    full[d] /= seqLen;
            }
            else
            {
                throw new InvalidOperationException(
                    $"No embedding output found with dim={_modelOutputDim} for batch.");
            }

            // Matryoshka truncation
            float[] embedding;
            if (_embeddingDim < _modelOutputDim)
            {
                embedding = new float[_embeddingDim];
                Array.Copy(full, embedding, _embeddingDim);
            }
            else
            {
                embedding = full;
            }

            NormalizeInPlace(embedding);
            results[resultsOffset + i] = embedding;
        }
    }

    ValueTask<float[]> EmbedWithPrefixAsync(string prefix, string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(text))
            return ValueTask.FromResult(new float[_embeddingDim]);

        var prefixed = $"{prefix}{text}";
        string? normalizedText = null;
        int charsConsumed = 0;
        var encoded = _tokenizer.EncodeToIds(
            prefixed, maxTokenCount: _maxSeqLen - 1,
            out normalizedText, out charsConsumed);
        if (encoded.Count == 0)
            return ValueTask.FromResult(new float[_embeddingDim]);

        var inputIds = new DenseTensor<long>(new[] { 1, encoded.Count });
        var attentionMask = new DenseTensor<long>(new[] { 1, encoded.Count });

        for (int i = 0; i < encoded.Count; i++)
        {
            inputIds[0, i] = encoded[i];
            attentionMask[0, i] = 1;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
        };

        if (_modelWantsTokenTypeIds)
        {
            var tokenTypeIds = new DenseTensor<long>(new[] { 1, encoded.Count });
            // All zeros for single-segment input
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds));
        }

        if (_modelWantsPositionIds)
        {
            var positionIds = new DenseTensor<long>(new[] { 1, encoded.Count });
            for (int i = 0; i < encoded.Count; i++)
                positionIds[0, i] = i;
            inputs.Add(NamedOnnxValue.CreateFromTensor("position_ids", positionIds));
        }

        using var outputs = _session.Run(inputs);

        var embedding = ExtractEmbedding(outputs);
        NormalizeInPlace(embedding);

        return ValueTask.FromResult(embedding);
    }

    /// <inheritdoc/>
    public int CountTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        return _tokenizer.CountTokens(text);
    }

    float[] ExtractEmbedding(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs)
    {
        // EmbeddingGemma ONNX models may provide:
        //   sentence_embedding: [1, dim]  — pre-pooled by the model (preferred)
        //   last_hidden_state: [1, seq_len, dim] — token-level, requires mean pooling
        // We scan all outputs, prefer sentence_embedding, fall back to mean pooling.
        // Matryoshka: the model outputs _modelOutputDim (768), we truncate to _embeddingDim (512).

        float[]? pooled = null;
        float[]? tokenLevel = null;

        foreach (var output in outputs)
        {
            var t = output.AsTensor<float>();
            if (t is null)
                continue;

            var dims = t.Dimensions.ToArray();

            // Pre-pooled sentence embedding: [1, dim] — preferred
            if (dims.Length == 2 && dims[0] == 1 && dims[1] == _modelOutputDim)
            {
                pooled = new float[_modelOutputDim];
                Array.Copy(t.ToArray(), pooled, _modelOutputDim);
            }

            // Token-level: [1, seq_len, dim] — mean pool as fallback
            if (dims.Length == 3 && dims[0] == 1 && dims[2] == _modelOutputDim)
            {
                int seqLen = dims[1];
                var values = t.ToArray();
                tokenLevel = new float[_modelOutputDim];
                for (int s = 0; s < seqLen; s++)
                {
                    int offset = s * _modelOutputDim;
                    for (int d = 0; d < _modelOutputDim; d++)
                        tokenLevel[d] += values[offset + d];
                }
                for (int d = 0; d < _modelOutputDim; d++)
                    tokenLevel[d] /= seqLen;
            }
        }

        var full = pooled ?? tokenLevel;

        if (full is null)
        {
            throw new InvalidOperationException(
                $"No embedding output found with dim={_modelOutputDim}. Outputs: " +
                string.Join(", ", outputs.Select(o =>
                {
                    var t = o.AsTensor<float>();
                    return t is not null ? $"{o.Name} [{string.Join(",", t.Dimensions.ToArray())}]" : o.Name;
                })));
        }

        // Matryoshka truncation: take first _embeddingDim dimensions
        if (_embeddingDim >= _modelOutputDim)
            return full;

        var truncated = new float[_embeddingDim];
        Array.Copy(full, truncated, _embeddingDim);
        return truncated;
    }

    private static SessionOptions BuildSessionOptions(int? intraOpThreads)
    {
        int logical = Environment.ProcessorCount;
        int physical = Math.Max(1, logical / 2);

        var opts = new SessionOptions();
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        opts.ExecutionMode = ExecutionMode.ORT_PARALLEL;
        opts.IntraOpNumThreads = intraOpThreads ?? physical;
        opts.InterOpNumThreads = Math.Min(2, physical);
        return opts;
    }

    static void NormalizeInPlace(float[] v)
    {
        float norm = TensorPrimitives.Norm(v);
        if (norm > 1e-9f)
            TensorPrimitives.Divide(v, norm, v);
    }

    public void Dispose() => _session.Dispose();
}
