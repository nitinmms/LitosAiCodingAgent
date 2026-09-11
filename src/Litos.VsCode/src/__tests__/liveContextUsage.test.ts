import { describe, it, expect } from "vitest";
import { computeLiveUsage, estimateTokens } from "../liveContextUsage";

// Mirrors Litos.Agent.Session.ContextUsage.Compute's formula (see ContextUsage.cs) — these tests
// pin the same thresholds its own ContextUsageTests.cs exercises server-side, so this client-side
// approximation can't silently drift from what the real turn-end reconciliation would report.
describe("computeLiveUsage", () => {
    it("reports Normal well under the reserve threshold", () => {
        const usage = computeLiveUsage(1_000, 200_000, false);
        expect(usage.level).toBe("Normal");
        expect(usage.contextLength).toBe(200_000);
        expect(usage.usedTokens).toBe(1_000);
    });

    it("reports Warning at 60% of the reserve threshold", () => {
        // contextLength=200_000, reserveTokens=16_000 -> reserveThreshold=184_000, 60% = 110_400.
        const usage = computeLiveUsage(110_400, 200_000, false);
        expect(usage.level).toBe("Warning");
    });

    it("reports Critical at or above the reserve threshold", () => {
        const usage = computeLiveUsage(184_000, 200_000, false);
        expect(usage.level).toBe("Critical");
    });

    it("caps reserveTokens at contextLength/4 for a small local-model window instead of going negative", () => {
        // contextLength=8_000 -> reserveTokens=min(16_000, 2_000)=2_000 -> reserveThreshold=6_000.
        // Without the cap, reserveThreshold would be 8_000-16_000 = -8_000, reporting Critical
        // from the very first token — this is the same regression ContextUsageTests.cs's
        // Compute_SmallContextLength_ScalesReserveThreshold_InsteadOfReportingCriticalImmediately
        // guards against server-side.
        const usage = computeLiveUsage(2_096, 8_000, false);
        expect(usage.level).toBe("Normal");
    });

    it("clamps fraction to [0, 1] even when usedTokens exceeds contextLength", () => {
        const usage = computeLiveUsage(250_000, 200_000, false);
        expect(usage.fraction).toBe(1);
        // usedTokens itself is not clamped — matches ContextUsageSnapshot's own contract.
        expect(usage.usedTokens).toBe(250_000);
    });

    it("carries isStale through unchanged", () => {
        expect(computeLiveUsage(1_000, 200_000, true).isStale).toBe(true);
        expect(computeLiveUsage(1_000, 200_000, false).isStale).toBe(false);
    });
});

describe("estimateTokens", () => {
    it("approximates chars/4, rounded up", () => {
        expect(estimateTokens("abcd")).toBe(1);
        expect(estimateTokens("abcde")).toBe(2);
        expect(estimateTokens("")).toBe(0);
    });
});
