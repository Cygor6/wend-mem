using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Wendmem.Options;

namespace Wendmem.Services;

/// <summary>
/// Resolves the request parameter(s) that disable a model's internal reasoning/"thinking"
/// pass. Reasoning models (GLM-5, Qwen3, DeepSeek-R1) spend output tokens on
/// <c>reasoning_content</c>, which is useless for the extraction/classification tasks
/// wend-mem runs (entity typing, memory distillation, validation, reflection) and can
/// exhaust the token budget before the model emits a usable answer.
/// </summary>
/// <remarks>
/// <para>
/// The effective value is the operator-supplied <c>DisableThinkingJson</c> (on the active
/// provider options) when set, otherwise a built-in default per provider. Different models
/// behind the same provider need different parameter shapes (Qwen3 on llama.cpp uses
/// <c>chat_template_kwargs.enable_thinking</c> while GLM uses <c>thinking.type</c>), which is
/// why the knob is configurable in <c>appsettings.json</c> rather than hardcoded.
/// </para>
/// <para>
/// Parsed values are kept as <see cref="JsonNode">JsonNodes</see> (a detached DOM, not tied
/// to a <see cref="JsonDocument"/> lifetime). <see cref="JsonNode"/> serializes through the
/// OpenAI/MEAI pipeline via STJ's built-in, reflection-free <c>JsonNodeConverter</c>, so this
/// is safe under AOT (<c>PublishAot</c>) where polymorphic <c>object</c> serialization would
/// otherwise produce trim errors.
/// </para>
/// </remarks>
internal static class DisableThinkingParams
{
    /// <summary>
    /// Resolves the effective additional-properties for <paramref name="provider"/>: the
    /// configured JSON when parseable, otherwise the built-in default. Returns null when
    /// neither yields anything (e.g. llama.cpp with no config and a non-reasoning default model).
    /// </summary>
    internal static AdditionalPropertiesDictionary? Resolve(LlmProvider provider, string? configuredJson) =>
        Parse(configuredJson) ?? DefaultFor(provider);

    /// <summary>
    /// Parses a raw JSON object string into an <see cref="AdditionalPropertiesDictionary"/>.
    /// Returns null on blank input, non-object input, or a JSON parse error. A null return
    /// lets the caller fall back to the built-in default.
    /// </summary>
    internal static AdditionalPropertiesDictionary? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            // JsonNode.Parse yields a detached DOM (no JsonDocument to dispose). We require a
            // top-level object; a bare array/scalar is not a valid additional-properties payload.
            if (JsonNode.Parse(json) is not JsonObject obj)
            {
                return null;
            }

            var dict = new AdditionalPropertiesDictionary();
            foreach (var (name, value) in obj)
            {
                if (name is not null)
                {
                    dict[name] = value;
                }
            }

            return dict;
        }
        catch (JsonException)
        {
            // Unparseable config is ignored: fall back to the built-in default rather than
            // throwing at startup over a typo'd appsettings value.
            return null;
        }
    }

    /// <summary>
    /// The built-in fallback per provider, used only when no <c>DisableThinkingJson</c> is
    /// configured. These match the documented vendor conventions so the fix works out of the
    /// box; operators with a non-standard model (e.g. Qwen3 on llama.cpp) override via config.
    /// </summary>
    /// <remarks>
    /// Parameter names per vendor docs: ZAi/GLM uses <c>thinking: { type: "disabled" }</c>
    /// (https://docs.z.ai/guides/capabilities/thinking-mode); Ollama Gemma3/Qwen3 use
    /// <c>think: false</c>. llama.cpp servers vary by build and model, so nothing is sent by
    /// default; set <c>DisableThinkingJson</c> for reasoning models served there.
    /// </remarks>
    internal static AdditionalPropertiesDictionary? DefaultFor(LlmProvider provider) => provider switch
    {
        LlmProvider.ZAi => new AdditionalPropertiesDictionary
        {
            ["thinking"] = new Dictionary<string, string> { ["type"] = "disabled" },
        },
        LlmProvider.Ollama => new AdditionalPropertiesDictionary
        {
            ["think"] = false,
        },
        _ => null,
    };
}
