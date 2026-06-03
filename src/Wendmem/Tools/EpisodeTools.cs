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
sealed class EpisodeTools
{
    [McpServerTool(Name = "RecordEpisode"), Description(
        "Record an episode at session end: goal, outcome, and what should be done differently next time. " +
        "Call BEFORE Distill when a non-trivial task is complete (success or failure). " +
        "Do NOT call for: trivial Q&A, single-tool lookups, sessions without a clear goal. " +
        "WakeUp surfaces past episodes matching the seedQuery — call FindEpisodes only for narrower lookups.")]
    static async Task<string> RecordEpisode(
        EpisodeStorage storage,
        IEmbedder embedder,
        PalaceConfig config,
        ILogger<EpisodeTools> logger,
        [Description("Wing namespace (e.g. 'wendmem', 'myproject')")] string wing,
        [Description("What the agent was trying to accomplish (1-2 sentences)")] string goal,
        [Description("Approach taken (1 paragraph)")] string plan,
        [Description("'success' | 'partial' | 'failure'")] string outcome,
        [Description("What helped — be specific, name tools, patterns, sources")] string whatWorked,
        [Description("What did NOT work — be specific")] string whatFailed,
        [Description("Concrete guidance to a future agent facing a similar task")] string nextTime,
        [Description("Comma-separated drawer IDs touched (optional)")] string? drawerRefs = null,
        [Description("Comma-separated skill IDs used (optional)")] string? skillRefs = null,
        CancellationToken ct = default)
    {
        // Validate and normalize inputs
        wing = PathValidator.ResolveWing(wing, config);

        if (string.IsNullOrWhiteSpace(goal))
            return JsonSerializer.Serialize(
                McpResponse.Fail<RecordEpisodeResult>("RecordEpisode", "validation", "goal is required"),
                WendmemJsonContext.Default.McpResponseRecordEpisodeResult);

        if (outcome is not ("success" or "partial" or "failure"))
            return JsonSerializer.Serialize(
                McpResponse.Fail<RecordEpisodeResult>("RecordEpisode", "validation",
                    $"outcome must be 'success', 'partial', or 'failure', got '{outcome}'"),
                WendmemJsonContext.Default.McpResponseRecordEpisodeResult);

        logger.LogInformation("RecordEpisode wing={Wing} goal={Goal} outcome={Outcome}", wing,
            goal.Length > 60 ? goal[..60] + "..." : goal, outcome);

        var episode = await storage.RecordAsync(
            wing, goal, plan, outcome, whatWorked, whatFailed, nextTime,
            drawerRefs, skillRefs, null, ct);

        var result = new RecordEpisodeResult(episode.Id, episode.Wing, episode.Outcome, episode.NextTime);

        return JsonSerializer.Serialize(
            McpResponse.Ok("RecordEpisode", result, 0,
                decisionSupport: new McpDecisionSupport(true, SuggestedAction.Proceed,
                    $"Episode recorded: {episode.Id} ({outcome}). WakeUp will surface this in future sessions.")),
            WendmemJsonContext.Default.McpResponseRecordEpisodeResult);
    }

    [McpServerTool(Name = "FindEpisodes"), Description(
        "Find past episodes relevant to the current goal. " +
        "WakeUp already returns top 3 episodes for the seedQuery — use this only when scope is narrower " +
        "or to filter by outcome. " +
        "Do NOT call this in casual conversation or when WakeUp's episodes field already has what you need.")]
    static async Task<string> FindEpisodes(
        EpisodeStorage storage,
        IEmbedder embedder,
        PalaceConfig config,
        ILogger<EpisodeTools> logger,
        [Description("Current goal or task description")] string query,
        [Description("Wing (optional — omit to use the configured default wing)")] string? wing = null,
        [Description("Filter outcome: 'success' | 'failure' | 'any'")] string outcome = "any",
        [Description("Max results")] int k = 3,
        CancellationToken ct = default)
    {
        wing = PathValidator.ResolveWing(wing, config);

        logger.LogInformation("FindEpisodes query={Query} wing={Wing} outcome={Outcome}", query, wing, outcome);

        var results = await storage.FindAsync(query, wing, outcome, k, ct);

        var dtos = results.Select(r => new EpisodeDto(
            r.Episode.Id, r.Episode.Goal, r.Episode.Outcome,
            r.Episode.NextTime, r.Episode.WhatWorked, r.Episode.WhatFailed,
            r.Score)).ToList();

        return JsonSerializer.Serialize(
            McpResponse.Ok("FindEpisodes", dtos, 0,
                decisionSupport: new McpDecisionSupport(true, SuggestedAction.Proceed,
                    $"Found {dtos.Count} relevant episodes.")),
            WendmemJsonContext.Default.McpResponseListEpisodeDto);
    }
}

public sealed record RecordEpisodeResult(string Id, string Wing, string Outcome, string? NextTime);
public sealed record EpisodeDto(
    string Id, string Goal, string Outcome,
    string? NextTime, string? WhatWorked, string? WhatFailed, float Score);
