namespace Wendmem.Services;

/// <summary>
/// Startup validation for ONNX model files. Called before the server
/// starts accepting requests to produce a clear error message instead
/// of an opaque ONNX Runtime exception at first tool call.
/// </summary>
static class ModelValidator
{
    /// <summary>
    /// Validates that the embedder is configured for exactly 512 dimensions.
    /// Throws <see cref="InvalidOperationException"/> if not.
    /// </summary>
    public static void EnsureEmbeddingDimension(IEmbedder embedder)
    {
        const int RequiredDim = 512;
        if (embedder.EmbeddingDimension != RequiredDim)
            throw new InvalidOperationException(
                $"Embedding dimension mismatch: expected {RequiredDim} but embedder reports " +
                $"{embedder.EmbeddingDimension}. " +
                $"This system only supports 512-dimensional Gemma embeddings. " +
                $"Check Models:EmbeddingModel:EmbeddingDimension in appsettings.json.");
    }

    /// <summary>
    /// Validates that all required model files exist on disk.
    /// Throws <see cref="FileNotFoundException"/> with a clear message
    /// listing every missing file.
    /// </summary>
    public static void EnsureFilesExist(string modelPath, string tokenizerPath)
    {
        var requiredFiles = new[]
        {
            modelPath,
            modelPath + "_data",  // ONNX external data
            tokenizerPath,
        };

        var missing = new List<string>();
        foreach (var f in requiredFiles)
        {
            if (!File.Exists(f))
                missing.Add(f);
        }

        if (missing.Count == 0)
            return;

        var nl = Environment.NewLine;
        throw new FileNotFoundException(
            $"Required embedding model files missing:{nl}" +
            string.Join(nl, missing.Select(f => $"  - {f}")) +
            $"{nl}{nl}Model path:     {modelPath}{nl}" +
            $"Tokenizer path: {tokenizerPath}{nl}{nl}" +
            $"Run integrations/download-model.ps1 to fetch the model files, " +
            $"or set Models:EmbeddingModel:OnnxPath in appsettings.json.");
    }
}
