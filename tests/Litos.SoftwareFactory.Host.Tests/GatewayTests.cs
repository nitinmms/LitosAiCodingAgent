using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Budget;
using Litos.SoftwareFactory.Core.Estimation;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Gateway;
using Litos.SoftwareFactory.Host.Runs;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// The model gateway against a real store and a scripted provider: admission, the output cap,
/// settlement, and what happens to a reservation when a call does not finish (§9.2, §9.3).
/// </summary>
public sealed class GatewayTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private ModelGateway _gateway = null!;
    private ActiveRun _run = null!;
    private Guid _threadId;

    public async Task InitializeAsync()
    {
        _host = await TestHost.StartAsync(startCoordinator: false);
        _gateway = _host.App.Services.GetRequiredService<ModelGateway>();
        _gateway.RateLimitBackoff = TimeSpan.FromMilliseconds(40);
        _gateway.NothingProducedBackoff = TimeSpan.FromMilliseconds(40);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    /// <summary>A thread with a claimed run and the given cap.</summary>
    private async Task StartRunAsync(long? cap)
    {
        _threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync(), budgetCap: cap);
        if (cap is null)
            await _host.Store.SetBudgetCapAsync(_threadId, null, DateTimeOffset.UtcNow, default);
        await _host.DelegateAsync(_threadId);
        var claimed = (await _host.Store.ClaimNextRunAsync(1, DateTimeOffset.UtcNow, default))!;
        _run = new ActiveRun(claimed.Run.Id, _threadId, claimed.Run.RequestedBy, "openrouter", "deepseek/deepseek-v4.1-flash");
    }

    /// <summary>A request of about `inputTokens` by the chars ÷ 4 rule.</summary>
    private static GatewayRequest Request(int inputTokens = 15_000, string? key = null, string session = "session-1", params ChatMessage[] more) => new(
        key ?? Guid.NewGuid().ToString(),
        new ChatRequest([ChatMessage.User(new string('x', inputTokens * 4)), .. more], [], "whatever-the-worker-asked-for", SessionId: session));

    private async Task<List<GatewayEvent>> CallAsync(GatewayRequest request, CancellationToken ct = default)
    {
        var events = new List<GatewayEvent>();
        await _gateway.HandleAsync(_run, request, evt =>
        {
            events.Add(evt);
            return Task.CompletedTask;
        }, ct);
        return events;
    }

    private async Task<TaskThread> ThreadAsync() => (await _host.ThreadAsync(_threadId)).Thread;

    private async Task<UsageEntry> OnlyEntryAsync() => Assert.Single(await _host.Store.ListUsageAsync(_threadId, default));

    // ---- Admission ----

    [Fact]
    public async Task AdmittedCall_IsSentWithTheOutputCap_AndTheTasksModel_ThenSettled()
    {
        await StartRunAsync(cap: 100_000);
        _host.Provider.EnqueueReply("done", new UsageInfo(14_200, 800));

        var events = await CallAsync(Request());

        var sent = Assert.Single(_host.Provider.Requests);
        Assert.Equal(4_000, sent.MaxOutputTokens);                  // so the reply cannot exceed the reservation
        Assert.Equal("deepseek/deepseek-v4.1-flash", sent.Model);   // the task's model, not what the worker named
        Assert.Equal(["openrouter"], _host.Provider.ResolvedNames);
        Assert.IsType<GatewayTextDelta>(events[0]);
        Assert.Equal(new UsageInfo(14_200, 800), Assert.IsType<GatewayMessageCompleted>(events[^1]).Usage);

        var thread = await ThreadAsync();
        Assert.Equal((15_000L, 0L), (thread.TokensUsed, thread.TokensReserved));
        var entry = await OnlyEntryAsync();
        Assert.Equal((UsageStatus.Settled, 15_000L, 20_500L, 15_000L), (entry.Status, entry.EstimatedInputRaw, entry.Reserved, entry.Charged));
        Assert.Equal(_run.UserId, entry.UserId);
    }

    /// <summary>Acceptance scenario 6: the next unaffordable call is refused before it is sent.</summary>
    [Fact]
    public async Task CallThatDoesNotFit_IsRefusedBeforeSending_AndTheRunIsToldWhy()
    {
        await StartRunAsync(cap: 20_000);

        var events = await CallAsync(Request());

        var error = Assert.IsType<GatewayError>(Assert.Single(events));
        Assert.Equal(GatewayErrorCodes.BudgetExhausted, error.Code);
        Assert.Contains("20,500", error.Message);
        Assert.Contains("20,000", error.Message);
        Assert.Empty(_host.Provider.Requests);                       // nothing was spent
        Assert.Equal(error.Message, _run.BudgetRefusal);            // the coordinator turns this into PausedBudget
        var thread = await ThreadAsync();
        Assert.Equal((0L, 0L), (thread.TokensUsed, thread.TokensReserved));
    }

    [Fact]
    public async Task NoCap_AdmitsWithoutLimit()
    {
        await StartRunAsync(cap: null);
        _host.Provider.EnqueueReply("done");

        Assert.IsType<GatewayMessageCompleted>((await CallAsync(Request(inputTokens: 900_000)))[^1]);
    }

    /// <summary>A unique request key per call prevents double-charging on a repeated request.</summary>
    [Fact]
    public async Task SameRequestKeyTwice_IsSentAndChargedOnce()
    {
        await StartRunAsync(cap: 100_000);
        _host.Provider.EnqueueReply("done", new UsageInfo(10_000, 500));

        await CallAsync(Request(key: "key-1"));
        var second = await CallAsync(Request(key: "key-1"));

        Assert.IsType<GatewayError>(Assert.Single(second));
        Assert.Single(_host.Provider.Requests);
        Assert.Equal(10_500, (await ThreadAsync()).TokensUsed);
    }

    // ---- Settlement ----

    [Fact]
    public async Task Settlement_CountsCacheReadsAtATenth_AndRecordsReasoningWithoutAddingItAgain()
    {
        await StartRunAsync(cap: 100_000);
        _host.Provider.EnqueueReply("done", new UsageInfo(200, 900, 3_000, 9_000, ReasoningTokens: 640));

        await CallAsync(Request());

        // 200 new + 3,000 written to the cache in full, 9,000 read from it at 10%, 900 output.
        Assert.Equal(5_000, (await ThreadAsync()).TokensUsed);
        // What the provider reported is recorded as reported, whatever it was charged at.
        var entry = await OnlyEntryAsync();
        Assert.Equal((12_200L, 12_000L, 900L, 640L), (entry.ActualInput, entry.ActualCachedInput, entry.ActualOutput, entry.ActualReasoning));
    }

    /// <summary>The first real run's sixteenth call: 23,567 input of which 22,784 came from the
    /// cache. Charged in full it cost 23,701; a task of 44 such calls used a million tokens.</summary>
    [Fact]
    public async Task Settlement_OfAMostlyCachedCall_ChargesMostlyTheNewInputAndTheOutput()
    {
        await StartRunAsync(cap: 100_000);
        _host.Provider.EnqueueReply("done", new UsageInfo(783, 134, 0, 22_784, ReasoningTokens: 68));

        await CallAsync(Request(inputTokens: 23_000));

        Assert.Equal(783 + 2_279 + 134, (await ThreadAsync()).TokensUsed);
        Assert.Equal(783 + 2_279 + 134, (await OnlyEntryAsync()).Charged);
    }

    [Fact]
    public async Task Settlement_WithFullWeightConfigured_CountsCachedInputInFull()
    {
        _host.Options.Budget = _host.Options.Budget with { CachedInputWeight = 1 };
        await StartRunAsync(cap: 100_000);
        _host.Provider.EnqueueReply("done", new UsageInfo(200, 900, 3_000, 9_000));

        await CallAsync(Request());

        Assert.Equal(13_100, (await ThreadAsync()).TokensUsed);
    }

    /// <summary>§9.4: a provider that reports no usage is charged the host's own estimate.</summary>
    [Fact]
    public async Task ProviderReportsNoUsage_TheHostChargesItsOwnEstimate_ForInputAndOutput()
    {
        await StartRunAsync(cap: 100_000);
        _host.Provider.EnqueueReply(new string('r', 2_000), new UsageInfo(0, 0)); // a 500-token reply

        await CallAsync(Request());

        Assert.Equal(15_500, (await ThreadAsync()).TokensUsed);
    }

    [Fact]
    public async Task UsageAboveTheReservation_IsChargedInFull()
    {
        await StartRunAsync(cap: 100_000);
        _host.Provider.EnqueueReply("done", new UsageInfo(40_000, 3_000));

        await CallAsync(Request());

        var thread = await ThreadAsync();
        Assert.Equal((43_000L, 0L), (thread.TokensUsed, thread.TokensReserved));
    }

    // ---- Estimation ----

    [Fact]
    public async Task SecondCallInASession_IsEstimatedFromTheFirstCallsRealInput_PlusWhatWasAppended()
    {
        await StartRunAsync(cap: 500_000);
        _host.Provider.EnqueueReply("first", new UsageInfo(22_000, 300)); // the real tokenizer ran heavier than chars ÷ 4
        _host.Provider.EnqueueReply("second", new UsageInfo(23_000, 300));

        await CallAsync(Request());
        await CallAsync(Request(more: [ChatMessage.Assistant([new TextBlock("first")]), ChatMessage.User(new string('y', 4_000))]));

        var entries = await _host.Store.ListUsageAsync(_threadId, default);
        Assert.Equal(15_000, entries[0].EstimatedInputRaw);
        // Baseline 22,000 + ("first" = 5 chars → 2 tokens) + (4,000 chars → 1,000 tokens).
        Assert.Equal(23_002, entries[1].EstimatedInputRaw);
        var baseline = _run.Baselines["session-1"];
        Assert.Equal((3, 23_000), (baseline.MessageCount, baseline.TotalInputTokens));
        Assert.NotNull(baseline.SettledAt);
    }

    // ---- A call is reserved for about what it will cost ----

    /// <summary>
    /// The gap the first real run showed: calls that cost about 6,000 tokens each held 55,000,
    /// because the whole input was reserved in full although the cache served nine tenths of it.
    /// Once a session has been served from the cache, the repeated part is reserved at the
    /// cached weight.
    /// </summary>
    [Fact]
    public async Task AfterACacheHit_TheNextCallInTheSession_ReservesItsRepeatedInputAtTheCachedWeight()
    {
        await StartRunAsync(cap: 500_000);
        _host.Provider.EnqueueReply("first", new UsageInfo(15_000, 300));                 // nothing cached yet
        _host.Provider.EnqueueReply("second", new UsageInfo(1_000, 300, 0, 15_000));     // the cache is hit
        _host.Provider.EnqueueReply("third", new UsageInfo(1_100, 300, 0, 16_000));
        ChatMessage[] turn2 = [ChatMessage.Assistant([new TextBlock("first")]), ChatMessage.User(new string('y', 4_000))];
        ChatMessage[] turn3 = [.. turn2, ChatMessage.Assistant([new TextBlock("second")]), ChatMessage.User(new string('z', 4_000))];

        await CallAsync(Request());
        await CallAsync(Request(more: turn2));
        await CallAsync(Request(more: turn3));

        var entries = await _host.Store.ListUsageAsync(_threadId, default);
        // First call: no baseline. Second: a baseline, but the cache has not been seen to work.
        Assert.Equal(16_500 + 4_000, entries[0].Reserved);
        Assert.Equal((long)Math.Ceiling(entries[1].EstimatedInput * 1.1m) + 4_000, entries[1].Reserved);
        // Third: the second call's 16,000 input is expected from the cache, at a tenth.
        var appended = entries[2].EstimatedInput - 16_000;
        Assert.Equal((long)Math.Ceiling((appended + 1_600) * 1.1m) + 4_000, entries[2].Reserved);
        Assert.True(entries[2].Reserved < entries[1].Reserved / 2, "The cached call should hold far less than the uncached one.");
        // And what each was charged: cache reads at a tenth.
        Assert.Equal(new[] { 15_300L, 1_000 + 1_500 + 300, 1_100 + 1_600 + 300 }, entries.Select(e => e.Charged));
    }

    [Fact]
    public async Task ACacheHitTooLongAgo_IsNotCountedOn_AndTheCallIsReservedInFull()
    {
        _host.Options.Budget = _host.Options.Budget with { CacheWindow = TimeSpan.Zero };
        await StartRunAsync(cap: 500_000);
        _host.Provider.EnqueueReply("first", new UsageInfo(1_000, 300, 0, 14_000));
        _host.Provider.EnqueueReply("second", new UsageInfo(1_000, 300, 0, 15_000));

        await CallAsync(Request());
        await Task.Delay(30);
        await CallAsync(Request(more: [ChatMessage.Assistant([new TextBlock("first")]), ChatMessage.User(new string('y', 4_000))]));

        var entries = await _host.Store.ListUsageAsync(_threadId, default);
        Assert.Equal((long)Math.Ceiling(entries[1].EstimatedInput * 1.1m) + 4_000, entries[1].Reserved);
    }

    /// <summary>With less than the output allowance left, the old rule paused the task. Now the
    /// call is sent with its output limited to what remains, so it still cannot pass the cap.</summary>
    [Fact]
    public async Task NearTheCap_TheCallIsSentWithItsOutputLimitedToWhatRemains()
    {
        _host.Options.Budget = _host.Options.Budget with { MinimumOutputTokens = 1_000 };
        await StartRunAsync(cap: 18_500);
        _host.Provider.EnqueueReply("done", new UsageInfo(15_000, 800));

        var events = await CallAsync(Request());

        Assert.IsType<GatewayMessageCompleted>(events[^1]);
        Assert.Equal(2_000, Assert.Single(_host.Provider.Requests).MaxOutputTokens);   // 18,500 less 16,500 for the input
        Assert.Equal(18_500, (await OnlyEntryAsync()).Reserved);
        Assert.Equal((15_800L, 0L), ((await ThreadAsync()).TokensUsed, (await ThreadAsync()).TokensReserved));
    }

    [Fact]
    public async Task WithoutRoomForTheMinimumOutput_TheCallIsRefused()
    {
        _host.Options.Budget = _host.Options.Budget with { MinimumOutputTokens = 1_000 };
        await StartRunAsync(cap: 17_499);

        var error = Assert.IsType<GatewayError>(Assert.Single(await CallAsync(Request())));

        Assert.Equal(GatewayErrorCodes.BudgetExhausted, error.Code);
        Assert.Contains("17,500", error.Message);
        Assert.Empty(_host.Provider.Requests);
    }

    /// <summary>A reply cut off by a limit the budget imposed says so in the same way: the
    /// message names the limit the call was actually sent with.</summary>
    [Fact]
    public async Task ReplyCutOffAtALimitTheBudgetImposed_NamesThatLimit()
    {
        _host.Options.Budget = _host.Options.Budget with { MinimumOutputTokens = 1_000 };
        await StartRunAsync(cap: 18_500);
        _host.Provider.Enqueue((_, _) => ScriptedProvider.Events(Completed(new UsageInfo(15_000, 2_000, ReasoningTokens: 2_000))));

        var error = Assert.IsType<GatewayError>(Assert.Single(await CallAsync(Request())));

        Assert.StartsWith("The model reached the output limit of 2,000 tokens without producing a reply", error.Message);
    }

    [Fact]
    public async Task Calibration_LearnsFromSettledCalls_AndRaisesLaterEstimates()
    {
        await StartRunAsync(cap: 500_000);
        _host.Provider.EnqueueReply("a", new UsageInfo(18_000, 100)); // 1.2 × the estimate
        _host.Provider.EnqueueReply("b", new UsageInfo(18_000, 100));

        await CallAsync(Request(session: "s1"));
        await CallAsync(Request(session: "s2")); // a new session: no baseline, so calibration is what helps

        var entries = await _host.Store.ListUsageAsync(_threadId, default);
        Assert.Equal((15_000L, 15_000L), (entries[0].EstimatedInputRaw, entries[0].EstimatedInput));
        Assert.Equal((15_000L, 18_000L), (entries[1].EstimatedInputRaw, entries[1].EstimatedInput));
        Assert.Equal(1.2, _gateway.CalibrationFor("openrouter", "deepseek/deepseek-v4.1-flash").Ratio, precision: 6);
    }

    // ---- Calls that do not finish ----

    /// <summary>Acceptance scenario 8: a 429 waits and retries without losing the reservation.</summary>
    [Fact]
    public async Task RateLimited_WaitsAndRetries_KeepingTheReservation_ThenSettlesOnce()
    {
        await StartRunAsync(cap: 100_000);
        long reservedDuringRetry = -1;
        _host.Provider.EnqueueThrow(new ChatProviderRateLimitedException("OpenRouter is rate-limiting requests."));
        _host.Provider.Enqueue((_, _) =>
        {
            reservedDuringRetry = _host.Store.GetThreadAsync(_threadId, default).GetAwaiter().GetResult()!.Thread.TokensReserved;
            return ScriptedProvider.Events(new MessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(14_000, 500)));
        });

        var events = await CallAsync(Request());

        Assert.Contains(events, e => e is GatewayHeartbeat);          // the worker's watchdog stays quiet during the wait
        Assert.IsType<GatewayMessageCompleted>(events[^1]);
        Assert.DoesNotContain(events, e => e is GatewayError);
        Assert.Equal(20_500, reservedDuringRetry);
        Assert.Equal(2, _host.Provider.Requests.Count);
        Assert.Single(await _host.Store.ListUsageAsync(_threadId, default));
        Assert.Equal(14_500, (await ThreadAsync()).TokensUsed);
    }

    [Fact]
    public async Task RateLimitedAsAnErrorEvent_IsRetriedToo()
    {
        await StartRunAsync(cap: 100_000);
        _host.Provider.Enqueue((_, _) => ScriptedProvider.Events(new ErrorOccurred(new ChatProviderRateLimitedException("Slow down."))));
        _host.Provider.EnqueueReply("done");

        Assert.IsType<GatewayMessageCompleted>((await CallAsync(Request()))[^1]);
    }

    [Fact]
    public async Task RateLimitedEveryTime_GivesUp_AndReturnsTheReservation()
    {
        await StartRunAsync(cap: 100_000);
        _gateway.RateLimitAttempts = 3;
        for (var i = 0; i < 3; i++)
            _host.Provider.EnqueueThrow(new ChatProviderRateLimitedException("Still rate-limited."));

        var events = await CallAsync(Request());

        Assert.Equal(GatewayErrorCodes.ProviderError, Assert.IsType<GatewayError>(events[^1]).Code);
        Assert.Equal(3, _host.Provider.Requests.Count);
        var thread = await ThreadAsync();
        Assert.Equal((0L, 0L), (thread.TokensUsed, thread.TokensReserved)); // never sent, so nothing is held
    }

    [Fact]
    public async Task ProviderFailsBeforeProducingAnything_EveryTime_GivesUp_AndTheReservationIsReturned()
    {
        await StartRunAsync(cap: 100_000);
        for (var i = 0; i < 3; i++)
            _host.Provider.EnqueueThrow(new HttpRequestException("Connection refused."));

        var events = await CallAsync(Request());

        var error = Assert.IsType<GatewayError>(events[^1]);
        Assert.Equal((GatewayErrorCodes.ProviderError, "Connection refused."), (error.Code, error.Message));
        Assert.Equal(3, _host.Provider.Requests.Count); // the first try and two retries
        Assert.Equal(0, (await ThreadAsync()).TokensReserved);
    }

    // ---- A call that failed before producing anything is sent again ----
    // On F5's re-run a stream ended after 100 seconds with no output; nothing was charged, but the
    // turn failed and the task was blocked.

    [Fact]
    public async Task AStreamThatEndsEmpty_IsSentAgain_AndSettlesOnce()
    {
        await StartRunAsync(cap: 100_000);
        _host.Provider.Enqueue((_, _) => ScriptedProvider.Events());
        _host.Provider.Enqueue((_, _) => ScriptedProvider.Events(new MessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(14_000, 500))));

        var events = await CallAsync(Request());

        Assert.IsType<GatewayMessageCompleted>(events[^1]);
        Assert.DoesNotContain(events, e => e is GatewayError);
        Assert.Contains(events, e => e is GatewayHeartbeat);
        Assert.Equal(2, _host.Provider.Requests.Count);
        var entry = Assert.Single(await _host.Store.ListUsageAsync(_threadId, default));
        Assert.Equal(UsageStatus.Settled, entry.Status);
        Assert.Equal(14_500, (await ThreadAsync()).TokensUsed);
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("error event")]
    public async Task AConnectionFailureBeforeAnything_IsSentAgain(string how)
    {
        await StartRunAsync(cap: 100_000);
        if (how == "throws")
            _host.Provider.EnqueueThrow(new IOException("The connection was reset."));
        else
            _host.Provider.Enqueue((_, _) => ScriptedProvider.Events(new ErrorOccurred(new HttpRequestException("Connection refused."))));
        _host.Provider.EnqueueReply("done");

        var events = await CallAsync(Request());

        Assert.IsType<GatewayMessageCompleted>(events[^1]);
        Assert.Equal(2, _host.Provider.Requests.Count);
    }

    [Fact]
    public async Task AStreamThatEndsEmptyEveryTime_GivesUp_AndTheReservationIsReturned()
    {
        await StartRunAsync(cap: 100_000);
        for (var i = 0; i < 3; i++)
            _host.Provider.Enqueue((_, _) => ScriptedProvider.Events());

        var events = await CallAsync(Request());

        Assert.Contains("ended before the message was complete", Assert.IsType<GatewayError>(events[^1]).Message);
        Assert.Equal(3, _host.Provider.Requests.Count);
        Assert.Equal((0L, 0L), ((await ThreadAsync()).TokensUsed, (await ThreadAsync()).TokensReserved));
    }

    /// <summary>An SDK-based provider (Anthropic, OpenAI, Gemini) raises its own exception type with
    /// the HTTP status on it; a 429 or a 5xx from one is handled like OpenRouter's.</summary>
    private sealed class SdkException(int status) : Exception($"HTTP {status}")
    {
        public int Status { get; } = status;
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(529)]
    public async Task AnSdkProvidersRateLimitOrServerError_IsSentAgain(int status)
    {
        await StartRunAsync(cap: 100_000);
        _host.Provider.EnqueueThrow(new SdkException(status));
        _host.Provider.EnqueueReply("done");

        var events = await CallAsync(Request());

        Assert.IsType<GatewayMessageCompleted>(events[^1]);
        Assert.Equal(2, _host.Provider.Requests.Count);
    }

    [Fact]
    public async Task AnSdkProvidersRejectedRequest_IsNotSentAgain()
    {
        await StartRunAsync(cap: 100_000);
        _host.Provider.EnqueueThrow(new SdkException(400));

        var events = await CallAsync(Request());

        Assert.Equal("HTTP 400", Assert.IsType<GatewayError>(Assert.Single(events)).Message);
        Assert.Single(_host.Provider.Requests);
    }

    [Fact]
    public async Task AFailureThatIsNotAConnectionFailure_IsNotSentAgain()
    {
        await StartRunAsync(cap: 100_000);
        _host.Provider.EnqueueThrow(new InvalidOperationException("The model does not exist."));

        var events = await CallAsync(Request());

        Assert.Equal("The model does not exist.", Assert.IsType<GatewayError>(Assert.Single(events)).Message);
        Assert.Single(_host.Provider.Requests);
    }

    /// <summary>Acceptance scenario 8: a call that ends with unknown usage cannot silently
    /// refund the allowance.</summary>
    [Fact]
    public async Task ProviderFailsMidResponse_UsageIsUnknown_SoTheReservationStaysCharged()
    {
        await StartRunAsync(cap: 100_000);
        _host.Provider.Enqueue((_, _) => ScriptedProvider.Events(new TextDelta("partial"), new ErrorOccurred(new IOException("The connection was reset."))));

        var events = await CallAsync(Request());

        Assert.IsType<GatewayTextDelta>(events[0]);
        Assert.Equal("The connection was reset.", Assert.IsType<GatewayError>(events[^1]).Message);
        Assert.Equal(20_500, (await ThreadAsync()).TokensReserved);
        Assert.Equal(UsageStatus.Unknown, (await OnlyEntryAsync()).Status);
    }

    [Fact]
    public async Task ProviderStreamEndsWithoutAMessage_IsAnError_WithUnknownUsage()
    {
        await StartRunAsync(cap: 100_000);
        _host.Provider.Enqueue((_, _) => ScriptedProvider.Events(new TextDelta("partial")));

        var events = await CallAsync(Request());

        Assert.Contains("ended before the message was complete", Assert.IsType<GatewayError>(events[^1]).Message);
        Assert.Equal(UsageStatus.Unknown, (await OnlyEntryAsync()).Status);
    }

    // ---- A reply cut off at the output limit ----

    private static MessageCompleted Completed(UsageInfo usage, params ContentBlock[] content) => new(ChatMessage.Assistant(content), usage);

    /// <summary>
    /// The first real run stopped here: the model spent its whole output allowance reasoning and
    /// returned an empty message, twice. Passed on, that looks like a turn that chose to stop,
    /// and the run was blamed for not calling submit_review.
    /// </summary>
    [Fact]
    public async Task ReplyCutOffAtTheOutputLimitWithNothingInIt_IsAnError_ThatSaysSo_AndIsStillCharged()
    {
        await StartRunAsync(cap: 100_000);
        _host.Provider.Enqueue((_, _) => ScriptedProvider.Events(Completed(new UsageInfo(14_200, 4_000, ReasoningTokens: 4_000))));

        var events = await CallAsync(Request());

        var error = Assert.IsType<GatewayError>(Assert.Single(events));
        Assert.Equal(GatewayErrorCodes.ProviderError, error.Code);
        Assert.Equal(
            "The model reached the output limit of 4,000 tokens without producing a reply (4,000 of them were reasoning). Nothing was lost; resuming tries the step again.",
            error.Message);

        // The call was made, so it is charged and its reservation released.
        var entry = await OnlyEntryAsync();
        Assert.Equal((UsageStatus.Settled, 18_200L), (entry.Status, entry.Charged));
        Assert.Equal((18_200L, 0L), ((await ThreadAsync()).TokensUsed, (await ThreadAsync()).TokensReserved));
    }

    [Fact]
    public async Task ReplyAtTheOutputLimit_ThatDidProduceSomething_IsPassedOn()
    {
        await StartRunAsync(cap: 100_000);
        _host.Provider.EnqueueReply("a long answer", new UsageInfo(14_200, 4_000));

        var events = await CallAsync(Request());

        Assert.IsType<GatewayMessageCompleted>(events[^1]);
        Assert.DoesNotContain(events, e => e is GatewayError);
    }

    [Fact]
    public async Task EmptyReply_BelowTheOutputLimit_IsPassedOn_BecauseTheModelChoseToSayNothing()
    {
        await StartRunAsync(cap: 100_000);
        _host.Provider.Enqueue((_, _) => ScriptedProvider.Events(Completed(new UsageInfo(14_200, 12))));

        var events = await CallAsync(Request());

        Assert.IsType<GatewayMessageCompleted>(Assert.Single(events));
    }

    public static TheoryData<string, MessageCompleted, int?, bool> CutOffCases => new()
    {
        { "empty at the limit", Completed(new UsageInfo(10, 4_000)), 4_000, true },
        { "empty over the limit", Completed(new UsageInfo(10, 4_100)), 4_000, true },
        { "only whitespace at the limit", Completed(new UsageInfo(10, 4_000), new TextBlock("  \n")), 4_000, true },
        { "text at the limit", Completed(new UsageInfo(10, 4_000), new TextBlock("answer")), 4_000, false },
        { "a tool call at the limit", Completed(new UsageInfo(10, 4_000), new ToolUseBlock("c", "submit_review", default)), 4_000, false },
        { "empty below the limit", Completed(new UsageInfo(10, 3_999)), 4_000, false },
        { "no limit was set", Completed(new UsageInfo(10, 4_000)), null, false },
    };

    [Theory]
    [MemberData(nameof(CutOffCases))]
    public void CutOffBeforeReplying_OnlyWhenTheLimitWasReachedAndNothingCameBack(string name, MessageCompleted completed, int? limit, bool expected)
    {
        Assert.True(expected == ModelGateway.CutOffBeforeReplying(completed, limit), name);
    }

    [Fact]
    public void CutOffMessage_LeavesReasoningOut_WhenTheProviderDidNotReportIt()
    {
        Assert.Equal(
            "The model reached the output limit of 32,768 tokens without producing a reply. Nothing was lost; resuming tries the step again.",
            ModelGateway.CutOffMessage(new UsageInfo(10, 32_768), 32_768));
    }

    [Fact]
    public async Task CancelledMidResponse_UsageIsUnknown_AndTheCancellationPropagates()
    {
        await StartRunAsync(cap: 100_000);
        using var cts = new CancellationTokenSource();
        _host.Provider.Enqueue((_, ct) => SlowAsync(cts, ct));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CallAsync(Request(), cts.Token));

        Assert.Equal(UsageStatus.Unknown, (await OnlyEntryAsync()).Status);
        Assert.Equal(20_500, (await ThreadAsync()).TokensReserved);
    }

    private static async IAsyncEnumerable<AgentEvent> SlowAsync(
        CancellationTokenSource cancel, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new TextDelta("partial");
        cancel.Cancel();
        await Task.Delay(Timeout.Infinite, ct);
    }

    [Fact]
    public async Task ToolCalls_AreRelayedToTheWorker()
    {
        await StartRunAsync(cap: 100_000);
        _host.Provider.EnqueueToolCall("read_file", new { path = "a.txt" });

        var events = await CallAsync(Request());

        Assert.IsType<GatewayToolCallStarted>(events[0]);
        Assert.Equal("a.txt", Assert.IsType<GatewayToolCallCompleted>(events[1]).Arguments.GetProperty("path").GetString());
        Assert.IsType<ToolUseBlock>(Assert.IsType<GatewayMessageCompleted>(events[2]).Message.Content[0]);
    }

    [Fact]
    public async Task ToolSchemasAndSystemPrompt_CountTowardsTheFirstEstimate()
    {
        await StartRunAsync(cap: 500_000);
        _host.Provider.EnqueueReply("done");
        var schema = System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" });
        var request = new GatewayRequest("key", new ChatRequest(
            [ChatMessage.User(new string('x', 4_000))], [new ToolSchema(new string('n', 100), new string('d', 3_883), schema)], "m",
            SystemPrompt: new string('s', 8_000), SessionId: "s"));

        await CallAsync(request);

        // 8,000 + (100 + 3,883 + 17) + 4,000 characters → 4,000 tokens.
        Assert.Equal(4_000, (await OnlyEntryAsync()).EstimatedInputRaw);
    }

    [Fact]
    public void HostProviders_AllowOnlyOpenRouter()
    {
        var providers = HostProviders.Create(new FactoryOptions { OpenRouterApiKey = "sk-or-test" });

        Assert.Equal("openrouter", providers.Resolve("openrouter").ProviderName);
        Assert.Throws<InvalidOperationException>(() => providers.Resolve("anthropic"));
    }
}
