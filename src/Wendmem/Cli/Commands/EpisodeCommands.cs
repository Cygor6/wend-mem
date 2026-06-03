using Microsoft.Extensions.DependencyInjection;
using Wendmem.Storage;

namespace Wendmem.Cli.Commands;

internal sealed class EpisodeListCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var wing = ArgvHelpers.GetOption(args, "--wing");
        if (wing is null)
        {
            Console.Error.WriteLine("Usage: wendmem episode list --wing <wing> [--outcome success|failure|partial] [--limit N]");
            return 1;
        }

        var outcome = ArgvHelpers.GetOption(args, "--outcome") ?? "any";
        var limit = ArgvHelpers.GetIntOption(args, "--limit", 20);

        var storage = services.GetRequiredService<EpisodeStorage>();
        var episodes = await storage.ListAsync(wing, outcome, limit, ct);

        if (episodes.Count == 0)
        {
            Console.Out.WriteLine("No episodes found.");
            return 0;
        }

        foreach (var ep in episodes)
        {
            var indicator = ep.Outcome switch
            {
                "success" => "✓",
                "failure" => "✗",
                "partial" => "~",
                _ => "?"
            };
            Console.Out.WriteLine($"{indicator} {ep.Id}  [{ep.Outcome}]  {ep.Goal}");
            if (ep.NextTime is not null)
                Console.Out.WriteLine($"  → {ep.NextTime}");
            Console.Out.WriteLine($"  ended: {ep.EndedAt:yyyy-MM-dd HH:mm}");
            Console.Out.WriteLine();
        }
        return 0;
    }
}

internal sealed class EpisodeShowCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var id = ArgvHelpers.GetPositional(args, 0);
        if (id is null)
        {
            Console.Error.WriteLine("Usage: wendmem episode show <id>");
            return 1;
        }

        var storage = services.GetRequiredService<EpisodeStorage>();
        var episode = await storage.GetByIdAsync(id, ct);

        if (episode is null)
        {
            Console.Error.WriteLine($"Episode '{id}' not found.");
            return 1;
        }

        Console.Out.WriteLine($"ID:       {episode.Id}");
        Console.Out.WriteLine($"Wing:     {episode.Wing}");
        Console.Out.WriteLine($"Goal:     {episode.Goal}");
        Console.Out.WriteLine($"Outcome:  {episode.Outcome}");
        Console.Out.WriteLine($"Plan:     {episode.Plan ?? "(none)"}");
        Console.Out.WriteLine($"Worked:   {episode.WhatWorked ?? "(none)"}");
        Console.Out.WriteLine($"Failed:   {episode.WhatFailed ?? "(none)"}");
        Console.Out.WriteLine($"NextTime: {episode.NextTime ?? "(none)"}");
        Console.Out.WriteLine($"Drawers:  {episode.DrawerRefs ?? "(none)"}");
        Console.Out.WriteLine($"Skills:   {episode.SkillRefs ?? "(none)"}");
        Console.Out.WriteLine($"Started:  {episode.StartedAt:yyyy-MM-dd HH:mm}");
        Console.Out.WriteLine($"Ended:    {episode.EndedAt:yyyy-MM-dd HH:mm}");
        Console.Out.WriteLine($"Agent:    {episode.Agent ?? "(unknown)"}");
        return 0;
    }
}

internal sealed class EpisodeDeleteCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var id = ArgvHelpers.GetPositional(args, 0);
        if (id is null)
        {
            Console.Error.WriteLine("Usage: wendmem episode delete <id>");
            return 1;
        }

        var storage = services.GetRequiredService<EpisodeStorage>();
        var deleted = await storage.DeleteAsync(id, ct);

        if (!deleted)
        {
            Console.Error.WriteLine($"Episode '{id}' not found.");
            return 1;
        }

        Console.Out.WriteLine($"Deleted episode {id}");
        return 0;
    }
}
