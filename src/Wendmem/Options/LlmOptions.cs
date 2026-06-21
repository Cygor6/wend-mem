namespace Wendmem.Options;

/// <summary>
/// LLM provider selector. Source-generated binder parses from config string.
/// </summary>
public enum LlmProvider
{
    ZAi,
    Ollama,
    LlamaCpp
}

/// <summary>
/// Unified LLM backend configuration. Bound from appsettings.json "Llm" section.
/// AOT-safe via EnableConfigurationBindingGenerator source generator.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>
    /// Active provider. Switching requires only a config change — no recompile.
    /// </summary>
    public LlmProvider Provider { get; set; } = LlmProvider.ZAi;

    /// <summary>z.ai provider settings.</summary>
    public ZAiOptions ZAi { get; set; } = new();

    /// <summary>Ollama provider settings.</summary>
    public OllamaOptions Ollama { get; set; } = new();

    /// <summary>llama.cpp server settings. Uses OpenAI-compatible /v1/ endpoint.</summary>
    public LlamaCppOptions LlamaCpp { get; set; } = new();

    /// <summary>
    /// Resolves the active provider's endpoint, model, API key, and a human-readable
    /// key source description. Single place for the provider switch — used by
    /// chat-client factory, LlmService factory, and startup validation.
    /// </summary>
    public (LlmProvider Provider, string Endpoint, string Model, string ApiKey, string KeySource) ResolveActive()
    {
        return Provider switch
        {
            LlmProvider.LlamaCpp => (
                Provider,
                LlamaCpp.Endpoint,
                LlamaCpp.ChatModel,
                string.IsNullOrWhiteSpace(LlamaCpp.ApiKey) ? "llamacpp" : LlamaCpp.ApiKey,
                "appsettings.json Llm:LlamaCpp:ApiKey"),
            LlmProvider.Ollama => (
                Provider,
                Ollama.Endpoint,
                Ollama.ChatModel,
                string.IsNullOrWhiteSpace(Ollama.ApiKey) ? "ollama" : Ollama.ApiKey,
                "appsettings.json Llm:Ollama:ApiKey"),
            _ => (
                Provider,
                ZAi.Endpoint,
                ZAi.ChatModel,
                Environment.GetEnvironmentVariable("ZAI_API_KEY") ?? ZAi.ApiKey,
                Environment.GetEnvironmentVariable("ZAI_API_KEY") is not null
                    ? "environment variable ZAI_API_KEY"
                    : "appsettings.json Llm:ZAi:ApiKey"),
        };
    }

    /// <summary>
    /// The configured <c>DisableThinkingJson</c> for the active provider (empty when unset).
    /// See <c>DisableThinkingParams.Resolve</c> for how the effective additional-properties
    /// are chosen. Added per-provider rather than per-call because all wend-mem LLM tasks are
    /// extraction/classification that never benefit from a reasoning pass.
    /// </summary>
    public string DisableThinkingJsonForActive() => Provider switch
    {
        LlmProvider.LlamaCpp => LlamaCpp.DisableThinkingJson ?? "",
        LlmProvider.Ollama => Ollama.DisableThinkingJson ?? "",
        _ => ZAi.DisableThinkingJson ?? "",
    };

    /// <summary>
    /// Optional per-subsystem override for entity refinement.
    /// Null fields inherit from the active provider.
    /// </summary>
    public LlmSubOptions? EntityRefinement { get; set; }

    /// <summary>
    /// Optional per-subsystem override for experience memory.
    /// Null fields inherit from the active provider.
    /// </summary>
    public LlmSubOptions? Experiences { get; set; }
}

/// <summary>
/// z.ai provider settings. ApiKey is overridden by ZAI_API_KEY env var if set.
/// </summary>
public sealed class ZAiOptions
{
    public string ApiKey { get; set; } = "";
    public string Endpoint { get; set; } = "https://api.z.ai/api/paas/v4/";
    public string ChatModel { get; set; } = "glm-5-turbo";
    public string? LightModel { get; set; }

    /// <summary>
    /// Raw JSON object (e.g. <c>{"thinking":{"type":"disabled"}}</c>) sent as additional
    /// request properties to disable the model's reasoning/thinking pass. Blank falls back to
    /// the built-in provider default. See <c>Services.DisableThinkingParams</c>.
    /// </summary>
    public string? DisableThinkingJson { get; set; }
}

/// <summary>
/// Ollama provider settings. ApiKey defaults to "ollama" (any non-empty string works).
/// </summary>
public sealed class OllamaOptions
{
    public string ApiKey { get; set; } = "ollama";
    public string Endpoint { get; set; } = "http://localhost:11434/v1/";
    public string ChatModel { get; set; } = "llama3.1";
    public string? LightModel { get; set; }

    /// <summary>
    /// Raw JSON object (e.g. <c>{"think":false}</c>) sent as additional request properties to
    /// disable the model's reasoning/thinking pass. Blank falls back to the built-in provider
    /// default. See <c>Services.DisableThinkingParams</c>.
    /// </summary>
    public string? DisableThinkingJson { get; set; }
}

/// <summary>
/// llama.cpp server provider settings.
/// Uses the OpenAI-compatible /v1/ chat completions endpoint exposed by llama-server.
/// ApiKey defaults to "llamacpp" (any non-empty string works; server often ignores it).
/// </summary>
public sealed class LlamaCppOptions
{
    public string ApiKey { get; set; } = "llamacpp";
    public string Endpoint { get; set; } = "http://localhost:8080/v1/";
    public string ChatModel { get; set; } = "default";
    public string? LightModel { get; set; }

    /// <summary>
    /// Raw JSON object sent as additional request properties to disable the model's
    /// reasoning/thinking pass. Model-dependent: e.g. Qwen3 uses
    /// <c>{"chat_template_kwargs":{"enable_thinking":false}}</c>. Blank falls back to the
    /// built-in provider default (llama.cpp default is empty/nothing). See
    /// <c>Services.DisableThinkingParams</c>.
    /// </summary>
    public string? DisableThinkingJson { get; set; }
}

/// <summary>
/// Optional per-subsystem override. Null fields inherit from the active provider.
/// </summary>
public sealed class LlmSubOptions
{
    public string? Endpoint { get; set; }
    public string? Model { get; set; }
    public string? ApiKey { get; set; }
    public bool? Enabled { get; set; }
}
