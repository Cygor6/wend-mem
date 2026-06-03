using Microsoft.Extensions.DependencyInjection;
using Wendmem.Services;
using Wendmem.Storage;

namespace Wendmem.Cli.Commands;

internal sealed class SkillsAddCommand
{
    public async Task<int> RunAsync(string[] args, IServiceProvider services, CancellationToken ct)
    {
        var folder = ArgvHelpers.GetPositional(args, 0);
        if (folder is null)
        {
            Console.Error.WriteLine("Usage: wendmem skills add <folder_path> [--wing <wing>] [--force]");
            return 1;
        }
        var wing = ArgvHelpers.GetOption(args, "--wing");
        var force = ArgvHelpers.HasFlag(args, "--force");

        var storage = services.GetRequiredService<SkillStorage>();

        try
        {
            var skill = await storage.RegisterAsync(folder, wing, ct);
            Console.Out.WriteLine($"Registered skill '{skill.Name}' (id: {skill.Id})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
}

internal sealed class SkillsListCommand
{
    public async Task<int> RunAsync(string[] args, IServiceProvider services, CancellationToken ct)
    {
        var wing = ArgvHelpers.GetOption(args, "--wing");
        var json = ArgvHelpers.HasFlag(args, "--json");

        var storage = services.GetRequiredService<SkillStorage>();
        var skills = await storage.ListAsync(wing, ct);

        if (skills.Count == 0)
        {
            Console.Out.WriteLine("No skills registered.");
            return 0;
        }

        if (json)
        {
            foreach (var s in skills)
            {
                var desc = s.Description.Length > 80 ? s.Description[..80] + "..." : s.Description;
                Console.Out.WriteLine($"{{\"id\":\"{s.Id}\",\"name\":\"{s.Name}\",\"success\":{s.SuccessCount},\"failure\":{s.FailureCount}}}");
            }
            return 0;
        }

        foreach (var s in skills)
        {
            var rate = s.SuccessCount + s.FailureCount > 0
                ? $"{100.0 * s.SuccessCount / (s.SuccessCount + s.FailureCount):F0}%"
                : "n/a";
            Console.Out.WriteLine($"  {s.Name}  [{rate}]  {s.Description[..Math.Min(60, s.Description.Length)]}");
        }
        return 0;
    }
}

internal sealed class SkillsShowCommand
{
    public async Task<int> RunAsync(string[] args, IServiceProvider services, CancellationToken ct)
    {
        var nameOrId = ArgvHelpers.GetPositional(args, 0);
        if (nameOrId is null)
        {
            Console.Error.WriteLine("Usage: wendmem skills show <name|id>");
            return 1;
        }
        var storage = services.GetRequiredService<SkillStorage>();
        var skill = await storage.GetByIdOrNameAsync(nameOrId, ct);
        if (skill is null)
        {
            Console.Error.WriteLine($"Skill '{nameOrId}' not found.");
            return 1;
        }

        var rate = skill.SuccessCount + skill.FailureCount > 0
            ? $"{100.0 * skill.SuccessCount / (skill.SuccessCount + skill.FailureCount):F1}%"
            : "n/a";

        Console.Out.WriteLine($"ID:          {skill.Id}");
        Console.Out.WriteLine($"Name:        {skill.Name}");
        Console.Out.WriteLine($"Folder:      {skill.FolderPath}");
        Console.Out.WriteLine($"Description: {skill.Description}");
        Console.Out.WriteLine($"License:     {skill.License ?? "(none)"}");
        Console.Out.WriteLine($"Success:     {skill.SuccessCount}  Failure: {skill.FailureCount}  Rate: {rate}");
        Console.Out.WriteLine($"Last used:   {skill.LastUsedAt?.ToString("yyyy-MM-dd HH:mm") ?? "never"}");
        Console.Out.WriteLine($"Registered:  {skill.RegisteredAt:yyyy-MM-dd HH:mm}");

        // Preview SKILL.md
        if (File.Exists(skill.SkillMdPath))
        {
            var content = File.ReadAllText(skill.SkillMdPath);
            var preview = content.Length > 500 ? content[..500] + "..." : content;
            Console.Out.WriteLine($"\n--- SKILL.md preview ---\n{preview}");
        }
        return 0;
    }
}

internal sealed class SkillsUpdateCommand
{
    public async Task<int> RunAsync(string[] args, IServiceProvider services, CancellationToken ct)
    {
        var nameOrId = ArgvHelpers.GetPositional(args, 0);
        if (nameOrId is null)
        {
            Console.Error.WriteLine("Usage: wendmem skills update <name|id>");
            return 1;
        }
        var storage = services.GetRequiredService<SkillStorage>();
        var existing = await storage.GetByIdOrNameAsync(nameOrId, ct);
        if (existing is null)
        {
            Console.Error.WriteLine($"Skill '{nameOrId}' not found.");
            return 1;
        }

        var skill = await storage.RegisterAsync(existing.FolderPath, existing.Wing, ct);
        Console.Out.WriteLine($"Updated skill '{skill.Name}' (id: {skill.Id})");
        return 0;
    }
}

internal sealed class SkillsRemoveCommand
{
    public async Task<int> RunAsync(string[] args, IServiceProvider services, CancellationToken ct)
    {
        var nameOrId = ArgvHelpers.GetPositional(args, 0);
        if (nameOrId is null)
        {
            Console.Error.WriteLine("Usage: wendmem skills remove <name|id> [--force] [--yes]");
            return 1;
        }
        var force = ArgvHelpers.HasFlag(args, "--force");
        var yes = ArgvHelpers.HasFlag(args, "--yes");

        var storage = services.GetRequiredService<SkillStorage>();
        var existing = await storage.GetByIdOrNameAsync(nameOrId, ct);
        if (existing is null)
        {
            Console.Error.WriteLine($"Skill '{nameOrId}' not found.");
            return 1;
        }

        if (force && !yes)
        {
            Console.Out.Write($"Delete folder {existing.FolderPath} from disk? [y/N] ");
            var response = Console.ReadLine();
            if (!response?.Equals("y", StringComparison.OrdinalIgnoreCase) ?? true)
            {
                Console.Out.WriteLine("Cancelled.");
                return 0;
            }
        }

        var removed = await storage.RemoveAsync(nameOrId, ct);
        if (removed && force)
        {
            try
            { Directory.Delete(existing.FolderPath, recursive: true); }
            catch (Exception ex) { Console.Error.WriteLine($"Warning: could not delete folder: {ex.Message}"); }
        }

        Console.Out.WriteLine(removed ? $"Removed skill '{existing.Name}'" : "Skill not found in DB");
        return 0;
    }
}

internal sealed class SkillsReindexCommand
{
    public async Task<int> RunAsync(string[] args, IServiceProvider services, CancellationToken ct)
    {
        var root = ArgvHelpers.GetOption(args, "--root");
        var wing = ArgvHelpers.GetOption(args, "--wing");

        var config = services.GetRequiredService<PalaceConfig>();
        var storage = services.GetRequiredService<SkillStorage>();

        var rootDir = root ?? ExpandSkillsRoot(config.SkillsRoot);
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
        {
            Console.Error.WriteLine($"Skills root not found: {rootDir}. Set Palace:SkillsRoot in appsettings.json or pass --root.");
            return 1;
        }

        var count = await storage.ReindexAsync(rootDir, wing, ct);
        Console.Out.WriteLine($"Reindex complete: {count} changes");
        return 0;
    }

    internal static string ExpandSkillsRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        if (path.StartsWith('~'))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\'));
        return Path.GetFullPath(path);
    }
}

internal sealed class SkillsValidateCommand
{
    public Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var folder = ArgvHelpers.GetPositional(args, 0);
        if (folder is null)
        {
            Console.Error.WriteLine("Usage: wendmem skills validate <folder_path>");
            return Task.FromResult(1);
        }

        var absPath = Path.GetFullPath(folder);
        var skillMdPath = Path.Combine(absPath, "SKILL.md");
        var folderName = Path.GetFileName(absPath);

        if (!File.Exists(skillMdPath))
        {
            Console.Error.WriteLine($"SKILL.md not found at {skillMdPath}");
            return Task.FromResult(1);
        }

        var content = File.ReadAllText(skillMdPath);
        var issues = SkillFrontmatterParser.Validate(content, folderName, skillMdPath);

        if (issues.Count == 0)
        {
            Console.Out.WriteLine($"✓ Skill '{folderName}' is valid.");
            return Task.FromResult(0);
        }

        foreach (var issue in issues)
            Console.Error.WriteLine($"✗ {issue}");
        return Task.FromResult(1);
    }
}

internal sealed class SkillsNewCommand
{
    public Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var name = ArgvHelpers.GetPositional(args, 0);
        if (name is null)
        {
            Console.Error.WriteLine("Usage: wendmem skills new <name> [--root <dir>]");
            return Task.FromResult(1);
        }

        var configArg = ArgvHelpers.GetOption(args, "--root");

        // Default root: ~/.wendmem/skills
        var root = string.IsNullOrWhiteSpace(configArg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wendmem", "skills")
            : configArg;

        if (root.StartsWith('~'))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), root[1..].TrimStart('/', '\\'));

        var folder = Path.Combine(root, name);
        if (Directory.Exists(folder))
        {
            Console.Error.WriteLine($"Folder already exists: {folder}");
            return Task.FromResult(1);
        }

        Directory.CreateDirectory(folder);

        var content = $""""
            ---
            name: {name}
            description: <TODO: what it does and when to use it>
            ---

            # {char.ToUpper(name[0])}{name[1..].Replace("-", " ")}

            ## Instructions

            <TODO>

            ## Examples

            <TODO>
            """";

        File.WriteAllText(Path.Combine(folder, "SKILL.md"), content);
        Console.Out.WriteLine($"Created skill scaffold at {folder}");
        Console.Out.WriteLine("Edit SKILL.md, then run: wendmem skills validate " + folder);
        Console.Out.WriteLine("Then register: wendmem skills add " + folder);
        return Task.FromResult(0);
    }
}
