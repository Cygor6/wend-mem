using Wendmem.Services.Okf;

namespace Wendmem.Tests;

/// <summary>
/// Round-trip + permissive-consumption tests for the OKF frontmatter parser.
/// The parser MUST tolerate: tags lists at column 0 (as OKF producers emit),
/// arbitrary producer keys, quoted scalars (including edge cases like '```'),
/// folded blocks, and empty type (caller skips, parser doesn't throw).
/// </summary>
public sealed class OkfFrontmatterParserTests
{
    [Test]
    public async Task Parse_TagsAtColumnZero()
    {
        const string content = """
            ---
            type: Reference
            title: Configuration
            description: A config reference.
            tags:
            - config
            - settings
            ---

            # Configuration
            """;

        var ok = OkfFrontmatterParser.TryParse(content, out var fm, out var body, out var error);

        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(fm!.Type).IsEqualTo("Reference");
        await Assert.That(fm.Tags).Contains("config");
        await Assert.That(fm.Tags).Contains("settings");
        await Assert.That(body.TrimStart().StartsWith("# Configuration")).IsTrue();
    }

    [Test]
    public async Task Parse_UnknownKeysPreserved()
    {
        const string content = """
            ---
            type: Playbook
            producer: acme-tools
            priority: high
            custom_field: whatever
            ---

            Body.
            """;

        var ok = OkfFrontmatterParser.TryParse(content, out var fm, out _, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(fm!.Extra).ContainsKey("producer");
        await Assert.That(fm.Extra["producer"]).IsEqualTo("acme-tools");
        await Assert.That(fm.Extra["priority"]).IsEqualTo("high");
    }

    [Test]
    public async Task Parse_QuotedBackticks()
    {
        // Real-world edge: a producer quoted a code block as the description value.
        const string content = """
            ---
            type: Glossary Term
            title: Pipeline
            description: '```'
            tags:
            - devops
            ---

            # Pipeline
            """;

        var ok = OkfFrontmatterParser.TryParse(content, out var fm, out _, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(fm!.Description).IsEqualTo("```");
        await Assert.That(fm.Tags[0]).IsEqualTo("devops");
    }

    [Test]
    public async Task Parse_EmptyType_ReturnsEmpty()
    {
        // Caller decides to skip; parser does not throw.
        const string content = """
            ---
            type:
            title: NoType
            ---

            Body.
            """;

        var ok = OkfFrontmatterParser.TryParse(content, out var fm, out _, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(fm!.Type).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Parse_NoFrontmatter_ReturnsFalse()
    {
        const string content = "# Just a heading, no frontmatter";

        var ok = OkfFrontmatterParser.TryParse(content, out var fm, out _, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(fm).IsNull();
        await Assert.That(error).IsEqualTo("no frontmatter block");
    }

    [Test]
    public async Task Parse_FoldedDescription()
    {
        const string content = """
            ---
            type: Reference
            description: >
              This is a long description
              that spans multiple lines
              and should be folded.
            ---

            Body.
            """;

        var ok = OkfFrontmatterParser.TryParse(content, out var fm, out _, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(fm!.Description!).Contains("long description");
        await Assert.That(fm.Description!).Contains("multiple lines");
        await Assert.That(fm.Description!).Contains("folded");
    }
}
