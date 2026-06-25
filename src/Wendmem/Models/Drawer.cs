using System.Text.Json.Serialization;

namespace Wendmem.Models;

public enum ClusterRegime { Tight, Spread, Unknown }

public record Drawer(
    string Id,
    string Wing,
    string Room,
    string Content,
    string? FtsText,
    string? EmbeddingText,
    string? ParentId,
    string ContentHash,
    string? Source,
    long? SourceMtime,
    float Importance,
    string DrawerType,
    DateTimeOffset MinedAt,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    bool IsRepresentative = true,
    DateTimeOffset? LastAccessedAt = null,
    int AccessCount = 0,
    int? ClusterId = null,
    float? ClusterDBar = null,
    float? ClusterDEff = null,
    float[]? Embedding = null
);

public record DrawerResult(Drawer Drawer, float Score, ClusterRegime Regime = ClusterRegime.Unknown)
{
    public static ClusterRegime ComputeRegime(float dBar, float dEff, float theta)
    {
        if (dBar == 0f)
            return ClusterRegime.Unknown;
        float thetaPrime = 1f - theta;
        return dBar < thetaPrime ? ClusterRegime.Tight : ClusterRegime.Spread;
    }
}

public record GrepExactResult(
    string Id,
    string Wing,
    string Room,
    string Content,
    string? SourceFile,
    DateTime MinedAt,
    string Snippet);

public record PruneReport(int Clusters, int Retired, int Kept);

/// <summary>
/// Result of admission control on AddDrawerAsync.
/// </summary>
public record AdmissionResult(string? Id, bool Admitted, string? Reason, string? MatchedId);

/// <summary>
/// Structured WakeUp result for tool-level logging.
/// SeedTopScore / SeedLabels are pre-exclusion seed-match diagnostics carried
/// for the WakeUp decision_support envelope only; they are not serialized into
/// the result payload (JsonIgnore) so the agent-visible result is unchanged.
/// </summary>
public record WakeUpResult(
    string Content,
    int L0,
    int L1,
    int L2,
    [property: JsonIgnore] float SeedTopScore = 0f,
    [property: JsonIgnore] string[]? SeedLabels = null);
