using System.Buffers;
using System.Collections.Frozen;

namespace Wendmem.Services;

/// <summary>
/// Deterministic entity classification. Uses heuristic rules (no LLM)
/// so classification is always available, consistent, and free.
/// </summary>
public static class EntityClassifier
{
    private static readonly FrozenSet<string> KnownTools = new string[]
    {
        "docker", "kubernetes", "k8s", "nginx", "postgres", "postgresql",
        "sqlserver", "sql-server", "sql server", "mssql", "ms-sql",
        "mysql", "redis", "mongodb", "elasticsearch", "kafka", "rabbitmq",
        "git", "github", "gitlab", "bitbucket", "vscode", "vs code", "visual studio code", "code", "vim", "emacs",
        "terraform", "ansible", "jenkins", "circleci", "travis",
        "webpack", "vite", "babel", "eslint", "prettier",
        "react", "vue", "angular", "svelte", "nextjs", "next.js", "nuxt", "bun",
        "express", "fastapi", "flask", "django", "rails", "spring",
        "dotnet", "net", "clr", "roslyn", "nuget", "msbuild", "aspire", ".net aspire",
        "python", "javascript", "typescript", "rust", "golang", "go",
        "java", "kotlin", "swift", "ruby", "php", "csharp", "c#", "cs",
        "haskell", "scala", "clojure", "elixir", "zig",
        "aws", "azure", "gcp", "cloudflare", "vercel", "octopus", "octopusdeploy", "octopus deploy",
        "linux", "ubuntu", "debian", "fedora", "macos", "windows",
        "sqlite", "mariadb", "cockroachdb", "supabase", "sql",
        "snowflake", "bigquery", "redshift", "databricks",
        "ollama", "openai", "anthropic", "litellm",
        "onnx", "pytorch", "tensorflow", "huggingface",
        "duckdb", "dvector", "chromadb", "qdrant", "milvus",
        "grafana", "prometheus", "datadog", "sentry",
        "masstransit", "mass-transit",
        "json", "xml", "csv", "markdown", "md", "yaml", "yml",
        "dbt", "dbt core", "dbt-core",
        "elasticsaerch", "elastic search"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> KnownToolsSpanLookup =
        KnownTools.GetAlternateLookup<ReadOnlySpan<char>>();

    private static readonly FrozenSet<string> KnownSuffixes = new string[]
    {
        "db", "sql", "cli", "api", "sdk", "ide", "os",
        "json", "xml", "csv", "md", "cs", "razor", "csproj", "sln", "slnx"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly SearchValues<char> BaseNameSeparators = SearchValues.Create(['-', '_', '.']);
    private static readonly SearchValues<char> PathSeparators = SearchValues.Create(['/', '\\']);

    /// <summary>
    /// Classify an entity name deterministically.
    /// </summary>
    public static string Classify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "concept";

        ReadOnlySpan<char> nameSpan = name.AsSpan().Trim();

        // 1. Concept: Numbers & Versions
        if (nameSpan.Length <= 4 && float.TryParse(nameSpan, out _))
            return "concept";

        if (IsPureVersionString(nameSpan))
            return "concept";

        // 2. Tool: File Extensions (e.g., ".razor")
        if (nameSpan[0] == '.' && nameSpan.Length > 1 && nameSpan[1..].IndexOf('.') < 0 && IsAllLetterOrDigit(nameSpan[1..]))
            return "tool";

        // 3. Tool: Paths with extensions
        if (nameSpan.ContainsAny(PathSeparators) && Path.HasExtension(nameSpan))
            return "tool";

        // 4. Tool: Known Tools
        int separatorIndex = nameSpan.IndexOfAny(BaseNameSeparators);

        ReadOnlySpan<char> baseName = separatorIndex >= 0 ? nameSpan[..separatorIndex] : nameSpan;

        if (KnownToolsSpanLookup.Contains(baseName) || KnownToolsSpanLookup.Contains(nameSpan))
            return "tool";

        // 5. Tool: Known Suffixes
        foreach (var suffix in KnownSuffixes)
        {
            if (nameSpan.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && nameSpan.Length > suffix.Length)
                return "tool";
        }

        // 6. Tool: Qualified Dot Names (System.Text.Json)
        if (nameSpan.Contains('.') && !nameSpan.Contains(' ') && IsQualifiedDotName(nameSpan))
            return "tool";

        // 7. Tool: PascalCase or ALL_CAPS
        if (nameSpan.Length >= 2 && !nameSpan.Contains(' ') && (IsPascalCase(nameSpan) || IsAllCapsOrSnakeCase(nameSpan)))
            return "tool";

        // 8. Person: Common indicators & name patterns
        if (nameSpan.StartsWith("user", StringComparison.OrdinalIgnoreCase) ||
            nameSpan.StartsWith("admin", StringComparison.OrdinalIgnoreCase) ||
            IsPersonName(nameSpan))
            return "person";

        // 9. Project: Common suffixes
        if (nameSpan.EndsWith("project", StringComparison.OrdinalIgnoreCase) ||
            nameSpan.EndsWith("app", StringComparison.OrdinalIgnoreCase) ||
            nameSpan.EndsWith("service", StringComparison.OrdinalIgnoreCase) ||
            nameSpan.EndsWith("server", StringComparison.OrdinalIgnoreCase) ||
            nameSpan.EndsWith("client", StringComparison.OrdinalIgnoreCase))
            return "project";

        // Default fallback
        return "concept";
    }

    private static bool IsPureVersionString(ReadOnlySpan<char> span)
    {
        if (span.Length < 3)
            return false;
        foreach (char c in span)
        {
            if (!char.IsDigit(c) && c != '.')
                return false;
        }
        return true;
    }

    private static bool IsAllLetterOrDigit(ReadOnlySpan<char> span)
    {
        foreach (char c in span)
        {
            if (!char.IsLetterOrDigit(c))
                return false;
        }
        return true;
    }

    private static bool IsQualifiedDotName(ReadOnlySpan<char> span)
    {
        int count = 0;
        int start = 0;

        while (start < span.Length)
        {
            int dotIndex = span[start..].IndexOf('.');
            ReadOnlySpan<char> part = dotIndex < 0 ? span[start..] : span.Slice(start, dotIndex);

            if (part.Length == 0 || !char.IsUpper(part[0]))
                return false;

            count++;
            if (dotIndex < 0)
                break;
            start += dotIndex + 1;
        }
        return count >= 2;
    }

    private static bool IsPascalCase(ReadOnlySpan<char> span)
    {
        if (!char.IsUpper(span[0]))
            return false;

        bool hasLower = false;
        bool hasSubsequentUpper = false;

        for (int i = 1; i < span.Length; i++)
        {
            if (char.IsLower(span[i]))
                hasLower = true;
            if (char.IsUpper(span[i]))
                hasSubsequentUpper = true;
        }
        return hasLower && hasSubsequentUpper;
    }

    private static bool IsAllCapsOrSnakeCase(ReadOnlySpan<char> span)
    {
        bool hasUpper = false;
        foreach (char c in span)
        {
            if (char.IsLower(c))
                return false;
            if (char.IsUpper(c))
                hasUpper = true;
            else if (!char.IsDigit(c) && c != '_')
                return false;
        }
        return hasUpper;
    }

    private static bool IsPersonName(ReadOnlySpan<char> span)
    {
        int spaces = 0;
        foreach (char c in span)
            if (c == ' ')
                spaces++;

        if (spaces < 1 || spaces > 2)
            return false;

        int start = 0;
        while (start < span.Length)
        {
            int spaceIndex = span[start..].IndexOf(' ');
            ReadOnlySpan<char> word = spaceIndex < 0 ? span[start..] : span.Slice(start, spaceIndex);

            if (word.Length < 2 || !char.IsUpper(word[0]))
                return false;
            for (int i = 1; i < word.Length; i++)
            {
                if (!char.IsLower(word[i]))
                    return false;
            }

            if (spaceIndex < 0)
                break;
            start += spaceIndex + 1;
        }
        return true;
    }
}
