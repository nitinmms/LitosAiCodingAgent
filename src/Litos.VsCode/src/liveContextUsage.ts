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
 * Mirrors Litos.Agent.Session.ContextUsage.Compute's fraction/level formula (see ContextUsage.cs)
 * so extension.ts's client-side running estimate renders with the same Warning/Critical banding
 * the real server computation would — reserveTokens uses the same contextWindowTokens/4 cap
 * CompactionSettings.ForContextWindow applies, not the raw 16_000 default, for the same reason:
 * a small local-model window must not push reserveThreshold negative and report Critical from the
 * very first token.
 */
export function computeLiveUsage(usedTokens: number, contextLength: number, isStale: boolean): ContextUsage {
    const reserveTokens = Math.min(16_000, Math.floor(contextLength / 4));
    const reserveThreshold = contextLength - reserveTokens;
    const level: ContextUsage["level"] =
        usedTokens >= reserveThreshold ? "Critical" : usedTokens >= reserveThreshold * 0.6 ? "Warning" : "Normal";
    const fraction = contextLength <= 0 ? 0 : Math.min(1, Math.max(0, usedTokens / contextLength));
    return { usedTokens, contextLength, fraction, level, isStale };
}
