using System.Text.Json;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Core.Tests.Contracts;

/// <summary>
/// The host and the worker are separate processes that only ever meet through this JSON, so
/// each shape is pinned by a round trip.
/// </summary>
public class WireContractTests
{
    private static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, FactoryWire.Json);
        return JsonSerializer.Deserialize<T>(json, FactoryWire.Json)!;
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, FactoryWire.Json);

    // ---- Submissions ----

    [Fact]
    public void WorkSubmission_RoundTrips_WithItsKind()
    {
        Submission submission = new WorkSubmission(
            "Added CSV export.",
            [new CriterionCoverage("Administrators can export.", ["Export_Admin_Succeeds"]), new CriterionCoverage("Looks right in Excel.", [], ManualOnly: true)],
            ["Export_Admin_Succeeds"], ["Large exports are not streamed."], ["Export as an administrator."]);

        var json = Json(new SubmissionRequest("session-1", submission));
        var back = JsonSerializer.Deserialize<SubmissionRequest>(json, FactoryWire.Json)!;

        Assert.Contains("\"kind\":\"work\"", json);
        var work = Assert.IsType<WorkSubmission>(back.Submission);
        Assert.Equal("session-1", back.SessionId);
        Assert.Equal("Added CSV export.", work.Summary);
        Assert.Equal(["Export_Admin_Succeeds"], work.Criteria[0].Tests);
        Assert.True(work.Criteria[1].ManualOnly);
        Assert.Equal(["Large exports are not streamed."], work.KnownLimitations);
        Assert.Equal(["Export as an administrator."], work.ManualTestSteps);
    }

    [Fact]
    public void DecisionSubmission_RoundTrips()
    {
        Submission submission = new DecisionSubmission(
            "All rows or the current page?", "The request does not say.", ["All rows", "Current page"], "All rows", "OrdersController");

        var back = Assert.IsType<DecisionSubmission>(RoundTrip(submission));

        Assert.Contains("\"kind\":\"decision\"", Json(submission));
        Assert.Equal("All rows or the current page?", back.Question);
        Assert.Equal(["All rows", "Current page"], back.Options);
        Assert.Equal("All rows", back.Recommendation);
        Assert.Equal("OrdersController", back.Impact);
    }

    [Fact]
    public void ReviewSubmission_RoundTrips_WithSeverityAsAName()
    {
        Submission submission = new ReviewSubmission(
        [
            new ReviewFinding(FindingSeverity.Blocking, "src/Orders.cs", 42, "Crashes on an empty list."),
            new ReviewFinding(FindingSeverity.Minor, "src/Orders.cs", null, "Leftover debug output."),
        ]);

        var json = Json(submission);
        var back = Assert.IsType<ReviewSubmission>(RoundTrip(submission));

        Assert.Contains("\"kind\":\"review\"", json);
        Assert.Contains("\"severity\":\"Blocking\"", json);
        Assert.Equal(FindingSeverity.Blocking, back.Findings[0].Severity);
        Assert.Equal(42, back.Findings[0].Line);
        Assert.Null(back.Findings[1].Line);
    }

    [Fact]
    public void SpecSubmission_RoundTrips()
    {
        Submission submission = new SpecSubmission(
            "Export orders.", ["Admins can export."], ["Orders"], "Unit tests cover criterion 1.", ["Which columns?"]);

        var back = Assert.IsType<SpecSubmission>(RoundTrip(submission));

        Assert.Contains("\"kind\":\"spec\"", Json(submission));
        Assert.Equal(["Admins can export."], back.AcceptanceCriteria);
        Assert.Equal(["Which columns?"], back.OpenQuestions);
    }

    [Fact]
    public void Submission_UnknownKind_IsRejected()
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<Submission>("""{"kind":"merge"}""", FactoryWire.Json));
    }

    [Fact]
    public void SubmissionResponse_RoundTrips()
    {
        Assert.Equal(new SubmissionResponse(false, "submit_review is only valid in a review turn."),
            RoundTrip(new SubmissionResponse(false, "submit_review is only valid in a review turn.")));
    }

    // ---- Gateway ----

    private static ChatRequest SampleRequest()
    {
        var arguments = JsonDocument.Parse("""{"path":"src/Orders.cs","limit":40}""").RootElement.Clone();
        return new ChatRequest(
            [
                ChatMessage.User([new TextBlock("Add CSV export."), new ImageBlock("image/png", [1, 2, 3, 250])]),
                ChatMessage.Assistant([new TextBlock("Reading the file."), new ToolUseBlock("call-1", "read_file", arguments)]),
                ChatMessage.ToolResult("call-1", ToolResult.Error("File not found.")),
                ChatMessage.CompactionSummary("Earlier: discussed the export.", 54_321),
            ],
            [new ToolSchema("read_file", "Reads a file.", JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""").RootElement.Clone())],
            "vendor/model", SystemPrompt: "You are Litos.", Temperature: 0.2, MaxOutputTokens: 4_000, SessionId: "session-1");
    }

    /// <summary>The architecture's "gateway serialization gaps" risk: every content block type
    /// must survive the trip from the worker to the host unchanged.</summary>
    [Fact]
    public void GatewayRequest_RoundTripsEveryContentBlockType()
    {
        var request = new GatewayRequest("9b1c7e1e-1111-4222-8333-444455556666", SampleRequest());

        var back = RoundTrip(request);

        Assert.Equal(request.RequestKey, back.RequestKey);
        var chat = back.ChatRequest;
        Assert.Equal("vendor/model", chat.Model);
        Assert.Equal("You are Litos.", chat.SystemPrompt);
        Assert.Equal(0.2, chat.Temperature);
        Assert.Equal(4_000, chat.MaxOutputTokens);
        Assert.Equal("session-1", chat.SessionId);

        Assert.Equal(Role.User, chat.Messages[0].Role);
        Assert.Equal("Add CSV export.", Assert.IsType<TextBlock>(chat.Messages[0].Content[0]).Text);
        var image = Assert.IsType<ImageBlock>(chat.Messages[0].Content[1]);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3, 250 }, image.Data);

        Assert.Equal(Role.Assistant, chat.Messages[1].Role);
        var toolUse = Assert.IsType<ToolUseBlock>(chat.Messages[1].Content[1]);
        Assert.Equal("call-1", toolUse.CallId);
        Assert.Equal("read_file", toolUse.ToolName);
        Assert.Equal("src/Orders.cs", toolUse.Arguments.GetProperty("path").GetString());
        Assert.Equal(40, toolUse.Arguments.GetProperty("limit").GetInt32());

        var toolResult = Assert.IsType<ToolResultBlock>(chat.Messages[2].Content[0]);
        Assert.Equal("call-1", toolResult.CallId);
        Assert.True(toolResult.IsError);
        Assert.Equal("File not found.", toolResult.Text);

        var summary = Assert.IsType<CompactionSummaryBlock>(chat.Messages[3].Content[0]);
        Assert.Equal(54_321, summary.TokensBefore);

        var tool = Assert.Single(chat.Tools);
        Assert.Equal("read_file", tool.Name);
        Assert.Equal("string", tool.ParameterSchema.GetProperty("properties").GetProperty("path").GetProperty("type").GetString());
    }

    [Fact]
    public void GatewayRequest_RoundTrip_IsStable()
    {
        var request = new GatewayRequest("key", SampleRequest());

        Assert.Equal(Json(request), Json(RoundTrip(request)));
    }

    public static TheoryData<GatewayEvent, string> Events() => new()
    {
        { new GatewayTextDelta("hello"), "text_delta" },
        { new GatewayReasoningDelta("thinking"), "reasoning_delta" },
        { new GatewayToolCallStarted("call-1", "read_file"), "tool_call_started" },
        { new GatewayToolCallArgsDelta("call-1", "{\"pa"), "tool_call_args_delta" },
        { new GatewayHeartbeat(), "heartbeat" },
        { new GatewayError(GatewayErrorCodes.BudgetExhausted, "Needs 20,900; 20,000 remain."), "error" },
    };

    [Theory]
    [MemberData(nameof(Events))]
    public void GatewayEvent_RoundTrips_AsOneNdjsonLine(GatewayEvent evt, string discriminator)
    {
        var json = Json(evt);

        Assert.DoesNotContain('\n', json); // one event per line
        Assert.Contains($"\"event\":\"{discriminator}\"", json);
        Assert.Equal(evt, JsonSerializer.Deserialize<GatewayEvent>(json, FactoryWire.Json));
    }

    [Fact]
    public void GatewayToolCallCompleted_RoundTripsItsArguments()
    {
        GatewayEvent evt = new GatewayToolCallCompleted("call-1", "read_file", JsonDocument.Parse("""{"path":"a.txt"}""").RootElement.Clone());

        var back = Assert.IsType<GatewayToolCallCompleted>(RoundTrip(evt));

        Assert.Equal("a.txt", back.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public void GatewayMessageCompleted_RoundTripsTheMessageAndAllUsageFields()
    {
        var usage = new UsageInfo(200, 900, 3_000, 12_000, ReasoningTokens: 640);
        GatewayEvent evt = new GatewayMessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), usage);

        var back = Assert.IsType<GatewayMessageCompleted>(RoundTrip(evt));

        Assert.Equal(usage, back.Usage);
        Assert.Equal(15_200, back.Usage.TotalInputTokens);
        Assert.Equal("done", Assert.IsType<TextBlock>(Assert.Single(back.Message.Content)).Text);
    }

    [Fact]
    public void GatewayEvent_UnknownEvent_IsRejected()
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<GatewayEvent>("""{"event":"mystery"}""", FactoryWire.Json));
    }

    // ---- Provider events onto the wire ----

    [Fact]
    public void FromAgentEvent_MapsEveryProviderEvent()
    {
        var arguments = JsonDocument.Parse("{}").RootElement.Clone();
        var message = ChatMessage.Assistant([new TextBlock("hi")]);
        var usage = new UsageInfo(1, 2);

        Assert.Equal(new GatewayTextDelta("a"), GatewayEvent.FromAgentEvent(new TextDelta("a")));
        Assert.Equal(new GatewayReasoningDelta("b"), GatewayEvent.FromAgentEvent(new ReasoningDelta("b")));
        Assert.Equal(new GatewayToolCallStarted("c", "t"), GatewayEvent.FromAgentEvent(new ToolCallStarted("c", "t")));
        Assert.Equal(new GatewayToolCallArgsDelta("c", "{"), GatewayEvent.FromAgentEvent(new ToolCallArgsDelta("c", "{")));
        Assert.IsType<GatewayToolCallCompleted>(GatewayEvent.FromAgentEvent(new ToolCallCompleted("c", "t", arguments)));
        Assert.Equal(new GatewayMessageCompleted(message, usage), GatewayEvent.FromAgentEvent(new MessageCompleted(message, usage)));
        Assert.IsType<GatewayHeartbeat>(GatewayEvent.FromAgentEvent(new StreamHeartbeat()));
    }

    [Fact]
    public void FromAgentEvent_ProviderError_BecomesAProviderErrorWithOnlyTheMessage()
    {
        var evt = GatewayEvent.FromAgentEvent(new ErrorOccurred(new InvalidOperationException("The provider returned 500.")));

        Assert.Equal(new GatewayError(GatewayErrorCodes.ProviderError, "The provider returned 500."), evt);
    }

    [Fact]
    public void FromAgentEvent_AgentLoopOnlyEvents_HaveNoWireForm()
    {
        Assert.Null(GatewayEvent.FromAgentEvent(new ToolCallResult("c", "t", ToolResult.Ok("ok"))));
        Assert.Null(GatewayEvent.FromAgentEvent(new CompactionOccurred(1_000)));
        Assert.Null(GatewayEvent.FromAgentEvent(new ToolCallSkipped("c", "steered")));
    }

    [Theory]
    [InlineData(GatewayErrorCodes.BudgetExhausted, true)]
    [InlineData(GatewayErrorCodes.QuotaExhausted, true)]
    [InlineData(GatewayErrorCodes.ProviderError, false)]
    [InlineData("something_else", false)]
    public void IsRefusal_OnlyTheTwoAdmissionCodes(string code, bool expected)
    {
        Assert.Equal(expected, GatewayErrorCodes.IsRefusal(code));
    }

    // ---- Names ----

    [Fact]
    public void Paths_MatchTheArchitecture()
    {
        Assert.Equal("/internal/runs/run-1/submissions", FactoryWire.SubmissionsPath("run-1"));
        Assert.Equal("/internal/runs/run-1/gateway", FactoryWire.GatewayPath("run-1"));
        Assert.Equal("/internal/runs/run-1/ready", FactoryWire.ReadyPath("run-1"));
        Assert.Equal("/sessions/session-1/compact", FactoryWire.CompactPath("session-1"));
        Assert.Equal("/shutdown", FactoryWire.ShutdownPath);
    }

    [Fact]
    public void Paths_EscapeTheirIds()
    {
        Assert.Equal("/internal/runs/a%2Fb/gateway", FactoryWire.GatewayPath("a/b"));
    }

    [Fact]
    public void Names_MatchTheArchitecture()
    {
        Assert.Equal("X-Factory-Secret", FactoryWire.SecretHeader);
        Assert.Equal("FACTORY_WORKER_SECRET", FactoryWire.WorkerSecretVariable);
        Assert.Equal("FACTORY_HOST_URL", FactoryWire.HostUrlVariable);
        Assert.Equal("FACTORY_RUN_ID", FactoryWire.RunIdVariable);
    }

    [Fact]
    public void TurnKind_HasTheSevenKinds_AndTravelsAsAName()
    {
        Assert.Equal(
            [TurnKind.Chat, TurnKind.Spec, TurnKind.Implement, TurnKind.Repair, TurnKind.Review, TurnKind.Rework, TurnKind.Nudge],
            Enum.GetValues<TurnKind>());
        Assert.Equal("\"Review\"", Json(TurnKind.Review));
    }

    [Fact]
    public void WorkerReadyAndCompact_RoundTrip()
    {
        Assert.Equal(new WorkerReady(51_234, true), RoundTrip(new WorkerReady(51_234, true)));
        Assert.Equal(new CompactRequest("keep the criteria"), RoundTrip(new CompactRequest("keep the criteria")));
        Assert.Equal(new CompactResponse(true), RoundTrip(new CompactResponse(true)));
    }

    [Fact]
    public void Json_UsesCamelCaseNames()
    {
        Assert.Equal("""{"port":1,"mcpReady":false}""", Json(new WorkerReady(1, false)));
    }
}
