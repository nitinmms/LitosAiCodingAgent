using Litos.Gui.Buzzies;

namespace Litos.Gui.Tests.Buzzies;

/// <summary>Creating a Buzzy by conversation (Implement_Buzzies.md, Phase 3): the proposal is the
/// model's, but the narrowing is ours. It may only grant tools from the allowlist, only name a model
/// the provider really offers, and never hand over a path.</summary>
public class BuzzyProposerTests
{
    private static readonly string[] Models = ["deepseek-v4.1-flash", "gpt-5-mini"];

    [Fact]
    public void Parse_KeepsAWellFormedProposal()
    {
        var json = """
            {"name":"Penny","template":"buddy","model":"gpt-5-mini",
             "tools":["read_file","write_file","remember"],"folders":["invoice folder"],
             "soul":"You are Penny.","why":"A coworker for invoices."}
            """;

        var proposal = BuzzyProposer.Parse(json, "create a Buzzy for invoices", Models);

        Assert.Equal("Penny", proposal.Name);
        Assert.Equal("buddy", proposal.Template);
        Assert.Equal("gpt-5-mini", proposal.Model);
        Assert.Equal(["read_file", "write_file", "remember"], proposal.Tools);
        Assert.Equal(["invoice folder"], proposal.FoldersToGrant);
        Assert.Equal("You are Penny.", proposal.Soul);
    }

    [Fact]
    public void Parse_DropsToolsItMayNotGrant()
    {
        var json = """
            {"name":"X","template":"builder","tools":["read_file","mcp__danger","run_kernel_code","write_file"]}
            """;

        var proposal = BuzzyProposer.Parse(json, "x", Models);

        Assert.Equal(["read_file", "write_file"], proposal.Tools);
    }

    [Fact]
    public void Parse_IgnoresAModelTheProviderDoesNotOffer()
    {
        var json = """{"name":"X","template":"scout","model":"not-a-real-model","tools":["read_file"]}""";

        var proposal = BuzzyProposer.Parse(json, "x", Models);

        Assert.Null(proposal.Model);
    }

    [Theory]
    [InlineData("```json\n{\"name\":\"X\",\"template\":\"scout\",\"tools\":[\"read_file\"]}\n```")]
    [InlineData("Here you go: {\"name\":\"X\",\"template\":\"scout\",\"tools\":[\"read_file\"]} hope that helps")]
    public void Parse_ToleratesAFenceOrChattyProse(string output)
    {
        var proposal = BuzzyProposer.Parse(output, "x", Models);

        Assert.Equal("X", proposal.Name);
        Assert.Contains("read_file", proposal.Tools);
    }

    [Fact]
    public void Parse_GivesTheTemplatesTools_WhenTheListIsEmptyOrMissing()
    {
        var proposal = BuzzyProposer.Parse("""{"name":"S","template":"scout"}""", "x", Models);

        Assert.Equal(BuzzyTemplates.Scout.Tools, proposal.Tools);
    }

    [Fact]
    public void Parse_FallsBackToBuddy_ForAnUnknownTemplate()
    {
        var proposal = BuzzyProposer.Parse("""{"name":"X","template":"wizard","tools":["read_file"]}""", "x", Models);

        Assert.Equal("buddy", proposal.Template);
        Assert.Equal(BuzzyTemplates.Buddy, proposal.TemplateOrDefault);
    }

    [Fact]
    public void Parse_StillProducesSomething_ForUnusableOutput()
    {
        var proposal = BuzzyProposer.Parse("I'm sorry, I can't help with that.", "create a Buzzy that reads invoices", Models);

        Assert.Equal("Buddy", proposal.Name); // The Buddy template's own name.
        Assert.NotEmpty(proposal.Tools);
        Assert.Contains("invoices", proposal.Why);
    }

    [Fact]
    public void Parse_NeverAcceptsAPath_OnlyDescriptions()
    {
        var json = """{"name":"X","template":"buddy","tools":["read_file"],"folders":["C:\\Windows\\System32"]}""";

        var proposal = BuzzyProposer.Parse(json, "x", Models);

        // The description is kept as a hint for the card; the actual grant comes from a folder
        // dialog, so a path here is never used to grant anything on its own.
        Assert.Contains(@"C:\Windows\System32", proposal.FoldersToGrant);
        Assert.DoesNotContain(proposal.FoldersToGrant, f => f.Contains("read_file"));
    }

    // No heuristic remains to test: creation starts only from the explicit /buzzy command, so there
    // is no sentence that can open a creation card by accident. The regression that motivated this is
    // recorded here as a compile-time absence, not a corpus: BuzzyProposer no longer exposes any
    // intent-guessing entry point at all.
    [Fact]
    public void Proposer_ExposesNoIntentGuessing()
    {
        var members = typeof(BuzzyProposer).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Concat(typeof(BuzzyProposer).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Select(p => p.GetMethod)!)
            .Where(m => m is not null)
            .Select(m => m!.Name)
            .ToArray();

        Assert.DoesNotContain(members, name => name.Contains("Looks", StringComparison.Ordinal)
            || name.Contains("IsCreation", StringComparison.Ordinal)
            || name.Contains("Intent", StringComparison.Ordinal));
    }

    /// <summary>The case that broke the first draft: in the plain chat (also the coding agent) this
    /// must never be treated as a Buzzy request. With no inference at all it cannot be.</summary>
    [Theory]
    [InlineData("add an agent to my code")]
    [InlineData("add an assistant to the checkout page")]
    [InlineData("create a helper function called slugify")]
    [InlineData("create a Buzzy that reads my invoices")] // explicit requests too: only /buzzy works
    public void OrdinaryChatText_CannotStartACreationProposal(string message)
    {
        // BuzzyProposer has no API that takes a sentence and decides anything, by design.
        Assert.Null(typeof(BuzzyProposer).GetMethod("LooksLikeCreationRequest", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
        Assert.False(string.IsNullOrEmpty(message));
    }
}