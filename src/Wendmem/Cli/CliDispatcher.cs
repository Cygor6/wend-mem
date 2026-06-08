using Wendmem.Cli.Commands;

namespace Wendmem.Cli;

internal static class CliDispatcher
{
    /// <summary>
    /// Returns:
    ///   -1 if no CLI subcommand recognized - caller should fall through to stdio MCP
    ///   -2 if "serve" subcommand was given - caller should start HTTP server
    ///   else the CLI exit code (0 success, non-zero error)
    /// </summary>
    public static async Task<int> TryDispatchAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        if (args.Length == 0)
            return -1;

        return args[0] switch
        {
            "stats" => await new StatsCommand().RunAsync(args[1..], services, ct),
            "wings" => await new WingsCommand().RunAsync(args[1..], services, ct),
            "search" => await new SearchCommand().RunAsync(args[1..], services, ct),
            "search-semantic" => await new SearchSemanticCommand().RunAsync(args[1..], services, ct),
            "grep" => await new GrepCommand().RunAsync(args[1..], services, ct),
            "grep-exact" => await new GrepExactCommand().RunAsync(args[1..], services, ct),
            "prune" => await new PruneCommand().RunAsync(args[1..], services, ct),
            "save-session" => await new SaveSessionCommand().RunAsync(args[1..], services, ct),
            "delete-drawer" => await new DeleteDrawerCommand().RunAsync(args[1..], services, ct),
            "mine" => await new MineCommand().RunAsync(args[1..], services, ct),
            "mine-conversation" => await new MineConversationCommand().RunAsync(args[1..], services, ct),
            "sweep" => await new SweepCommand().RunAsync(args[1..], services, ct),
            "wakeup-full" => await new WakeUpFullCommand().RunAsync(args[1..], services, ct),
            "add-tunnel" => await new AddTunnelCommand().RunAsync(args[1..], services, ct),
            "list-tunnels" => await new ListTunnelsCommand().RunAsync(args[1..], services, ct),
            "list-tunnels-by-topic" => await new ListTunnelsByTopicCommand().RunAsync(args[1..], services, ct),
            "pending" => await DispatchPendingAsync(args[1..], services, ct),
            "activity" => await new ActivityCommand().RunAsync(args[1..], services, ct),
            "distill" => await new DistillCommand().RunAsync(args[1..], services, ct),
            "wiki" => await DispatchWikiAsync(args[1..], services, ct),
            "search-task-memory" => await new SearchTaskMemoryCommand().RunAsync(args[1..], services, ct),
            "episode" => await DispatchEpisodeAsync(args[1..], services, ct),
            "skills" => await DispatchSkillsAsync(args[1..], services, ct),
            "reflect" => await DispatchReflectAsync(args[1..], services, ct),
            "distill-task-memory" => await new DistillTaskMemoryCommand().RunAsync(args[1..], services, ct),
            "record-outcome" => await new RecordTaskOutcomeCommand().RunAsync(args[1..], services, ct),
            "reflect-on-failure" => await new ReflectOnFailureCommand().RunAsync(args[1..], services, ct),
            "prune-task-memory" => await new PruneTaskMemoryCommand().RunAsync(args[1..], services, ct),
            "export-task-memory" => await new ExportTaskMemoryCommand().RunAsync(args[1..], services, ct),
            "import-task-memory" => await new ImportTaskMemoryCommand().RunAsync(args[1..], services, ct),
            "record-tool-call" => await new RecordToolCallCommand().RunAsync(args[1..], services, ct),
            "summarize-tool-calls" => await new SummarizeToolCallsCommand().RunAsync(args[1..], services, ct),
            "get-tool-guidelines" => await new GetToolGuidelinesCommand().RunAsync(args[1..], services, ct),
            "get-tool-statistics" => await new GetToolStatisticsCommand().RunAsync(args[1..], services, ct),
            "list-tool-calls" => await new ListToolCallsCommand().RunAsync(args[1..], services, ct),
            "kg-resolve" => await new KgResolveCommand().RunAsync(args[1..], services, ct),
            "room-patterns" => await new RoomPatternsCommand().RunAsync(args[1..], services, ct),
            "kg-eval" => await new KgEvalCommand().RunAsync(args[1..], services, ct),
            "skill-opt" => await new SkillOptCommand().RunAsync(args[1..], services, ct),
            "graph" => await GraphCommand.RunAsync(args[1..], services, ct),
            "rescore" => await new RescoreCommand().RunAsync(args[1..], services, ct),
            "calibrate" => await new CalibrateCommand().RunAsync(args[1..], services, ct),
            "serve" => -2,
            "--help" or "-h" or "help" => PrintRootHelp(),
            "--version" or "-v" => PrintVersion(),
            _ => -1, // unknown - fall through to stdio MCP
        };
    }

    private static async Task<int> DispatchPendingAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        if (args.Length == 0)
            return PrintPendingHelp();

        return args[0] switch
        {
            "list" => await new PendingListCommand().RunAsync(args[1..], services, ct),
            "dismiss" => await new PendingDismissCommand().RunAsync(args[1..], services, ct),
            _ => UnknownPending(args[0]),
        };
    }

    private static int PrintPendingHelp()
    {
        Console.Out.WriteLine("""
            Usage:
              wendmem pending list --wing W [--page <path>] [--limit N]
              wendmem pending dismiss --page <path> --drawer <id>
            """);
        return 0;
    }

    private static int UnknownPending(string sub)
    {
        Console.Error.WriteLine($"Unknown pending subcommand: '{sub}'");
        return 2;
    }

    private static async Task<int> DispatchWikiAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        if (args.Length == 0)
            return PrintWikiHelp();

        return args[0] switch
        {
            "list" => await new WikiListCommand().RunAsync(args[1..], services, ct),
            "lint" => await new WikiLintCommand().RunAsync(args[1..], services, ct),
            "read" => await new WikiReadCommand().RunAsync(args[1..], services, ct),
            _ => UnknownWiki(args[0]),
        };
    }

    private static int PrintRootHelp()
    {
        Console.Out.WriteLine("""
            wendmem - local AI agent memory system

            Usage:
              wendmem                             Start stdio MCP server (default)
              wendmem serve [--http]              Start MCP server (use --http for HTTP transport on port 5133)

            Drawer operations:
              wendmem search <query>              Search drawers (FTS)
              wendmem search-semantic <query>     Search drawers (cosine)
              wendmem grep <query>                Context-window grep
              wendmem grep-exact <pattern>        Exact string or regex search
              wendmem mine <path> --wing W        Mine a file or directory
              wendmem mine-conversation <path>    Mine a conversation transcript
              wendmem sweep <path> --wing W       Scan for missed/stale files
              wendmem wakeup-full [--wing W]        Full wakeup with content
              wendmem save-session <text> --wing W   Save session state
              wendmem delete-drawer <id>          Delete a drawer
              wendmem prune --wing W              Prune near-duplicate drawers

            Wiki:
              wendmem wiki list [--wing W]        List wiki pages
              wendmem wiki lint [--wing W] [--json]  Run wiki health checks
              wendmem wiki read <path>            Read a wiki page

            Pending updates:
              wendmem pending list --wing W [--page <path>] [--limit N]
              wendmem pending dismiss --page <path> --drawer <id>

            Activity:
              wendmem activity [--wing W] [--limit N]

            Distill:
              wendmem distill --wing W --summary <text> [--hints <paths>]

            Knowledge graph:
              wendmem add-tunnel --topic T ...    Create cross-wing tunnel
              wendmem list-tunnels --wing W --room R
              wendmem list-tunnels-by-topic <topic>
              wendmem kg-resolve --wing W         Resolve duplicate entities & predicates
              wendmem kg-eval --wing W            Evaluate retrieval quality via KG triples
              wendmem skill-opt --wing W --skill S   Optimize SKILL.md using kg-eval validation
              wendmem room-patterns               Show fallback extensions to add to config

            Salience:
              wendmem rescore --wing W [--llm] [--limit N]

            Episodes:
              wendmem episode list --wing <wing> [--outcome success|failure|partial] [--limit N]
              wendmem episode show <id>
              wendmem episode delete <id>

            Skills:
              wendmem skills add <folder_path> [--wing <wing>] [--force]
              wendmem skills list [--wing <wing>] [--json]
              wendmem skills show <name|id>
              wendmem skills update <name|id>
              wendmem skills remove <name|id> [--force] [--yes]
              wendmem skills reindex [--root <dir>] [--wing <wing>]
              wendmem skills validate <folder_path>
              wendmem skills new <name> [--root <dir>]

            Reflection:
              wendmem reflect run --wing <wing> [--lookback N] [--write]
              wendmem reflect drafts list --wing <wing>
              wendmem reflect drafts show <id>
              wendmem reflect drafts dismiss <id>
              wendmem reflect drafts accept <id>

            Calibration:
              wendmem calibrate --wing W [--samples N] [--write-config] [--dry-run]

            Graph visualization:
              wendmem graph --wing W [--output <path>] [--limit N]
                            [--no-drawers] [--no-triples] [--no-episodes] [--no-skills]

            Experience memory:
              wendmem search-task-memory <query> --wing W
              wendmem distill-task-memory <file> --wing W
              wendmem record-outcome [--success] <id...>
              wendmem reflect-on-failure --failed <f> --wing W [--success <s>]
              wendmem prune-task-memory --wing W
              wendmem export-task-memory --wing W --output <path>
              wendmem import-task-memory <path> --wing W

            Tool memory:
              wendmem record-tool-call --wing W --tool T
              wendmem summarize-tool-calls --wing W --tool T
              wendmem get-tool-guidelines --wing W --tool T
              wendmem get-tool-statistics --wing W --tool T
              wendmem list-tool-calls --wing W --tool T

            General:
              wendmem stats                       Show palace statistics
              wendmem wings                       List all wings and rooms
              wendmem --help                      Show this help
              wendmem --version                   Show version
            """);
        return 0;
    }

    private static int PrintWikiHelp()
    {
        Console.Out.WriteLine("""
            Usage:
              wendmem wiki list [--wing W]
              wendmem wiki lint [--wing W]
              wendmem wiki read <path>
            """);
        return 0;
    }

    private static int PrintVersion()
    {
        var v = typeof(CliDispatcher).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        Console.Out.WriteLine($"wendmem {v}");
        return 0;
    }

    private static int UnknownWiki(string sub)
    {
        Console.Error.WriteLine($"Unknown wiki subcommand: '{sub}'");
        return 2;
    }

    private static async Task<int> DispatchEpisodeAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            Console.Out.WriteLine("""
                Usage:
                  wendmem episode list --wing <wing> [--outcome success|failure|partial] [--limit N]
                  wendmem episode show <id>
                  wendmem episode delete <id>
                """);
            return 0;
        }

        return args[0] switch
        {
            "list" => await new EpisodeListCommand().RunAsync(args[1..], services, ct),
            "show" => await new EpisodeShowCommand().RunAsync(args[1..], services, ct),
            "delete" => await new EpisodeDeleteCommand().RunAsync(args[1..], services, ct),
            _ => UnknownEpisode(args[0]),
        };
    }

    private static int UnknownEpisode(string sub)
    {
        Console.Error.WriteLine($"Unknown episode subcommand: '{sub}'");
        return 2;
    }

    private static async Task<int> DispatchSkillsAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            Console.Out.WriteLine("""
                Usage:
                  wendmem skills add <folder_path> [--wing <wing>] [--force]
                  wendmem skills list [--wing <wing>] [--json]
                  wendmem skills show <name|id>
                  wendmem skills update <name|id>
                  wendmem skills remove <name|id> [--force] [--yes]
                  wendmem skills reindex [--root <dir>] [--wing <wing>]
                  wendmem skills validate <folder_path>
                  wendmem skills new <name> [--root <dir>]
                """);
            return 0;
        }

        return args[0] switch
        {
            "add" => await new SkillsAddCommand().RunAsync(args[1..], services, ct),
            "list" => await new SkillsListCommand().RunAsync(args[1..], services, ct),
            "show" => await new SkillsShowCommand().RunAsync(args[1..], services, ct),
            "update" => await new SkillsUpdateCommand().RunAsync(args[1..], services, ct),
            "remove" => await new SkillsRemoveCommand().RunAsync(args[1..], services, ct),
            "reindex" => await new SkillsReindexCommand().RunAsync(args[1..], services, ct),
            "validate" => await new SkillsValidateCommand().RunAsync(args[1..], ct),
            "new" => await new SkillsNewCommand().RunAsync(args[1..], ct),
            _ => UnknownSkills(args[0]),
        };
    }

    private static int UnknownSkills(string sub)
    {
        Console.Error.WriteLine($"Unknown skills subcommand: '{sub}'");
        return 2;
    }

    private static async Task<int> DispatchReflectAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            Console.Out.WriteLine("""
                Usage:
                  wendmem reflect run --wing <wing> [--lookback N] [--write]
                  wendmem reflect drafts list --wing <wing> [--status pending|accepted|dismissed]
                  wendmem reflect drafts show <id>
                  wendmem reflect drafts dismiss <id>
                  wendmem reflect drafts accept <id>
                """);
            return 0;
        }

        if (args[0] == "run")
            return await new ReflectRunCommand().RunAsync(args[1..], services, ct);

        if (args[0] == "drafts")
        {
            if (args.Length < 2)
                goto help;
            return args[1] switch
            {
                "list" => await new ReflectDraftsListCommand().RunAsync(args[2..], services, ct),
                "show" => await new ReflectDraftsShowCommand().RunAsync(args[2..], services, ct),
                "dismiss" => await new ReflectDraftsDismissCommand().RunAsync(args[2..], services, ct),
                "accept" => await new ReflectDraftsAcceptCommand().RunAsync(args[2..], services, ct),
                _ => UnknownReflect(args[1])
            };
        }

help:
        Console.Error.WriteLine("Unknown reflect subcommand. Use 'run' or 'drafts'.");
        return 2;
    }

    private static int UnknownReflect(string sub)
    {
        Console.Error.WriteLine($"Unknown reflect subcommand: '{sub}'");
        return 2;
    }
}
