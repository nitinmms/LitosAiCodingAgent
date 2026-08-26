using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;

namespace Litos.Agent;

public sealed class AgentLoop(
    IChatProvider provider,
    ToolRegistry tools,
    ITranscriptStore store,
    ContextAccountant accountant,
    ISystemPromptProvider? systemPromptProvider = null,
    Compactor? compactor = null,
    TimeSpan? streamIdleTimeout = null)
{
    /// <summary>
    /// How long to wait for the NEXT chunk of a provider's response stream before treating the
    /// connection as stalled. Resets on every chunk received (not an absolute per-request cap),
    /// so a slow-but-actively-streaming response never times out — only total silence does.
    /// Some providers/models can legitimately take tens of seconds before their first token
    /// (e.g. extended-thinking modes), so this must stay generous. Guards only "waiting on the
    /// provider" — tool execution (a slow shell command, a big file read) is unbounded by this
    /// and by design (see InvokeToolSafelyAsync), since those are expected to sometimes run long.
    /// </summary>
    private static readonly TimeSpan DefaultStreamIdleTimeout = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _streamIdleTimeout = streamIdleTimeout ?? DefaultStreamIdleTimeout;


    public IAsyncEnumerable<AgentEvent> RunTurnAsync(
        SessionOwner owner,
        string sessionId,
        Transcript transcript,
        string model,
        string userInput,
        CancellationToken ct,
        ChannelReader<SteeringMessage>? steering = null) =>
        RunTurnAsync(owner, sessionId, transcript, model, [new TextBlock(userInput)], ct, steering);

    /// <summary>
    /// When <paramref name="steering"/> is supplied, the caller (a face) can post
    /// SteeringMessages while this turn is in flight: a Steer message is picked up right
    /// after the currently-executing tool call finishes, and every tool call still pending
    /// in that round is skipped (each gets a synthetic "skipped" ToolResult so every
    /// tool_use the model already emitted still has a matching tool_result — providers
    /// reject a request otherwise) rather than invoked. A FollowUp message is only
    /// delivered once the turn has no more pending tool calls, i.e. appended as the next
    /// round's input instead of ending the turn. See ReadMe_AgentDesign.md §7.2.
    /// </summary>
    public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
        SessionOwner owner,
        string sessionId,
        Transcript transcript,
        string model,
        IReadOnlyList<ContentBlock> userContent,
        [EnumeratorCancellation] CancellationToken ct,
        ChannelReader<SteeringMessage>? steering = null)
    {
        if (transcript.Messages.Count == 0 && transcript.WorkingDirectory is not null)
            await store.AppendAsync(owner, sessionId, TranscriptEntry.SessionHeader(transcript.WorkingDirectory), ct);

        transcript.Append(ChatMessage.User(userContent));
        await store.AppendAsync(owner, sessionId, TranscriptEntry.FromMessage(transcript.Last), ct);

        if (compactor is not null && await compactor.TryCompactAsync(transcript, provider, model, ct))
        {
            yield return new CompactionOccurred(TokensBefore: transcript.Messages[0].Content.OfType<Messages.CompactionSummaryBlock>().First().TokensBefore);
            await store.AppendAsync(owner, sessionId, TranscriptEntry.FromMessage(transcript.Messages[0]), ct);
        }

        var systemPromptSections = systemPromptProvider is null ? null : await systemPromptProvider.BuildAsync(tools, transcript.WorkingDirectory, ct);
        var systemPrompt = systemPromptSections?.Render();

        while (true)
        {
            var request = accountant.BuildRequest(transcript, tools.Schemas, model, systemPrompt);
            var pendingToolCalls = new List<(string CallId, string Name, JsonElement Args)>();

            // Manually drive the enumerator instead of `await foreach` so a provider-thrown
            // exception (network failure, non-2xx status, malformed stream) can be turned into
            // an ErrorOccurred event for the face to render, rather than tearing down the whole
            // turn/process unhandled. `yield return` isn't allowed inside a try that has a catch,
            // hence the MoveNextAsync dance — same broad-catch-but-let-cancellation-through shape
            // as InvokeToolSafelyAsync below.
            //
            // The enumerator is bound to a linked token (not `ct` directly) so that an idle-gap
            // timeout can actually cancel just the stalled MoveNextAsync call — DisposeAsync on
            // an async-iterator that's still suspended mid-MoveNextAsync throws NotSupportedException,
            // so the abandoned call must be properly cancelled and allowed to unwind before the
            // `await using` below can safely dispose the enumerator.
            using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var stream = provider.StreamAsync(request, streamCts.Token).GetAsyncEnumerator(streamCts.Token);
            await using (stream)
            {
                while (true)
                {
                    AgentEvent evt;
                    Exception? streamError = null;
                    try
                    {
                        if (!await MoveNextWithIdleTimeoutAsync(stream, _streamIdleTimeout, streamCts, ct))
                            break;
                        evt = stream.Current;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        streamError = ex;
                        evt = null!;
                    }

                    if (streamError is not null)
                    {
                        yield return new ErrorOccurred(streamError);
                        break;
                    }

                    // MessageCompleted is appended to transcript BEFORE it's yielded (unlike
                    // ToolCallCompleted below, which only needs pendingToolCalls updated for this
                    // method's own next iteration) — a consumer reacting to the yielded event
                    // (e.g. the GUI refreshing its context-usage meter from transcript.LastUsage)
                    // must see transcript already reflecting this message, not the state from
                    // before it. Getting this backwards previously left the meter one message
                    // stale until the *next* turn.
                    if (evt is MessageCompleted m)
                    {
                        transcript.Append(m.Message, m.Usage);
                        await store.AppendAsync(owner, sessionId, TranscriptEntry.FromMessage(m.Message, m.Usage), ct);
                    }

                    yield return evt;
                    switch (evt)
                    {
                        case ToolCallCompleted t:
                            pendingToolCalls.Add((t.CallId, t.ToolName, t.Arguments));
                            break;
                    }
                }
            }

            if (pendingToolCalls.Count == 0)
            {
                if (await TryDeliverFollowUpAsync(owner, sessionId, transcript, steering, ct))
                    continue;
                yield break;
            }

            var steered = false;
            for (var i = 0; i < pendingToolCalls.Count; i++)
            {
                var call = pendingToolCalls[i];
                ToolResult result;
                try
                {
                    result = await InvokeToolSafelyAsync(call.Name, call.Args, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The turn is being aborted (CancelTurn, or a face's own cancel) with this
                    // call (i) and everything after it in pendingToolCalls still unresolved. Every
                    // tool_use the model already emitted needs a matching tool_result before the
                    // transcript is used again — the same invariant the steering skip-path below
                    // maintains — or the next request this session sends is rejected outright by
                    // the provider (a dangling tool_use with no result). Without this, a cancelled
                    // turn left the transcript permanently broken: even a plain "please continue"
                    // afterward would 500, since nothing had ever repaired it.
                    await WriteCancelledToolResultsAsync(owner, sessionId, transcript, pendingToolCalls, i);
                    throw;
                }
                yield return new ToolCallResult(call.CallId, call.Name, result);
                var resultMsg = ChatMessage.ToolResult(call.CallId, result);
                transcript.Append(resultMsg);
                await store.AppendAsync(owner, sessionId, TranscriptEntry.FromMessage(resultMsg), ct);

                if (steering is not null && steering.TryRead(out var steer) && steer.Mode == SteeringMode.Steer)
                {
                    // Framed (not the raw steer.Text) specifically here, where a batch of
                    // still-pending tool calls is about to be skipped out from under the model's
                    // own plan — an unframed message reads as ordinary chat, so a model mid-plan
                    // would often just note it and continue anyway (observed: it kept issuing
                    // more of the same tool calls next round). The framing asks for a brief
                    // acknowledgment before addressing it, purely to close that feedback gap for
                    // the user; it doesn't change how the skip logic below behaves either way.
                    transcript.Append(ChatMessage.User($"[The user interrupted your in-progress work with this message — acknowledge briefly, then address it before continuing]: {steer.Text}"));
                    await store.AppendAsync(owner, sessionId, TranscriptEntry.FromMessage(transcript.Last), ct);

                    // Skip by position (i + 1 onward), not by matching CallId — pendingToolCalls
                    // can contain duplicate CallIds (a misbehaving/fake provider emitting the
                    // same id twice, already tolerated elsewhere in AgentLoop), so matching by
                    // value would stop at the first equal CallId instead of the call just invoked.
                    for (var j = i + 1; j < pendingToolCalls.Count; j++)
                    {
                        var skipped = pendingToolCalls[j];
                        var skipResult = ChatMessage.ToolResult(skipped.CallId, ToolResult.Ok("Skipped due to steering message"));
                        transcript.Append(skipResult);
                        await store.AppendAsync(owner, sessionId, TranscriptEntry.FromMessage(skipResult), ct);
                        yield return new ToolCallSkipped(skipped.CallId, "Skipped due to steering message");
                    }

                    steered = true;
                    break;
                }
            }

            if (steered)
                continue;
        }
    }

    /// <summary>
    /// Writes a synthetic "cancelled" tool_result for pendingToolCalls[fromIndex..] — the call
    /// that was actually in flight when cancellation struck, plus every call after it that was
    /// never even started. Persisted with CancellationToken.None (ct is already cancelled at this
    /// point, so anything gated on it would throw immediately) so this cleanup write survives
    /// the very cancellation it's repairing the transcript for. See the call site's own remarks
    /// on why leaving any of these unresolved corrupts the transcript for every future turn.
    /// </summary>
    private async Task WriteCancelledToolResultsAsync(
        SessionOwner owner, string sessionId, Transcript transcript,
        List<(string CallId, string Name, JsonElement Args)> pendingToolCalls, int fromIndex)
    {
        for (var j = fromIndex; j < pendingToolCalls.Count; j++)
        {
            var cancelled = pendingToolCalls[j];
            var resultMsg = ChatMessage.ToolResult(cancelled.CallId, ToolResult.Error("Cancelled by user."));
            transcript.Append(resultMsg);
            await store.AppendAsync(owner, sessionId, TranscriptEntry.FromMessage(resultMsg), CancellationToken.None);
        }
    }

    /// <summary>
    /// Drains at most one queued FollowUp message once a round produced no pending tool
    /// calls — appended as an ordinary user message so the outer while(true) re-requests
    /// the model, exactly like a fresh /-prompt would. A Steer message left in the channel
    /// at this point (never picked up because there was no tool call to check it after) is
    /// treated the same way: there's nothing left to skip, so steer and follow-up are
    /// equivalent once the turn is between rounds.
    /// </summary>
    private async Task<bool> TryDeliverFollowUpAsync(
        SessionOwner owner, string sessionId, Transcript transcript, ChannelReader<SteeringMessage>? steering, CancellationToken ct)
    {
        if (steering is null || !steering.TryRead(out var message))
            return false;

        transcript.Append(ChatMessage.User(message.Text));
        await store.AppendAsync(owner, sessionId, TranscriptEntry.FromMessage(transcript.Last), ct);
        return true;
    }

    /// <summary>
    /// Races one provider MoveNextAsync call against an idle timer. On timeout, cancels
    /// <paramref name="streamCts"/> (the same source the enumerator itself was created with, see
    /// its call site above) so the stalled call actually unwinds via OperationCanceledException
    /// rather than being abandoned mid-flight — an async-iterator's DisposeAsync throws
    /// NotSupportedException if invoked while a MoveNextAsync on it is still suspended, so the
    /// caller's `await using` can only safely dispose the enumerator after this returns/throws.
    /// The timeout resets every call, since this only wraps one step of the caller's read loop —
    /// an idle-gap timeout for the overall stream, not an absolute per-request cap.
    ///
    /// <paramref name="callerCt"/> — the token the caller (a face) cancels for a user-initiated
    /// abort — is threaded through separately from <paramref name="streamCts"/> so this method
    /// can tell the two apart. streamCts.Token is a linked child of callerCt (see call site), so
    /// a user cancel ALSO cancels streamCts.Token, making Task.Delay(idleTimeout, streamCts.Token)
    /// win the race instantly, same as it would after a genuine <paramref name="idleTimeout"/>
    /// wait — without this check that instant win was previously misreported as a 60s stall
    /// (TimeoutException, surfaced to the user as a red "connection appears stalled" error) even
    /// though zero time had actually elapsed, and — because TimeoutException doesn't match
    /// RunTurnAsync's `catch (OperationCanceledException) when (ct.IsCancellationRequested)`
    /// rethrow — the turn fell through to the generic error path instead of aborting cleanly,
    /// leaving the loop free to keep running further rounds after a user pressed Cancel.
    /// </summary>
    private static async ValueTask<bool> MoveNextWithIdleTimeoutAsync(
        IAsyncEnumerator<AgentEvent> stream, TimeSpan idleTimeout, CancellationTokenSource streamCts, CancellationToken callerCt)
    {
        var moveNextTask = stream.MoveNextAsync().AsTask();
        var delayTask = Task.Delay(idleTimeout, streamCts.Token);

        var completed = await Task.WhenAny(moveNextTask, delayTask);
        if (completed == moveNextTask)
            return await moveNextTask;

        await streamCts.CancelAsync();
        try
        {
            await moveNextTask; // let the stalled call unwind before the caller disposes the enumerator
        }
        catch
        {
            // Whatever the stalled call actually threw once cancelled (typically
            // OperationCanceledException, but a provider could throw something else while
            // tearing down) is superseded by the exception thrown below — this is a stall or a
            // user cancel, not a "the provider errored" outcome from the caller's view.
        }

        if (callerCt.IsCancellationRequested)
            throw new OperationCanceledException(callerCt);

        throw new TimeoutException($"No response from the model for {idleTimeout.TotalSeconds:0}s — the connection appears stalled.");
    }

    /// <summary>
    /// A tool is arbitrary code the model chose to invoke — I/O against files the process
    /// doesn't control the state of (locked by another process, permission-denied, deleted
    /// mid-turn), a hallucinated tool name, or a plain bug in a tool's own logic can all
    /// throw. Without this guard, any such exception propagated out of RunTurnAsync's
    /// IAsyncEnumerable and crashed the entire host process — one bad file lock ended the
    /// whole session instead of producing a result the model (and user) could see and react
    /// to, the same way a "file not found" ToolResult.Error already does today.
    /// Cancellation is intentionally not caught here: OperationCanceledException means the
    /// caller actually wants the turn to stop, not that this specific tool call failed.
    /// </summary>
    private async Task<ToolResult> InvokeToolSafelyAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        try
        {
            var tool = tools.Resolve(toolName);
            return await tool.InvokeAsync(args, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Tool '{toolName}' failed: {ex.Message}");
        }
    }
}
