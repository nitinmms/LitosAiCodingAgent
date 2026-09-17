import { ContextUsage } from "./agentEvents";

/**
 * Approximates Litos.Agent.Session.CompactionPlanner.EstimateChars' per-block-type char count
 * (see Compaction.cs) for the one wire shape this ever needs to estimate — a tool result's text —
 * divided by 4 to match EstimatedTokensUsed's own chars/4 approximation. Deliberately not a
 * byte-for-byte port: resultText is already the client-parsed string (see agentEvents.ts's
 * parseAgentEvent toolCallResult case), not the original ToolResultBlock, so this is an estimate
 * of an estimate — acceptable because extension.ts's refreshContextUsage always reconciles
 * against the real server-computed value at turn-end (and on-demand at panel open), the same
 * self-correcting pattern CompactionPlanner's own estimate already relies on relative to real
 * provider usage.
 */
export function estimateTokens(text: string): number {
    return Math.ceil(text.length / 4);
}

/**
 * Mirrors Litos.Agent.Session.CompactionSettings.ForContextWindow's TriggerAtTokens (see
 * Compaction.cs) — the point at which the server actually fires compaction, which is what the
 * meter's Critical band means. Kept in lockstep with those constants: 0.65 of the window, capped
 * at 250_000 absolute, and never above the window's own capacity.
 *
 * Windows at or below SMALL_WINDOW_THRESHOLD keep the capacity-only trigger (75% of the window via
 * the window/4 reserve cap) rather than the proportional bound, because compacting a small local
 * model early costs more than it saves — see ForContextWindow's own remarks.
 */
const TRIGGER_FRACTION_OF_WINDOW = 0.65;
const MAX_TRIGGER_TOKENS = 250_000;
const SMALL_WINDOW_THRESHOLD_TOKENS = 64_000;

function triggerAtTokens(contextLength: number): number {
    const reserveTokens = Math.min(16_000, Math.floor(contextLength / 4));
    const capacityBound = contextLength - reserveTokens;
    if (contextLength <= SMALL_WINDOW_THRESHOLD_TOKENS) return capacityBound;
    const proportionalBound = Math.min(Math.floor(contextLength * TRIGGER_FRACTION_OF_WINDOW), MAX_TRIGGER_TOKENS);
    return Math.min(capacityBound, proportionalBound);
}

/**
 * Mirrors Litos.Agent.Session.ContextUsage.Compute's fraction/level formula (see ContextUsage.cs)
 * so extension.ts's client-side running estimate renders with the same Warning/Critical banding
 * the real server computation would. Bands are measured against the compaction trigger, not the
 * window's capacity: since the trigger became proportional, Critical means "compaction is about to
 * fire", which on a large window is well below full (250_000 of a 1M window) — the meter tracks
 * the decision, not the cliff. Keeping the old capacity-based threshold here would have shown
 * Normal all the way to 984_000 on a 1M model while the server compacted at 250_000.
 */
export function computeLiveUsage(usedTokens: number, contextLength: number, isStale: boolean): ContextUsage {
    const triggerThreshold = triggerAtTokens(contextLength);
    const level: ContextUsage["level"] =
        usedTokens >= triggerThreshold ? "Critical" : usedTokens >= triggerThreshold * 0.6 ? "Warning" : "Normal";
    const fraction = contextLength <= 0 ? 0 : Math.min(1, Math.max(0, usedTokens / contextLength));
    return { usedTokens, contextLength, fraction, level, isStale };
}
