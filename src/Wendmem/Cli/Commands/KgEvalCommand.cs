using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wendmem.Eval;
using Wendmem.Serialization;

namespace Wendmem.Cli.Commands;

internal sealed class KgEvalCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var wing = ArgvHelpers.GetOption(args, "--wing");
        if (wing is null)
        {
            Console.Error.WriteLine("Usage: wendmem kg-eval --wing W [--questions N] [--seed S] [--json]");
            return 1;
        }

        var questions = ArgvHelpers.GetIntOption(args, "--questions", 20);
        var seedRaw = ArgvHelpers.GetOption(args, "--seed");
        int? seed = seedRaw is not null && int.TryParse(seedRaw, out var s) ? s : null;
        var json = ArgvHelpers.HasFlag(args, "--json");

        var evaluator = services.GetRequiredService<KgEvaluator>();
        var result = await evaluator.EvaluateAsync(wing, questions, seed, ct);

        if (json)
        {
            Console.Out.WriteLine(
                JsonSerializer.Serialize(result, WendmemJsonContext.Default.KgEvalResult));
        }
        else
        {
            PrintHumanReport(result);
        }

        return 0;
    }

    static void PrintHumanReport(KgEvalResult r)
    {
        Console.Out.WriteLine($"KG-Eval — wing '{r.Wing}'");
        Console.Out.WriteLine(
            $"Questions: {r.Total} | Passed: {r.Passed} | Failed: {r.Failed} | " +
            $"Precision: {r.Precision:P1}");
        Console.Out.WriteLine($"  Passed by answer-match: {r.PassedByAnswer}");
        Console.Out.WriteLine($"  Passed by source-match: {r.PassedBySource}");

        if (r.FailedQuestions.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("Failed questions:");
            foreach (var q in r.FailedQuestions)
            {
                Console.Out.WriteLine($"  - \"{q.Question}\" (expected: {q.ExpectedAnswer})");
            }
        }
    }
}
