namespace Litos.Artifacts.Tests;

/// <summary>The pieces every face that publishes pages shares: how a page is named, and the design rules
/// a face with its own workflow (the software factory's chat) appends to it.</summary>
public sealed class ArtifactTitlesAndRulesTests
{
    [Theory]
    [InlineData("<title>Export Pipeline</title>", "Export Pipeline")]
    [InlineData("<TITLE lang=\"en\">  Fish &amp; Chips \n</TITLE>", "Fish & Chips")]
    [InlineData("<p>no title</p>", null)]
    [InlineData("<title>   </title>", null)]
    public void FromHtml_TakesThePagesTitle_DecodedAndCleaned(string html, string? expected) =>
        Assert.Equal(expected, ArtifactTitles.FromHtml(html));

    [Fact]
    public void Clean_MakesOneLine_AndCutsAtTheMaximum()
    {
        Assert.Equal("Chosen name", ArtifactTitles.Clean("  Chosen\n\t name "));
        Assert.Null(ArtifactTitles.Clean(null));
        Assert.Null(ArtifactTitles.Clean("  "));
        Assert.Equal(ArtifactTitles.MaxLength, ArtifactTitles.Clean(new string('x', 500))!.Length);
    }

    [Fact]
    public void DesignRules_StartAtTheSandboxSection_WithoutTheSkillsFrontmatterOrFileWorkflow()
    {
        var rules = ArtifactSkills.DesignRules;

        Assert.StartsWith("## Where the page runs", rules);
        Assert.Contains("## Page contract", rules);
        Assert.Contains("## Before publishing, check", rules);
        Assert.DoesNotContain("name: artifact-design", rules);
        Assert.DoesNotContain("## Workflow", rules);
        Assert.DoesNotContain("write_file", rules);
        Assert.DoesNotContain("\r", rules);
    }

    [Fact]
    public void DesignRules_DoNotNameOneFacesSizeLimit()
    {
        // Each face's publish states its own limit (10 MB in VS Code, 1 MB in the factory).
        Assert.DoesNotContain("10 MB", ArtifactSkills.DesignRules);
        Assert.Contains("says what the limit is", ArtifactSkills.DesignRules);
    }

    [Fact]
    public void ExtractDesignRules_FromASkillWithoutTheSection_FailsLoudly() =>
        Assert.Throws<InvalidOperationException>(() => ArtifactSkills.ExtractDesignRules("---\nname: x\n---\n# Nothing here"));
}
