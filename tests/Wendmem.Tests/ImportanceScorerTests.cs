using Microsoft.Extensions.Logging;
using Wendmem.Services;

namespace Wendmem.Tests;

public sealed class ImportanceScorerTests
{
    [Test]
    public async Task Heuristic_Deterministic_SameContentSameScore()
    {
        var scorer = CreateScorer();

        var content = "We decided to use DuckDB because it offers fast analytical queries and therefore must be our primary store.";
        var score1 = scorer.ScoreHeuristic(content);
        var score2 = scorer.ScoreHeuristic(content);

        await Assert.That(score1).IsEqualTo(score2);
        await Assert.That(score1).IsGreaterThan(0f);
        await Assert.That(score1).IsLessThanOrEqualTo(1f);
    }

    [Test]
    public async Task Heuristic_DecisionMarkers_BoostScore()
    {
        var scorer = CreateScorer();

        var neutral = "The system has a database layer with tables and indexes.";
        var decisive = "We decided to use DuckDB because it is critical for performance and therefore must be configured properly.";

        var neutralScore = scorer.ScoreHeuristic(neutral);
        var decisiveScore = scorer.ScoreHeuristic(decisive);

        await Assert.That(decisiveScore).IsGreaterThan(neutralScore);
    }

    [Test]
    public async Task Heuristic_EntityMatch_BoostsScore()
    {
        var scorer = CreateScorer();

        var content = "The project uses Wendmem for memory management.";
        // Entity names must be lowercase — the scorer lowercases content and
        // expects callers (e.g. LoadEntityNamesAsync) to provide lowered names.
        var entities = new List<string> { "wendmem", "duckdb", "gemma" };

        var withoutEntities = scorer.ScoreHeuristic(content, null);
        var withEntities = scorer.ScoreHeuristic(content, entities);

        await Assert.That(withEntities).IsGreaterThan(withoutEntities);
    }

    [Test]
    public async Task Heuristic_LengthNormalisation_SweetSpot()
    {
        var scorer = CreateScorer();

        var tooShort = "ok";
        var sweetSpot = new string('a', 500);
        var tooLong = new string('a', 6000);

        var shortScore = scorer.ScoreHeuristic(tooShort);
        var sweetScore = scorer.ScoreHeuristic(sweetSpot);
        var longScore = scorer.ScoreHeuristic(tooLong);

        await Assert.That(sweetScore).IsGreaterThan(shortScore);
        await Assert.That(sweetScore).IsGreaterThan(longScore);
    }

    [Test]
    public async Task Heuristic_NumericDensity_ConfigContent()
    {
        var scorer = CreateScorer();

        var noNumbers = "The system stores data in memory and retrieves it when needed.";
        var manyNumbers = "Set timeout 30s retry 3 times port 5432 batch 100 size 4096 limit 50 connections 8";

        var noNumScore = scorer.ScoreHeuristic(noNumbers);
        var numScore = scorer.ScoreHeuristic(manyNumbers);

        await Assert.That(numScore).IsGreaterThan(noNumScore);
    }

    [Test]
    public async Task Heuristic_ClampsToUnitRange()
    {
        var scorer = CreateScorer();

        var extreme = "decided chose must will because therefore critical important essential mandatory required blocker urgent resolved agreed " +
                       string.Join(" ", Enumerable.Range(0, 50).Select(i => $"value{i}"));
        var score = scorer.ScoreHeuristic(extreme, Enumerable.Range(0, 10).Select(i => $"value{i}").ToList());

        await Assert.That(score).IsGreaterThanOrEqualTo(0f);
        await Assert.That(score).IsLessThanOrEqualTo(1f);
    }

    [Test]
    public async Task Heuristic_EmptyContent_LowScore()
    {
        var scorer = CreateScorer();
        var score = scorer.ScoreHeuristic("");
        await Assert.That(score).IsLessThan(0.2f);
    }

    private static ImportanceScorer CreateScorer()
    {
        return new ImportanceScorer(
            null,
            null,
            new LoggerFactory().CreateLogger<ImportanceScorer>());
    }
}
