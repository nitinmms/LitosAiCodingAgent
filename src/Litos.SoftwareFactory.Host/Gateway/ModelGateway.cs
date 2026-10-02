using System.Collections.Concurrent;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Budget;
using Litos.SoftwareFactory.Core.Estimation;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Runs;

namespace Litos.SoftwareFactory.Host.Gateway;

/// <summary>
/// The model gateway (ReadMe_LitosSoftwareFactory_V1.md §9.2, §9.3): every model call a worker
/// makes — turns and compaction alike — arrives here, because the host holds the provider keys
/// and the worker holds none.
///
/// Each call is admitted before it is sent: its input is estimated — the part the provider's
/// cache is expected to serve counted at the cached weight — and that, with a margin, plus the
/// call's output limit is reserved against the task's allowance. A call whose input and a
/// minimum of output do not fit is refused before anything is spent. An admitted call is sent
/// with the provider's output limit set to what remains, up to the output allowance, so it
/// cannot exceed its reservation; it is then settled against what the provider reports.
/// </summary>
public sealed class ModelGateway(
    IFactoryStore store, IChatProviderFactory providers, FactoryOptions options, FactorySignals signals, IClock clock,
    ILogger<ModelGateway> logger)
{
    /// <summary>One calibration window per provider and model, learned from settled calls (§9.5).</summary>
    private readonly ConcurrentDictionary<string, CalibrationWindow> _calibration = new();

    /// <summary>How long to wait between attempts while the provider is rate-limiting; tests shorten it.</summary>
    public TimeSpan RateLimitBackoff { get; set; } = TimeSpan.FromSeconds(10);

    public int RateLimitAttempts { get; set; } = 6;

    public CalibrationWindow CalibrationFor(string provider, string model) =>
        _calibration.GetOrAdd($"{provider}\n{model}", _ => new CalibrationWindow());

    /// <summary>Handles one gateway request, writing each event the worker should receive.</summary>
    public async Task HandleAsync(ActiveRun run, GatewayRequest request, Func<GatewayEvent, Task> write, CancellationToken ct)
    {
        // The model is the task's, fixed when the thread was created: a worker cannot ask for another.
        var chat = request.ChatRequest with { Model = run.Model };
        var sessionKey = chat.SessionId ?? "";
        var calibration = CalibrationFor(run.Provider, run.Model);
        var baseline = run.Baselines.GetValueOrDefault(sessionKey);
        var estimate = RequestEstimator.Estimate(chat, baseline, calibration.Ratio);
        // Only a request that continues the baseline's conversation repeats its input.
        var expectedCached = estimate.Basis == EstimateBasis.BaselinePlusDelta
            ? baseline!.ExpectedCachedTokens(clock.UtcNow, options.Budget.CacheWindow)
            : 0;

        var reservation = await store.ReserveAsync(
            new ReserveCommand(request.RequestKey, run.ThreadId, run.RunId, run.UserId, run.Provider, run.Model, estimate.RawTokens, estimate.Tokens)
            {
                ExpectedCachedInput = expectedCached,
            },
            options.Budget, clock.UtcNow, ct);
        signals.EventsWritten();

        if (reservation.Decision is Refused refused)
        {
            var reason = refused.Reason == RefusalReason.UserQuota
                ? $"The next model call needs about {refused.Needed:N0} tokens, but your quota has {refused.Remaining:N0} left."
                : $"The next model call needs about {refused.Needed:N0} tokens, but the task's budget has {refused.Remaining:N0} left.";
            run.RefuseForBudget(reason);
            await write(new GatewayError(
                refused.Reason == RefusalReason.UserQuota ? GatewayErrorCodes.QuotaExhausted : GatewayErrorCodes.BudgetExhausted, reason));
            return;
        }

        // A request key the store has seen before is the same call arriving twice. It was
        // reserved once; sending it again would spend twice.
        if (reservation.AlreadyKnown)
        {
            await write(new GatewayError(GatewayErrorCodes.ProviderError, "This model call was already submitted."));
            return;
        }

        var admitted = (Admitted)reservation.Decision;
        chat = chat with { MaxOutputTokens = admitted.MaxOutputTokens };
        var provider = providers.Resolve(run.Provider);

        var call = new CallState();
        try
        {
            await SendAsync(provider, chat, estimate, calibration, request.RequestKey, run, sessionKey, call, write, ct);
        }
        catch (OperationCanceledException)
        {
            await SettleUnfinishedAsync(request.RequestKey, call);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Model call {RequestKey} failed.", request.RequestKey);
            await SettleUnfinishedAsync(request.RequestKey, call);
            await write(new GatewayError(GatewayErrorCodes.ProviderError, ex.Message));
            return;
        }

        await SettleUnfinishedAsync(request.RequestKey, call);
    }

    /// <summary>How far one call got, which decides what happens to its reservation.</summary>
    private sealed class CallState
    {
        /// <summary>The provider produced at least one event: usage may have been incurred.</summary>
        public bool Started { get; set; }

        public bool Settled { get; set; }
    }

    private async Task SendAsync(
        IChatProvider provider, ChatRequest chat, RequestEstimate estimate, CalibrationWindow calibration, string requestKey,
        ActiveRun run, string sessionKey, CallState call, Func<GatewayEvent, Task> write, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await foreach (var evt in provider.StreamAsync(chat, ct))
                {
                    // A provider reports a 429 either by throwing or as an error event before
                    // anything else; both mean nothing was produced, so both are retried.
                    if (evt is ErrorOccurred { Exception: ChatProviderRateLimitedException limited } && !call.Started)
                        throw limited;

                    call.Started = true;
                    if (evt is ErrorOccurred error)
                    {
                        await write(new GatewayError(GatewayErrorCodes.ProviderError, error.Exception.Message));
                        return;
                    }

                    if (evt is MessageCompleted completed)
                    {
                        await SettleAsync(requestKey, completed, estimate, calibration);
                        call.Settled = true;

                        // Charged, but not passed on: to the agent loop an empty reply looks like
                        // a turn that chose to stop, and the run would be blamed for not calling
                        // its completion tool when the model never got to answer.
                        if (CutOffBeforeReplying(completed, chat.MaxOutputTokens))
                        {
                            await write(new GatewayError(GatewayErrorCodes.ProviderError, CutOffMessage(completed.Usage, chat.MaxOutputTokens!.Value)));
                            return;
                        }

                        run.Baselines[sessionKey] = SessionBaseline.From(chat, completed.Usage, clock.UtcNow);
                    }

                    if (GatewayEvent.FromAgentEvent(evt) is { } wire)
                        await write(wire);

                    if (call.Settled)
                        return;
                }

                await write(new GatewayError(GatewayErrorCodes.ProviderError, "The provider's response ended before the message was complete."));
                return;
            }
            catch (ChatProviderRateLimitedException ex) when (!call.Started && attempt < RateLimitAttempts)
            {
                // HTTP 429 is "wait and retry with the reservation kept", not a failed run (§9.3).
                // Heartbeats keep the worker's stream-idle watchdog from treating the wait as a
                // dead connection.
                logger.LogInformation("Rate limited on attempt {Attempt}: {Message}", attempt, ex.Message);
                var waited = TimeSpan.Zero;
                var beat = TimeSpan.FromSeconds(Math.Min(5, Math.Max(0.02, RateLimitBackoff.TotalSeconds)));
                do
                {
                    await write(new GatewayHeartbeat());
                    await Task.Delay(beat, ct);
                    waited += beat;
                }
                while (waited < RateLimitBackoff);
            }
        }
    }

    /// <summary>
    /// True when the model reached the call's output limit having produced no text and no tool
    /// call — in practice, a reasoning model that spent the whole allowance thinking.
    /// </summary>
    internal static bool CutOffBeforeReplying(MessageCompleted completed, int? maxOutputTokens) =>
        maxOutputTokens is { } limit
        && completed.Usage.OutputTokens >= limit
        && !completed.Message.Content.Any(block => block is not Litos.Agent.Messages.TextBlock text || !string.IsNullOrWhiteSpace(text.Text));

    internal static string CutOffMessage(UsageInfo usage, int maxOutputTokens) =>
        $"The model reached the output limit of {maxOutputTokens:N0} tokens without producing a reply"
        + (usage.ReasoningTokens > 0 ? $" ({usage.ReasoningTokens:N0} of them were reasoning)" : "")
        + ". Nothing was lost; resuming tries the step again.";

    private async Task SettleAsync(string requestKey, MessageCompleted completed, RequestEstimate estimate, CalibrationWindow calibration)
    {
        var usage = completed.Usage;
        var charge = BudgetLedger.SettlementCharge(
            usage, estimate.Tokens, RequestEstimator.EstimateOutputTokens(completed.Message), options.Budget.CachedInputWeight);

        // Settled even if the worker has gone away: the call was made and must be charged.
        await store.SettleAsync(requestKey, usage, charge, clock.UtcNow, CancellationToken.None);
        calibration.Record(estimate.RawTokens, usage.TotalInputTokens);
        signals.EventsWritten();

        logger.LogInformation(
            "Model call {RequestKey}: estimated {Estimated} (raw {Raw}, x{Ratio:0.00}, {Basis}), actual input {Actual}, output {Output}, charged {Charged}.",
            requestKey, estimate.Tokens, estimate.RawTokens, estimate.CalibrationRatio, estimate.Basis, usage.TotalInputTokens, usage.OutputTokens, charge);
    }

    /// <summary>
    /// Closes the books on a call that did not settle. If the provider never started responding,
    /// nothing was spent and the reservation is returned. If it had started, the usage is
    /// unknown: the reservation stays charged until reconciled, never silently refunded (§9.2).
    /// </summary>
    private async Task SettleUnfinishedAsync(string requestKey, CallState call)
    {
        if (call.Settled)
            return;

        if (call.Started)
            await store.MarkUsageUnknownAsync(requestKey, CancellationToken.None);
        else
            await store.ReleaseReservationAsync(requestKey, CancellationToken.None);
        signals.EventsWritten();
    }
}
