using System.Text.Json;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.SoftwareFactory.Core.Estimation;

namespace Litos.SoftwareFactory.Core.Tests.Estimation;

public class RequestEstimatorTests
{
    private static ChatRequest Request(IReadOnlyList<ChatMessage> messages, string? systemPrompt = null, params ToolSchema[] tools) =>
        new(messages, tools, "model", systemPrompt);

    private static ChatMessage UserText(int chars) => ChatMessage.User(new string('x', chars));

    private static ToolSchema Tool(string name, string description, string schemaJson) =>
        new(name, description, JsonDocument.Parse(schemaJson).RootElement.Clone());

    // ---- Layer 3: first call of a session ----

    [Fact]
    public void FirstCall_EstimatesSystemPromptToolsAndMessages_AtFourCharsPerToken()
    {
        var schema = """{"type":"object"}""";                              // 17 chars
        var request = Request(
            [UserText(400)],
            systemPrompt: new string('s', 800),
            Tool("read_file", new string('d', 174), schema));              // 9 + 174 + 17 = 200

        var estimate = RequestEstimator.Estimate(request, baseline: null);

        Assert.Equal(EstimateBasis.FromScratch, estimate.Basis);
        Assert.Equal((800 + 200 + 400) / 4, estimate.RawTokens);
        Assert.Equal(estimate.RawTokens, estimate.Tokens);
    }

    [Fact]
    public void FirstCall_NoSystemPromptOrTools_CountsOnlyMessages()
    {
        var estimate = RequestEstimator.Estimate(Request([UserText(40)]), baseline: null);

        Assert.Equal(10, estimate.RawTokens);
    }

    [Fact]
    public void Tokens_RoundUp()
    {
        var estimate = RequestEstimator.Estimate(Request([UserText(41)]), baseline: null);

        Assert.Equal(11, estimate.RawTokens);
    }

    [Fact]
    public void EmptyRequest_IsZero()
    {
        Assert.Equal(0, RequestEstimator.Estimate(Request([]), baseline: null).Tokens);
    }

    [Fact]
    public void Messages_AreCountedByCompactionsRules_IncludingToolCallsResultsAndImages()
    {
        var arguments = JsonDocument.Parse("""{"path":"a.txt"}""").RootElement.Clone(); // 16 chars
        var request = Request(
        [
            ChatMessage.Assistant([new ToolUseBlock("c1", "read_file", arguments)]),      // 9 + 16 = 25
            ChatMessage.ToolResult("c1", ToolResult.Ok(new string('r', 75))),             // 75
            ChatMessage.User([new ImageBlock("image/png", new byte[10])]),                // fixed 4,800
        ]);

        var estimate = RequestEstimator.Estimate(request, baseline: null);

        Assert.Equal((25 + 75 + 4_800) / 4, estimate.RawTokens);
    }

    // ---- Output, for a provider that reports none ----

    [Fact]
    public void EstimateOutputTokens_CountsTheReplysTextAndToolCalls_RoundedUp()
    {
        var arguments = JsonDocument.Parse("""{"path":"a.txt"}""").RootElement.Clone(); // 16 chars
        var reply = ChatMessage.Assistant([new TextBlock(new string('t', 100)), new ToolUseBlock("c1", "read_file", arguments)]);

        // 100 + (9 + 16) = 125 characters → 32 tokens.
        Assert.Equal(32, RequestEstimator.EstimateOutputTokens(reply));
    }

    [Fact]
    public void EstimateOutputTokens_EmptyReply_IsZero()
    {
        Assert.Equal(0, RequestEstimator.EstimateOutputTokens(ChatMessage.Assistant([])));
    }

    // ---- Layers 1 and 2: session baseline plus delta ----

    [Fact]
    public void WithBaseline_StartsFromTheLastSettledInput_AndAddsOnlyWhatWasAppended()
    {
        // The last settled request had 2 messages and cost 30,000 input tokens (which already
        // includes the system prompt and tools). Two messages were appended since.
        var request = Request(
            [UserText(100_000), UserText(100_000), UserText(400), UserText(800)],
            systemPrompt: new string('s', 50_000));

        var estimate = RequestEstimator.Estimate(request, new SessionBaseline(MessageCount: 2, TotalInputTokens: 30_000));

        Assert.Equal(EstimateBasis.BaselinePlusDelta, estimate.Basis);
        Assert.Equal(30_000 + (400 + 800) / 4, estimate.RawTokens);
    }

    [Fact]
    public void WithBaseline_NothingAppended_IsTheBaselineItself()
    {
        var request = Request([UserText(400)]);

        var estimate = RequestEstimator.Estimate(request, new SessionBaseline(1, 12_345));

        Assert.Equal(12_345, estimate.RawTokens);
    }

    /// <summary>The baseline must be the provider's whole input — cached portions included —
    /// or a heavily cached session would be estimated at a few hundred tokens.</summary>
    [Fact]
    public void SessionBaseline_From_UsesTotalInputTokens_NotJustTheUncachedRemainder()
    {
        var request = Request([UserText(10), UserText(10), UserText(10)]);
        var usage = new UsageInfo(InputTokens: 200, OutputTokens: 900, CacheCreationInputTokens: 3_000, CacheReadInputTokens: 40_000);

        var baseline = SessionBaseline.From(request, usage);

        Assert.Equal((3, 43_200), (baseline.MessageCount, baseline.TotalInputTokens));
        Assert.Equal(40_000, baseline.CacheReadTokens);
        Assert.Null(baseline.SettledAt);
    }

    // ---- What the next call can expect from the provider's cache ----

    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(4);

    private static SessionBaseline Baseline(int cacheRead, DateTimeOffset? settledAt) =>
        new(3, 23_000) { CacheReadTokens = cacheRead, SettledAt = settledAt };

    [Fact]
    public void ExpectedCachedTokens_OnceTheCacheHasBeenHit_IsTheWholePreviousInput()
    {
        Assert.Equal(23_000, Baseline(22_000, Now.AddSeconds(-20)).ExpectedCachedTokens(Now, Window));
        Assert.Equal(23_000, Baseline(1, Now).ExpectedCachedTokens(Now, Window));
        Assert.Equal(23_000, Baseline(22_000, Now - Window).ExpectedCachedTokens(Now, Window));
    }

    /// <summary>Anything that makes a cache hit doubtful is reserved in full: better to hold
    /// too much than to let a call spend more than was reserved for it.</summary>
    [Fact]
    public void ExpectedCachedTokens_IsNothing_WhenAHitIsNotAssured()
    {
        // The session has never been served from the cache.
        Assert.Equal(0, Baseline(0, Now.AddSeconds(-20)).ExpectedCachedTokens(Now, Window));
        // The last call was too long ago: the provider will have dropped it.
        Assert.Equal(0, Baseline(22_000, Now - Window - TimeSpan.FromSeconds(1)).ExpectedCachedTokens(Now, Window));
        // When it settled was not recorded.
        Assert.Equal(0, Baseline(22_000, null).ExpectedCachedTokens(Now, Window));
        // A clock that went backwards.
        Assert.Equal(0, Baseline(22_000, Now.AddMinutes(1)).ExpectedCachedTokens(Now, Window));
    }

    [Fact]
    public void SessionBaseline_From_RecordsWhenItSettled()
    {
        var baseline = SessionBaseline.From(Request([UserText(10)]), new UsageInfo(700, 100, 0, 22_000), Now);

        Assert.Equal(Now, baseline.SettledAt);
        Assert.Equal(22_700, baseline.ExpectedCachedTokens(Now.AddMinutes(1), Window));
    }

    [Fact]
    public void RequestShorterThanTheBaseline_WasCompacted_SoItIsEstimatedFromScratch()
    {
        var request = Request([UserText(400)], systemPrompt: new string('s', 400));

        var estimate = RequestEstimator.Estimate(request, new SessionBaseline(MessageCount: 40, TotalInputTokens: 150_000));

        Assert.Equal(EstimateBasis.FromScratch, estimate.Basis);
        Assert.Equal(200, estimate.RawTokens);
    }

    // ---- Calibration ----

    [Fact]
    public void CalibrationRatio_ScalesTheEstimate_RoundingUp()
    {
        var estimate = RequestEstimator.Estimate(Request([UserText(404)]), null, calibrationRatio: 1.25);

        Assert.Equal(101, estimate.RawTokens);
        Assert.Equal(127, estimate.Tokens); // 126.25 → 127
        Assert.Equal(1.25, estimate.CalibrationRatio);
    }

    [Fact]
    public void CalibrationRatio_BelowOne_IsFlooredAtOne()
    {
        var estimate = RequestEstimator.Estimate(Request([UserText(400)]), null, calibrationRatio: 0.5);

        Assert.Equal(100, estimate.Tokens);
        Assert.Equal(1.0, estimate.CalibrationRatio);
    }

    [Fact]
    public void CalibrationRatio_AppliesToBaselineEstimatesToo()
    {
        var estimate = RequestEstimator.Estimate(Request([UserText(400)]), new SessionBaseline(1, 10_000), calibrationRatio: 1.1);

        Assert.Equal(11_000, estimate.Tokens);
    }
}

public class CalibrationWindowTests
{
    [Fact]
    public void Ratio_NothingRecorded_IsOne()
    {
        Assert.Equal(1.0, new CalibrationWindow().Ratio);
    }

    [Fact]
    public void Ratio_IsTheMeanOfActualOverEstimated()
    {
        var window = new CalibrationWindow();
        window.Record(rawEstimateTokens: 1_000, actualInputTokens: 1_200); // 1.2
        window.Record(rawEstimateTokens: 1_000, actualInputTokens: 1_400); // 1.4

        Assert.Equal(1.3, window.Ratio, precision: 10);
    }

    [Fact]
    public void Ratio_NeverDropsBelowOne_EvenWhenEstimatesRunHigh()
    {
        var window = new CalibrationWindow();
        window.Record(1_000, 600);
        window.Record(1_000, 700);

        Assert.Equal(1.0, window.Ratio);
    }

    [Fact]
    public void Ratio_OverEstimatesStillPullAMixedMeanDown_ButNotBelowOne()
    {
        var window = new CalibrationWindow();
        window.Record(1_000, 1_500); // 1.5
        window.Record(1_000, 900);   // 0.9

        Assert.Equal(1.2, window.Ratio, precision: 10);
    }

    [Fact]
    public void Window_KeepsOnlyTheMostRecentCalls()
    {
        var window = new CalibrationWindow(capacity: 3);
        window.Record(1_000, 5_000); // will fall out
        window.Record(1_000, 1_100);
        window.Record(1_000, 1_100);
        window.Record(1_000, 1_100);

        Assert.Equal(3, window.Count);
        Assert.Equal(1.1, window.Ratio, precision: 10);
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(-1, 500)]
    [InlineData(500, 0)]   // a provider that reported no input says nothing about the estimate
    public void Record_NothingToCompare_IsIgnored(long estimate, long actual)
    {
        var window = new CalibrationWindow();

        window.Record(estimate, actual);

        Assert.Equal(0, window.Count);
        Assert.Equal(1.0, window.Ratio);
    }

    [Fact]
    public void Capacity_MustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CalibrationWindow(0));
    }

    [Fact]
    public void DefaultCapacity_IsTwentyCalls()
    {
        Assert.Equal(20, new CalibrationWindow().Capacity);
    }

    [Fact]
    public void LearnedRatio_MakesTheNextEstimateCoverWhatTheProviderCharged()
    {
        // This model's tokenizer runs 30% heavier than chars/4.
        var window = new CalibrationWindow();
        for (var i = 0; i < 5; i++)
            window.Record(10_000, 13_000);
        var request = new ChatRequest([ChatMessage.User(new string('x', 40_000))], [], "model");

        var estimate = RequestEstimator.Estimate(request, null, window.Ratio);

        Assert.Equal(10_000, estimate.RawTokens);
        Assert.Equal(13_000, estimate.Tokens);
    }

    [Fact]
    public async Task Record_FromManyThreads_LosesNothing()
    {
        var window = new CalibrationWindow(capacity: 10_000);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
                window.Record(1_000, 1_100);
        })));

        Assert.Equal(4_000, window.Count);
    }
}

public class EstimatorAccuracyTests
{
    [Fact]
    public void UnderEstimate_IsTheShortfallAsAFractionOfTheEstimate()
    {
        Assert.Equal(0.08, new EstimateSample(10_000, 10_800).UnderEstimate, precision: 10);
        Assert.Equal(-0.10, new EstimateSample(10_000, 9_000).UnderEstimate, precision: 10);
        Assert.Equal(0, new EstimateSample(0, 500).UnderEstimate);
    }

    [Fact]
    public void UnderEstimateP95_NoSamples_IsZero()
    {
        Assert.Equal(0, EstimatorAccuracy.UnderEstimateP95([]));
    }

    [Fact]
    public void UnderEstimateP95_IsTheNearestRankPercentile()
    {
        // 100 calls: 95 cost exactly the estimate, 5 cost 30% more. The 95th-ranked call is one
        // of the exact ones.
        List<EstimateSample> samples =
        [
            .. Enumerable.Repeat(new EstimateSample(1_000, 1_000), 95),
            .. Enumerable.Repeat(new EstimateSample(1_000, 1_300), 5),
        ];

        Assert.Equal(0, EstimatorAccuracy.UnderEstimateP95(samples));
    }

    [Fact]
    public void UnderEstimateP95_SixPercentOfCallsOver_ShowsUp()
    {
        List<EstimateSample> samples =
        [
            .. Enumerable.Repeat(new EstimateSample(1_000, 1_000), 94),
            .. Enumerable.Repeat(new EstimateSample(1_000, 1_300), 6),
        ];

        Assert.Equal(0.3, EstimatorAccuracy.UnderEstimateP95(samples), precision: 10);
    }

    [Fact]
    public void UnderEstimateP95_AllOverEstimates_IsZero_NotNegative()
    {
        List<EstimateSample> samples = [.. Enumerable.Repeat(new EstimateSample(1_000, 800), 20)];

        Assert.Equal(0, EstimatorAccuracy.UnderEstimateP95(samples));
    }

    [Fact]
    public void UnderEstimateP95_SingleSample_IsThatSample()
    {
        Assert.Equal(0.05, EstimatorAccuracy.UnderEstimateP95([new EstimateSample(1_000, 1_050)]), precision: 10);
    }

    /// <summary>The M1 exit criterion: the 95th-percentile under-estimate stays within the margin.</summary>
    [Fact]
    public void IsWithinMargin_ComparesP95WithTheReservationMargin()
    {
        List<EstimateSample> within = [.. Enumerable.Repeat(new EstimateSample(1_000, 1_080), 20)];
        List<EstimateSample> outside = [.. Enumerable.Repeat(new EstimateSample(1_000, 1_150), 20)];

        Assert.True(EstimatorAccuracy.IsWithinMargin(within, 0.10));
        Assert.False(EstimatorAccuracy.IsWithinMargin(outside, 0.10));
    }
}
