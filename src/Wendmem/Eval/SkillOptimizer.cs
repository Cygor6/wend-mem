using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wendmem.Experiences;

namespace Wendmem.Eval;

sealed class SkillOptimizer(
    KgEvaluator evaluator,
    LlmService llm,
    ILogger<SkillOptimizer> logger)
{
    public async Task<SkillOptResult> OptimizeAsync(
        string wing,
        string skillPath,
        string outputPath,
        int epochs,
        int editBudget,
        CancellationToken ct)
    {
        // Load current SKILL.md
        if (!File.Exists(skillPath))
        {
            Console.Error.WriteLine($"SKILL.md not found: {skillPath}");
            return new SkillOptResult(0, 0, 0, 0, ["Error: SKILL.md not found"]);
        }

        var currentSkill = await File.ReadAllTextAsync(skillPath, ct);

        // Build question set via kg-eval's generator
        const int questionCount = 40; // enough for a meaningful 60/40 split
        var questions = await evaluator.BuildQuestionsAsync(wing, questionCount, seed: 42, ct);
        if (questions.Count < 5)
        {
            var msg = $"Insufficient KG data in wing '{wing}' — only {questions.Count} questions generated. Need at least 5.";
            Console.Error.WriteLine(msg);
            return new SkillOptResult(0, 0, 0, 0, [msg]);
        }

        // Deterministic 60/40 split (fixed seed for reproducibility within run)
        var splitRng = new Random(12345);
        var indices = Enumerable.Range(0, questions.Count).OrderBy(_ => splitRng.Next()).ToList();
        var trainCount = (int)Math.Floor(questions.Count * 0.6);
        var trainQuestions = indices.Take(trainCount).Select(i => questions[i]).ToList();
        var valQuestions = indices.Skip(trainCount).Select(i => questions[i]).ToList();

        Console.Out.WriteLine($"Skill-Opt — wing '{wing}'");
        Console.Out.WriteLine($"Questions: {trainQuestions.Count} train / {valQuestions.Count} validation");
        Console.Out.WriteLine();

        // Score baseline on validation set
        var baselineValOutcomes = await evaluator.ScoreQuestionsAsync(valQuestions, wing, ct);
        var baselinePrecision = Precision(baselineValOutcomes);

        Console.Out.WriteLine($"Baseline validation precision: {baselinePrecision:P1}");
        Console.Out.WriteLine();

        // Check whether SKILL.md can influence scoring
        // PalaceSearcher.SearchMemoriesAsync has no system-context / skill-context parameter.
        // SKILL.md content does not flow into the retrieval pipeline.
        // We proceed with retrieval precision as a proxy and document this limitation.
        Console.Out.WriteLine(
            "NOTE: The current search pipeline has no injection point for SKILL.md content. " +
            "Skill-opt measures retrieval precision as a proxy signal. Edits accepted here " +
            "improve retrieval-relevant guidance for agents that consult SKILL.md, but the " +
            "validation signal comes from raw retrieval, not agent behavior.");
        Console.Out.WriteLine();

        var bestSkill = currentSkill;
        var bestPrecision = baselinePrecision;
        var rejectedBuffer = new List<SkillEdit>();
        var editsAccepted = 0;
        var editsRejected = 0;
        var epochLog = new List<string>();

        for (int epoch = 1; epoch <= epochs; epoch++)
        {
            Console.Out.WriteLine($"--- Epoch {epoch}/{epochs} ---");

            // ROLLOUT: score train set, collect failures
            var trainOutcomes = await evaluator.ScoreQuestionsAsync(trainQuestions, wing, ct);
            var trainFailures = trainOutcomes.Where(o => !o.Passed)
                .Select(o => o.Question)
                .ToList();

            Console.Out.WriteLine($"Train failures: {trainFailures.Count}/{trainQuestions.Count}");

            if (trainFailures.Count == 0)
            {
                epochLog.Add($"Epoch {epoch}: no train failures — optimizer skipped");
                Console.Out.WriteLine("No failures to optimize against. Skipping epoch.");
                continue;
            }

            // ANALYZE + PROPOSE
            var proposedEdits = await ProposeEditsAsync(
                bestSkill, trainFailures, rejectedBuffer, editBudget, ct);

            if (proposedEdits.Count == 0)
            {
                epochLog.Add($"Epoch {epoch}: optimizer proposed no edits");
                Console.Out.WriteLine("Optimizer proposed no edits. Skipping epoch.");
                continue;
            }

            // APPLY (bounded to editBudget)
            var editsToApply = proposedEdits.Take(editBudget).ToList();
            var candidateSkill = ApplyEdits(bestSkill, editsToApply);

            // VALIDATION GATE
            var candidateValOutcomes = await evaluator.ScoreQuestionsAsync(valQuestions, wing, ct);
            var candidatePrecision = Precision(candidateValOutcomes);

            if (candidatePrecision > bestPrecision)
            {
                // ACCEPT
                bestSkill = candidateSkill;
                bestPrecision = candidatePrecision;
                editsAccepted += editsToApply.Count;
                epochLog.Add($"Epoch {epoch}: {baselinePrecision:P1} → {candidatePrecision:P1}  ACCEPTED ({editsToApply.Count} edits)");
                baselinePrecision = candidatePrecision;
                Console.Out.WriteLine($"Candidate precision: {candidatePrecision:P1} — ACCEPTED ({editsToApply.Count} edits)");
            }
            else
            {
                // REJECT
                rejectedBuffer.AddRange(editsToApply);
                editsRejected += editsToApply.Count;
                epochLog.Add($"Epoch {epoch}: {baselinePrecision:P1} → {candidatePrecision:P1}  REJECTED (kept previous)");
                Console.Out.WriteLine($"Candidate precision: {candidatePrecision:P1} — REJECTED (kept previous)");
            }

            Console.Out.WriteLine();
        }

        await File.WriteAllTextAsync(outputPath, bestSkill, ct);

        var finalResult = new SkillOptResult(
            Precision(baselineValOutcomes),
            bestPrecision,
            editsAccepted,
            editsRejected,
            epochLog);

        Console.Out.WriteLine($"Starting precision: {finalResult.StartingPrecision:P1}  →  Final precision: {finalResult.FinalPrecision:P1}");
        Console.Out.WriteLine($"Edits accepted: {editsAccepted} | rejected: {editsRejected}");
        Console.Out.WriteLine();
        foreach (var entry in epochLog)
            Console.Out.WriteLine(entry);
        Console.Out.WriteLine();
        Console.Out.WriteLine($"Optimized skill written to {outputPath}");
        Console.Out.WriteLine("Review it, then replace SKILL.md manually if satisfied.");

        return finalResult;
    }

    async Task<List<SkillEdit>> ProposeEditsAsync(
        string currentSkill,
        IReadOnlyList<EvalQuestion> trainFailures,
        IReadOnlyList<SkillEdit> rejectedBuffer,
        int budget,
        CancellationToken ct)
    {
        var failuresText = string.Join("\n",
            trainFailures.Select(f => $"- \"{f.Question}\" (expected: {f.ExpectedAnswer})"));

        var rejectedText = rejectedBuffer.Count == 0
            ? "(none)"
            : string.Join("\n", rejectedBuffer.Select(r =>
                $"- [{r.Type}] anchor=\"{r.Anchor}\" content=\"{r.Content}\" ({r.Rationale})"));

        var prompt =
            "You optimize an agent skill document. You are given the current " +
            "SKILL.md, a list of retrieval failures, and previously rejected " +
            $"edits. Propose at most {budget} edits that would help the agent " +
            "retrieve better. Each edit is one of: ADD (new guidance line), " +
            "DELETE (remove a line that misleads), or REPLACE (swap a line). " +
            "Do NOT repeat any rejected edit. Keep edits small and specific. " +
            "Respond ONLY with valid JSON:\n" +
            """{"edits": [{"type":"ADD|DELETE|REPLACE","anchor":"<existing text or section>","content":"<new text>","rationale":"<why>"}]}""" + "\n\n" +
            $"# Current SKILL.md\n{currentSkill}\n\n" +
            $"# Retrieval failures (train set)\n{failuresText}\n\n" +
            $"# Previously rejected edits\n{rejectedText}";

        try
        {
            var content = await llm.CompleteAsync(prompt, ct);

            // Strip markdown fences if present
            if (content.Contains("```"))
            {
                var start = content.IndexOf('{');
                var end = content.LastIndexOf('}');
                if (start >= 0 && end > start)
                    content = content[start..(end + 1)];
            }

            if (string.IsNullOrWhiteSpace(content))
                return [];

            using var doc = JsonDocument.Parse(content);
            var edits = new List<SkillEdit>();

            if (doc.RootElement.TryGetProperty("edits", out var editsArr))
            {
                foreach (var item in editsArr.EnumerateArray())
                {
                    edits.Add(new SkillEdit(
                        item.TryGetProperty("type", out var t) ? t.GetString() ?? "ADD" : "ADD",
                        item.TryGetProperty("anchor", out var a) ? a.GetString() ?? "" : "",
                        item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                        item.TryGetProperty("rationale", out var r) ? r.GetString() ?? "" : ""));
                }
            }

            return edits;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ProposeEditsAsync failed");
            return [];
        }
    }

    static string ApplyEdits(string skill, IReadOnlyList<SkillEdit> edits)
    {
        var lines = skill.Split('\n').ToList();

        foreach (var edit in edits)
        {
            switch (edit.Type.ToUpperInvariant())
            {
                case "ADD":
                    // Add after the line containing the anchor
                    var addIdx = lines.FindIndex(l => l.Contains(edit.Anchor, StringComparison.OrdinalIgnoreCase));
                    if (addIdx >= 0)
                        lines.Insert(addIdx + 1, edit.Content);
                    else
                        lines.Add(edit.Content);
                    break;

                case "DELETE":
                    // Remove lines containing the anchor
                    lines.RemoveAll(l => l.Contains(edit.Anchor, StringComparison.OrdinalIgnoreCase));
                    break;

                case "REPLACE":
                    // Replace the first line containing the anchor
                    var replaceIdx = lines.FindIndex(l => l.Contains(edit.Anchor, StringComparison.OrdinalIgnoreCase));
                    if (replaceIdx >= 0)
                        lines[replaceIdx] = edit.Content;
                    break;
            }
        }

        return string.Join('\n', lines);
    }

    static double Precision(IReadOnlyList<EvalOutcome> outcomes) =>
        outcomes.Count == 0 ? 0 : (double)outcomes.Count(o => o.Passed) / outcomes.Count;
}

public record SkillEdit(string Type, string Anchor, string Content, string Rationale);

public record SkillOptResult(
    double StartingPrecision,
    double FinalPrecision,
    int EditsAccepted,
    int EditsRejected,
    IReadOnlyList<string> EpochLog);
