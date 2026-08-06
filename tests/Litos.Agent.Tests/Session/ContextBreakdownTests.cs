using System.Text.Json;
using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;

namespace Litos.Agent.Tests.Session;

public class ContextBreakdownTests
{
    private static JsonElement EmptyArgs() => JsonDocument.Parse("{}").RootElement;

    private static SystemPromptSections Sections(
        string identity = "", string toolsList = "", string guidelines = "", string? skillsCatalog = null,
        IReadOnlyList<SystemPromptInstructionFile>? instructions = null, string footer = "") =>
        new(identity, toolsList, guidelines, skillsCatalog, instructions ?? [], footer);

    // ---- System prompt sections ----

    [Fact]
    public void Compute_NullSystemPrompt_OmitsSystemPromptMemoryAndSkillsEntries()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User("hi"));

        var snapshot = ContextBreakdown.Compute(transcript, systemPrompt: null, tools: []);

        Assert.DoesNotContain(snapshot.Entries, e => e.Category is ContextCategory.SystemPrompt or ContextCategory.Memory or ContextCategory.Skills);
    }

    [Fact]
    public void Compute_SystemPromptIdentityGuidelinesAndFooter_AreCombinedIntoOneSystemPromptEntry()
    {
        var transcript = Transcript.CreateNew("/repo");
        var sections = Sections(identity: new string('a', 40), guidelines: new string('b', 40), footer: new string('c', 40));

        var snapshot = ContextBreakdown.Compute(transcript, sections, tools: []);

        var entry = Assert.Single(snapshot.Entries, e => e.Category == ContextCategory.SystemPrompt);
        Assert.Equal(30, entry.EstimatedTokens); // 120 chars / 4
    }

    [Fact]
    public void Compute_InstructionFiles_AreAttributedToMemoryCategory_NotSystemPrompt()
    {
        var transcript = Transcript.CreateNew("/repo");
        var sections = Sections(instructions: [new SystemPromptInstructionFile("AGENTS.md", new string('x', 400))]);

        var snapshot = ContextBreakdown.Compute(transcript, sections, tools: []);

        var memory = Assert.Single(snapshot.Entries, e => e.Category == ContextCategory.Memory);
        Assert.Equal(100, memory.EstimatedTokens);
        Assert.DoesNotContain(snapshot.Entries, e => e.Category == ContextCategory.SystemPrompt);
    }

    [Fact]
    public void Compute_NullSkillsCatalog_OmitsSkillsEntry()
    {
        var transcript = Transcript.CreateNew("/repo");
        var sections = Sections(skillsCatalog: null);

        var snapshot = ContextBreakdown.Compute(transcript, sections, tools: []);

        Assert.DoesNotContain(snapshot.Entries, e => e.Category == ContextCategory.Skills);
    }

    [Fact]
    public void Compute_NonNullSkillsCatalog_ProducesSkillsEntry()
    {
        var transcript = Transcript.CreateNew("/repo");
        var sections = Sections(skillsCatalog: new string('s', 80));

        var snapshot = ContextBreakdown.Compute(transcript, sections, tools: []);

        var skills = Assert.Single(snapshot.Entries, e => e.Category == ContextCategory.Skills);
        Assert.Equal(20, skills.EstimatedTokens);
    }

    // ---- Tool schemas ----

    [Fact]
    public void Compute_ToolSchemas_SumNameDescriptionAndParameterSchemaLengths()
    {
        var transcript = Transcript.CreateNew("/repo");
        var tools = new[] { new ToolSchema("read_file", "Reads a file.", EmptyArgs()) };

        var snapshot = ContextBreakdown.Compute(transcript, systemPrompt: null, tools);

        var entry = Assert.Single(snapshot.Entries, e => e.Category == ContextCategory.ToolSchemas);
        var expected = ("read_file".Length + "Reads a file.".Length + EmptyArgs().GetRawText().Length) / 4;
        Assert.Equal(expected, entry.EstimatedTokens);
    }

    [Fact]
    public void Compute_NoTools_OmitsToolSchemasEntry()
    {
        var transcript = Transcript.CreateNew("/repo");

        var snapshot = ContextBreakdown.Compute(transcript, systemPrompt: null, tools: []);

        Assert.DoesNotContain(snapshot.Entries, e => e.Category == ContextCategory.ToolSchemas);
    }

    // ---- History ----

    [Fact]
    public void Compute_TextBlocks_FromUserAndAssistantMessages_AreAttributedToHistory()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User(new string('a', 40)));
        transcript.Append(ChatMessage.Assistant([new TextBlock(new string('b', 40))]));

        var snapshot = ContextBreakdown.Compute(transcript, systemPrompt: null, tools: []);

        var history = Assert.Single(snapshot.Entries, e => e.Category == ContextCategory.History);
        Assert.Equal(20, history.EstimatedTokens); // 80 chars / 4
    }

    [Fact]
    public void Compute_ToolUseBlocks_AreAttributedToHistory_NotToolResults()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.Assistant([new ToolUseBlock("call_1", "read_file", EmptyArgs())]));

        var snapshot = ContextBreakdown.Compute(transcript, systemPrompt: null, tools: []);

        Assert.Contains(snapshot.Entries, e => e.Category == ContextCategory.History);
        Assert.DoesNotContain(snapshot.Entries, e => e.Category == ContextCategory.ToolResults);
    }

    // ---- Tool results, sub-grouped by originating tool ----

    [Fact]
    public void Compute_ToolResultBlock_IsAttributedToItsOriginatingToolName_ViaMatchingCallId()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.Assistant([new ToolUseBlock("call_1", "read_file", EmptyArgs())]));
        transcript.Append(ChatMessage.ToolResult("call_1", new ToolResult(new string('x', 400))));

        var snapshot = ContextBreakdown.Compute(transcript, systemPrompt: null, tools: []);

        var toolResults = Assert.Single(snapshot.Entries, e => e.Category == ContextCategory.ToolResults);
        Assert.Equal(100, toolResults.EstimatedTokens);
        Assert.NotNull(toolResults.SubItems);
        var subItem = Assert.Single(toolResults.SubItems!);
        Assert.Equal("read_file", subItem.Label);
        Assert.Equal(100, subItem.EstimatedTokens);
    }

    [Fact]
    public void Compute_ToolResultWithNoMatchingToolUse_FallsBackToUnknownToolLabel()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.ToolResult("call_missing", new ToolResult(new string('x', 40))));

        var snapshot = ContextBreakdown.Compute(transcript, systemPrompt: null, tools: []);

        var toolResults = Assert.Single(snapshot.Entries, e => e.Category == ContextCategory.ToolResults);
        var subItem = Assert.Single(toolResults.SubItems!);
        Assert.Equal("(unknown tool)", subItem.Label);
    }

    [Fact]
    public void Compute_MultipleToolResultsForSameTool_AreSummedIntoOneSubItem()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.Assistant([
            new ToolUseBlock("call_1", "search_code", EmptyArgs()),
            new ToolUseBlock("call_2", "search_code", EmptyArgs()),
        ]));
        transcript.Append(ChatMessage.ToolResult("call_1", new ToolResult(new string('x', 40))));
        transcript.Append(ChatMessage.ToolResult("call_2", new ToolResult(new string('y', 40))));

        var snapshot = ContextBreakdown.Compute(transcript, systemPrompt: null, tools: []);

        var toolResults = Assert.Single(snapshot.Entries, e => e.Category == ContextCategory.ToolResults);
        var subItem = Assert.Single(toolResults.SubItems!);
        Assert.Equal("search_code", subItem.Label);
        Assert.Equal(20, subItem.EstimatedTokens); // (40+40)/4
    }

    [Fact]
    public void Compute_ToolResultSubItems_AreOrderedByTokensDescending()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.Assistant([
            new ToolUseBlock("call_1", "small_tool", EmptyArgs()),
            new ToolUseBlock("call_2", "big_tool", EmptyArgs()),
        ]));
        transcript.Append(ChatMessage.ToolResult("call_1", new ToolResult(new string('x', 40))));
        transcript.Append(ChatMessage.ToolResult("call_2", new ToolResult(new string('y', 400))));

        var snapshot = ContextBreakdown.Compute(transcript, systemPrompt: null, tools: []);

        var toolResults = Assert.Single(snapshot.Entries, e => e.Category == ContextCategory.ToolResults);
        Assert.Equal(["big_tool", "small_tool"], toolResults.SubItems!.Select(s => s.Label));
    }

    [Fact]
    public void Compute_NoToolResults_OmitsToolResultsEntry()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User("hi"));

        var snapshot = ContextBreakdown.Compute(transcript, systemPrompt: null, tools: []);

        Assert.DoesNotContain(snapshot.Entries, e => e.Category == ContextCategory.ToolResults);
    }

    // ---- Images ----

    [Fact]
    public void Compute_ImageBlock_UsesFixedEstimate_RegardlessOfByteSize()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User([new ImageBlock("image/png", new byte[1_000_000])]));

        var snapshot = ContextBreakdown.Compute(transcript, systemPrompt: null, tools: []);

        var images = Assert.Single(snapshot.Entries, e => e.Category == ContextCategory.Images);
        Assert.Equal(1200, images.EstimatedTokens); // 4800 fixed chars / 4, matches CompactionPlanner's EstimatedImageChars
    }

    // ---- Compaction summary ----

    [Fact]
    public void Compute_CompactionSummaryBlock_IsItsOwnCategory_SeparateFromHistory()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.CompactionSummary(new string('s', 400), tokensBefore: 50_000));

        var snapshot = ContextBreakdown.Compute(transcript, systemPrompt: null, tools: []);

        var summary = Assert.Single(snapshot.Entries, e => e.Category == ContextCategory.CompactionSummary);
        Assert.Equal(100, summary.EstimatedTokens);
        Assert.DoesNotContain(snapshot.Entries, e => e.Category == ContextCategory.History);
    }

    // ---- Scaling against real usage ----

    [Fact]
    public void Compute_NoRealUsageYet_ReturnsUnscaledRawEstimate_AndNullLastRealUsageTokens()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User(new string('a', 400)));

        var snapshot = ContextBreakdown.Compute(transcript, systemPrompt: null, tools: []);

        Assert.Null(snapshot.LastRealUsageTokens);
        Assert.Equal(100, snapshot.TotalEstimatedTokens);
    }

    [Fact]
    public void Compute_WithRealUsage_ScalesTotalToMatchExactly()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User(new string('a', 400))); // raw estimate: 100 tokens
        transcript.Append(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(500, 100)); // real total: 600

        var snapshot = ContextBreakdown.Compute(transcript, systemPrompt: null, tools: []);

        Assert.Equal(600, snapshot.LastRealUsageTokens);
        Assert.Equal(600, snapshot.TotalEstimatedTokens);
    }

    [Fact]
    public void Compute_WithRealUsage_ScalesEachEntryProportionally()
    {
        var transcript = Transcript.CreateNew("/repo");
        // Two equal-sized categories pre-scaling: system prompt (100 raw) and history (100 raw).
        var sections = Sections(identity: new string('a', 400));
        transcript.Append(ChatMessage.User(new string('b', 400)));
        transcript.Append(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(150, 50)); // real total: 200 -> factor 1x

        var snapshot = ContextBreakdown.Compute(transcript, sections, tools: []);

        var systemPrompt = snapshot.Entries.Single(e => e.Category == ContextCategory.SystemPrompt);
        var history = snapshot.Entries.Single(e => e.Category == ContextCategory.History);
        Assert.Equal(100, systemPrompt.EstimatedTokens);
        Assert.Equal(100, history.EstimatedTokens);
    }

    [Fact]
    public void Compute_ScalingWithNoRawTokensAtAll_DoesNotDivideByZero()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.Assistant([new TextBlock("")]), new UsageInfo(0, 0));

        var snapshot = ContextBreakdown.Compute(transcript, systemPrompt: null, tools: []);

        Assert.Equal(0, snapshot.TotalEstimatedTokens);
    }

    // ---- Category ordering ----

    [Fact]
    public void Compute_Entries_AreOrderedByContextCategoryDeclarationOrder()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User([new ImageBlock("image/png", [1])]));
        transcript.Append(ChatMessage.Assistant([new TextBlock("hello")]));
        var sections = Sections(identity: new string('a', 40));

        var snapshot = ContextBreakdown.Compute(transcript, sections, tools: []);

        var categories = snapshot.Entries.Select(e => e.Category).ToList();
        var sorted = categories.OrderBy(c => (int)c).ToList();
        Assert.Equal(sorted, categories);
    }
}
