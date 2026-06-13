using System.Buffers;
using System.Collections.Frozen;

namespace Wendmem.Services;

/// <summary>
/// Deterministic entity classification. Uses heuristic rules (no LLM)
/// so classification is always available, consistent, and free.
///
/// Bilingual (English/Swedish) aware:
///  - Swedish person names (å/ä/ö, hyphenated given names, "von"/"af" particles)
///  - Swedish project/system suffixes incl. definite forms ("tjänsten", "flödet")
///  - Organization detection via legal suffixes (AB, HB, GmbH, Inc, ...) and a
///    configurable known-organizations list (customers like Nestlé etc.)
///
/// Returned types: "tool", "person", "project", "organization", "concept".
/// </summary>
public static class EntityClassifier
{
    // ── Default data ─────────────────────────────────────────────────────

    static readonly string[] DefaultKnownTools =
    [
        // Containers / infra / devops
        "docker", "kubernetes", "k8s", "helm", "kustomize", "argocd", "flux",
        "rancher", "traefik", "istio", "consul", "vault", "nginx",
        "terraform", "ansible", "jenkins", "circleci", "travis", "teamcity",
        "octopus", "octopusdeploy", "octopus deploy",
        "github actions", "azure devops",
        "git", "github", "gitlab", "bitbucket",

        // OS / platforms
        "linux", "ubuntu", "debian", "fedora", "macos",
        "windows", "windows server", "wsl", "wsl2", "hyper-v", "iis",
        "aws", "azure", "gcp", "cloudflare", "vercel",

        // Databases / data
        "sqlserver", "sql-server", "sql server", "mssql", "ms-sql",
        "postgres", "postgresql", "mysql", "mariadb", "sqlite", "cockroachdb",
        "redis", "mongodb", "elasticsearch", "elasticsaerch", "elastic search",
        "supabase", "sql", "duckdb", "ducklake", "dvector", "chromadb",
        "qdrant", "milvus", "snowflake", "bigquery", "redshift", "databricks",
        "unity catalog", "delta lake", "iceberg", "parquet", "arrow",
        "dbt", "dbt core", "dbt-core", "power bi", "powerbi", "fabric",
        "synapse", "data factory", "ssis", "ssrs", "ssas",
        "spark", "apache spark", "polars", "pandas",

        // Messaging / integration
        "kafka", "rabbitmq", "masstransit", "mass-transit",
        "service bus", "servicebus", "azure service bus",
        "nats", "mqtt", "amqp", "grpc", "soap", "wcf",
        "edi", "edifact", "sftp",

        // .NET ecosystem
        "dotnet", "net", ".net aspire", "aspire", "clr", "roslyn", "nuget",
        "msbuild", "nativeaot", "kestrel", "blazor", "maui", "wpf", "winforms",
        "signalr", "dapper", "efcore", "ef core", "entity framework",
        "polly", "serilog", "nlog", "mediatr", "automapper", "fluentvalidation",
        "hangfire", "quartz", "swagger", "openapi", "swashbuckle",
        "xunit", "nunit", "moq", "nsubstitute",

        // Languages
        "python", "javascript", "typescript", "rust", "golang", "go",
        "java", "kotlin", "swift", "ruby", "php", "csharp", "c#", "cs",
        "haskell", "scala", "clojure", "elixir", "zig",
        "powershell", "pwsh", "bash", "zsh",

        // Editors / IDEs
        "vscode", "vs code", "visual studio code", "visual studio", "code",
        "rider", "jetbrains", "zed", "vim", "emacs",

        // Web / frontend
        "react", "vue", "angular", "svelte", "nextjs", "next.js", "nuxt", "bun",
        "express", "fastapi", "flask", "django", "rails", "spring",
        "webpack", "vite", "babel", "eslint", "prettier",

        // AI / ML
        "ollama", "openai", "anthropic", "litellm", "claude", "claude code",
        "gemini", "goose", "copilot", "github copilot", "mcp",
        "llama.cpp", "llamacpp", "vllm", "qwen", "mistral",
        "langchain", "semantic kernel",
        "onnx", "pytorch", "tensorflow", "huggingface",

        // Observability
        "grafana", "prometheus", "datadog", "sentry",

        // Formats
        "json", "xml", "csv", "markdown", "md", "yaml", "yml",
    ];

    static readonly string[] DefaultKnownOrganizations =
    [
        // Configure() can extend this with customer/partner names per installation.
        "nowaste", "nowaste logistics",
        "nestlé", "nestle",
        "peak performance",
        "öresundskraft", "oresundskraft",
        "postnord", "dhl", "schenker", "dsv", "bring",
        "microsoft", "google", "amazon", "apple", "meta",
        "anthropic", "nvidia", "intel",
    ];

    static readonly string[] DefaultKnownSuffixes =
    [
        "db", "sql", "cli", "api", "sdk", "ide", "os", "ui", "etl", "dto",
        "json", "xml", "csv", "md", "cs", "razor", "csproj", "sln", "slnx",
    ];

    // Project-ish suffixes, English + Swedish (incl. Swedish definite forms,
    // since EndsWith("tjänst") does not match "tjänsten").
    static readonly string[] ProjectSuffixes =
    [
        // English
        "project", "app", "application", "service", "server", "client",
        "platform", "portal", "integration", "system", "pipeline", "module",
        // Swedish
        "projekt", "projektet", "tjänst", "tjänsten", "applikation",
        "applikationen", "appen", "klient", "klienten", "servern",
        "plattform", "plattformen", "portalen", "integrationen",
        "systemet", "modul", "modulen", "flöde", "flödet",
        "lösning", "lösningen",
    ];

    // Legal-form suffixes that mark an organization when they appear as the
    // last whitespace-separated token ("Nowaste Logistics AB").
    static readonly FrozenSet<string> OrgLegalSuffixes = new string[]
    {
        "ab", "hb", "kb",                       // Sweden
        "as", "a/s", "asa",                     // Norway/Denmark
        "aps", "oy", "oyj",                     // Denmark/Finland
        "gmbh", "ag",                           // DACH
        "inc", "inc.", "ltd", "ltd.", "llc",    // EN
        "corp", "corp.", "plc", "co.",
        "bv", "b.v.", "sa", "s.a.", "srl",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> OrgLegalSuffixesLookup =
        OrgLegalSuffixes.GetAlternateLookup<ReadOnlySpan<char>>();

    // Lowercase particles allowed inside (not first/last word of) a person name:
    // "Jan Erik von Sydow", "Maria af Klint".
    static readonly FrozenSet<string> NameParticles = new string[]
    {
        "von", "af", "de", "van", "der", "den", "di", "da", "la", "le", "el",
    }.ToFrozenSet(StringComparer.Ordinal);

    static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> NameParticlesLookup =
        NameParticles.GetAlternateLookup<ReadOnlySpan<char>>();

    static readonly SearchValues<char> BaseNameSeparators = SearchValues.Create(['-', '_', '.']);
    static readonly SearchValues<char> PathSeparators = SearchValues.Create(['/', '\\']);

    // ── Configurable sets (rebuilt by Configure) ─────────────────────────

    static FrozenSet<string> _knownTools = null!;
    static FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> _knownToolsLookup;
    static FrozenSet<string> _knownOrganizations = null!;
    static FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> _knownOrganizationsLookup;
    static FrozenSet<string> _knownSuffixes = null!;
    static FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> _knownSuffixesLookup;

    static EntityClassifier()
    {
        SetTools(DefaultKnownTools);
        SetOrganizations(DefaultKnownOrganizations);
        SetSuffixes(DefaultKnownSuffixes);
    }

    /// <summary>
    /// Extends the built-in defaults with installation-specific names, e.g.
    /// customer organizations or in-house tools loaded from appsettings.json.
    /// Call once at startup (not thread-safe to call concurrently with Classify).
    /// </summary>
    public static void Configure(
        IEnumerable<string>? extraTools = null,
        IEnumerable<string>? extraOrganizations = null,
        IEnumerable<string>? extraSuffixes = null)
    {
        if (extraTools is not null)
            SetTools(DefaultKnownTools.Concat(extraTools));
        if (extraOrganizations is not null)
            SetOrganizations(DefaultKnownOrganizations.Concat(extraOrganizations));
        if (extraSuffixes is not null)
            SetSuffixes(DefaultKnownSuffixes.Concat(extraSuffixes));
    }

    static void SetTools(IEnumerable<string> values)
    {
        _knownTools = values.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _knownToolsLookup = _knownTools.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    static void SetOrganizations(IEnumerable<string> values)
    {
        _knownOrganizations = values.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _knownOrganizationsLookup = _knownOrganizations.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    static void SetSuffixes(IEnumerable<string> values)
    {
        _knownSuffixes = values.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _knownSuffixesLookup = _knownSuffixes.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    // ── Classification ───────────────────────────────────────────────────

    /// <summary>
    /// Classify an entity name deterministically.
    /// </summary>
    public static string Classify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "concept";

        ReadOnlySpan<char> nameSpan = name.AsSpan().Trim();

        // 1. Concept: numbers, versions ("1.2.3", "v2.1"), GUIDs
        if (nameSpan.Length <= 4 && float.TryParse(nameSpan, out _))
            return "concept";

        if (IsVersionLike(nameSpan))
            return "concept";

        if (nameSpan.Length is 32 or 36 or 38 && Guid.TryParse(nameSpan, out _))
            return "concept";

        // 2. Tool: file extensions (".razor")
        if (nameSpan[0] == '.' && nameSpan.Length > 1
            && nameSpan[1..].IndexOf('.') < 0 && IsAllLetterOrDigit(nameSpan[1..]))
            return "tool";

        // 3. Tool: paths with extensions
        if (nameSpan.ContainsAny(PathSeparators) && Path.HasExtension(nameSpan))
            return "tool";

        // 4. Tool: known tools (full name, or base name before -/_/.)
        int separatorIndex = nameSpan.IndexOfAny(BaseNameSeparators);
        ReadOnlySpan<char> baseName = separatorIndex >= 0 ? nameSpan[..separatorIndex] : nameSpan;

        if (_knownToolsLookup.Contains(baseName) || _knownToolsLookup.Contains(nameSpan))
            return "tool";

        // 5. Organization: known organizations, or legal suffix ("... AB")
        if (_knownOrganizationsLookup.Contains(nameSpan) || HasOrgLegalSuffix(nameSpan))
            return "organization";

        // 6. Tool: known suffixes — but only at a real boundary, so Swedish
        //    words like "kaos" (ends in "os") or "ide" don't misclassify.
        //    Matches "order-db", "kund_api", "Order.DB", "OrderDB", "kundApi".
        if (HasToolSuffix(nameSpan))
            return "tool";

        // 7. Tool: qualified dot names (System.Text.Json)
        if (nameSpan.Contains('.') && !nameSpan.Contains(' ') && IsQualifiedDotName(nameSpan))
            return "tool";

        // 8. Tool: PascalCase, or ALL_CAPS of length >= 3
        if (!nameSpan.Contains(' ')
            && (IsPascalCase(nameSpan)
                || (nameSpan.Length >= 3 && IsAllCapsOrSnakeCase(nameSpan))))
            return "tool";

        // 9. Person: indicators ("user42", "admin", "användare") & name patterns
        if (IsUserIndicator(nameSpan) || IsPersonName(nameSpan))
            return "person";

        // 10. Project: common suffixes (English + Swedish)
        foreach (var suffix in ProjectSuffixes)
        {
            if (nameSpan.Length > suffix.Length
                && nameSpan.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return "project";
        }

        // Default fallback
        return "concept";
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    static bool IsVersionLike(ReadOnlySpan<char> span)
    {
        if ((span[0] == 'v' || span[0] == 'V') && span.Length >= 3)
            span = span[1..];

        if (span.Length < 3 || !char.IsDigit(span[0]) || !char.IsDigit(span[^1]))
            return false;

        foreach (char c in span)
        {
            if (!char.IsDigit(c) && c != '.')
                return false;
        }
        return true;
    }

    static bool IsAllLetterOrDigit(ReadOnlySpan<char> span)
    {
        foreach (char c in span)
        {
            if (!char.IsLetterOrDigit(c))
                return false;
        }
        return true;
    }

    static bool HasOrgLegalSuffix(ReadOnlySpan<char> span)
    {
        int lastSpace = span.LastIndexOf(' ');
        if (lastSpace <= 0 || lastSpace == span.Length - 1)
            return false;
        return OrgLegalSuffixesLookup.Contains(span[(lastSpace + 1)..]);
    }

    static bool HasToolSuffix(ReadOnlySpan<char> span)
    {
        // a) Suffix as its own segment after -/_/. : "order-db", "kund_api"
        int sep = span.LastIndexOfAny(BaseNameSeparators);
        if (sep > 0 && sep < span.Length - 1 && _knownSuffixesLookup.Contains(span[(sep + 1)..]))
            return true;

        // b) Case-boundary suffix: "OrderDB", "kundApi" — the suffix starts
        //    with an uppercase letter preceded by a non-uppercase character.
        foreach (var suffix in _knownSuffixes)
        {
            if (span.Length > suffix.Length
                && span.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && char.IsUpper(span[span.Length - suffix.Length])
                && !char.IsUpper(span[span.Length - suffix.Length - 1]))
                return true;
        }
        return false;
    }

    static bool IsQualifiedDotName(ReadOnlySpan<char> span)
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

    static bool IsPascalCase(ReadOnlySpan<char> span)
    {
        if (span.Length < 3 || !char.IsUpper(span[0]))
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

    static bool IsAllCapsOrSnakeCase(ReadOnlySpan<char> span)
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

    static bool IsUserIndicator(ReadOnlySpan<char> span)
    {
        foreach (var prefix in (ReadOnlySpan<string>)["user", "admin", "användar"])
        {
            if (!span.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var rest = span[prefix.Length..];
            // "user", "admin42", "användare", "user 7" — but not "user-service".
            if (rest.IsEmpty || char.IsDigit(rest[0]) || rest[0] == ' '
                || rest.Equals("e", StringComparison.OrdinalIgnoreCase)
                || rest.Equals("en", StringComparison.OrdinalIgnoreCase)
                || rest.Equals("name", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static bool IsPersonName(ReadOnlySpan<char> span)
    {
        // 2–4 words: "Jonas Andersson", "Karl-Johan Svensson", "Jan von Sydow".
        // char.IsUpper/IsLower are Unicode-aware, so å/ä/ö work out of the box.
        Span<Range> ranges = stackalloc Range[5];
        int count = span.Split(ranges, ' ', StringSplitOptions.RemoveEmptyEntries);

        if (count < 2 || count > 4)
            return false;

        for (int i = 0; i < count; i++)
        {
            var word = span[ranges[i]];
            bool particleAllowed = i > 0 && i < count - 1;

            if (particleAllowed && NameParticlesLookup.Contains(word))
                continue;

            if (!IsCapitalizedNameWord(word))
                return false;
        }
        return true;
    }

    static bool IsCapitalizedNameWord(ReadOnlySpan<char> word)
    {
        if (word.Length < 2 || !char.IsUpper(word[0]))
            return false;

        for (int i = 1; i < word.Length; i++)
        {
            char c = word[i];
            if (char.IsLower(c))
                continue;

            // Allow "Karl-Johan" and "O'Brien": -/' followed by an uppercase letter.
            if ((c == '-' || c == '\'') && i + 1 < word.Length && char.IsUpper(word[i + 1]))
            {
                i++; // the uppercase letter is already validated
                continue;
            }
            return false;
        }
        return true;
    }
}
