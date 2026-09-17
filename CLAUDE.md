# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Avalonia (`Litos.Gui`)

- **Prefer `Margin` on a `ScrollViewer`'s direct child over `Padding` on the `ScrollViewer` itself.** In this project's Avalonia version, `ScrollViewer.Padding` is applied visually but not correctly folded into the scrollable extent calculation — content within the padding region at the trailing edge renders but is permanently unreachable by scrolling (observed as roughly the last `Padding` amount of content, e.g. one line of text, left as a clipped sliver no resize or layout-invalidation could recover). See `ReadMe_AgentDesign.md` §7.7 for the full investigation.
  - **Diagnostic tell**: if clipped/unreachable content is a *fixed, roughly-constant amount* regardless of window size or added settle time, suspect this `Padding`-vs-`Margin` issue before chasing `ScrollViewer` extent-staleness or virtualization theories. True extent-staleness bugs (e.g. `AvaloniaUI/Avalonia#3707`/`#4011`/`#3791`) are all-or-nothing — a forced relayout recovers everything or nothing, not a fixed sliver.

## Context/compaction constants are mirrored in TypeScript (`Litos.VsCode`)

- **A change to the compaction or context-meter constants in `src/Litos.Agent/Session/Compaction.cs` must be mirrored by hand in `src/Litos.VsCode/src/liveContextUsage.ts`.** The VS Code extension computes its own running context estimate client-side during a turn (`extension.ts`'s `updateLiveUsageForEvent`), so `liveContextUsage.ts` re-implements `CompactionSettings.ForContextWindow`'s `TriggerAtTokens` and `ContextUsage.Compute`'s banding in TypeScript. Nothing enforces agreement between the two — no codegen, no shared schema, no build-time check. The C# side alone being correct is not enough.
  - **Why it matters**: the composer-row meter and the breakdown popup are computed by *different* code (client-side TS vs. server-side C#), and they reconcile only at turn end when `refreshContextUsage` overwrites the client estimate with the server's figure. Drift is therefore invisible in the popup and visible only mid-turn, in the row below the composer.
  - **Diagnostic tell**: if the composer-row context number and the "Context Usage" popup disagree *while a turn is running* but agree once it finishes, the client-side mirror has drifted — check `liveContextUsage.ts` and `agentEvents.ts`'s `messageCompleted` parsing before suspecting the server.
  - The tests pin specific numbers on both sides (`tests/Litos.Agent.Tests/Session/CompactionPlannerTests.cs` and `src/Litos.VsCode/src/__tests__/liveContextUsage.test.ts`), so a one-sided change fails a test rather than silently rendering a wrong number.

## Token accounting under prompt caching

- **`UsageInfo.InputTokens` is not a measure of context-window occupancy once prompt caching is active — use `TotalInputTokens`.** Providers report `input_tokens`, `cache_creation_input_tokens` and `cache_read_input_tokens` as *mutually exclusive* counts, with `input_tokens` covering only what follows the last cache breakpoint. Anything reasoning about how full the window is (`CompactionPlanner.EstimatedTokensUsed`, `ContextBreakdown`, and the extension's client-side estimate) must sum all three; `InputTokens` alone remains the right figure only for billed, non-cached cost.
  - **Failure mode**: summing `InputTokens + OutputTokens` on a heavily-cached session reports a few hundred tokens for a context holding tens of thousands, so compaction never fires and the meter reads near-zero.
  - **Exception — OpenRouter**: its `prompt_tokens` is normally *inclusive* of the cached counts, but not reliably so across every upstream (Anthropic routes have been observed reporting them as separate additions). `OpenRouterChatProvider` therefore detects which convention a response used rather than assuming one; don't "simplify" that to a plain subtraction.
