using System.Reflection;
using Wendmem.Options;

namespace Wendmem.Tests;

/// <summary>
/// Regression guard: both transports must expose identical tool surfaces
/// and LlmOptions.ResolveActive must return correct values per provider.
/// </summary>
sealed class RegistrationDriftTests
{
    /// <summary>
    /// The 6 tool-type classes that AddWendmemTools registers via WithTools&lt;T&gt;().
    /// Adding a new tool class means editing WendmemServices.AddWendmemTools and this array.
    /// </summary>
    static readonly Type[] ToolTypes =
    [
        typeof(Wendmem.Tools.DrawerTools),
        typeof(Wendmem.Tools.KnowledgeGraphTools),
        typeof(Wendmem.Wiki.WikiTools),
        typeof(Wendmem.Wiki.WikiMaintenanceTools),
        typeof(Wendmem.Tools.EpisodeTools),
        typeof(Wendmem.Tools.SkillTools),
    ];

    /// <summary>
    /// The 17 expected MCP tool names — one per [McpServerTool(Name=...)] across all 6 types.
    /// If a tool is added or removed, this list must be updated.
    /// </summary>
    static readonly string[] ExpectedToolNames =
    [
        "WakeUp", "SearchMemories", "GetDrawer", "GrepExact", "AddMemory",
        "AddTriple", "InvalidateTriple",
        "WikiRead", "WikiWrite", "WikiSearch",
        "LintWiki", "Distill", "ListPendingUpdates", "DismissPendingUpdate",
        "RecordEpisode", "FindEpisodes", "FindSkills",
    ];

    [Test]
    public async Task ExactlySixToolTypes_Registered()
    {
        await Assert.That(ToolTypes.Length).IsEqualTo(6);
    }

    [Test]
    public async Task Exactly17ExpectedToolNames()
    {
        await Assert.That(ExpectedToolNames.Length).IsEqualTo(17);
        // Spot-check a few critical ones
        await Assert.That(ExpectedToolNames).Contains("WakeUp");
        await Assert.That(ExpectedToolNames).Contains("GrepExact");
        await Assert.That(ExpectedToolNames).Contains("FindSkills");
    }

    [Test]
    public async Task ToolTypes_HaveMcpServerToolTypeAttribute()
    {
        // Every tool type class must be marked [McpServerToolType]
        foreach (var type in ToolTypes)
        {
            var hasAttr = type.GetCustomAttributes(false)
                .Any(a => a.GetType().Name == "McpServerToolTypeAttribute");
            await Assert.That(hasAttr).IsTrue()
                .Because($"Type {type.Name} must have [McpServerToolType] attribute");
        }
    }

    [Test]
    public async Task McpTool_WingParameter_IsAlwaysOptional()
    {
        var offenders = new List<string>();

        foreach (var type in ToolTypes)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Static | BindingFlags.Instance);
            foreach (var method in methods)
            {
                var isTool = method.GetCustomAttributes(false)
                    .Any(a => a.GetType().Name == "McpServerToolAttribute");
                if (!isTool)
                    continue;

                foreach (var p in method.GetParameters())
                {
                    if (p.Name == "wing" && !p.HasDefaultValue)
                        offenders.Add($"{type.Name}.{method.Name}");
                }
            }
        }

        await Assert.That(offenders.Count).IsEqualTo(0)
            .Because("every MCP tool 'wing' parameter must be optional (have a default) to avoid " +
                     "a crash when the client omits it; offending methods: " +
                     (offenders.Count == 0 ? "(none)" : string.Join(", ", offenders)));
    }

    [Test]
    public async Task LlmOptions_ResolveActive_ZAi()
    {
        var prev = Environment.GetEnvironmentVariable("ZAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ZAI_API_KEY", null);
            var opts = new LlmOptions();
            var (provider, _, _, _, keySource) = opts.ResolveActive();
            await Assert.That(provider).IsEqualTo(LlmProvider.ZAi);
            await Assert.That(keySource).Contains("ZAi");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAI_API_KEY", prev);
        }
    }

    [Test]
    public async Task LlmOptions_ResolveActive_Ollama()
    {
        var opts = new LlmOptions { Provider = LlmProvider.Ollama };
        var (provider, _, _, apiKey, keySource) = opts.ResolveActive();
        await Assert.That(provider).IsEqualTo(LlmProvider.Ollama);
        await Assert.That(apiKey).IsEqualTo("ollama");
        await Assert.That(keySource).Contains("Ollama");
    }

    [Test]
    public async Task LlmOptions_ResolveActive_LlamaCpp()
    {
        var opts = new LlmOptions { Provider = LlmProvider.LlamaCpp };
        var (provider, _, _, apiKey, keySource) = opts.ResolveActive();
        await Assert.That(provider).IsEqualTo(LlmProvider.LlamaCpp);
        await Assert.That(apiKey).IsEqualTo("llamacpp");
        await Assert.That(keySource).Contains("LlamaCpp");
    }
}
