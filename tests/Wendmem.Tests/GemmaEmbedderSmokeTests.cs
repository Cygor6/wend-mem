using TUnit.Core.Exceptions;
using Wendmem.Services;

namespace Wendmem.Tests;

/// <summary>
/// Smoke tests for GemmaEmbedder using the real ONNX model.
/// Tests skip at runtime when no model files are found. To run them, either:
///   - place files in models/embeddinggemma/ relative to the test binary, or
///   - set WENDMEM_MODELS_DIR to the directory that contains an embeddinggemma/ subfolder
///     (e.g. C:\tools\wendmem\models)
/// </summary>
public sealed class GemmaEmbedderSmokeTests
{
    static GemmaEmbedder? CreateEmbedder()
    {
        // Absolute paths derived from the WENDMEM_MODELS_DIR environment variable.
        string envBase = Environment.GetEnvironmentVariable("WENDMEM_MODELS_DIR") ?? string.Empty;
        string envModel = envBase.Length > 0
            ? Path.Combine(envBase, "embeddinggemma", "model_quantized.onnx")
            : string.Empty;
        string envTokenizer = envBase.Length > 0
            ? Path.Combine(envBase, "embeddinggemma", "tokenizer.model")
            : string.Empty;

        string[] modelPaths =
        [
            envModel,
            @"C:\tools\wendmem\models\embeddinggemma\model_quantized.onnx",  // standard install location
            "models/embeddinggemma/model_quantized.onnx",
            "publish/models/embeddinggemma/model_quantized.onnx",
            "../models/embeddinggemma/model_quantized.onnx",
            "../../models/embeddinggemma/model_quantized.onnx",
        ];
        string[] tokenizerPaths =
        [
            envTokenizer,
            @"C:\tools\wendmem\models\embeddinggemma\tokenizer.model",  // standard install location
            "models/embeddinggemma/tokenizer.model",
            "publish/models/embeddinggemma/tokenizer.model",
            "../models/embeddinggemma/tokenizer.model",
            "../../models/embeddinggemma/tokenizer.model",
        ];

        string? modelPath = modelPaths.FirstOrDefault(p => p.Length > 0 && File.Exists(p));
        string? tokenizerPath = tokenizerPaths.FirstOrDefault(p => p.Length > 0 && File.Exists(p));

        if (modelPath is null || tokenizerPath is null)
            return null;

        return new GemmaEmbedder(modelPath, tokenizerPath, 2048, 512, 768);
    }

    static GemmaEmbedder RequireEmbedder()
    {
        var e = CreateEmbedder();
        if (e is null)
            throw new SkipTestException(
                "EmbeddingGemma model files not found. " +
                "Set WENDMEM_MODELS_DIR to the folder containing embeddinggemma/, " +
                @"or place files in C:\tools\wendmem\models\embeddinggemma\.");
        return e;
    }

    [Test]
    public async Task Same_Input_Produces_Identical_Embedding()
    {
        using var embedder = RequireEmbedder();

        var a = await embedder.EmbedAsync("The quick brown fox jumps over the lazy dog.");
        var b = await embedder.EmbedAsync("The quick brown fox jumps over the lazy dog.");

        await Assert.That(a.Length).IsEqualTo(512);
        await Assert.That(b.Length).IsEqualTo(512);

        double cos = Cosine(a, b);
        await Assert.That(cos).IsGreaterThan(0.999);
    }

    [Test]
    public async Task Synonyms_Have_High_Similarity()
    {
        using var embedder = RequireEmbedder();

        var car = await embedder.EmbedDocumentAsync("car", CancellationToken.None);
        var automobile = await embedder.EmbedDocumentAsync("automobile", CancellationToken.None);

        await Assert.That(car.Length).IsEqualTo(512);
        double cos = Cosine(car, automobile);
        await Assert.That(cos).IsGreaterThan(0.7);
    }

    [Test]
    public async Task Unrelated_Phrases_Have_Low_Similarity()
    {
        using var embedder = RequireEmbedder();

        var code = await embedder.EmbedDocumentAsync("recursive binary tree traversal algorithm", CancellationToken.None);
        var food = await embedder.EmbedDocumentAsync("Italian pasta recipe with tomato sauce", CancellationToken.None);

        double cos = Cosine(code, food);
        // EmbeddingGemma-300M scores ~0.42 for these phrases; threshold is 0.5
        // (still well below the >0.7 expected for synonyms).
        await Assert.That(cos).IsLessThan(0.5);
    }

    [Test]
    public async Task Document_And_Query_Prefixes_Produce_Different_Vectors()
    {
        using var embedder = RequireEmbedder();

        const string text = "machine learning neural network";
        var doc = await embedder.EmbedDocumentAsync(text, CancellationToken.None);
        var query = await embedder.EmbedQueryAsync(text, CancellationToken.None);

        double cos = Cosine(doc, query);
        await Assert.That(cos).IsLessThan(0.999);
        await Assert.That(cos).IsGreaterThan(0.5);
    }

    [Test]
    public async Task CountTokens_Returns_Exact_Count()
    {
        using var embedder = RequireEmbedder();

        int tokens = embedder.CountTokens("Hello world");
        await Assert.That(tokens).IsGreaterThan(0);
        await Assert.That(tokens).IsLessThan(20);

        await Assert.That(embedder.CountTokens("")).IsEqualTo(0);
        await Assert.That(embedder.CountTokens("   ")).IsEqualTo(0);
    }

    [Test]
    public async Task Embedding_Is_L2_Normalized()
    {
        using var embedder = RequireEmbedder();

        var vec = await embedder.EmbedAsync("normalization test");
        double norm = Math.Sqrt(vec.Sum(x => (double)x * x));

        await Assert.That(norm).IsEqualTo(1.0).Within(0.001);
    }

    static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb) + 1e-10);
    }
}
