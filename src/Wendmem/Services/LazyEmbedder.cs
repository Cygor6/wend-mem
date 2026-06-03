using Microsoft.ML.OnnxRuntime;

namespace Wendmem.Services;

/// <summary>
/// Lazy wrapper around <see cref="GemmaEmbedder"/>. Defers ONNX model loading
/// until the first embedding call. If the model files are missing or loading fails,
/// <see cref="IsAvailable"/> returns false and all embedding methods throw
/// <see cref="InvalidOperationException"/> with a clear message.
/// </summary>
public sealed class LazyEmbedder : IEmbedder
{
    readonly object _lock = new();
    readonly string _modelPath;
    readonly string _tokenizerPath;
    readonly int _maxSeqLen;
    readonly int _embeddingDim;
    readonly int _modelOutputDim;

    GemmaEmbedder? _inner;
    bool _triedLoad;
    string? _loadError;

    public bool IsAvailable
    {
        get
        {
            EnsureLoaded();
            return _inner is not null;
        }
    }

    public int EmbeddingDimension => _embeddingDim;

    public LazyEmbedder(
        string modelPath, string tokenizerPath,
        int maxSeqLen = 2048, int embeddingDim = 512,
        int modelOutputDim = 768)
    {
        _modelPath = modelPath;
        _tokenizerPath = tokenizerPath;
        _maxSeqLen = maxSeqLen;
        _embeddingDim = embeddingDim;
        _modelOutputDim = modelOutputDim;
    }

    public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => DelegateOrThrow(e => e.EmbedAsync(text, ct));

    public ValueTask<float[]> EmbedDocumentAsync(string text, CancellationToken ct = default)
        => DelegateOrThrow(e => e.EmbedDocumentAsync(text, ct));

    public ValueTask<float[]> EmbedQueryAsync(string text, CancellationToken ct = default)
        => DelegateOrThrow(e => e.EmbedQueryAsync(text, ct));

    public async ValueTask<IReadOnlyList<float[]>> EmbedDocumentBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
        => await DelegateOrThrow(e => e.EmbedDocumentBatchAsync(texts, ct));

    public async ValueTask<IReadOnlyList<float[]>> EmbedQueryBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
        => await DelegateOrThrow(e => e.EmbedQueryBatchAsync(texts, ct));

    public int CountTokens(string text)
    {
        EnsureLoaded();
        if (_inner is null)
            return text.Length / 4;
        return _inner.CountTokens(text);
    }

    void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_triedLoad)
                return;
            _triedLoad = true;

            try
            {
                _inner = new GemmaEmbedder(
                    _modelPath, _tokenizerPath,
                    _maxSeqLen, _embeddingDim,
                    modelOutputDim: _modelOutputDim);
            }
            catch (OnnxRuntimeException ex)
            {
                _loadError = $"Failed to load ONNX embedding model: {ex.Message}. " +
                             $"Model path: {_modelPath}. " +
                             $"Run integrations/download-model.ps1 to fetch the model files.";
                Console.Error.WriteLine($"[wendmem] {_loadError}");
            }
            catch (FileNotFoundException ex)
            {
                _loadError = ex.Message;
                Console.Error.WriteLine($"[wendmem] {_loadError}");
            }
        }
    }

    ValueTask<T> DelegateOrThrow<T>(Func<GemmaEmbedder, ValueTask<T>> fn)
    {
        EnsureLoaded();
        if (_inner is not null)
            return fn(_inner);

        throw new InvalidOperationException(
            _loadError ?? "Embedding model is not available. " +
            $"Model path: {_modelPath}. Run download-model.ps1.");
    }
}
