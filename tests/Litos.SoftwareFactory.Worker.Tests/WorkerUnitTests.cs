using System.Net;
using System.Text;
using System.Text.Json;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Worker.Tests;

public class WorkerOptionsTests
{
    private static readonly Dictionary<string, string?> Environment = new()
    {
        ["FACTORY_WORKER_SECRET"] = "s3cret",
        ["FACTORY_HOST_URL"] = "http://127.0.0.1:5180",
        ["FACTORY_RUN_ID"] = "run-42",
    };

    private static readonly string[] Arguments =
        ["--parent-pid", "4242", "--provider", "openrouter", "--model", "vendor/model", "--data-dir", "factory-data", "--context-length", "200000"];

    private static WorkerOptions Parse(string[]? arguments = null, Dictionary<string, string?>? environment = null) =>
        WorkerOptions.Parse(arguments ?? Arguments, name => (environment ?? Environment).GetValueOrDefault(name));

    [Fact]
    public void Parse_ReadsArgumentsAndEnvironment()
    {
        var options = Parse();

        Assert.Equal("run-42", options.RunId);
        Assert.Equal("s3cret", options.Secret);
        Assert.Equal(new Uri("http://127.0.0.1:5180"), options.HostUrl);
        Assert.Equal("openrouter", options.Provider);
        Assert.Equal("vendor/model", options.Model);
        Assert.Equal(200_000, options.ContextLength);
        Assert.Equal(4242, options.ParentProcessId);
        Assert.Equal(Path.GetFullPath("factory-data"), options.DataDirectory);
    }

    [Fact]
    public void Parse_PtcIsOnByDefault_AndCanBeTurnedOff()
    {
        Assert.True(Parse().PtcEnabled);
        Assert.False(Parse([.. Arguments, "--ptc", "off"]).PtcEnabled);
        Assert.True(Parse([.. Arguments, "--ptc", "on"]).PtcEnabled);
    }

    [Fact]
    public void Parse_OptionalArguments_MayBeOmitted()
    {
        var options = Parse(["--provider", "anthropic", "--model", "m", "--data-dir", "d"]);

        Assert.Null(options.ContextLength);
        Assert.Null(options.ParentProcessId);
    }

    [Fact]
    public void SessionsDirectory_IsUnderTheRunInTheFactoryDataDirectory()
    {
        var options = Parse();

        Assert.Equal(Path.Combine(options.DataDirectory, "runs", "run-42", "sessions"), options.SessionsDirectory);
    }

    /// <summary>Matches what LocalProcessWorkerLauncher.BuildArguments produces.</summary>
    [Fact]
    public void Parse_AcceptsExactlyWhatTheLauncherSends()
    {
        var options = Parse(["--parent-pid", "1", "--provider", "openrouter", "--model", "vendor/model", "--data-dir", "d", "--context-length", "64000"]);

        Assert.Equal(64_000, options.ContextLength);
    }

    [Theory]
    [InlineData("--provider")]
    [InlineData("--model")]
    [InlineData("--data-dir")]
    public void Parse_MissingRequiredArgument_Throws(string missing)
    {
        var arguments = new List<string>(Arguments);
        var index = arguments.IndexOf(missing);
        arguments.RemoveRange(index, 2);

        var ex = Assert.Throws<WorkerOptionsException>(() => Parse([.. arguments]));

        Assert.Contains(missing, ex.Message);
    }

    [Theory]
    [InlineData("FACTORY_WORKER_SECRET")]
    [InlineData("FACTORY_HOST_URL")]
    [InlineData("FACTORY_RUN_ID")]
    public void Parse_MissingEnvironmentVariable_Throws(string missing)
    {
        var environment = new Dictionary<string, string?>(Environment) { [missing] = null };

        var ex = Assert.Throws<WorkerOptionsException>(() => Parse(environment: environment));

        Assert.Contains(missing, ex.Message);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://127.0.0.1")]
    [InlineData("/relative")]
    public void Parse_HostUrlMustBeHttp(string url)
    {
        var environment = new Dictionary<string, string?>(Environment) { ["FACTORY_HOST_URL"] = url };

        Assert.Throws<WorkerOptionsException>(() => Parse(environment: environment));
    }

    [Theory]
    [InlineData("--context-length", "lots")]
    [InlineData("--context-length", "0")]
    [InlineData("--parent-pid", "-1")]
    [InlineData("--ptc", "maybe")]
    public void Parse_MalformedValue_Throws(string name, string value)
    {
        var ex = Assert.Throws<WorkerOptionsException>(() => Parse(["--provider", "p", "--model", "m", "--data-dir", "d", name, value]));

        Assert.Contains(name, ex.Message);
    }

    [Fact]
    public void Parse_ArgumentWithNoValue_OrStrayArgument_Throws()
    {
        Assert.Throws<WorkerOptionsException>(() => Parse(["--provider"]));
        Assert.Throws<WorkerOptionsException>(() => Parse(["stray", "--provider", "p"]));
    }

    /// <summary>The agent's shell commands inherit the worker's environment. With the secret,
    /// host URL and run id left there, an agent could call the host as if it were the worker.</summary>
    [Fact]
    public void RemoveFromEnvironment_ClearsTheSecretTheHostUrlAndTheRunId()
    {
        var environment = new Dictionary<string, string?>
        {
            ["FACTORY_WORKER_SECRET"] = "s3cret",
            ["FACTORY_HOST_URL"] = "http://127.0.0.1:5180",
            ["FACTORY_RUN_ID"] = "run-42",
            ["PATH"] = "/usr/bin",
        };

        WorkerLaunchVariables.RemoveFromEnvironment((name, value) => environment[name] = value);

        Assert.Null(environment["FACTORY_WORKER_SECRET"]);
        Assert.Null(environment["FACTORY_HOST_URL"]);
        Assert.Null(environment["FACTORY_RUN_ID"]);
        Assert.Equal("/usr/bin", environment["PATH"]);
    }

    [Fact]
    public void RemoveFromEnvironment_HappensAfterParsing_SoTheOptionsStillHoldTheValues()
    {
        var environment = new Dictionary<string, string?>(Environment);

        var options = WorkerOptions.Parse(Arguments, name => environment.GetValueOrDefault(name));
        WorkerLaunchVariables.RemoveFromEnvironment((name, value) => environment[name] = value);

        Assert.Equal("s3cret", options.Secret);
        Assert.Equal("run-42", options.RunId);
        Assert.Throws<WorkerOptionsException>(() => WorkerOptions.Parse(Arguments, name => environment.GetValueOrDefault(name)));
    }

    /// <summary>The secret arrives by environment only: a command line is visible to other processes.</summary>
    [Fact]
    public void Parse_SecretOnTheCommandLine_IsNotAccepted()
    {
        var environment = new Dictionary<string, string?>(Environment) { ["FACTORY_WORKER_SECRET"] = null };

        Assert.Throws<WorkerOptionsException>(() => Parse([.. Arguments, "--secret", "s3cret"], environment));
    }
}

public class WorkerSecurityHelperTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("[::1]", true)]
    [InlineData("evil.example", false)]
    [InlineData("127.0.0.1.evil.example", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("", false)]
    public void IsLoopbackHost(string host, bool expected)
    {
        Assert.Equal(expected, WorkerSecurity.IsLoopbackHost(host));
    }

    [Theory]
    [InlineData("s3cret", true)]
    [InlineData("s3cre", false)]
    [InlineData("s3cret ", false)]
    [InlineData("S3CRET", false)]
    [InlineData("", false)]
    public void HasSecret(string presented, bool expected)
    {
        Assert.Equal(expected, WorkerSecurity.HasSecret(presented, Encoding.UTF8.GetBytes("s3cret")));
    }
}

/// <summary>The completion tools called directly, as the model does when PTC is off.</summary>
public class CompletionToolTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static (T Tool, FakeHttpMessageHandler Handler) Create<T>(Func<FactoryHostClient, string, T> create, SubmissionResponse? response = null)
        where T : CompletionTool
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(response ?? new SubmissionResponse(true, ""));
        return (create(TestOptions.HostClient(handler), "session-7"), handler);
    }

    private static SubmissionRequest Posted(FakeHttpMessageHandler handler)
    {
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/internal/runs/run-1/submissions", request.Uri.AbsolutePath);
        Assert.Equal(TestOptions.Secret, request.Headers["X-Factory-Secret"]);
        return JsonSerializer.Deserialize<SubmissionRequest>(request.Body!, FactoryWire.Json)!;
    }

    // ---- submit_work ----

    [Fact]
    public async Task SubmitWork_PostsTheTypedPayload_ForItsSession()
    {
        var (tool, handler) = Create((h, s) => new SubmitWorkTool(h, s));

        var result = await tool.InvokeAsync(Args("""
            {
              "summary": "Added CSV export for Orders.",
              "criteria": [
                { "criterion": "Administrators can export.", "tests": ["Export_Admin_Succeeds"] },
                { "criterion": "Opens correctly in Excel.", "manualOnly": true }
              ],
              "testsAdded": ["Export_Admin_Succeeds", "Export_NonAdmin_Refused"],
              "knownLimitations": ["Large exports are not streamed."],
              "manualTestSteps": ["Sign in as an administrator and export."]
            }
            """), default);

        Assert.False(result.IsError, result.Text);
        Assert.Equal("Recorded.", result.Text);
        var posted = Posted(handler);
        Assert.Equal("session-7", posted.SessionId);
        var work = Assert.IsType<WorkSubmission>(posted.Submission);
        Assert.Equal("Added CSV export for Orders.", work.Summary);
        Assert.Equal(["Export_Admin_Succeeds"], work.Criteria[0].Tests);
        Assert.True(work.Criteria[1].ManualOnly);
        Assert.Equal(2, work.TestsAdded.Count);
        Assert.Equal(["Large exports are not streamed."], work.KnownLimitations);
        Assert.Equal(["Sign in as an administrator and export."], work.ManualTestSteps);
    }

    [Fact]
    public async Task SubmitWork_OnlyASummary_IsEnough()
    {
        var (tool, handler) = Create((h, s) => new SubmitWorkTool(h, s));

        var result = await tool.InvokeAsync(Args("""{"summary":"Fixed the typo."}"""), default);

        Assert.False(result.IsError);
        var work = Assert.IsType<WorkSubmission>(Posted(handler).Submission);
        Assert.Empty(work.Criteria);
        Assert.Empty(work.TestsAdded);
    }

    [Theory]
    [InlineData("""{}""", "'summary' is required")]
    [InlineData("""{"summary":"   "}""", "'summary' is required")]
    [InlineData("""{"summary":"x","criteria":"all of them"}""", "'criteria' must be an array")]
    [InlineData("""{"summary":"x","criteria":[{"tests":["T"]}]}""", "needs a 'criterion'")]
    [InlineData("""{"summary":"x","criteria":[{"criterion":"Works"}]}""", "names no tests")]
    [InlineData("""{"summary":"x","criteria":[{"criterion":"Works","tests":"T"}]}""", "'tests' must be an array")]
    [InlineData("""{"summary":"x","testsAdded":"one"}""", "must be arrays of strings")]
    [InlineData("""{"summary":"x","knownLimitations":[1,2]}""", "must be arrays of strings")]
    public async Task SubmitWork_BadArguments_AreRejectedWithTheReason_AndNothingIsPosted(string json, string expected)
    {
        var (tool, handler) = Create((h, s) => new SubmitWorkTool(h, s));

        var result = await tool.InvokeAsync(Args(json), default);

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Text);
        Assert.Contains("Call submit_work again", result.Text);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ArgumentsThatAreNotAnObject_AreRejected()
    {
        var (tool, handler) = Create((h, s) => new SubmitWorkTool(h, s));

        var result = await tool.InvokeAsync(Args("""["summary"]"""), default);

        Assert.True(result.IsError);
        Assert.Empty(handler.Requests);
    }

    // ---- request_decision ----

    [Fact]
    public async Task RequestDecision_PostsTheDecision_AndTellsTheModelToStop()
    {
        var (tool, handler) = Create((h, s) => new RequestDecisionTool(h, s));

        var result = await tool.InvokeAsync(Args("""
            {
              "question": "Should export include all filtered rows or only the current page?",
              "whyItBlocks": "The request does not say, and the two behave differently for users.",
              "options": ["All filtered rows", "Current page only"],
              "recommendation": "All filtered rows",
              "impact": "OrdersController.Export"
            }
            """), default);

        Assert.False(result.IsError, result.Text);
        Assert.StartsWith("Recorded. Stop now and wait for the answer", result.Text);
        var decision = Assert.IsType<DecisionSubmission>(Posted(handler).Submission);
        Assert.Equal("Should export include all filtered rows or only the current page?", decision.Question);
        Assert.Equal(["All filtered rows", "Current page only"], decision.Options);
        Assert.Equal("All filtered rows", decision.Recommendation);
        Assert.Equal("OrdersController.Export", decision.Impact);
    }

    [Theory]
    [InlineData("""{"whyItBlocks":"w","options":["a","b"]}""", "'question' is required")]
    [InlineData("""{"question":"q","options":["a","b"]}""", "'whyItBlocks' is required")]
    [InlineData("""{"question":"q","whyItBlocks":"w","options":["only one"]}""", "two to four choices, but has 1")]
    [InlineData("""{"question":"q","whyItBlocks":"w"}""", "two to four choices, but has 0")]
    [InlineData("""{"question":"q","whyItBlocks":"w","options":["a","b","c","d","e"]}""", "two to four choices, but has 5")]
    [InlineData("""{"question":"q","whyItBlocks":"w","options":"a or b"}""", "'options' must be an array")]
    public async Task RequestDecision_BadArguments_AreRejected(string json, string expected)
    {
        var (tool, handler) = Create((h, s) => new RequestDecisionTool(h, s));

        var result = await tool.InvokeAsync(Args(json), default);

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Text);
        Assert.Empty(handler.Requests);
    }

    // ---- submit_review ----

    [Fact]
    public async Task SubmitReview_PostsFindings_WithSeverityFileLineAndText()
    {
        var (tool, handler) = Create((h, s) => new SubmitReviewTool(h, s));

        var result = await tool.InvokeAsync(Args("""
            {"findings":[
              {"severity":"blocking","file":"src/Orders.cs","line":42,"text":"Crashes on an empty list."},
              {"severity":"MINOR","file":"src/Orders.cs","text":"Leftover debug output."}
            ]}
            """), default);

        Assert.False(result.IsError, result.Text);
        var review = Assert.IsType<ReviewSubmission>(Posted(handler).Submission);
        Assert.Equal(new ReviewFinding(FindingSeverity.Blocking, "src/Orders.cs", 42, "Crashes on an empty list."), review.Findings[0]);
        Assert.Equal(new ReviewFinding(FindingSeverity.Minor, "src/Orders.cs", null, "Leftover debug output."), review.Findings[1]);
    }

    [Fact]
    public async Task SubmitReview_EmptyFindings_IsACleanReview()
    {
        var (tool, handler) = Create((h, s) => new SubmitReviewTool(h, s));

        var result = await tool.InvokeAsync(Args("""{"findings":[]}"""), default);

        Assert.False(result.IsError);
        Assert.Empty(Assert.IsType<ReviewSubmission>(Posted(handler).Submission).Findings);
    }

    [Theory]
    [InlineData("""{}""", "'findings' is required")]
    [InlineData("""{"findings":"none"}""", "'findings' is required")]
    [InlineData("""{"findings":[{"severity":"critical","file":"a.cs","text":"t"}]}""", "'blocking' or 'minor'")]
    [InlineData("""{"findings":[{"file":"a.cs","text":"t"}]}""", "'blocking' or 'minor'")]
    [InlineData("""{"findings":[{"severity":"minor","text":"t"}]}""", "needs a 'file' and a 'text'")]
    [InlineData("""{"findings":["looks fine"]}""", "must be an object")]
    public async Task SubmitReview_BadArguments_AreRejected(string json, string expected)
    {
        var (tool, handler) = Create((h, s) => new SubmitReviewTool(h, s));

        var result = await tool.InvokeAsync(Args(json), default);

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Text);
        Assert.Empty(handler.Requests);
    }

    // ---- submit_spec ----

    [Fact]
    public async Task SubmitSpec_PostsTheSpecification()
    {
        var (tool, handler) = Create((h, s) => new SubmitSpecTool(h, s));

        var result = await tool.InvokeAsync(Args("""
            {"summary":"Export orders as CSV.","acceptanceCriteria":["Admins can export.","Others are refused."],
             "affectedAreas":["Orders"],"testPlan":"Unit tests cover both criteria.","openQuestions":["Which columns?"]}
            """), default);

        Assert.False(result.IsError, result.Text);
        Assert.StartsWith("Recorded. Stop now", result.Text);
        var spec = Assert.IsType<SpecSubmission>(Posted(handler).Submission);
        Assert.Equal(2, spec.AcceptanceCriteria.Count);
        Assert.Equal("Unit tests cover both criteria.", spec.TestPlan);
        Assert.Equal(["Which columns?"], spec.OpenQuestions);
    }

    [Theory]
    [InlineData("""{"acceptanceCriteria":["a"],"testPlan":"t"}""", "'summary' is required")]
    [InlineData("""{"summary":"s","testPlan":"t"}""", "at least one criterion")]
    [InlineData("""{"summary":"s","acceptanceCriteria":["a"]}""", "'testPlan' is required")]
    public async Task SubmitSpec_BadArguments_AreRejected(string json, string expected)
    {
        var (tool, handler) = Create((h, s) => new SubmitSpecTool(h, s));

        var result = await tool.InvokeAsync(Args(json), default);

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Text);
        Assert.Empty(handler.Requests);
    }

    // ---- The host's answer ----

    [Fact]
    public async Task HostRefusesTheSubmission_TheModelIsToldWhy()
    {
        var (tool, _) = Create(
            (h, s) => new SubmitReviewTool(h, s), new SubmissionResponse(false, "submit_review is only valid in a review turn."));

        var result = await tool.InvokeAsync(Args("""{"findings":[]}"""), default);

        Assert.True(result.IsError);
        Assert.Equal("submit_review was not recorded: submit_review is only valid in a review turn.", result.Text);
    }

    [Fact]
    public async Task HostAddsAMessage_ItFollowsRecorded()
    {
        var (tool, _) = Create((h, s) => new SubmitWorkTool(h, s), new SubmissionResponse(true, "Verification will start now."));

        var result = await tool.InvokeAsync(Args("""{"summary":"done"}"""), default);

        Assert.Equal("Recorded. Verification will start now.", result.Text);
    }

    /// <summary>A turn that believes it submitted when it did not would end with nothing for the
    /// host to act on, so a failed post is an error the model sees.</summary>
    [Fact]
    public async Task HostUnreachable_IsAnErrorTheModelSees_NotASilentSuccess()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueFailure();
        var tool = new SubmitWorkTool(TestOptions.HostClient(handler), "s");

        var result = await tool.InvokeAsync(Args("""{"summary":"done"}"""), default);

        Assert.True(result.IsError);
        Assert.Contains("could not be recorded", result.Text);
        Assert.Contains("Call submit_work again", result.Text);
    }

    [Fact]
    public async Task HostAnswersWithAnError_IsAnErrorTheModelSees()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError);
        var tool = new SubmitWorkTool(TestOptions.HostClient(handler), "s");

        Assert.True((await tool.InvokeAsync(Args("""{"summary":"done"}"""), default)).IsError);
    }

    [Fact]
    public void Tools_HaveTheirBlueprintNames_AndObjectSchemasWithRequiredFields()
    {
        var host = TestOptions.HostClient(new FakeHttpMessageHandler());
        CompletionTool[] tools = [new SubmitWorkTool(host, "s"), new RequestDecisionTool(host, "s"), new SubmitReviewTool(host, "s"), new SubmitSpecTool(host, "s")];

        Assert.Equal(["submit_work", "request_decision", "submit_review", "submit_spec"], tools.Select(t => t.Name));
        Assert.All(tools, tool =>
        {
            Assert.Equal("object", tool.ParameterSchema.GetProperty("type").GetString());
            Assert.True(tool.ParameterSchema.GetProperty("required").GetArrayLength() > 0);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        });
    }
}

public class FactoryToolSetPolicyTests
{
    private sealed class NamedTool(string name) : ITool
    {
        public string Name { get; } = name;
        public string Description => name;
        public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new { type = "object" });
        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct) => Task.FromResult(ToolResult.Ok(name));
    }

    /// <summary>What AddLitosAgent registers, by name.</summary>
    private static readonly string[] Registered =
        ["read_file", "write_file", "edit_file", "list_directory", "search_code", "shell", "web_search", "skill"];

    private static FactoryToolSetPolicy Policy(IEnumerable<string>? registered = null) => new(
        (registered ?? Registered).Select(n => new NamedTool(n)), TestOptions.HostClient(new FakeHttpMessageHandler()));

    private static string[] Names(ToolRegistry registry) => [.. registry.Schemas.Select(s => s.Name)];

    private static readonly string[] WorkSet =
        ["read_file", "write_file", "edit_file", "list_directory", "search_code", "shell", "submit_work", "request_decision"];

    [Theory]
    [InlineData("Implement")]
    [InlineData("Repair")]
    [InlineData("Rework")]
    public void WorkTurns_GetTheFullSet_AndSubmitWorkAndRequestDecision(string kind)
    {
        Assert.Equal(WorkSet, Names(Policy().Create("s", kind)));
    }

    [Fact]
    public void ReviewTurn_IsReadOnly_AndFinishesWithSubmitReview()
    {
        Assert.Equal(["read_file", "list_directory", "search_code", "submit_review"], Names(Policy().Create("s", "Review")));
    }

    [Fact]
    public void SpecTurn_IsReadOnly_AndFinishesWithSubmitSpec()
    {
        Assert.Equal(["read_file", "list_directory", "search_code", "submit_spec"], Names(Policy().Create("s", "Spec")));
    }

    [Fact]
    public void ChatTurn_IsReadOnly_WithNoCompletionTool()
    {
        Assert.Equal(["read_file", "list_directory", "search_code"], Names(Policy().Create("s", "Chat")));
    }

    /// <summary>M1's fixed set: no skills and no web search, whatever the engine registers.</summary>
    [Theory]
    [InlineData("Implement")]
    [InlineData("Review")]
    [InlineData("Chat")]
    public void NoTurn_GetsWebSearchOrSkills(string kind)
    {
        var names = Names(Policy().Create("s", kind));

        Assert.DoesNotContain("web_search", names);
        Assert.DoesNotContain("skill", names);
    }

    [Fact]
    public void Nudge_KeepsTheToolSetOfTheTurnItFollows()
    {
        var policy = Policy();
        policy.Create("thread", "Implement");
        policy.Create("review", "Review");

        Assert.Equal(WorkSet, Names(policy.Create("thread", "Nudge")));
        Assert.Equal(["read_file", "list_directory", "search_code", "submit_review"], Names(policy.Create("review", "Nudge")));
        // And a second nudge still follows the original turn, not the nudge.
        Assert.Contains("submit_review", Names(policy.Create("review", "Nudge")));
    }

    /// <summary>A restarted worker has no record of the session: guessing "implement" would hand
    /// write tools and the shell to what may be a review session.</summary>
    [Fact]
    public void Nudge_ForASessionWithNoEarlierTurn_IsRefused_RatherThanGivenWriteTools()
    {
        var policy = Policy();

        var ex = Assert.Throws<InvalidOperationException>(() => policy.Create("never-started", "Nudge"));

        Assert.Contains("has had no turn", ex.Message);
        Assert.DoesNotContain("shell", Names(policy.CreateForBridge("never-started")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("implement")]
    [InlineData("Deploy")]
    [InlineData("99")]
    public void UnknownOrMissingTurnKind_IsRefused_RatherThanGuessed(string? kind)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Policy().Create("s", kind));

        Assert.Contains("Unknown turn kind", ex.Message);
    }

    // ---- The kernel bridge ----

    /// <summary>PTC must not widen a turn: what kernel code can call is what the turn can call.</summary>
    [Fact]
    public void Bridge_OffersExactlyTheCurrentTurnsTools()
    {
        var policy = Policy();

        policy.Create("thread", "Implement");
        Assert.Equal(WorkSet, Names(policy.CreateForBridge("thread")));

        policy.Create("review", "Review");
        var reviewBridge = Names(policy.CreateForBridge("review"));
        Assert.DoesNotContain("write_file", reviewBridge);
        Assert.DoesNotContain("edit_file", reviewBridge);
        Assert.DoesNotContain("shell", reviewBridge);
        Assert.Contains("submit_review", reviewBridge);
    }

    [Fact]
    public void Bridge_FollowsTheSessionWhenItsTurnKindChanges()
    {
        var policy = Policy();
        policy.Create("thread", "Implement");
        policy.Create("thread", "Chat");

        Assert.Equal(["read_file", "list_directory", "search_code"], Names(policy.CreateForBridge("thread")));
    }

    [Fact]
    public void Bridge_SessionThatHasHadNoTurn_CanOnlyRead()
    {
        Assert.Equal(["read_file", "list_directory", "search_code"], Names(Policy().CreateForBridge("never-started")));
    }

    [Fact]
    public void Bridge_SessionsAreIndependent()
    {
        var policy = Policy();
        policy.Create("a", "Implement");
        policy.Create("b", "Review");

        Assert.Contains("shell", Names(policy.CreateForBridge("a")));
        Assert.DoesNotContain("shell", Names(policy.CreateForBridge("b")));
    }

    [Fact]
    public void Constructor_RequiredToolMissing_FailsAtStartup_NamingIt()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Policy(Registered.Where(n => n is not ("shell" or "edit_file"))));

        Assert.Contains("edit_file", ex.Message);
        Assert.Contains("shell", ex.Message);
    }

    [Fact]
    public async Task CompletionTools_AreBoundToTheSessionTheyWereCreatedFor()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(new SubmissionResponse(true, ""));
        var policy = new FactoryToolSetPolicy(Registered.Select(n => new NamedTool(n)), TestOptions.HostClient(handler));

        var tool = policy.Create("thread-42", "Implement").Resolve("submit_work");
        await tool.InvokeAsync(JsonDocument.Parse("""{"summary":"done"}""").RootElement, default);

        Assert.Equal("thread-42", JsonSerializer.Deserialize<SubmissionRequest>(handler.Requests[0].Body!, FactoryWire.Json)!.SessionId);
    }
}

public class GatewayChatProviderTests
{
    private static readonly ChatRequest Request = new(
        [ChatMessage.User("Add CSV export.")],
        [new ToolSchema("read_file", "Reads a file.", JsonSerializer.SerializeToElement(new { type = "object" }))],
        "vendor/model", SystemPrompt: "You are Litos.", SessionId: "session-1");

    private static string Ndjson(params GatewayEvent[] events) =>
        string.Concat(events.Select(e => JsonSerializer.Serialize(e, FactoryWire.Json) + "\n"));

    private static (GatewayChatProvider Provider, FakeHttpMessageHandler Handler) Create(string? body = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new FakeHttpMessageHandler();
        if (body is not null)
            handler.Enqueue(status, body, "application/x-ndjson");
        return (new GatewayChatProvider(TestOptions.HostClient(handler), "openrouter", "vendor/model", 200_000), handler);
    }

    private static async Task<List<AgentEvent>> DrainAsync(IAsyncEnumerable<AgentEvent> events)
    {
        var list = new List<AgentEvent>();
        await foreach (var evt in events)
            list.Add(evt);
        return list;
    }

    private static readonly GatewayMessageCompleted Completed =
        new(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(200, 900, 3_000, 12_000, ReasoningTokens: 640));

    [Fact]
    public async Task StreamAsync_PostsTheWholeChatRequest_ToTheRunsGateway_WithTheSecretAndAFreshKey()
    {
        var (provider, handler) = Create(Ndjson(Completed));

        await DrainAsync(provider.StreamAsync(Request, default));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/internal/runs/run-1/gateway", request.Uri.AbsolutePath);
        Assert.Equal(TestOptions.Secret, request.Headers["X-Factory-Secret"]);
        var sent = JsonSerializer.Deserialize<GatewayRequest>(request.Body!, FactoryWire.Json)!;
        Assert.True(Guid.TryParse(sent.RequestKey, out _));
        Assert.Equal("vendor/model", sent.ChatRequest.Model);
        Assert.Equal("You are Litos.", sent.ChatRequest.SystemPrompt);
        Assert.Equal("session-1", sent.ChatRequest.SessionId);
        Assert.Equal("read_file", Assert.Single(sent.ChatRequest.Tools).Name);
        Assert.Equal("Add CSV export.", Assert.IsType<TextBlock>(sent.ChatRequest.Messages[0].Content[0]).Text);
    }

    [Fact]
    public async Task StreamAsync_EachCall_HasItsOwnRequestKey()
    {
        var (provider, handler) = Create(Ndjson(Completed));
        handler.Enqueue(HttpStatusCode.OK, Ndjson(Completed), "application/x-ndjson");

        await DrainAsync(provider.StreamAsync(Request, default));
        await DrainAsync(provider.StreamAsync(Request, default));

        var keys = handler.Requests.Select(r => JsonSerializer.Deserialize<GatewayRequest>(r.Body!, FactoryWire.Json)!.RequestKey).ToList();
        Assert.NotEqual(keys[0], keys[1]);
    }

    [Fact]
    public async Task StreamAsync_MapsEveryGatewayEventToTheProviderEventTheLoopExpects()
    {
        var arguments = JsonSerializer.SerializeToElement(new { path = "a.txt" });
        var (provider, _) = Create(Ndjson(
            new GatewayReasoningDelta("thinking"),
            new GatewayTextDelta("Reading."),
            new GatewayHeartbeat(),
            new GatewayToolCallStarted("c1", "read_file"),
            new GatewayToolCallArgsDelta("c1", "{\"pa"),
            new GatewayToolCallCompleted("c1", "read_file", arguments),
            Completed));

        var events = await DrainAsync(provider.StreamAsync(Request, default));

        Assert.Collection(events,
            e => Assert.Equal(new ReasoningDelta("thinking"), e),
            e => Assert.Equal(new TextDelta("Reading."), e),
            e => Assert.IsType<StreamHeartbeat>(e),
            e => Assert.Equal(new ToolCallStarted("c1", "read_file"), e),
            e => Assert.Equal(new ToolCallArgsDelta("c1", "{\"pa"), e),
            e => Assert.Equal("a.txt", Assert.IsType<ToolCallCompleted>(e).Arguments.GetProperty("path").GetString()),
            e =>
            {
                var completed = Assert.IsType<MessageCompleted>(e);
                Assert.Equal(new UsageInfo(200, 900, 3_000, 12_000, ReasoningTokens: 640), completed.Usage);
                Assert.Equal("done", Assert.IsType<TextBlock>(Assert.Single(completed.Message.Content)).Text);
            });
    }

    [Fact]
    public async Task StreamAsync_BlankLines_AreSkipped()
    {
        var (provider, _) = Create("\n" + Ndjson(new GatewayTextDelta("a")) + "\n\n" + Ndjson(Completed));

        Assert.Equal(2, (await DrainAsync(provider.StreamAsync(Request, default))).Count);
    }

    [Theory]
    [InlineData(GatewayErrorCodes.BudgetExhausted)]
    [InlineData(GatewayErrorCodes.QuotaExhausted)]
    public async Task StreamAsync_AdmissionRefused_ThrowsGatewayRefused_WithTheCode(string code)
    {
        var (provider, _) = Create(Ndjson(new GatewayError(code, "Needs 20,900 tokens; 20,000 remain.")));

        var ex = await Assert.ThrowsAsync<GatewayRefusedException>(() => DrainAsync(provider.StreamAsync(Request, default)));

        Assert.Equal(code, ex.Code);
        Assert.Equal("Needs 20,900 tokens; 20,000 remain.", ex.Message);
    }

    [Fact]
    public async Task StreamAsync_ProviderError_ThrowsCallFailed_AfterYieldingWhatCameBefore()
    {
        var (provider, _) = Create(Ndjson(new GatewayTextDelta("partial"), new GatewayError(GatewayErrorCodes.ProviderError, "The provider returned 500.")));
        var seen = new List<AgentEvent>();

        var ex = await Assert.ThrowsAsync<GatewayCallFailedException>(async () =>
        {
            await foreach (var evt in provider.StreamAsync(Request, default))
                seen.Add(evt);
        });

        Assert.Equal("The provider returned 500.", ex.Message);
        Assert.Equal(new TextDelta("partial"), Assert.Single(seen));
    }

    /// <summary>A stream that just stops is a broken connection, not an empty reply.</summary>
    [Fact]
    public async Task StreamAsync_StreamEndsWithoutACompletedMessage_Throws()
    {
        var (provider, _) = Create(Ndjson(new GatewayTextDelta("partial")));

        var ex = await Assert.ThrowsAsync<GatewayCallFailedException>(() => DrainAsync(provider.StreamAsync(Request, default)));

        Assert.Contains("ended before the message was complete", ex.Message);
    }

    [Fact]
    public async Task StreamAsync_EmptyResponse_Throws()
    {
        var (provider, _) = Create("");

        await Assert.ThrowsAsync<GatewayCallFailedException>(() => DrainAsync(provider.StreamAsync(Request, default)));
    }

    [Theory]
    [InlineData("this is not json\n")]
    [InlineData("{\"event\":\"mystery\"}\n")]
    [InlineData("null\n")]
    public async Task StreamAsync_UnreadableEvent_Throws(string body)
    {
        var (provider, _) = Create(body);

        await Assert.ThrowsAsync<GatewayCallFailedException>(() => DrainAsync(provider.StreamAsync(Request, default)));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task StreamAsync_HttpError_Throws(HttpStatusCode status, string code)
    {
        var (provider, _) = Create("{}", status);

        var ex = await Assert.ThrowsAsync<GatewayCallFailedException>(() => DrainAsync(provider.StreamAsync(Request, default)));

        Assert.Contains(code, ex.Message);
    }

    [Fact]
    public async Task StreamAsync_HostUnreachable_Throws()
    {
        var (provider, handler) = Create();
        handler.EnqueueFailure();

        var ex = await Assert.ThrowsAsync<GatewayCallFailedException>(() => DrainAsync(provider.StreamAsync(Request, default)));

        Assert.Contains("could not be reached", ex.Message);
    }

    [Fact]
    public async Task ListModelsAsync_IsTheOneModelFixedAtLaunch()
    {
        var (provider, handler) = Create();

        var model = Assert.Single(await provider.ListModelsAsync(default));

        Assert.Equal(new ModelInfo("vendor/model", "vendor/model", IsDefault: true, ContextLength: 200_000), model);
        Assert.Equal("openrouter", provider.ProviderName);
        Assert.Empty(handler.Requests); // no catalog call: the worker has nothing to choose from
    }

    [Fact]
    public void Factory_AnswersEveryProviderNameWithTheGateway()
    {
        var (provider, _) = Create();
        var factory = new GatewayChatProviderFactory(provider);

        Assert.Same(provider, factory.Resolve("openrouter"));
        Assert.Same(provider, factory.Resolve("anthropic"));
    }
}
