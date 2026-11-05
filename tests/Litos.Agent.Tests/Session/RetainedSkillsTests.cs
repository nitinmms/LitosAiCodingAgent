using System.Text.Json;
using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tests.Fakes;

namespace Litos.Agent.Tests.Session;

public class RetainedSkillsTests
{
    private static ChatMessage SkillCall(string callId, string name) =>
        ChatMessage.Assistant([new ToolUseBlock(callId, "skill", JsonSerializer.SerializeToElement(new { name }))]);

    private static ChatMessage Result(string callId, string text, bool isError = false) =>
        new(Role.User, [new ToolResultBlock(callId, text, isError)]);

    private static ChatMessage[] Load(string callId, string name, string body) => [SkillCall(callId, name), Result(callId, body)];

    [Fact]
    public void Collect_FindsSuccessfulSkillLoads_InLoadOrder()
    {
        var skills = RetainedSkills.Collect([.. Load("c1", "alpha", "A body"), .. Load("c2", "beta", "B body")]);

        Assert.Equal([new RetainedSkill("alpha", "A body"), new RetainedSkill("beta", "B body")], skills);
    }

    [Fact]
    public void Collect_IgnoresFailedLoadsOtherToolsAndCallsWithoutAName()
    {
        ChatMessage[] messages =
        [
            SkillCall("c1", "missing"), Result("c1", "Unknown skill 'missing'.", isError: true),
            ChatMessage.Assistant([new ToolUseBlock("c2", "read_file", JsonSerializer.SerializeToElement(new { path = "x" }))]),
            Result("c2", "file contents"),
            ChatMessage.Assistant([new ToolUseBlock("c3", "skill", JsonSerializer.SerializeToElement(new { other = 1 }))]),
            Result("c3", "?"),
            SkillCall("c4", "pending"), // no result yet
        ];

        Assert.Empty(RetainedSkills.Collect(messages));
    }

    [Fact]
    public void Collect_ALaterLoadOfTheSameSkill_WinsAndMovesItToTheEnd()
    {
        var skills = RetainedSkills.Collect(
            [.. Load("c1", "alpha", "old"), .. Load("c2", "beta", "B"), .. Load("c3", "alpha", "new")]);

        Assert.Equal([new RetainedSkill("beta", "B"), new RetainedSkill("alpha", "new")], skills);
    }

    [Fact]
    public void Collect_ReadsSkillsReAttachedByAnEarlierCompaction()
    {
        var earlierSummary = RetainedSkills.Append("Earlier summary.", [new RetainedSkill("alpha", "A body\nline two")]);

        var skills = RetainedSkills.Collect(
            [ChatMessage.CompactionSummary(earlierSummary, 1000), .. Load("c1", "beta", "B")]);

        Assert.Equal([new RetainedSkill("alpha", "A body\nline two"), new RetainedSkill("beta", "B")], skills);
    }

    [Fact]
    public void Append_NoSkills_LeavesTheSummaryUnchanged() =>
        Assert.Equal("Summary.", RetainedSkills.Append("Summary.", []));

    [Fact]
    public void Append_ThenParse_RoundTripsBodiesVerbatim()
    {
        RetainedSkill[] skills = [new("alpha", "# Heading\n\n- rule `code`\n<tag attr=\"x\">"), new("beta", "B")];

        var summary = RetainedSkills.Append("## Goal\nShip it.", skills);

        Assert.StartsWith("## Goal\nShip it.\n\n<litos-retained-skills>", summary);
        Assert.Equal(skills, RetainedSkills.Parse(summary));
        Assert.Equal("## Goal\nShip it.", RetainedSkills.StripSection(summary));
    }

    [Fact]
    public void Append_ReplacesAnExistingSection_RatherThanNestingIt()
    {
        var once = RetainedSkills.Append("Summary.", [new RetainedSkill("alpha", "A")]);

        var twice = RetainedSkills.Append(once, [new RetainedSkill("beta", "B")]);

        Assert.Equal([new RetainedSkill("beta", "B")], RetainedSkills.Parse(twice));
        Assert.Equal(1, CountOf(twice, "<litos-retained-skills>"));
    }

    [Fact]
    public void Append_OverTheSkillCount_KeepsTheMostRecentAndNamesTheDropped()
    {
        var skills = Enumerable.Range(1, RetainedSkills.MaxSkills + 2).Select(i => new RetainedSkill($"s{i}", "body")).ToList();

        var summary = RetainedSkills.Append("Summary.", skills);

        Assert.Equal(skills.Skip(2).Select(s => s.Name), RetainedSkills.Parse(summary).Select(s => s.Name));
        Assert.Contains("load again with the skill tool if needed: s1, s2", summary);
    }

    [Fact]
    public void Append_OverTheCharacterBudget_DropsOlderSkillsFirst()
    {
        var big = new string('x', RetainedSkills.MaxTotalChars - 10);
        RetainedSkill[] skills = [new("old", "older body that no longer fits"), new("recent", big)];

        var summary = RetainedSkills.Append("Summary.", skills);

        Assert.Equal(["recent"], RetainedSkills.Parse(summary).Select(s => s.Name));
        Assert.Contains("if needed: old", summary);
    }

    [Fact]
    public void Append_UsesTheBudgetItIsGiven()
    {
        RetainedSkill[] skills = [new("small", new string('s', 900)), new("large", new string('l', 2_000))];

        var summary = RetainedSkills.Append("Summary.", skills, maxTotalChars: 1_000);

        // The recent large skill doesn't fit; the smaller older one still does.
        Assert.Equal(["small"], RetainedSkills.Parse(summary).Select(s => s.Name));
        Assert.Contains("if needed: large", summary);
    }

    [Theory]
    [InlineData(16_000, 8_000)] // small local window: keep-recent is a quarter of it (4K tokens)
    [InlineData(32_000, 16_000)]
    [InlineData(200_000, 40_000)]
    [InlineData(1_000_000, RetainedSkills.MaxTotalChars)] // capped
    public void CharBudget_ScalesWithTheContextWindow(int window, int expectedChars) =>
        Assert.Equal(expectedChars, RetainedSkills.CharBudgetFor(new CompactionSettings().ForContextWindow(window)));

    [Fact]
    public void Parse_And_Strip_AreNoOpsOnAPlainSummary()
    {
        Assert.Empty(RetainedSkills.Parse("Just a summary."));
        Assert.Equal("Just a summary.", RetainedSkills.StripSection("Just a summary."));
    }

    private static int CountOf(string text, string value) => (text.Length - text.Replace(value, "").Length) / value.Length;
}

/// <summary>Compactor-level behaviour: skills survive one compaction, then another.</summary>
public class CompactorSkillRetentionTests
{
    private static ChatMessage SkillCall(string callId, string name) =>
        ChatMessage.Assistant([new ToolUseBlock(callId, "skill", JsonSerializer.SerializeToElement(new { name }))]);

    private static ChatMessage Result(string callId, string text) => new(Role.User, [new ToolResultBlock(callId, text)]);

    /// <summary>Old history big enough to compact: a skill load, filler, then a usage over the trigger.</summary>
    private static Transcript TranscriptWithEarlySkillLoad(string skillName, string skillBody)
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User("make me a dashboard"));
        transcript.Append(SkillCall("c1", skillName));
        transcript.Append(Result("c1", skillBody));
        transcript.Append(ChatMessage.Assistant([new TextBlock(new string('b', 100_000))]));
        transcript.Append(ChatMessage.User(new string('a', 100_000)));
        transcript.Append(ChatMessage.Assistant([new TextBlock("ok")]), new UsageInfo(190_000, 0));
        transcript.Append(ChatMessage.User("recent question"));
        return transcript;
    }

    private static string SummaryOf(Transcript transcript) =>
        Assert.IsType<CompactionSummaryBlock>(transcript.Messages[0].Content[0]).Summary;

    [Fact]
    public async Task Compaction_ReAttachesASkillLoadedBeforeTheCut()
    {
        var transcript = TranscriptWithEarlySkillLoad("artifact-design", "# Artifact design\nrules");
        var provider = new FakeChatProvider();
        provider.Enqueue(new TextDelta("LLM summary."));

        Assert.True(await new Compactor(new CompactionSettings()).TryCompactAsync(transcript, provider, "m", null, CancellationToken.None));

        var summary = SummaryOf(transcript);
        Assert.StartsWith("LLM summary.", summary);
        Assert.Equal([new RetainedSkill("artifact-design", "# Artifact design\nrules")], RetainedSkills.Parse(summary));
    }

    [Fact]
    public async Task Compaction_OnASmallWindow_DropsASkillTooLargeForItsBudget()
    {
        // 16K window → 8,000-char budget: a 10,000-char skill would leave the context at the trigger.
        var transcript = TranscriptWithEarlySkillLoad("big-skill", new string('x', 10_000));
        var provider = new FakeChatProvider();
        provider.Enqueue(new TextDelta("LLM summary."));

        await new Compactor(new CompactionSettings()).ForceCompactAsync(transcript, provider, "m", 16_000, CancellationToken.None);

        var summary = SummaryOf(transcript);
        Assert.Empty(RetainedSkills.Parse(summary));
        Assert.Contains("if needed: big-skill", summary);
    }

    [Fact]
    public async Task Compaction_WithNoSkillLoads_LeavesTheSummaryExactlyAsTheLlmWroteIt()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User(new string('a', 100_000)));
        transcript.Append(ChatMessage.Assistant([new TextBlock(new string('b', 100_000))]), new UsageInfo(190_000, 0));
        transcript.Append(ChatMessage.User("recent"));
        var provider = new FakeChatProvider();
        provider.Enqueue(new TextDelta("LLM summary."));

        await new Compactor(new CompactionSettings()).TryCompactAsync(transcript, provider, "m", null, CancellationToken.None);

        Assert.Equal("LLM summary.", SummaryOf(transcript));
    }

    [Fact]
    public async Task ASkill_SurvivesASecondCompaction_AndIsNotShownToTheSummarizer()
    {
        var transcript = TranscriptWithEarlySkillLoad("artifact-design", "SKILL-BODY-MARKER");
        var compactor = new Compactor(new CompactionSettings());
        var provider = new FakeChatProvider();
        provider.Enqueue(new TextDelta("First summary."));
        await compactor.TryCompactAsync(transcript, provider, "m", null, CancellationToken.None);

        // Enough new history after the first compaction to force a second one past it.
        transcript.Append(ChatMessage.Assistant([new TextBlock(new string('c', 100_000))]));
        transcript.Append(ChatMessage.User(new string('d', 100_000)));
        transcript.Append(ChatMessage.Assistant([new TextBlock("ok")]), new UsageInfo(190_000, 0));
        transcript.Append(ChatMessage.User("latest question"));
        provider.Enqueue(new TextDelta("Second summary."));
        Assert.True(await compactor.ForceCompactAsync(transcript, provider, "m", null, CancellationToken.None));

        var summary = SummaryOf(transcript);
        Assert.StartsWith("Second summary.", summary);
        Assert.Equal([new RetainedSkill("artifact-design", "SKILL-BODY-MARKER")], RetainedSkills.Parse(summary));

        // The summarizer saw the first summary's narrative, but not the verbatim skill body.
        var secondRequest = provider.ReceivedRequests[^1];
        var sentText = string.Join("\n", secondRequest.Messages.SelectMany(m => m.Content).Select(b => b switch
        {
            CompactionSummaryBlock c => c.Summary,
            TextBlock t => t.Text,
            ToolResultBlock r => r.Text,
            _ => "",
        }));
        Assert.Contains("First summary.", sentText);
        Assert.DoesNotContain("SKILL-BODY-MARKER", sentText);
    }

    [Fact]
    public async Task ASkillLoadedAgainAfterTheCut_IsNotCarriedTwice()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User("start"));
        transcript.Append(SkillCall("c1", "artifact-design"));
        transcript.Append(Result("c1", "old body"));
        transcript.Append(ChatMessage.Assistant([new TextBlock(new string('b', 100_000))]));
        transcript.Append(ChatMessage.User(new string('a', 100_000)));
        transcript.Append(ChatMessage.Assistant([new TextBlock("ok")]), new UsageInfo(190_000, 0));
        transcript.Append(ChatMessage.User("recent question"));
        transcript.Append(SkillCall("c2", "artifact-design"));
        transcript.Append(Result("c2", "new body"));
        var provider = new FakeChatProvider();
        provider.Enqueue(new TextDelta("Summary."));

        await new Compactor(new CompactionSettings()).TryCompactAsync(transcript, provider, "m", null, CancellationToken.None);

        Assert.Empty(RetainedSkills.Parse(SummaryOf(transcript)));
        Assert.Contains(transcript.Messages, m => m.Content.OfType<ToolResultBlock>().Any(r => r.Text == "new body"));
    }
}
