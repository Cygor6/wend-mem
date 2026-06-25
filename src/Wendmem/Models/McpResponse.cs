using System.Text.Json.Serialization;

namespace Wendmem.Models;

/// <summary>
/// Closed set of suggested next actions for the agent.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SuggestedAction>))]
public enum SuggestedAction
{
    [JsonPropertyName("proceed")] Proceed,
    [JsonPropertyName("verify")] Verify,
    [JsonPropertyName("retry")] Retry,
    [JsonPropertyName("ask_user")] AskUser
}

/// <summary>
/// Structured error information.
/// </summary>
public sealed record McpError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// Multi-signal agreement data for search confidence.
/// </summary>
public sealed record McpSignals(
    [property: JsonPropertyName("bm25")] bool Bm25,
    [property: JsonPropertyName("semantic")] float Semantic,
    [property: JsonPropertyName("kg_entity")] bool KgEntity);

/// <summary>
/// Normalized confidence for agent decisions.
/// </summary>
public sealed record McpConfidence(
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("score")] float Score,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("signals")] McpSignals? Signals = null,
    [property: JsonPropertyName("agreement")] string Agreement = "not_applicable")
{
    /// <summary>
    /// Standard confidence for deterministic operations (writes, reads, state changes).
    /// </summary>
    public static McpConfidence Deterministic { get; } = new("high", 1.0f, "deterministic");
}

/// <summary>
/// Explicit guidance for the agent's next logical step.
/// </summary>
public sealed record McpDecisionSupport(
    [property: JsonPropertyName("can_proceed")] bool CanProceed,
    [property: JsonPropertyName("suggested_action")] SuggestedAction SuggestedAction,
    [property: JsonPropertyName("summary")] string Summary);

/// <summary>
/// Tool execution metadata.
/// </summary>
public sealed record McpMeta(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("duration_ms")] long DurationMs);

/// <summary>
/// Standardized MCP tool response envelope.
/// </summary>
public sealed record McpResponse<T>(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("result")] T? Result = default,
    [property: JsonPropertyName("confidence")] McpConfidence? Confidence = null,
    [property: JsonPropertyName("decision_support")] McpDecisionSupport? DecisionSupport = null,
    [property: JsonPropertyName("error")] McpError? Error = null,
    [property: JsonPropertyName("meta")] McpMeta? Meta = null);

/// <summary>
/// Helper for creating standardized MCP responses.
/// </summary>
public static class McpResponse
{
    public static McpResponse<T> Ok<T>(
        string tool,
        T result,
        long durationMs = 0,
        McpConfidence? confidence = null,
        McpDecisionSupport? decisionSupport = null)
        => new(true, result,
            confidence,
            decisionSupport,
            Meta: new(tool, durationMs));

    public static McpResponse<T> Fail<T>(
        string tool,
        string errorCode,
        string errorMessage,
        long durationMs = 0)
        => new(false,
            Error: new McpError(errorCode, errorMessage),
            Meta: new(tool, durationMs));
}
