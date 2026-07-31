using Litos.Tools.Skills;

namespace Litos.Tools.Tests.Skills;

public class SkillFrontmatterTests
{
    [Fact]
    public void Parse_ExtractsNameAndDescription_AndBody()
    {
        var content = """
            ---
            name: pdf-extraction
            description: Extract tables from PDFs.
            ---
            Full instructions here.
            """;

        var (fields, body) = SkillFrontmatter.Parse(content);

        Assert.Equal("pdf-extraction", fields["name"]);
        Assert.Equal("Extract tables from PDFs.", fields["description"]);
        Assert.Equal("Full instructions here.", body);
    }

    [Fact]
    public void Parse_NoFrontmatter_ReturnsEmptyFieldsAndOriginalContent()
    {
        var content = "Just a plain document, no frontmatter.";

        var (fields, body) = SkillFrontmatter.Parse(content);

        Assert.Empty(fields);
        Assert.Equal(content, body);
    }

    [Fact]
    public void Parse_UnterminatedFrontmatter_DiscardsPartialFields_ReturnsOriginalContentUnchanged()
    {
        // Opening --- present, real-looking fields, but no closing --- — the whole block
        // degrades to "no frontmatter at all" rather than surfacing an error or returning
        // the partially-parsed fields.
        var content = """
            ---
            name: broken
            description: never closed
            still going...
            """;

        var (fields, body) = SkillFrontmatter.Parse(content);

        Assert.Empty(fields);
        Assert.Equal(content, body);
    }

    [Fact]
    public void Parse_OnlyFirstColonIsTheSeparator_RestStaysInTheValue()
    {
        var content = """
            ---
            description: has: a colon
            ---
            body
            """;

        var (fields, _) = SkillFrontmatter.Parse(content);

        Assert.Equal("has: a colon", fields["description"]);
    }

    [Fact]
    public void Parse_LinesWithoutColon_AreSilentlySkipped_NotAnError()
    {
        var content = """
            ---
            name: skill-with-junk-line
            this line has no colon and should be ignored
            description: still works
            ---
            body
            """;

        var (fields, _) = SkillFrontmatter.Parse(content);

        Assert.Equal("skill-with-junk-line", fields["name"]);
        Assert.Equal("still works", fields["description"]);
        Assert.Equal(2, fields.Count);
    }

    [Fact]
    public void Parse_DuplicateKeys_LastOneWins()
    {
        var content = """
            ---
            name: first
            name: second
            ---
            body
            """;

        var (fields, _) = SkillFrontmatter.Parse(content);

        Assert.Equal("second", fields["name"]);
    }

    [Fact]
    public void Parse_TrimsWhitespaceAroundKeysAndValues()
    {
        var content = """
            ---
              name  :   Foo Bar
            ---
            body
            """;

        var (fields, _) = SkillFrontmatter.Parse(content);

        Assert.Equal("Foo Bar", fields["name"]);
    }

    [Fact]
    public void Parse_EmptyContent_ReturnsEmptyFieldsAndEmptyBody()
    {
        var (fields, body) = SkillFrontmatter.Parse("");

        Assert.Empty(fields);
        Assert.Equal("", body);
    }

    [Fact]
    public void Parse_CrLfLineEndings_NormalizedBeforeParsing()
    {
        var content = "---\r\nname: crlf-skill\r\n---\r\nbody text";

        var (fields, body) = SkillFrontmatter.Parse(content);

        Assert.Equal("crlf-skill", fields["name"]);
        Assert.Equal("body text", body);
    }

    [Fact]
    public void Parse_LeadingBlankLinesAfterClosingMarker_AreTrimmedFromBody()
    {
        var content = "---\nname: x\n---\n\n\nactual body";

        var (_, body) = SkillFrontmatter.Parse(content);

        Assert.Equal("actual body", body);
    }
}
