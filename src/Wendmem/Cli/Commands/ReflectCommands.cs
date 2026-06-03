using Microsoft.Extensions.DependencyInjection;
using Wendmem.Services;
using Wendmem.Storage;

namespace Wendmem.Cli.Commands;

internal sealed class ReflectRunCommand
{
    public async Task<int> RunAsync(string[] args, IServiceProvider services, CancellationToken ct)
    {
        var wing = ArgvHelpers.GetOption(args, "--wing");
        if (wing is null)
        {
            Console.Error.WriteLine("Usage: wendmem reflect run --wing <wing> [--lookback N] [--write]");
            return 1;
        }
        var lookback = ArgvHelpers.GetIntOption(args, "--lookback", 50);
        var write = ArgvHelpers.HasFlag(args, "--write");

        var reflection = services.GetRequiredService<ReflectionService>();
        var result = await reflection.ReflectAsync(wing, lookback, ct);

        Console.Out.WriteLine($"Reflection: processed {result.DrawerCount} drawers, produced {result.Drafts.Count} drafts");

        foreach (var draft in result.Drafts)
        {
            Console.Out.WriteLine($"\n--- Draft: {draft.SuggestedTitle} ---");
            Console.Out.WriteLine($"  Question: {draft.Question}");
            Console.Out.WriteLine($"  Path: {draft.SuggestedPath}");
            Console.Out.WriteLine($"  ID: {draft.Id}");
            Console.Out.WriteLine($"  Content preview: {draft.DraftContent[..Math.Min(200, draft.DraftContent.Length)]}...");
            Console.Out.WriteLine($"  Citations: {draft.Citations}");

            if (write)
            {
                var accepted = await reflection.AcceptDraftAsync(draft.Id, ct);
                Console.Out.WriteLine(accepted ? $"  ✓ Written to wiki at {draft.SuggestedPath}" : $"  ✗ Failed to write");
            }
        }
        return 0;
    }
}

internal sealed class ReflectDraftsListCommand
{
    public async Task<int> RunAsync(string[] args, IServiceProvider services, CancellationToken ct)
    {
        var wing = ArgvHelpers.GetOption(args, "--wing");
        if (wing is null)
        {
            Console.Error.WriteLine("Usage: wendmem reflect drafts list --wing <wing> [--status pending|accepted|dismissed]");
            return 1;
        }
        var status = ArgvHelpers.GetOption(args, "--status") ?? "pending";
        var draftStorage = services.GetRequiredService<ReflectionDraftStorage>();
        var drafts = await draftStorage.ListPendingAsync(wing, 20, ct);

        if (drafts.Count == 0)
        {
            Console.Out.WriteLine("No reflection drafts found.");
            return 0;
        }

        foreach (var d in drafts)
        {
            var indicator = d.Status switch { "accepted" => "✓", "dismissed" => "✗", _ => "○" };
            Console.Out.WriteLine($"{indicator} {d.Id}  {d.SuggestedTitle}");
            Console.Out.WriteLine($"  Question: {d.Question}");
            Console.Out.WriteLine($"  Path: {d.SuggestedPath}");
            Console.Out.WriteLine($"  Created: {d.CreatedAt:yyyy-MM-dd HH:mm}");
            Console.Out.WriteLine();
        }
        return 0;
    }
}

internal sealed class ReflectDraftsShowCommand
{
    public async Task<int> RunAsync(string[] args, IServiceProvider services, CancellationToken ct)
    {
        var id = ArgvHelpers.GetPositional(args, 0);
        if (id is null)
        {
            Console.Error.WriteLine("Usage: wendmem reflect drafts show <id>");
            return 1;
        }
        var draftStorage = services.GetRequiredService<ReflectionDraftStorage>();
        var draft = await draftStorage.GetByIdAsync(id, ct);
        if (draft is null)
        {
            Console.Error.WriteLine($"Draft '{id}' not found.");
            return 1;
        }

        Console.Out.WriteLine($"ID:       {draft.Id}");
        Console.Out.WriteLine($"Wing:     {draft.Wing}");
        Console.Out.WriteLine($"Question: {draft.Question}");
        Console.Out.WriteLine($"Path:     {draft.SuggestedPath}");
        Console.Out.WriteLine($"Title:    {draft.SuggestedTitle}");
        Console.Out.WriteLine($"Status:   {draft.Status}");
        Console.Out.WriteLine($"Created:  {draft.CreatedAt:yyyy-MM-dd HH:mm}");
        Console.Out.WriteLine($"Citations: {draft.Citations}");
        Console.Out.WriteLine($"\n--- Content ---\n{draft.DraftContent}");
        return 0;
    }
}

internal sealed class ReflectDraftsDismissCommand
{
    public async Task<int> RunAsync(string[] args, IServiceProvider services, CancellationToken ct)
    {
        var id = ArgvHelpers.GetPositional(args, 0);
        if (id is null)
        {
            Console.Error.WriteLine("Usage: wendmem reflect drafts dismiss <id>");
            return 1;
        }
        var draftStorage = services.GetRequiredService<ReflectionDraftStorage>();
        var updated = await draftStorage.UpdateStatusAsync(id, "dismissed", ct);
        if (!updated)
        {
            Console.Error.WriteLine($"Draft '{id}' not found.");
            return 1;
        }
        Console.Out.WriteLine($"Dismissed draft {id}");
        return 0;
    }
}

internal sealed class ReflectDraftsAcceptCommand
{
    public async Task<int> RunAsync(string[] args, IServiceProvider services, CancellationToken ct)
    {
        var id = ArgvHelpers.GetPositional(args, 0);
        if (id is null)
        {
            Console.Error.WriteLine("Usage: wendmem reflect drafts accept <id>");
            return 1;
        }
        var reflection = services.GetRequiredService<ReflectionService>();
        var accepted = await reflection.AcceptDraftAsync(id, ct);
        if (!accepted)
        {
            Console.Error.WriteLine($"Draft '{id}' not found or accept failed.");
            return 1;
        }
        Console.Out.WriteLine($"Accepted draft {id} — written to wiki");
        return 0;
    }
}
