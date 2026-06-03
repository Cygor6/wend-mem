using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wendmem.Experiences;
using Wendmem.Storage;
using Wendmem.Wiki;

namespace Wendmem.Services;

internal sealed class ReflectionService
{
    readonly DrawerStorage _drawers;
    readonly KnowledgeGraph _kg;
    readonly WikiStorage _wiki;
    readonly ReflectionDraftStorage _drafts;
    readonly IEmbedder _embedder;
    readonly LlmService _llm;
    readonly ILogger<ReflectionService> _logger;

    public ReflectionService(
        DrawerStorage drawers,
        KnowledgeGraph kg,
        WikiStorage wiki,
        ReflectionDraftStorage drafts,
        IEmbedder embedder,
        LlmService llm,
        ILogger<ReflectionService> logger)
    {
        _drawers = drawers;
        _kg = kg;
        _wiki = wiki;
        _drafts = drafts;
        _embedder = embedder;
        _llm = llm;
        _logger = logger;
    }

    public async Task<ReflectionResult> ReflectAsync(
        string wing, int lookback, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 1. Load recent representative drawers
        var recentDrawers = await _drawers.RecentSourceDrawersAsync(wing, lookback, ct);
        if (recentDrawers.Count < 5)
        {
            _logger.LogInformation("Reflection skipped: only {Count} recent drawers (need ≥5)", recentDrawers.Count);
            return new ReflectionResult(wing, 0, []);
        }

        // 2. Compose numbered snippets for LLM
        var snippets = recentDrawers
            .Select((d, i) => $"{i + 1}. [{d.Id[..8]}] {Truncate(d.Content, 200)}")
            .ToList();
        var snippetText = string.Join("\n", snippets);

        var questionPrompt = $$"""
                Given these recent observations from project '{{wing}}':

                {{snippetText}}

                What are the 3 most salient high-level questions a reflective observer would ask
                about patterns, decisions, or gaps? Respond ONLY with JSON:
                {"questions": ["question1", "question2", "question3"]}
                """;

        var questionsRaw = await _llm.CompleteAsync(questionPrompt, ct);
        var questions = ParseQuestions(questionsRaw);
        if (questions.Count == 0)
        {
            _logger.LogDebug("Reflection: LLM returned no questions");
            return new ReflectionResult(wing, recentDrawers.Count, []);
        }

        // 4. Per question: retrieve relevant drawers, synthesize wiki draft
        var draftResults = new List<ReflectionDraft>();
        foreach (var question in questions.Take(3))
        {
            var queryVec = await _embedder.EmbedQueryAsync(question, ct);
            var searchResults = await _drawers.HybridSearchAsync(
                question, queryVec, wing, null, k: 8, ct: ct);

            var citedIds = searchResults.Select(r => r.Drawer.Id).ToList();
            var citedContent = searchResults
                .Select((r, i) => $"[{i + 1}] (id:{r.Drawer.Id}) {Truncate(r.Drawer.Content, 300)}")
                .ToList();
            var citedText = string.Join("\n", citedContent);

            var citedIdsJoined = string.Join(",", citedIds.Take(5));
            var synthesizePrompt = $$"""
                Synthesize a 200-400 word wiki page answering this question about project '{{wing}}':

                Question: {{question}}

                Evidence:
                {{citedText}}

                Respond ONLY with JSON:
                {"path": "suggested-wiki-path", "title": "Suggested Title", "content": "...", "citations": ["{{citedIdsJoined}}"]}
                """;

            var draftRaw = await _llm.CompleteAsync(synthesizePrompt, ct);
            var draft = ParseDraft(draftRaw, question);
            if (draft is not null)
            {
                var saved = await _drafts.SaveDraftAsync(
                    wing, question, draft.Value.Path, draft.Value.Title,
                    draft.Value.Content, string.Join(",", draft.Value.Citations), ct);
                draftResults.Add(saved);
            }
        }

        _logger.LogInformation("Reflection: {Drafts} drafts from {Drawers} drawers in {Ms}ms",
            draftResults.Count, recentDrawers.Count, sw.ElapsedMilliseconds);
        return new ReflectionResult(wing, recentDrawers.Count, draftResults);
    }

    /// <summary>
    /// Accept a draft by writing it to the wiki and marking it accepted.
    /// </summary>
    public async Task<bool> AcceptDraftAsync(string draftId, CancellationToken ct)
    {
        var draft = await _drafts.GetByIdAsync(draftId, ct);
        if (draft is null)
            return false;

        var citations = draft.Citations.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        await _wiki.WriteAsync(draft.SuggestedPath, draft.Wing, draft.SuggestedTitle,
            draft.DraftContent, citations, "reflection", ct);

        await _drafts.UpdateStatusAsync(draftId, "accepted", ct);
        _logger.LogInformation("Accepted reflection draft {Id} → {Path}", draftId, draft.SuggestedPath);
        return true;
    }

    static List<string> ParseQuestions(string raw)
    {
        try
        {
            var json = ExtractJson(raw);
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement.GetProperty("questions");
            var result = new List<string>();
            foreach (var el in arr.EnumerateArray())
                result.Add(el.GetString() ?? "");
            return result.Where(q => !string.IsNullOrWhiteSpace(q)).ToList();
        }
        catch { return []; }
    }

    static (string Path, string Title, string Content, List<string> Citations)? ParseDraft(string raw, string question)
    {
        try
        {
            var json = ExtractJson(raw);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var path = root.GetProperty("path").GetString() ?? "reflection/untitled";
            var title = root.GetProperty("title").GetString() ?? "Reflection";
            var content = root.GetProperty("content").GetString() ?? "";
            var citations = new List<string>();
            if (root.TryGetProperty("citations", out var citArr))
                foreach (var el in citArr.EnumerateArray())
                    citations.Add(el.GetString() ?? "");
            return (path, title, content, citations);
        }
        catch { return null; }
    }

    static string ExtractJson(string text)
    {
        // Try to find JSON in markdown code fences
        var fenceStart = text.IndexOf("```json", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            fenceStart = text.IndexOf('\n', fenceStart) + 1;
            var fenceEnd = text.IndexOf("```", fenceStart, StringComparison.Ordinal);
            if (fenceEnd > fenceStart)
                return text[fenceStart..fenceEnd].Trim();
        }
        // Try raw braces
        var braceStart = text.IndexOf('{');
        var braceEnd = text.LastIndexOf('}');
        if (braceStart >= 0 && braceEnd > braceStart)
            return text[braceStart..(braceEnd + 1)];
        return text;
    }

    static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";
}

internal sealed record ReflectionResult(string Wing, int DrawerCount, List<ReflectionDraft> Drafts);
