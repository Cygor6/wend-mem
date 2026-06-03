using DuckDB.NET.Data;
using Wendmem.Experiences;
using Wendmem.Services;
using Wendmem.Storage;

namespace Wendmem.Eval;

sealed class KgEvaluator(
    DuckDbConnectionFactory dbFactory,
    PalaceSearcher searcher,
    LlmService llm)
{
    public async Task<KgEvalResult> EvaluateAsync(
        string wing, int questionCount, int? seed, CancellationToken ct)
    {
        var rng = seed is not null ? new Random(seed.Value) : new Random();

        var triples = await LoadActiveTriplesForWingAsync(wing, ct);
        if (triples.Count < 5)
        {
            Console.Out.WriteLine(
                $"Wing '{wing}' has only {triples.Count} active triple(s) with associated drawers. " +
                "At least 5 are required for evaluation.");
            return new KgEvalResult(wing, 0, 0, 0, 0, 0, 0, []);
        }

        var questions = await BuildQuestionsFromTriplesAsync(triples, questionCount, rng, ct);
        var outcomes = await ScoreQuestionsAsync(questions, wing, ct);

        var passed = outcomes.Count(o => o.Passed);
        var failed = outcomes.Count - passed;
        var passedByAnswer = outcomes.Count(o => o.AnswerFound);
        var passedBySource = outcomes.Count(o => o.SourceFound && !o.AnswerFound);
        var precision = outcomes.Count > 0 ? (double)passed / outcomes.Count : 0;

        return new KgEvalResult(
            wing,
            outcomes.Count,
            passed,
            failed,
            precision,
            passedByAnswer,
            passedBySource,
            outcomes.Where(o => !o.Passed).Select(o => o.Question).ToList());
    }

    /// <summary>
    /// Build evaluation questions from KG triples for a wing. Returns empty if &lt;5 triples.
    /// </summary>
    public async Task<List<EvalQuestion>> BuildQuestionsAsync(
        string wing, int count, int? seed, CancellationToken ct)
    {
        var rng = seed is not null ? new Random(seed.Value) : new Random();
        var triples = await LoadActiveTriplesForWingAsync(wing, ct);
        if (triples.Count < 5)
            return [];
        return await BuildQuestionsFromTriplesAsync(triples, count, rng, ct);
    }

    /// <summary>
    /// Score a set of questions against the real search pipeline.
    /// </summary>
    public async Task<List<EvalOutcome>> ScoreQuestionsAsync(
        IReadOnlyList<EvalQuestion> questions, string wing, CancellationToken ct)
    {
        var outcomes = new List<EvalOutcome>();
        foreach (var q in questions)
        {
            var outcome = await ScoreQuestionAsync(q, wing, ct);
            outcomes.Add(outcome);
        }
        return outcomes;
    }

    async Task<List<TripleRow>> LoadActiveTriplesForWingAsync(string wing, CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT t.subject, t.predicate, t.object, t.drawer_id
            FROM triples t
            JOIN drawers d ON d.id = t.drawer_id
            WHERE t.valid_to IS NULL
              AND t.drawer_id IS NOT NULL
              AND d.wing = $wing
            """;
        cmd.Parameters.Add(new DuckDBParameter("wing", wing));

        var list = new List<TripleRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var drawerId = reader.IsDBNull(3) ? null : reader.GetString(3);
            if (drawerId is not null)
            {
                list.Add(new TripleRow(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), drawerId));
            }
        }
        return list;
    }

    async Task<List<EvalQuestion>> BuildQuestionsFromTriplesAsync(
        List<TripleRow> triples, int count, Random rng, CancellationToken ct)
    {
        // Pre-compute: which objects also appear as subjects (for 1-hop extension)
        var subjects = new HashSet<string>(triples.Select(t => t.Subject));
        var bySubject = triples
            .Where(t => subjects.Contains(t.Object))
            .GroupBy(t => t.Subject)
            .ToDictionary(g => g.Key, g => g.ToList());

        var questions = new List<EvalQuestion>();

        for (int i = 0; i < count; i++)
        {
            var first = triples[rng.Next(triples.Count)];
            string factPath;
            string expectedAnswer;
            string sourceDrawerId;

            // ~30% chance of 2-hop if the object is also a subject
            bool canHop = bySubject.TryGetValue(first.Object, out var continuations)
                          && continuations.Count > 0;

            if (canHop && rng.NextDouble() < 0.3)
            {
                var second = continuations![rng.Next(continuations.Count)];
                factPath = $"{first.Subject} -> {first.Predicate} -> {first.Object} -> {second.Predicate} -> {second.Object}";
                expectedAnswer = second.Object;
                sourceDrawerId = second.DrawerId;
            }
            else
            {
                factPath = $"{first.Subject} -> {first.Predicate} -> {first.Object}";
                expectedAnswer = first.Object;
                sourceDrawerId = first.DrawerId;
            }

            var questionText = await GenerateQuestionAsync(factPath, expectedAnswer, ct);
            questions.Add(new EvalQuestion(questionText, expectedAnswer, sourceDrawerId));
        }

        return questions;
    }

    async Task<string> GenerateQuestionAsync(string factPath, string expectedAnswer, CancellationToken ct)
    {
        var systemPrompt =
            "You convert knowledge-graph facts into natural questions. " +
            "Given a fact path, produce ONE concise natural-language question " +
            "whose answer is the final object in the path. Respond ONLY with " +
            "the question text, no preamble, no quotes.";

        var userPrompt =
            $"Fact path: {factPath}\n" +
            $"Expected answer: {expectedAnswer}\n" +
            "Question:";

        var prompt = $"{systemPrompt}\n\n{userPrompt}";
        var content = await llm.CompleteAsync(prompt, ct);

        // Clean up: strip quotes, preamble
        content = content.Trim().Trim('"').Trim();
        var newlineIdx = content.IndexOf('\n');
        if (newlineIdx > 0)
            content = content[..newlineIdx].Trim();

        return string.IsNullOrWhiteSpace(content)
            ? $"What is {expectedAnswer}?"
            : content;
    }

    async Task<EvalOutcome> ScoreQuestionAsync(EvalQuestion q, string wing, CancellationToken ct)
    {
        var results = await searcher.SearchMemoriesAsync(q.Question, wing, null, 10, ct);

        var answerFound = results.Any(r =>
            r.Drawer.Content.Contains(q.ExpectedAnswer, StringComparison.OrdinalIgnoreCase));

        var sourceFound = results.Any(r =>
            string.Equals(r.Drawer.Id, q.SourceDrawerId, StringComparison.OrdinalIgnoreCase));

        return new EvalOutcome(q, answerFound, sourceFound);
    }

    record TripleRow(string Subject, string Predicate, string Object, string DrawerId);
}

public record EvalQuestion(string Question, string ExpectedAnswer, string SourceDrawerId);

public record EvalOutcome(EvalQuestion Question, bool AnswerFound, bool SourceFound)
{
    public bool Passed => AnswerFound || SourceFound;
}

public record KgEvalResult(
    string Wing,
    int Total,
    int Passed,
    int Failed,
    double Precision,
    int PassedByAnswer,
    int PassedBySource,
    IReadOnlyList<EvalQuestion> FailedQuestions);
