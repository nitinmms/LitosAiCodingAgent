import { describe, it, expect } from "vitest";
import { computeLiveUsage, estimateTokens } from "../liveContextUsage";

// Mirrors Litos.Agent.Session.ContextUsage.Compute's formula (see ContextUsage.cs) — these tests
// pin the same thresholds its own ContextUsageTests.cs exercises server-side, so this client-side
// approximation can't silently drift from what the real turn-end reconciliation would report.
describe("computeLiveUsage", () => {
    it("reports Normal well under the trigger threshold", () => {
        const usage = computeLiveUsage(1_000, 200_000, false);
        expect(usage.level).toBe("Normal");
        expect(usage.contextLength).toBe(200_000);
        expect(usage.usedTokens).toBe(1_000);
    });

    it("reports Warning at 60% of the trigger threshold", () => {
        // contextLength=200_000 -> trigger = 0.65 * 200_000 = 130_000; 60% = 78_000.
        const usage = computeLiveUsage(78_000, 200_000, false);
        expect(usage.level).toBe("Warning");
    });

    it("reports Critical at or above the trigger threshold", () => {
        expect(computeLiveUsage(130_000, 200_000, false).level).toBe("Critical");
        // One under the trigger is Warning, not Normal — it is still well past 60% of it.
        expect(computeLiveUsage(129_999, 200_000, false).level).toBe("Warning");
    });

    it("bands a 1M window against the 250_000 ceiling, not the window's capacity", () => {
        // Regression test for client/server drift: the old capacity-based threshold (contextLength
        // - reserve = 984_000) left this row reading Normal all the way to 984_000 while the server
        // fired compaction at 250_000. Critical must mean "compaction is about to fire".
        expect(computeLiveUsage(250_000, 1_048_576, false).level).toBe("Critical");
        expect(computeLiveUsage(249_999, 1_048_576, false).level).toBe("Warning");
        expect(computeLiveUsage(900_000, 1_048_576, false).level).toBe("Critical");
    });

    it("keeps the capacity-only trigger for small local-model windows", () => {
        // At or below 64_000 the proportional bound does not apply: contextLength=8_000 ->
        // reserveTokens=min(16_000, 2_000)=2_000 -> trigger=6_000 (75%), matching what these
        // windows had before the proportional trigger existed.
        expect(computeLiveUsage(2_096, 8_000, false).level).toBe("Normal");
        expect(computeLiveUsage(6_000, 8_000, false).level).toBe("Critical");
        // 64_000 is the boundary: still capacity-only -> 64_000 - 16_000 = 48_000.
        expect(computeLiveUsage(48_000, 64_000, false).level).toBe("Critical");
        expect(computeLiveUsage(47_999, 64_000, false).level).toBe("Warning");
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
