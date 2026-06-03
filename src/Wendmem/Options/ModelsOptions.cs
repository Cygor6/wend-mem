namespace Wendmem.Options;

/// <summary>
/// Bound from appsettings.json "Models" section.
/// AOT-safe via EnableConfigurationBindingGenerator source generator.
/// </summary>
public sealed class ModelsOptions
{
    public const string SectionName = "Models";

    public EmbeddingModelOptions EmbeddingModel { get; set; } = new();
}

public sealed class EmbeddingModelOptions
{
    public string OnnxPath { get; set; } = "models/model.onnx";
    public string TokenizerPath { get; set; } = "models/tokenizer.model";
    public int MaxSequenceTokens { get; set; } = 2048;

    /// <summary>
    /// Matryoshka-truncated output dimension. The model natively produces
    /// ModelOutputDimension (768) but we truncate to this value for storage.
    /// Valid values: 768, 512, 256, 128.
    /// </summary>
    public int EmbeddingDimension { get; set; } = 512;

    /// <summary>
    /// Native model output dimension (768 for EmbeddingGemma-300M).
    /// Used internally to parse the ONNX tensor before truncation.
    /// </summary>
    public int ModelOutputDimension { get; set; } = 768;

}
