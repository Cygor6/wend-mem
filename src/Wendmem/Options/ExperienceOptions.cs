namespace Wendmem.Options;

/// <summary>
/// Configuration for the ReMe experience memory subsystem.
/// Bound from appsettings.json "Experiences" section.
/// LLM backend settings are in LlmOptions (with optional Experiences sub-override).
/// </summary>
public sealed class ExperienceOptions
{
    public const string SectionName = "Experiences";

    public int TopK { get; set; } = 5;
    public int SamplingN { get; set; } = 8;
    public int MaxReflectionAttempts { get; set; } = 3;
    public int PruneMinRetrievals { get; set; } = 5;
    public float PruneUtilityThreshold { get; set; } = 0.5f;
    public float ValidationMinScore { get; set; } = 0.5f;
    public float SuccessScoreThreshold { get; set; } = 1.0f;
    public float DedupSimilarityThreshold { get; set; } = 0.92f;
    public bool EnableSoftComparison { get; set; } = true;
    public bool UseSimpleFlow { get; set; } = false;
    public bool EnableLlmRerank { get; set; } = false;
    public bool EnableLlmRewrite { get; set; } = false;
}
