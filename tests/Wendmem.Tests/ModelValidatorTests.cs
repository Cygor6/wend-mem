using Wendmem.Services;

namespace Wendmem.Tests;

/// <summary>
/// Smoke tests for ModelValidator startup validation.
/// Verifies that missing model files produce clear FileNotFoundExceptions.
/// </summary>
public sealed class ModelValidatorTests
{
    [Test]
    public async Task EnsureFilesExist_MissingModel_ThrowsFileNotFoundException()
    {
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            ModelValidator.EnsureFilesExist(
                @"C:\nonexistent\path\model_quantized.onnx",
                @"C:\nonexistent\path\tokenizer.model"));

        await Assert.That(ex.Message).Contains("missing");
        await Assert.That(ex.Message).Contains("download-model.ps1");
    }

    [Test]
    public async Task EnsureFilesExist_MissingFiles_ListsAllMissingInMessage()
    {
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            ModelValidator.EnsureFilesExist(
                @"C:\nonexistent\model_quantized.onnx",
                @"C:\nonexistent\tokenizer.model"));

        // Should list the model, external data, and tokenizer as missing
        await Assert.That(ex.Message).Contains("model_quantized.onnx");
        await Assert.That(ex.Message).Contains("model_quantized.onnx_data");
        await Assert.That(ex.Message).Contains("tokenizer.model");
    }

    [Test]
    public async Task EnsureFilesExist_AllFilesPresent_DoesNotThrow()
    {
        // Use the real model files if they exist; skip otherwise.
        var candidates = new[]
        {
            "models/embeddinggemma/model_quantized.onnx",
            "publish/models/embeddinggemma/model_quantized.onnx",
            "../models/embeddinggemma/model_quantized.onnx",
            "../../models/embeddinggemma/model_quantized.onnx",
        };
        var tokenizerCandidates = new[]
        {
            "models/embeddinggemma/tokenizer.model",
            "publish/models/embeddinggemma/tokenizer.model",
            "../models/embeddinggemma/tokenizer.model",
            "../../models/embeddinggemma/tokenizer.model",
        };

        var modelPath = candidates.FirstOrDefault(File.Exists);
        var tokenizerPath = tokenizerCandidates.FirstOrDefault(File.Exists);

        if (modelPath is null || tokenizerPath is null)
            return; // Skip: no model files available

        // Should not throw
        ModelValidator.EnsureFilesExist(modelPath, tokenizerPath);
        await Assert.That(true).IsTrue();
    }
}
