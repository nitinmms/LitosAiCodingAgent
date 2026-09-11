# Known Issue: Silent, Repeating 500 Errors From Context Overflow

**Status:** Root-caused, not yet fixed.
**First observed:** 2026-09-01, session `8aff23388ca2b1d666c8e9c06cae9df9` (VS Code local session store, `~/.litos/sessions/local/`).

## Symptom

A long-running session starts returning HTTP 500 errors mid-conversation, with no
visible reason. Reloading the session and prompting it again reproduces the same
500 every time — the session never recovers on its own.

## Root cause

1. Long sessions accumulate a large conversation transcript (many turns, large
   generated documents, large tool results such as file reads).
2. Litos decides whether to compact (summarize and trim) old messages using an
   **estimate**, not an exact token count:
   `CompactionPlanner.EstimateChars` (`src/Litos.Agent/Session/Compaction.cs`)
   approximates tokens as `characters / 4` for each message, and
   `ShouldCompact` only fires once this estimate crosses
   `ContextWindowTokens - ReserveTokens` (default `200,000 - 16,000`).
3. Because this is an approximation, it can under-count the real payload size.
   The session keeps growing without compaction firing, until a single
   request's *actual* size exceeds what the model/provider will accept.
4. The provider (OpenAI, via `src/Litos.Providers.OpenAI/OpenAiChatProvider.cs`,
   `CreateResponseAsync`) rejects the oversized request. This throws out of
   `StreamAsync` with no special handling for this case.
5. `AgentLoop.RunTurnAsync` (`src/Litos.Agent/AgentLoop.cs`) catches *any*
   exception from the provider stream generically:
   ```csharp
   catch (Exception ex)
   {
       streamError = ex;
       evt = null!;
   }
   ...
   yield return new ErrorOccurred(streamError);
   break;
   ```
   This `ErrorOccurred` event is only sent to the live UI. **It is never
   appended to the transcript store and never written to any log file.**
   The failure leaves no trace on disk.

## Why it repeats identically on every reload

Because the error is never persisted, reloading the session restores the exact
same (already-too-large) transcript that caused the failure. Prompting it again
resends that same oversized history to the provider, which rejects it the same
way, every time. There is no automatic recovery path — the session is stuck
until it is abandoned or manually repaired.

## How this was diagnosed

- The session `.jsonl` transcript (`~/.litos/sessions/local/8aff23388...jsonl`)
  shows the exact failure point: after a `read_file` tool call at turn 62-63,
  the assistant never responds. Two subsequent user messages ("Continue
  please", "Please continue") also go unanswered — consistent with every
  retry hitting the same oversized-request rejection.
- All tool_use/tool_result call IDs in the session are fully paired (no
  dangling tool calls), which rules out the *other* known transcript-corruption
  failure mode this codebase already guards against (see
  `AgentLoop.cs` comments around `WriteCancelledToolResultsAsync` and
  `CompactionPlanner.IsValidCutPoint` / `SnapToSafeBranchPoint`).
- No application log (VS Code `agenthost.log`, `~/.litos/debug.log`, or any
  extension output channel) contains the actual exception — confirming
  `ErrorOccurred` truly isn't logged anywhere today.

## Suggested fixes

1. **Log `ErrorOccurred`.** At minimum, write the exception (type, message,
   stack trace) to a file when `AgentLoop` yields `ErrorOccurred`, so future
   occurrences are diagnosable without manual transcript archaeology.
2. **Persist a marker in the transcript** when a turn fails, so reloading the
   session surfaces "this turn previously failed" instead of silently
   resending the same request.
3. **Replace the `chars / 4` estimate with an exact tokenizer count** (or a
   tighter, provider-aware estimate) in `CompactionPlanner.EstimateChars` /
   `EstimatedTokensUsed`, so compaction reliably fires before a request
   actually exceeds the provider's limit.
4. **Add a pre-flight size check** in `OpenAiChatProvider.StreamAsync` (or a
   shared layer above all providers) that rejects/compacts before calling
   `CreateResponseAsync`, rather than relying solely on the proactive
   compaction threshold.
5. Consider mapping known provider rejection reasons (context length
   exceeded, payload too large) to a specific, user-visible error message
   instead of a generic 500/`ErrorOccurred`, so the user knows to compact or
   start a new session rather than retrying indefinitely.

## Workaround (until fixed)

Do not reload and re-prompt a session that is 500ing — it will keep failing
identically. Start a new session instead, or use `/compact` (if available) on
the affected session *before* it becomes unrecoverable, once early warning
signs (very large generated documents, many turns, large file reads) are
visible.
