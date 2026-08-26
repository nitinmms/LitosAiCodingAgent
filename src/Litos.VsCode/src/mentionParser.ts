/**
 * TypeScript port of Litos.Gui/Litos.Console's MentionParser.cs — extracts "@path" mentions from
 * submitted message text at send time, so extension.ts can resolve each to a file under the
 * session's working directory and fold it into the outgoing turn via the same attachFromPath
 * pipeline /attach already uses. Mention text itself is left untouched in the sent message (it
 * reads naturally as part of the sentence, e.g. "explain @src/Foo.ts") — this only extracts
 * candidates for the attachment side effect.
 */

// Matches @ followed by everything up to the next @ or end of line, including spaces — a filename
// with a space is a valid mention, but so is "@foo.ts please describe it", where only "foo.ts" is
// really a path and the rest is the user's sentence. A regex alone can't know which words are part
// of a real file, so this captures the whole greedy run; expandCandidates (below) turns it into an
// ordered list of shrinking prefixes for the caller to test, longest (most likely intended) first.
const MENTION_PATTERN = /(?<![\w@])@([A-Za-z0-9_.\-\\/:~][^@]*?)(?=@|$)/g;

export function extractMentionPaths(input: string): string[] {
    const seen = new Set<string>();
    for (const match of input.matchAll(MENTION_PATTERN)) {
        const raw = match[1].trimEnd();
        if (raw.length > 0) seen.add(raw);
    }
    return [...seen];
}

/**
 * A raw mention capture ("foo.ts please describe it") may include trailing words that are part of
 * the sentence, not the filename. Returns candidates from longest to shortest, trimmed of trailing
 * sentence punctuation, so the caller can test each against the filesystem and stop at the first
 * one that actually exists.
 */
export function expandMentionCandidates(rawMention: string): string[] {
    const words = rawMention.split(" ").filter((w) => w.length > 0);
    const candidates: string[] = [];
    for (let take = words.length; take >= 1; take--) {
        candidates.push(words.slice(0, take).join(" ").replace(/[.,):;!?]+$/, ""));
    }
    return candidates;
}
