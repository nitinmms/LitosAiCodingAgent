import { describe, it, expect } from "vitest";
import { extractMentionPaths, expandMentionCandidates } from "../mentionParser";

describe("extractMentionPaths", () => {
    it("extracts a single simple mention", () => {
        expect(extractMentionPaths("please look at @src/Foo.ts")).toEqual(["src/Foo.ts"]);
    });

    it("extracts multiple mentions in one message — the first capture greedily includes the words up to the next @", () => {
        expect(extractMentionPaths("compare @a.ts and @b.ts")).toEqual(["a.ts and", "b.ts"]);
    });

    it("does not trigger on an email-like foo@bar — the @ is preceded by a word character", () => {
        expect(extractMentionPaths("contact me at foo@bar.com")).toEqual([]);
    });

    it("returns an empty array when there's no mention at all", () => {
        expect(extractMentionPaths("just a normal message")).toEqual([]);
    });

    it("captures the whole greedy run including spaces up to end of string", () => {
        expect(extractMentionPaths("@Plant Resource.png")).toEqual(["Plant Resource.png"]);
    });

    it("a second @ immediately after a word character (no space) is not a new mention start", () => {
        // Matches the C# source regex's (?<![\w@])@ lookbehind exactly: the capture group can
        // never itself contain "@" ([^@]*? excludes it), so "@a.ts@b.ts" captures only "a.ts"
        // (stopping at the lookahead's "@"); the second "@" is preceded by "s" (a word char), so
        // it's rejected as a mention start too — "b.ts" is dropped entirely, not a separate
        // mention. Same reason "foo@bar.com" above doesn't match at all: an "@" needs whitespace
        // (or start-of-string) immediately before it to count.
        expect(extractMentionPaths("@a.ts@b.ts")).toEqual(["a.ts"]);
    });

    it("two mentions separated by whitespace are both extracted as separate raw captures", () => {
        // Each capture is greedy up to the next "@" or end of string — the first one legitimately
        // swallows "and " as part of its raw text (expandMentionCandidates is what later shrinks
        // "a.ts and" down to just "a.ts" when resolving against the filesystem, see its own tests
        // below), so these two captures are NOT duplicates of each other even though a human
        // reading the sentence sees the same file mentioned twice.
        expect(extractMentionPaths("@a.ts and @a.ts")).toEqual(["a.ts and", "a.ts"]);
    });

    it("trims trailing whitespace from a mention at end of string", () => {
        expect(extractMentionPaths("@a.ts   ")).toEqual(["a.ts"]);
    });
});

describe("expandMentionCandidates", () => {
    it("yields only one candidate for a single-word mention", () => {
        expect(expandMentionCandidates("src/Foo.ts")).toEqual(["src/Foo.ts"]);
    });

    it("yields shrinking prefixes, longest first, for a multi-word raw capture", () => {
        expect(expandMentionCandidates("Plant Resource.png please describe it")).toEqual([
            "Plant Resource.png please describe it",
            "Plant Resource.png please describe",
            "Plant Resource.png please",
            "Plant Resource.png",
            "Plant",
        ]);
    });

    it("trims trailing sentence punctuation from each candidate", () => {
        expect(expandMentionCandidates("src/Foo.ts.")).toEqual(["src/Foo.ts"]);
    });

    it("trims a trailing question mark from the raw capture extractMentionPaths would produce", () => {
        // extractMentionPaths("what is @src/Foo.ts?") captures "src/Foo.ts?" (the "?" isn't a
        // mention-terminating character on its own) — expandMentionCandidates strips it here.
        expect(expandMentionCandidates("src/Foo.ts?")).toEqual(["src/Foo.ts"]);
    });
});
