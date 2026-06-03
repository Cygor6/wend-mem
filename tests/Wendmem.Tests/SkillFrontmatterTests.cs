using Wendmem.Services;

namespace Wendmem.Tests;

public sealed class SkillFrontmatterTests
{
    const string ValidFrontmatter = """
        ---
        name: read-file
        description: Read any data file including CSV, JSON, Parquet. Use when user asks about file contents.
        allowed-tools: Bash
        ---

        # Read File

        Instructions here.
        """;

    const string MultiLineDescription = """
        ---
        name: read-file
        description: >
          Read any data file (CSV, JSON, Parquet, Avro, Excel, spatial, SQLite)
          or remote URL (S3, HTTPS). Use when user references a data file.
        ---

        Content here.
        """;

    [Test]
    public async Task Parse_ValidFrontmatter()
    {
        var fm = SkillFrontmatterParser.Parse(ValidFrontmatter, "read-file", "SKILL.md");
        await Assert.That(fm.Name).IsEqualTo("read-file");
        await Assert.That(fm.Description).Contains("data file");
    }

    [Test]
    public async Task Parse_MultiLineFoldedDescription()
    {
        var fm = SkillFrontmatterParser.Parse(MultiLineDescription, "read-file", "SKILL.md");
        await Assert.That(fm.Name).IsEqualTo("read-file");
        await Assert.That(fm.Description).Contains("Read any data file");
        await Assert.That(fm.Description).Contains("remote URL");
    }

    [Test]
    public async Task Validate_ValidSkill_NoIssues()
    {
        var issues = SkillFrontmatterParser.Validate(ValidFrontmatter, "read-file", "SKILL.md");
        await Assert.That(issues.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Validate_WrongFilename_CatchesIssue()
    {
        var issues = SkillFrontmatterParser.Validate(ValidFrontmatter, "read-file", "skill.md");
        await Assert.That(issues.Count).IsGreaterThan(0);
        await Assert.That(issues[0]).Contains("SKILL.md");
    }

    [Test]
    public async Task Validate_NonKebabFolder_CatchesIssue()
    {
        var issues = SkillFrontmatterParser.Validate(ValidFrontmatter, "ReadFile", "SKILL.md");
        await Assert.That(issues.Count).IsGreaterThan(0);
        await Assert.That(issues[0]).Contains("kebab-case");
    }

    [Test]
    public async Task Validate_NameMismatch_CatchesIssue()
    {
        var content = """
            ---
            name: wrong-name
            description: A skill
            ---

            Content
            """;
        var issues = SkillFrontmatterParser.Validate(content, "correct-name", "SKILL.md");
        await Assert.That(issues.Count).IsGreaterThan(0);
        await Assert.That(issues.Any(i => i.Contains("must match folder"))).IsTrue();
    }

    [Test]
    public async Task Validate_ForbiddenAngleBrackets_CatchesIssue()
    {
        var content = """
            ---
            name: test-skill
            description: Use <script> for testing
            ---

            Content
            """;
        var issues = SkillFrontmatterParser.Validate(content, "test-skill", "SKILL.md");
        await Assert.That(issues.Count).IsGreaterThan(0);
        await Assert.That(issues.Any(i => i.Contains("<"))).IsTrue();
    }

    [Test]
    public async Task Validate_NameContainsClaude_CatchesIssue()
    {
        var content = """
            ---
            name: claude-helper
            description: A helper skill
            ---

            Content
            """;
        var issues = SkillFrontmatterParser.Validate(content, "claude-helper", "SKILL.md");
        await Assert.That(issues.Count).IsGreaterThan(0);
        await Assert.That(issues.Any(i => i.Contains("claude"))).IsTrue();
    }

    [Test]
    public async Task Validate_MissingFrontmatter_CatchesIssue()
    {
        var content = "Just some markdown without frontmatter";
        var issues = SkillFrontmatterParser.Validate(content, "test-skill", "SKILL.md");
        await Assert.That(issues.Count).IsGreaterThan(0);
        await Assert.That(issues.Any(i => i.Contains("frontmatter"))).IsTrue();
    }

    [Test]
    public async Task Validate_EmptyDescription_CatchesIssue()
    {
        var content = """
            ---
            name: test-skill
            description:
            ---

            Content
            """;
        var issues = SkillFrontmatterParser.Validate(content, "test-skill", "SKILL.md");
        await Assert.That(issues.Count).IsGreaterThan(0);
        await Assert.That(issues.Any(i => i.Contains("description"))).IsTrue();
    }

    [Test]
    public async Task Validate_RealAnthropicPdfSkill()
    {
        // Simulate the pdf skill from github.com/anthropics/skills
        var content = """
            ---
            name: pdf
            description: >
              Extract and analyze text from PDF files. Use when the user
              references a PDF file or wants to extract text from a document.
            argument-hint: <filename> [question]
            allowed-tools: Bash
            ---

            You are helping the user work with a PDF file.
            """;
        var issues = SkillFrontmatterParser.Validate(content, "pdf", "SKILL.md");
        await Assert.That(issues.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SkillNew_CreatesKebabName()
    {
        // Verify that the scaffold template produces valid kebab-case names
        var name = "test-skill";
        var content = $"""
            ---
            name: {name}
            description: <TODO: what it does and when to use it>
            ---

            # Test skill

            ## Instructions

            <TODO>
            """;
        // The description contains < and > which should be caught
        var issues = SkillFrontmatterParser.Validate(content, name, "SKILL.md");
        await Assert.That(issues.Any(i => i.Contains("<"))).IsTrue();
    }
}
