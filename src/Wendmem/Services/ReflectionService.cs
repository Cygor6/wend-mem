using System.Numerics.Tensors;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wendmem.Experiences;
using Wendmem.Storage;
using Wendmem.Wiki;

namespace Wendmem.Services;

internal sealed class ReflectionService(
    DrawerStorage drawers,
    KnowledgeGraph kg,
    WikiStorage wiki,
    ReflectionDraftStorage drafts,
    IEmbedder embedder,
    LlmService llm,
    ILogger<ReflectionService> logger)
{
    // Questions at or above this cosine similarity to an earlier draft question
    // (or another question in the same run) are skipped as duplicates.
    // 0.90 catches paraphrases while letting genuinely new angles through.
    // Move to PalaceConfig if you want it configurable.
    const float DuplicateQuestionThreshold = 0.90f;
    const int DedupLookbackQuestions = 50;

    // Triples extracted from an accepted draft are machine-extracted from
    // human-approved content — high trust, but not hand-entered.
    const double ExtractedTripleConfidence = 0.8;
    const int MaxTriplesPerDraft = 10;

    public async Task<ReflectionResult> ReflectAsync(
        string wing, int lookback, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 1. Load recent representative drawers
        var recentDrawers = await drawers.RecentSourceDrawersAsync(wing, lookback, ct);
        if (recentDrawers.Count < 5)
        {
            logger.LogInformation("Reflection skipped: only {Count} recent drawers (need ≥5)", recentDrawers.Count);
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

        string questionsRaw;
        try
        {
            questionsRaw = await llm.CompleteAsync(questionPrompt, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reflection: question generation failed (LLM unavailable?)");
            return new ReflectionResult(wing, recentDrawers.Count, []);
        }

        // 3. Parse questions
        var candidates = ParseQuestions(questionsRaw).Take(3).ToList();
        if (candidates.Count == 0)
        {
            logger.LogDebug("Reflection: LLM returned no questions");
            return new ReflectionResult(wing, recentDrawers.Count, []);
        }

        // 4. Dedup guard: embed candidate questions and earlier draft questions
        //    so near-duplicates can be skipped before the expensive synthesis.
        //    Both sides use document embeddings (same space). Degrades
        //    gracefully: if storage or embedding fails, reflection proceeds
        //    without the guard.
        IReadOnlyList<string> existingQuestions = [];
        IReadOnlyList<float[]> existingVecs = [];
        IReadOnlyList<float[]> candidateVecs = [];
        try
        {
            existingQuestions = await drafts.RecentQuestionsAsync(wing, DedupLookbackQuestions, ct);
            if (existingQuestions.Count > 0 || candidates.Count > 1)
            {
                candidateVecs = await embedder.EmbedDocumentBatchAsync(candidates, ct);
                if (existingQuestions.Count > 0)
                    existingVecs = await embedder.EmbedDocumentBatchAsync(existingQuestions.ToList(), ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Reflection: question dedup unavailable, proceeding without it");
            candidateVecs = [];
            existingVecs = [];
        }

        // 5. Per question: retrieve relevant drawers, synthesize wiki draft
        var draftResults = new List<ReflectionDraft>();
        var acceptedVecs = new List<float[]>();

        for (int qi = 0; qi < candidates.Count; qi++)
        {
            var question = candidates[qi];

            if (IsDuplicateQuestion(question, qi, candidateVecs,
                    existingQuestions, existingVecs, acceptedVecs, out var reason))
            {
                logger.LogInformation("Reflection: skipping duplicate question ({Reason}): {Question}",
                    reason, question);
                continue;
            }
            if (qi < candidateVecs.Count)
                acceptedVecs.Add(candidateVecs[qi]);

            try
            {
                var queryVec = await embedder.EmbedQueryAsync(question, ct);
                var searchResults = await drawers.HybridSearchAsync(
                    question, queryVec, wing, null, k: 8, ct: ct);

                var citedIds = searchResults.Select(r => r.Drawer.Id).ToList();
                var citedContent = searchResults
                    .Select((r, i) => $"[{i + 1}] (id:{r.Drawer.Id}) {Truncate(r.Drawer.Content, 300)}")
                    .ToList();
                var citedText = string.Join("\n", citedContent);

                var synthesizePrompt = $$"""
                    Synthesize a 200-400 word wiki page answering this question about project '{{wing}}':

                    Question: {{question}}

                    Evidence:
                    {{citedText}}

                    Respond ONLY with JSON. The "citations" array must contain ONLY the ids
                    (the id:... values above) of evidence you actually used — one id per
                    array element, e.g. ["a1b2c3d4e5f6a7b8", "c9d0e1f2a3b4c5d6"]:
                    {"path": "suggested-wiki-path", "title": "Suggested Title", "content": "...", "citations": ["<id>", "<id>"]}
                    """;

                var draftRaw = await llm.CompleteAsync(synthesizePrompt, ct);
                var draft = ParseDraft(draftRaw);
                if (draft is null)
                    continue;

                // Validate citations against the evidence actually shown to the LLM.
                // Hallucinated ids must not end up as wiki provenance. SelectMany on
                // ',' also tolerates the model returning one comma-joined string.
                var validIds = citedIds.ToHashSet(StringComparer.Ordinal);
                var citations = draft.Value.Citations
                    .SelectMany(c => c.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Where(validIds.Contains)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (citations.Count == 0)
                    citations = citedIds.Take(5).ToList();

                var saved = await drafts.SaveDraftAsync(
                    wing, question, draft.Value.Path, draft.Value.Title,
                    draft.Value.Content, string.Join(",", citations), ct);
                draftResults.Add(saved);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reflection: synthesis failed for question: {Question}", question);
            }
        }

        logger.LogInformation("Reflection: {Drafts} drafts from {Drawers} drawers in {Ms}ms",
            draftResults.Count, recentDrawers.Count, sw.ElapsedMilliseconds);

        return new ReflectionResult(wing, recentDrawers.Count, draftResults);
    }

    /// <summary>
    /// Accept a draft by writing it to the wiki and marking it accepted.
    /// Accepted drafts are the only human-verified artifact in the loop, so
    /// they are also distilled into knowledge-graph triples, feeding the
    /// predicate-aware active search channel.
    /// </summary>
    public async Task<bool> AcceptDraftAsync(string draftId, CancellationToken ct)
    {
        var draft = await drafts.GetByIdAsync(draftId, ct);
        if (draft is null)
            return false;

        var citations = draft.Citations.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        await wiki.WriteAsync(draft.SuggestedPath, draft.Wing, draft.SuggestedTitle,
            draft.DraftContent, citations, "reflection", ct);

        await drafts.UpdateStatusAsync(draftId, "accepted", ct);

        logger.LogInformation("Accepted reflection draft {Id} → {Path}", draftId, draft.SuggestedPath);

        await ExtractTriplesFromDraftAsync(draft, ct);

        return true;
    }

    /// <summary>
    /// LLM-extracts subject-predicate-object triples from an accepted draft
    /// into the knowledge graph. Steered toward the canonical predicate
    /// vocabulary so extraction doesn't reintroduce the predicate sparsity
    /// the aliases exist to prevent. Failures are logged but never fail the
    /// accept — the wiki write has already happened.
    /// </summary>
    async Task ExtractTriplesFromDraftAsync(ReflectionDraft draft, CancellationToken ct)
    {
        try
        {
            var predicateList = string.Join(", ", KnowledgeGraph.CanonicalPredicates);

            var extractPrompt = $$"""
                Extract factual subject-predicate-object triples from this wiki page
                about project '{{draft.Wing}}'.

                {{draft.DraftContent}}

                Rules:
                - Use ONLY these predicates when one fits: {{predicateList}}.
                  Otherwise use a short snake_case predicate.
                - Subjects and objects must be SHORT entity names (a tool, person,
                  project, organization, or concept) — never sentences.
                - Only include facts explicitly stated in the text.
                - At most {{MaxTriplesPerDraft}} triples. If there are none, return an empty array.

                Respond ONLY with JSON:
                {"triples": [{"subject": "...", "predicate": "...", "object": "..."}]}
                """;

            var raw = await llm.CompleteAsync(extractPrompt, ct);
            var triples = ParseTriples(raw);

            int added = 0;
            foreach (var (subject, predicate, obj) in triples.Take(MaxTriplesPerDraft))
            {
                if (!IsPlausibleTriple(subject, predicate, obj))
                    continue;

                var (_, conflict) = await kg.AddTripleAsync(
                    subject, predicate, obj,
                    confidence: ExtractedTripleConfidence,
                    sourceFile: draft.SuggestedPath,
                    sourceRef: $"reflection:{draft.Id}",
                    ct: ct);
                added++;

                // A conflict means an active triple with the same subject+predicate
                // but a different object exists — free staleness detection.
                if (conflict is not null)
                    logger.LogInformation("Reflection triple conflict: {Message}", conflict.Message);
            }

            if (added > 0)
                logger.LogInformation("Reflection: extracted {Count} triples from accepted draft {Id}",
                    added, draft.Id);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reflection: triple extraction failed for draft {Id} (wiki write already done)",
                draft.Id);
        }
    }

    static bool IsPlausibleTriple(string subject, string predicate, string obj)
        => subject.Length is > 0 and <= 100
           && obj.Length is > 0 and <= 100
           && predicate.Length is > 0 and <= 50
           && !string.Equals(subject.Trim(), obj.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A question is a duplicate if it is (case-insensitively) identical to an
    /// earlier draft question, semantically close to one, or semantically close
    /// to a question already accepted in this run. Embedding-based checks are
    /// only applied when vectors are available.
    /// </summary>
    static bool IsDuplicateQuestion(
        string question, int index,
        IReadOnlyList<float[]> candidateVecs,
        IReadOnlyList<string> existingQuestions,
        IReadOnlyList<float[]> existingVecs,
        List<float[]> acceptedVecs,
        out string reason)
    {
        foreach (var existing in existingQuestions)
        {
            if (string.Equals(existing.Trim(), question.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                reason = "identical to earlier draft";
                return true;
            }
        }

        if (index < candidateVecs.Count)
        {
            var vec = candidateVecs[index];

            foreach (var existingVec in existingVecs)
            {
                if (CosineSimilarity(vec, existingVec) >= DuplicateQuestionThreshold)
                {
                    reason = "semantically close to earlier draft";
                    return true;
                }
            }

            foreach (var acceptedVec in acceptedVecs)
            {
                if (CosineSimilarity(vec, acceptedVec) >= DuplicateQuestionThreshold)
                {
                    reason = "semantically close to another question this run";
                    return true;
                }
            }
        }

        reason = "";
        return false;
    }

    static float CosineSimilarity(float[] a, float[] b)
    {
        float denom = TensorPrimitives.Norm(a) * TensorPrimitives.Norm(b);
        return denom < 1e-9f ? 0f : TensorPrimitives.Dot(a, b) / denom;
    }

    static List<string> ParseQuestions(string raw)
    {
        try
        {
            var json = ExtractJson(raw);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Accept both {"questions": [...]} and a bare top-level array.
            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Array)
                arr = root;
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("questions", out var q))
                arr = q;
            else
                return [];

            var result = new List<string>();
            foreach (var el in arr.EnumerateArray())
                result.Add(el.GetString() ?? "");
            return result.Where(q => !string.IsNullOrWhiteSpace(q)).ToList();
        }
        catch { return []; }
    }

    static List<(string Subject, string Predicate, string Object)> ParseTriples(string raw)
    {
        try
        {
            var json = ExtractJson(raw);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Array)
                arr = root;
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("triples", out var t))
                arr = t;
            else
                return [];

            var list = new List<(string, string, string)>();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                    continue;
                var s = el.TryGetProperty("subject", out var sv) ? sv.GetString() : null;
                var p = el.TryGetProperty("predicate", out var pv) ? pv.GetString() : null;
                var o = el.TryGetProperty("object", out var ov) ? ov.GetString() : null;
                if (!string.IsNullOrWhiteSpace(s) && !string.IsNullOrWhiteSpace(p) && !string.IsNullOrWhiteSpace(o))
                    list.Add((s.Trim(), p.Trim(), o.Trim()));
            }
            return list;
        }
        catch { return []; }
    }

    static (string Path, string Title, string Content, List<string> Citations)? ParseDraft(string raw)
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
            if (root.TryGetProperty("citations", out var citArr) && citArr.ValueKind == JsonValueKind.Array)
                foreach (var el in citArr.EnumerateArray())
                    citations.Add(el.GetString() ?? "");
            return (path, title, content, citations);
        }
        catch { return null; }
    }

    static string ExtractJson(string text)
    {
        // Try to find JSON in markdown code fences. The fence content starts
        // after the newline; without one (```json{...}``` on a single line)
        // we fall through to the brace scan instead of returning a bad slice.
        var fenceStart = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (fenceStart >= 0)
        {
            int contentStart = text.IndexOf('\n', fenceStart);
            if (contentStart >= 0)
            {
                contentStart++;
                var fenceEnd = text.IndexOf("```", contentStart, StringComparison.Ordinal);
                if (fenceEnd > contentStart)
                    return text[contentStart..fenceEnd].Trim();
            }
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
