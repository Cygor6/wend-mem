using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Wendmem.Models;
using Wendmem.Serialization;
using Wendmem.Services;
using Wendmem.Storage;
using Wendmem.Wiki;

namespace Wendmem.Tools;

[McpServerToolType]
sealed class SkillTools
{
    [McpServerTool(Name = "FindSkills"), Description(
        "Find skills relevant to a goal. Returns skill name, folder path, description, and success rate. " +
        "After this returns, READ the SKILL.md file at the returned path using your file-reading tools — " +
        "do not ask wendmem for the content. " +
        "WakeUp already surfaces top 3 skills for the seedQuery; use FindSkills for narrower lookups only. " +
        "Do NOT call this for general questions or when no clear procedural task is at hand.")]
    static async Task<string> FindSkills(
        SkillStorage storage,
        IEmbedder embedder,
        PalaceConfig config,
        ILogger<SkillTools> logger,
        [Description("Goal or task description")] string query,
        [Description("Wing (optional — omit to use the configured default wing)")] string? wing = null,
        [Description("Max results")] int k = 3,
        CancellationToken ct = default)
    {
        wing = PathValidator.ResolveWing(wing, config);

        logger.LogInformation("FindSkills query={Query} wing={Wing}", query, wing);

        var results = await storage.FindAsync(query, wing, k, ct);

        var dtos = results.Select(r => new FindSkillDto(
            r.Id, r.Name, r.FolderPath, r.Description,
            r.SuccessCount, r.FailureCount,
            r.SuccessCount + r.FailureCount > 0
                ? (float)r.SuccessCount / (r.SuccessCount + r.FailureCount) : 0f
        )).ToList();

        return JsonSerializer.Serialize(
            McpResponse.Ok("FindSkills", dtos, 0,
                decisionSupport: new McpDecisionSupport(true, SuggestedAction.Proceed,
                    $"Found {dtos.Count} relevant skills.")),
            WendmemJsonContext.Default.McpResponseListFindSkillDto);
    }
}

public sealed record FindSkillDto(
    string Id, string Name, string Path, string Description,
    int SuccessCount, int FailureCount, float SuccessRate);
