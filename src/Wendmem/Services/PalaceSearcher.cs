using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Wendmem.Experiences;
using Wendmem.Models;
using Wendmem.Storage;
using Wendmem.Wiki.Json;
using Wendmem.Wiki.Models;

namespace Wendmem.Services;

sealed class PalaceSearcher(
    DrawerStorage storage,
    IEmbedder embedder,
    KnowledgeGraph kg,
    Wiki.WikiStorage wiki,
    EntityIndexService entityIndex,
    PalaceConfig config,
    IMemoryCache cache,
    LlmService llm,
    ILogger<PalaceSearcher> logger,
    Wiki.PendingUpdateService? pendingUpdateService = null,
    ActivityLog? activityLog = null,
    EpisodeStorage? episodeStorage = null,
    SkillStorage? skillStorage = null,
    ReflectionDraftStorage? reflectionDraftStorage = null)
{

    const int RecencyCount = 5;
    const int RelevanceCount = 10;
    // Hard character budget for total WakeUp output — driven by PalaceConfig.WakeUpCharBudget.
    const int TailMinBudget = 600;
    // L0 (synthesis) gets first claim on the budget. L1/L2 absorb what remains.
    // The hard-ceiling at the end cuts from the tail, preserving synthesis at the start.
    const int SynthesisGuaranteedMin = 1600;

    // How much weight the structured side-index overlap gets in re-ranking
    const float SideIndexBoostWeight = 0.15f;

    // MMR lambda — higher = more relevance, lower = more diversity
    const float MmrLambda = 0.7f;

    public async Task<WakeUpResult> WakeUpAsync(
        string? wing, string? seedQuery, CancellationToken ct = default)
    {
        var cacheKey = $"wakeup:{wing ?? ""}:{seedQuery ?? ""}";
        if (cache.TryGetValue(cacheKey, out WakeUpResult? cached))
            return cached;

        var result = await WakeUpCoreAsync(wing, seedQuery, ct);

        cache.Set(cacheKey, result, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30),
            Size = 1
        });
        return result;
    }

    async Task<WakeUpResult> WakeUpCoreAsync(
        string? wing, string? seedQuery, CancellationToken ct)
    {
        // L0: All synthesis drawers (always included, regardless of budget)
        var synthesis = await storage.SynthesisDrawersAsync(wing, ct);

        // Attempts: recent attempt summaries (fire alongside L1)
        var attemptsTask = storage.GetRecentAttemptsAsync(wing ?? string.Empty, 3, ct);

        // L1: Recent source drawers
        var recentSource = await storage.RecentSourceDrawersAsync(wing, RecencyCount, ct);

        // Await attempts now (ran in parallel with L1)
        var attempts = await attemptsTask;

        // L2: Semantic source drawers via MMR (over-fetch then diversify)
        var excludeIds = synthesis.Select(d => d.Id)
            .Concat(recentSource.Select(d => d.Id))
            .ToHashSet();
        IReadOnlyList<DrawerResult> semanticSource = [];
        if (!string.IsNullOrWhiteSpace(seedQuery))
        {
            var rawVec = await embedder.EmbedQueryAsync(seedQuery, ct);
            var overFetch = await storage.SearchAsync(rawVec, wing, null,
                k: RelevanceCount * 3, ct: ct);

            // MMR diversify to break hubness
            var candidates = overFetch
                .Where(r => !excludeIds.Contains(r.Drawer.Id))
                .Where(r => r.Score >= config.WakeUpMinL2Score)
                .ToList();

            // Structured side-index boost
            var boosted = await ApplySideIndexBoostAsync(
                candidates, seedQuery, ct);

            // MMR rerank to break hubness
            var mmrResult = DrawerStorage.MmrRerank(
                boosted, RelevanceCount, lambda: config.MmrLambda);

            // Apply recency/frequency boost to L2 semantic candidates
            semanticSource = ApplyDecayAndRecencyBoost(mmrResult);
        }

        var seen = new HashSet<string>();
        var sb = new StringBuilder();

        // Active KG triples (queried first for top placement)
        var facts = await kg.GetActiveTriplesAsync(wing, limit: 20, ct);
        if (facts.Count > 0)
        {
            sb.AppendLine("## Active Facts");
            foreach (var f in facts)
                sb.AppendLine(f.SourceRef is not null
                    ? $"{f.Subject} → {f.Predicate} → {f.Object} (ref:{f.SourceRef})"
                    : $"{f.Subject} → {f.Predicate} → {f.Object}");
            sb.AppendLine();
        }

        // Recent Attempts (only shown when attempts drawers exist)
        if (attempts.Count > 0)
        {
            sb.AppendLine("## Recent Attempts");
            foreach (var attempt in attempts)
            {
                sb.AppendLine($"[{attempt.Wing}/{attempt.Room}] " +
                              $"{attempt.MinedAt:yyyy-MM-dd HH:mm}");
                sb.AppendLine(attempt.Content);
                sb.AppendLine();
            }
        }

        // Wiki page index
        var pages = await wiki.IndexAsync(wing, ct);
        if (pages.Count > 0)
        {
            sb.AppendLine("## Pages Available");
            for (int i = 0; i < pages.Count && i < 30; i++)
                sb.AppendLine($"- {pages[i].Path} — {pages[i].Title}");
            sb.AppendLine();
        }

        // Pending reviews
        if (pendingUpdateService is not null && wing is not null)
        {
            var summary = await pendingUpdateService.SummaryAsync(wing, ct);
            if (summary.Count > 0)
            {
                sb.AppendLine("## Pending Reviews");
                foreach (var kv in summary.Take(10))
                    sb.AppendLine($"- {kv.Key} ({kv.Value} candidates)");
                sb.AppendLine();
            }
        }

        if (sb.Length > 0)
            sb.AppendLine("---");

        // L0 synthesis gets first claim on remaining budget after header.
        // L1/L2 only get what's left after synthesis is fully rendered.
        var charBudget = config.WakeUpCharBudget;
        var headerLen = sb.Length;

        var l0Budget = Math.Max(SynthesisGuaranteedMin, charBudget - headerLen - TailMinBudget);
        var l0Ids = new List<string>();
        var l0Sb = new StringBuilder();
        foreach (var d in synthesis)
        {
            if (!seen.Add(d.Id))
                continue;
            int entryLen = $"[{d.Wing}/{d.Room}] (synthesis)\n{d.Content}\n\n".Length;
            if (l0Sb.Length + entryLen > l0Budget)
                break;
            l0Sb.Append($"[{d.Wing}/{d.Room}] (synthesis)\n{d.Content}\n\n");
            l0Ids.Add(d.Id);
        }
        sb.Append(l0Sb);

        // Tail budget is whatever remains after header + L0 synthesis.
        var tailBudget = Math.Max(TailMinBudget, charBudget - headerLen - l0Sb.Length);

        var l1List = new List<Drawer>();
        var l1Chars = 0;
        foreach (var d in recentSource)
        {
            if (seen.Contains(d.Id))
                continue;
            int entryLen = $"[{d.Wing}/{d.Room}]\n{d.Content}\n\n".Length;
            if (l1Chars + entryLen > tailBudget)
                break;
            l1Chars += entryLen;
            l1List.Add(d);
        }

        var l2List = new List<DrawerResult>();
        var l2Chars = 0;
        foreach (var r in semanticSource)
        {
            if (seen.Contains(r.Drawer.Id))
                continue;
            int entryLen = $"[{r.Drawer.Wing}/{r.Drawer.Room}]\n{r.Drawer.Content}\n\n".Length;
            if (l1Chars + l2Chars + entryLen > tailBudget)
                break;
            l2Chars += entryLen;
            l2List.Add(r);
        }

        foreach (var d in l1List)
        { seen.Add(d.Id); sb.AppendLine($"[{d.Wing}/{d.Room}]"); sb.AppendLine(d.Content); sb.AppendLine(); }
        foreach (var r in l2List)
        { seen.Add(r.Drawer.Id); sb.AppendLine($"[{r.Drawer.Wing}/{r.Drawer.Room}] [{r.Regime}]"); sb.AppendLine(r.Drawer.Content); sb.AppendLine(); }

        storage.RecordAccess(l0Ids.Concat(l1List.Select(d => d.Id)).Concat(l2List.Select(r => r.Drawer.Id)).ToList());

        sb.AppendLine("---");

        List<WakeUpActivityDto> activity;
        if (activityLog is not null)
        {
            var entries = await activityLog.RecentAsync(wing, 5, ct);
            activity = entries.Select(e => new WakeUpActivityDto(
                e.Action, e.Target,
                e.Ts.ToUnixTimeSeconds(),
                e.Summary ?? "")).ToList();
        }
        else
        {
            var logEntries = await wiki.LogAsync(wing, limit: 5, ct);
            activity = new List<WakeUpActivityDto>(logEntries.Count);
            foreach (var e in logEntries)
                activity.Add(new WakeUpActivityDto(
                    e.EventType, e.PagePath,
                    e.OccurredAt.ToUnixTimeSeconds(),
                    e.Summary));
        }

        // Episodes from EpisodeStorage
        List<WakeUpEpisodeDto>? episodeDtos = null;
        if (episodeStorage is not null && !string.IsNullOrWhiteSpace(seedQuery))
        {
            try
            {
                var episodes = await episodeStorage.FindForWakeUpAsync(seedQuery, wing, 3, ct);
                if (episodes.Count > 0)
                    episodeDtos = episodes.Select(e => new WakeUpEpisodeDto(
                        e.Episode.Id, e.Episode.Goal, e.Episode.Outcome, e.Episode.NextTime)).ToList();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to load episodes for WakeUp");
            }
        }

        // Skills from SkillStorage
        List<WakeUpSkillDto>? skillDtos = null;
        if (skillStorage is not null && !string.IsNullOrWhiteSpace(seedQuery))
        {
            try
            {
                var skills = await skillStorage.FindAsync(seedQuery, wing, 3, ct);
                if (skills.Count > 0)
                    skillDtos = skills.Select(s => new WakeUpSkillDto(
                        s.Id, s.Name, s.FolderPath, s.Description,
                        (float)(s.SuccessCount + 1) / (s.SuccessCount + s.FailureCount + 2))).ToList();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to load skills for WakeUp");
            }
        }

        // Reflection drafts
        List<WakeUpReflectionDraftDto>? reflectionDraftDtos = null;
        if (reflectionDraftStorage is not null && wing is not null)
        {
            try
            {
                var pendingDrafts = await reflectionDraftStorage.ListPendingAsync(wing, 5, ct);
                if (pendingDrafts.Count > 0)
                    reflectionDraftDtos = pendingDrafts.Select(d => new WakeUpReflectionDraftDto(
                        d.Id, d.SuggestedPath, d.SuggestedTitle, d.Question)).ToList();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to load reflection drafts for WakeUp");
            }
        }

        var tail = new WakeUpActivityTailDto(activity, episodeDtos, skillDtos, reflectionDraftDtos);
        sb.AppendLine(JsonSerializer.Serialize(tail, WendmemWikiJsonContext.Default.WakeUpActivityTailDto));
        sb.AppendLine("---");
        sb.AppendLine("Next steps: read relevant pages with WikiRead. Recent Attempts show what was " +
                       "already tried — do not repeat a failed approach, and treat any 'failure' " +
                       "guidance as a hard constraint. For exact symbols use GrepExact; for concepts " +
                       "use SearchMemories. Verify any citation with GetDrawer before relying on it.");

        var content = sb.Length > 0 ? sb.ToString().TrimEnd() : "(no context available)";

        // Hard ceiling: ensure total output does not exceed the configured budget.
        // Truncation cuts from the end (tail JSON / next-steps), preserving L0 synthesis
        // at the start. L1/L2 also get cut before L0 since they sit after synthesis.
        if (content.Length > charBudget)
            content = content[..charBudget];

        return new WakeUpResult(content, synthesis.Count, l1List.Count, l2List.Count);
    }

    public async Task<string> WakeUpFullAsync(
        string? wing, string? seedQuery, CancellationToken ct)
    {
        // L0: All synthesis drawers
        var synthesis = await storage.SynthesisDrawersAsync(wing, ct);

        // Attempts: recent attempt summaries (fire alongside L1)
        var attemptsTask = storage.GetRecentAttemptsAsync(wing ?? string.Empty, 3, ct);

        // L1: Recent source drawers
        var recentSource = await storage.RecentSourceDrawersAsync(wing, RecencyCount, ct);

        // Await attempts now (ran in parallel with L1)
        var attempts = await attemptsTask;

        // L2: Semantic source drawers (excluding L0+L1 ids)
        var excludeIds = synthesis.Select(d => d.Id)
            .Concat(recentSource.Select(d => d.Id))
            .ToHashSet();
        IReadOnlyList<DrawerResult> semanticSource = [];
        if (!string.IsNullOrWhiteSpace(seedQuery))
        {
            var vec = await embedder.EmbedQueryAsync(seedQuery, ct);
            var overFetch = await storage.CosinSearchSourceAsync(vec, wing, RelevanceCount * 3, excludeIds, ct);

            // Filter out low-relevance results to prevent hard distractors from entering context
            var filtered = overFetch
                .Where(r => r.Score >= config.WakeUpMinL2Score)
                .ToList();

            // Apply side-index boost then MMR diversify
            var boosted = await ApplySideIndexBoostAsync(filtered, seedQuery, ct);
            // Apply recency/frequency boost before final L2 selection
            semanticSource = ApplyDecayAndRecencyBoost(
                DrawerStorage.MmrRerank(boosted, RelevanceCount, lambda: config.MmrLambda));
        }

        var seen = new HashSet<string>();
        var sb = new StringBuilder();

        var facts = await kg.GetActiveTriplesAsync(wing, limit: 20, ct);
        if (facts.Count > 0)
        {
            sb.AppendLine("## Active Facts");
            foreach (var f in facts)
                sb.AppendLine(f.SourceRef is not null
                    ? $"{f.Subject} → {f.Predicate} → {f.Object} (ref:{f.SourceRef})"
                    : $"{f.Subject} → {f.Predicate} → {f.Object}");
            sb.AppendLine();
        }

        // Recent Attempts (only shown when attempts drawers exist)
        if (attempts.Count > 0)
        {
            sb.AppendLine("## Recent Attempts");
            foreach (var attempt in attempts)
            {
                sb.AppendLine($"[{attempt.Wing}/{attempt.Room}] " +
                              $"{attempt.MinedAt:yyyy-MM-dd HH:mm}");
                sb.AppendLine(attempt.Content);
                sb.AppendLine();
            }
        }

        var pages = await wiki.IndexAsync(wing, ct);
        if (pages.Count > 0)
        {
            sb.AppendLine("## Pages Available");
            for (int i = 0; i < pages.Count && i < 30; i++)
                sb.AppendLine($"- {pages[i].Path} — {pages[i].Title}");
            sb.AppendLine();
        }

        if (pendingUpdateService is not null && wing is not null)
        {
            var summary = await pendingUpdateService.SummaryAsync(wing, ct);
            if (summary.Count > 0)
            {
                sb.AppendLine("## Pending Reviews");
                foreach (var kv in summary.Take(10))
                    sb.AppendLine($"- {kv.Key} ({kv.Value} candidates)");
                sb.AppendLine();
            }
        }

        if (sb.Length > 0)
            sb.AppendLine("---");

        var charBudget = config.WakeUpCharBudget;
        var headerLen = sb.Length;

        var l0Budget = Math.Max(SynthesisGuaranteedMin, charBudget - headerLen - TailMinBudget);
        var l0Ids = new List<string>();
        var l0Sb = new StringBuilder();
        foreach (var d in synthesis)
        {
            if (!seen.Add(d.Id))
                continue;
            int entryLen = $"[{d.Wing}/{d.Room}] (synthesis)\n{d.Content}\n\n".Length;
            if (l0Sb.Length + entryLen > l0Budget)
                break;
            l0Sb.Append($"[{d.Wing}/{d.Room}] (synthesis)\n{d.Content}\n\n");
            l0Ids.Add(d.Id);
        }
        sb.Append(l0Sb);

        var tailBudget = Math.Max(TailMinBudget, charBudget - headerLen - l0Sb.Length);

        var l1List = new List<Drawer>();
        var l1Chars = 0;
        foreach (var d in recentSource)
        {
            if (seen.Contains(d.Id))
                continue;
            int entryLen = $"[{d.Wing}/{d.Room}]\n{d.Content}\n\n".Length;
            if (l1Chars + entryLen > tailBudget)
                break;
            l1Chars += entryLen;
            l1List.Add(d);
        }

        var l2List = new List<DrawerResult>();
        var l2Chars = 0;
        foreach (var r in semanticSource)
        {
            if (seen.Contains(r.Drawer.Id))
                continue;
            int entryLen = $"[{r.Drawer.Wing}/{r.Drawer.Room}]\n{r.Drawer.Content}\n\n".Length;
            if (l1Chars + l2Chars + entryLen > tailBudget)
                break;
            l2Chars += entryLen;
            l2List.Add(r);
        }

        foreach (var d in l1List)
        { seen.Add(d.Id); sb.AppendLine($"[{d.Wing}/{d.Room}]"); sb.AppendLine(d.Content); sb.AppendLine(); }
        foreach (var r in l2List)
        { seen.Add(r.Drawer.Id); sb.AppendLine($"[{r.Drawer.Wing}/{r.Drawer.Room}]"); sb.AppendLine(r.Drawer.Content); sb.AppendLine(); }

        storage.RecordAccess(l0Ids.Concat(l1List.Select(d => d.Id)).Concat(l2List.Select(r => r.Drawer.Id)).ToList());

        var result = sb.Length > 0 ? sb.ToString().TrimEnd() : "(no context available)";

        if (result.Length > charBudget)
            result = result[..charBudget];

        return result;
    }

    /// <summary>
    /// Search memories with KG enrichment. After hybrid retrieval,
    /// queries the KG for entities mentioned in the query and uses predicate
    /// structure to re-rank candidates that mention the same entity relationships.
    /// </summary>
    public async Task<IReadOnlyList<DrawerResult>> SearchMemoriesAsync(
        string query, string? wing, string? room, int k, CancellationToken ct)
    {
        IReadOnlyList<DrawerResult> hybrid;

        if (config.EnableQueryExpansion)
        {
            var variants = await ExpandQueryAsync(query, ct);
            var queries = new List<string> { query };
            queries.AddRange(variants);

            var tasks = queries.Select(q => Task.Run(async () =>
            {
                var qVec = await embedder.EmbedQueryAsync(q, ct);
                return await storage.HybridSearchAsync(q, qVec, wing, room, k, ct, config.MmrLambda, includeKgChannel: true);
            }, ct)).ToList();

            var allResults = await Task.WhenAll(tasks);

            var bestById = new Dictionary<string, DrawerResult>();
            foreach (var results in allResults)
            {
                foreach (var r in results)
                {
                    if (!bestById.TryGetValue(r.Drawer.Id, out var existing) || r.Score > existing.Score)
                        bestById[r.Drawer.Id] = r;
                }
            }

            hybrid = bestById.Values.OrderByDescending(r => r.Score).Take(k).ToList();
        }
        else
        {
            var vec = await embedder.EmbedQueryAsync(query, ct);
            hybrid = await storage.HybridSearchAsync(query, vec, wing, room, k, ct, config.MmrLambda, includeKgChannel: true);
        }

        // Structured side-index boost
        var boosted = await ApplySideIndexBoostAsync(hybrid, query, ct);

        // KG predicate re-ranking
        var kgBoosted = await ApplyKgPredicateBoostAsync(boosted, query, ct);

        // SSGM conflict governance — ensure multi-topic coverage
        var governed = ApplyConflictGovernance(kgBoosted, k);

        // Backlink-driven recall boost.
        // Drawers cited by well-connected wiki pages get a topical-centrality boost.
        var backlinked = await ApplyBacklinkBoostAsync(governed, wing, ct);

        // HybridSearchAsync already applies storage-level decay/recency handling
        // and records access for the returned results. Do not apply a second
        // PalaceSearcher-level RF-Mem boost or RecordAccess pass here.
        return backlinked;
    }

    private async Task<IReadOnlyList<string>> ExpandQueryAsync(string query, CancellationToken ct)
    {
        var variants = config.QueryExpansionVariants;

        var prompt =
            $"You are a search query expansion assistant. Given a search query, " +
            $"return exactly {variants} alternative phrasings that cover the same " +
            "intent using different terminology. Respond ONLY with a JSON array of " +
            "strings, no markdown, no preamble, no explanation. " +
            "Example output: [\"alternative one\", \"alternative two\"]\n\n" +
            $"Query: {query}";

        try
        {
            var content = await llm.CompleteAsync(prompt, ct);

            // Strip ```json fences if present.
            if (content.Contains("```"))
            {
                var start = content.IndexOf('[');
                var end = content.LastIndexOf(']');
                if (start >= 0 && end > start)
                    content = content[start..(end + 1)];
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                logger.LogDebug("Query expansion skipped: {Reason}", "empty content");
                return [];
            }

            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in doc.RootElement.EnumerateArray())
                    list.Add(item.GetString() ?? "");

                if (list.Count == 0)
                {
                    logger.LogDebug("Query expansion skipped: {Reason}", "empty array");
                    return [];
                }

                return list;
            }

            logger.LogDebug("Query expansion skipped: {Reason}", "response was not a JSON array");
            return [];
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Query expansion skipped: {Reason}", "exception");
            return [];
        }
    }

    const float BacklinkBoostWeight = 0.12f;
    const float BacklinkMaxBoost = 0.25f;

    /// <summary>
    /// Re-ranks candidates by boosting drawers that are cited by wiki pages
    /// with many backlinks. Well-connected wiki pages indicate topical
    /// centrality - their cited drawers are more likely to be authoritative
    /// for that topic.
    /// </summary>
    async Task<IReadOnlyList<DrawerResult>> ApplyBacklinkBoostAsync(
        IReadOnlyList<DrawerResult> candidates, string? wing, CancellationToken ct)
    {
        if (candidates.Count == 0)
            return candidates;

        var drawerIds = candidates.Select(c => c.Drawer.Id).ToList();
        var blCounts = await wiki.GetCitationBacklinkCountsAsync(drawerIds, wing, ct);

        if (blCounts.Count == 0)
            return candidates;

        return candidates.Select(c =>
        {
            if (blCounts.TryGetValue(c.Drawer.Id, out var blCount) && blCount > 0)
            {
                var boost = MathF.Min(BacklinkMaxBoost,
                    BacklinkBoostWeight * MathF.Log(blCount + 1));
                return c with { Score = c.Score + boost };
            }
            return c;
        }).OrderByDescending(c => c.Score).ToList();
    }

    /// <summary>
    /// Re-ranks candidates by boosting those that share structured tokens
    /// (numbers, qualified names, arities) with the query.
    /// This breaks the "takes 3 args" ≈ "takes 5 args" confusion.
    /// </summary>
    async Task<IReadOnlyList<DrawerResult>> ApplySideIndexBoostAsync(
        IReadOnlyList<DrawerResult> candidates, string query, CancellationToken ct)
    {
        if (candidates.Count == 0)
            return candidates;

        var ids = candidates.Select(c => c.Drawer.Id).ToList();
        var overlap = await entityIndex.OverlapBoostAsync(ids, query, ct);

        if (overlap.Count == 0)
            return candidates;

        return candidates.Select(c =>
        {
            if (overlap.TryGetValue(c.Drawer.Id, out var boost))
            {
                return c with { Score = c.Score + SideIndexBoostWeight * boost };
            }
            return c;
        }).OrderByDescending(c => c.Score).ToList();
    }

    /// <summary>
    /// Re-ranks candidates using KG predicate structure. Drawers whose
    /// content mentions the same entity-relationship pair found in the KG
    /// get a boost. This breaks "A calls B" ≈ "B calls A" confusion by
    /// verifying predicate direction.
    /// </summary>
    async Task<IReadOnlyList<DrawerResult>> ApplyKgPredicateBoostAsync(
        IReadOnlyList<DrawerResult> candidates, string query, CancellationToken ct)
    {
        if (candidates.Count == 0)
            return candidates;

        var entityNames = await kg.MatchEntitiesInTextAsync(query, limit: 5, ct);
        if (entityNames.Count == 0)
            return candidates;

        var entityFacts = await kg.LookupEntitiesForQueryAsync(entityNames, limitPerEntity: 10, ct);
        if (entityFacts.Count == 0)
            return candidates;

        var predicatePatterns = entityFacts
            .SelectMany(ef => ef.Triples)
            .Select(t => new PredicatePattern(t.Subject, t.Predicate, t.Object))
            .Distinct()
            .ToList();

        return candidates.Select(c =>
        {
            var content = c.Drawer.Content;
            int matchCount = 0;
            foreach (var pattern in predicatePatterns)
            {
                if (ContainsPredicateInDirection(content, pattern))
                    matchCount++;
            }

            if (matchCount > 0)
                return c with { Score = c.Score + 0.1f * matchCount };

            return c;
        }).OrderByDescending(c => c.Score).ToList();
    }

    readonly record struct PredicatePattern(string Subject, string Predicate, string Object);

    static bool ContainsPredicateInDirection(string content, PredicatePattern pattern)
    {
        var subjectIdx = content.IndexOf(pattern.Subject, StringComparison.OrdinalIgnoreCase);
        if (subjectIdx < 0)
            return false;

        var predicateIdx = IndexOfPredicate(content, pattern.Predicate, subjectIdx + pattern.Subject.Length);
        if (predicateIdx < 0)
            return false;

        var objectIdx = content.IndexOf(pattern.Object, predicateIdx + pattern.Predicate.Length,
            StringComparison.OrdinalIgnoreCase);
        return objectIdx >= 0;
    }

    static int IndexOfPredicate(string content, string predicate, int startIndex)
    {
        var rawIdx = content.IndexOf(predicate, startIndex, StringComparison.OrdinalIgnoreCase);
        if (rawIdx >= 0)
            return rawIdx;

        var normalized = predicate.Replace('_', ' ');
        if (!string.Equals(normalized, predicate, StringComparison.Ordinal))
            return content.IndexOf(normalized, startIndex, StringComparison.OrdinalIgnoreCase);

        return -1;
    }

    /// <summary>
    /// Detects when search results span conflicting concept clusters
    /// and ensures balanced representation from each cluster.
    /// Mode is read from config: "off" skips governance, "balanced" allocates
    /// proportional slots, "aggressive" forces equal slots per cluster.
    /// </summary>
    IReadOnlyList<DrawerResult> ApplyConflictGovernance(
        IReadOnlyList<DrawerResult> candidates, int k)
    {
        if (config.ConflictGovernance == "off" || candidates.Count <= k)
            return candidates;

        var groups = new Dictionary<int?, List<DrawerResult>>();
        foreach (var c in candidates)
        {
            var key = c.Drawer.ClusterId;
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }
            list.Add(c);
        }

        if (groups.Count <= 1)
            return candidates.Take(k).ToList();

        var rankedGroups = groups
            .OrderByDescending(g => g.Value.Max(r => r.Score))
            .ToList();

        var result = new List<DrawerResult>();
        int slotsPerGroup = config.ConflictGovernance == "aggressive"
            ? Math.Max(1, k / rankedGroups.Count)              // equal slots
            : Math.Max(1, (int)MathF.Ceiling((float)k / rankedGroups.Count)); // proportional
        int remaining = k;

        foreach (var (_, groupItems) in rankedGroups)
        {
            int take = Math.Min(Math.Min(slotsPerGroup, remaining), groupItems.Count);
            result.AddRange(groupItems.OrderByDescending(r => r.Score).Take(take));
            remaining -= take;
            if (remaining <= 0)
                break;
        }

        if (result.Count < k)
        {
            var usedIds = new HashSet<string>(result.Select(r => r.Drawer.Id));
            var extras = candidates
                .Where(c => !usedIds.Contains(c.Drawer.Id))
                .OrderByDescending(c => c.Score)
                .Take(k - result.Count);
            result.AddRange(extras);
        }

        return result.OrderByDescending(r => r.Score).ToList();
    }

    /// <summary>
    /// Applies a recency/access-frequency boost to search results.
    /// Drawers accessed recently get a small boost; stale drawers (not accessed
    /// in more than DecayStaleDays) get a decay penalty. Uses configurable
    /// half-life for the recency exponential.
    /// </summary>
    IReadOnlyList<DrawerResult> ApplyDecayAndRecencyBoost(IReadOnlyList<DrawerResult> candidates)
    {
        if (candidates.Count == 0)
            return candidates;

        var now = DateTimeOffset.UtcNow;
        float staleAfterDays = config.DecayStaleDays;
        float halfLife = config.RecencyHalfLifeDays;
        const float maxBoost = 0.15f;
        float maxDecay = config.DecayMaxPenalty;

        return candidates.Select(c =>
        {
            var drawer = c.Drawer;

            // Unaccessed drawers: mild decay based on age since mining
            if (drawer.LastAccessedAt is null)
            {
                var ageDays = (float)(now - drawer.MinedAt).TotalDays;
                if (ageDays > staleAfterDays)
                {
                    var decay = Math.Min(maxDecay, (ageDays - staleAfterDays) / staleAfterDays * 0.1f);
                    return c with { Score = c.Score - decay };
                }
                return c;
            }

            // Recently accessed drawers: recency boost
            var daysSinceAccess = (float)(now - drawer.LastAccessedAt.Value).TotalDays;

            // Frequency boost: more accesses = higher boost
            float freqBoost = Math.Min(maxBoost, drawer.AccessCount * 0.02f);

            // Recency boost: exponentially decay the boost over configured half-life
            float recencyBoost = maxBoost * MathF.Exp(-daysSinceAccess / halfLife);

            return c with { Score = c.Score + freqBoost + recencyBoost };
        })
        .OrderByDescending(c => c.Score)
        .ToList();
    }
}
