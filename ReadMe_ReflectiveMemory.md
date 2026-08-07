# Reflective Memory for LitosAiAgent

A design for a user-triggered `/reflect` command that distills a coding session into durable, structured memory stored in a project-level `AGENTS.md`.

---

## 1. Concept

At the end of a working session, the user types `/reflect`. The agent looks back over the current session's transcript (tool calls, diffs, user corrections, decisions made) and asks itself: *"What from this session is worth remembering next time I work in this project?"*

The output is written into `AGENTS.md` at the project root — a single, human-readable file that:
- The agent reads at the start of every future session in that project.
- The user can read, edit, or delete by hand, like any other file in the repo.
- Can be committed to git, so the whole team benefits from what the agent has learned.

This is intentionally simple: no vector DB, no embeddings, no background daemon. One command, one file, one LLM pass. You can add retrieval sophistication later once the basic loop proves useful.

---

## 2. Why user-triggered (not automatic)

Automatic reflection after every task sounds appealing but has real costs:
- Extra LLM call + latency after every single task, even trivial ones.
- Risk of polluting `AGENTS.md` with noise from sessions that didn't produce anything durable.
- No natural checkpoint for the user to review what's about to be written.

`/reflect` as an explicit command means the user decides *when a session was worth distilling* — usually after finishing a feature, fixing a tricky bug, or after a back-and-forth where they corrected the agent multiple times. It also makes the feature easy to demo: run a session, hit `/reflect`, show the diff to `AGENTS.md`.

A "suggest reflect" nudge (agent says "This session had a few corrections — run `/reflect`?") is a nice v2 addition, but the trigger stays user-owned.

---

## 3. AGENTS.md schema

Keep it flat, greppable, and boring — this file is read by both the agent and humans.

```markdown
# AGENTS.md
<!-- Maintained by LitosAiAgent's /reflect command. Edit freely; the agent treats this as ground truth. -->

## Conventions
- Tests live under `tests/`, mirroring the source folder structure.
- Use MediatR for all command/query handling; no direct service calls from controllers.
- Prefer `Result<T>` return types over throwing exceptions for expected failure paths.

## Decisions
- Chose PostgreSQL over SQL Server (2026-08-02) — team already runs Postgres for other services; avoids a second DB engine.
- Auth uses ASP.NET Identity, not a custom JWT implementation — decided against custom to reduce security surface area.

## Corrections (things the agent got wrong before)
- Do not add null-checks with `??` on DTOs already validated by FluentValidation — redundant, flagged by user twice.
- Migration files must be named `YYYYMMDD_Description`, not auto-generated GUIDs.

## User preferences
- Keep PRs small; one logical change per PR.
- No inline comments explaining "what" — only "why". Code should be self-explanatory otherwise.

## Open threads
- Rate limiting on `/api/orders` was flagged as needed but not implemented (2026-08-05).
```

Design notes:
- **Five fixed sections.** Predictable structure means both the reflector prompt and the agent's read-time parser are simple.
- **Dated entries** for Decisions and Open threads — decisions and TODOs age; conventions and preferences generally don't need dates.
- **No confidence scores or JSON.** This file is meant to be read by a human without translation. Save structured metadata (if you want it later) in a sidecar file, not here.
- **Size discipline.** When a section gets long (~20+ bullets), the reflect pass should consolidate/de-duplicate rather than keep appending — same principle as any long-lived memory file.

---

## 4. The `/reflect` flow

```
User types /reflect
        │
        ▼
Agent gathers session transcript
(tool calls, file diffs, user messages, corrections)
        │
        ▼
Agent reads current AGENTS.md (if it exists)
        │
        ▼
Reflector LLM call:
  - Input: transcript + existing AGENTS.md
  - Task: extract NEW durable facts, and merge/de-dupe
    against what's already there
  - Output: full updated AGENTS.md content
        │
        ▼
Show user a diff (old vs new AGENTS.md)
        │
        ▼
User confirms / edits / rejects
        │
        ▼
Write file to disk
```

The diff-and-confirm step matters — it's the trust mechanism. An agent that silently rewrites a file the user didn't ask it to touch (outside the session's actual task) will feel invasive. Showing a diff and asking for a quick confirm keeps it collaborative.

---

## 5. The reflector prompt (sketch)

This is a separate, focused LLM call — not the same context as the coding session itself, so it doesn't inherit task-specific noise.

```
You are reviewing a coding session to extract durable knowledge for future sessions
in this project. You are given:
1. The current AGENTS.md (may be empty)
2. A transcript of the session just completed

Extract ONLY facts that would still be true and useful weeks from now:
- Codebase conventions (naming, structure, patterns used)
- Decisions made and their rationale
- Corrections: cases where the user fixed or pushed back on something the agent did
- User preferences about how they like the agent to work
- Open threads: things noticed but not addressed this session

Do NOT include:
- Session-specific details with no future relevance (e.g. "fixed a typo on line 42")
- Anything already captured in AGENTS.md (merge/de-dupe instead of repeating)
- Speculation not grounded in what actually happened this session

Output the FULL updated AGENTS.md content, preserving existing entries that are
still valid, updating any that changed, and appending genuinely new ones under
the correct section. Keep entries terse — one line each where possible.
```

The "output full file content" instruction (rather than a patch) keeps the .NET implementation simple: one LLM call, one file write, no merge logic to hand-roll.

---

## 6. .NET implementation sketch

```csharp
public class ReflectCommand
{
    private readonly ISessionTranscriptStore _transcripts;
    private readonly IAnthropicClient _claude;
    private readonly IProjectFileSystem _fs;

    public async Task<ReflectResult> ExecuteAsync(string projectRoot, CancellationToken ct)
    {
        var transcript = _transcripts.GetCurrentSessionTranscript();
        var agentsMdPath = Path.Combine(projectRoot, "AGENTS.md");
        var existing = _fs.Exists(agentsMdPath) ? await _fs.ReadAllTextAsync(agentsMdPath, ct) : "";

        var prompt = ReflectorPromptBuilder.Build(existing, transcript);
        var response = await _claude.CompleteAsync(prompt, ct);

        var updated = ResponseParser.ExtractMarkdown(response);

        return new ReflectResult
        {
            Before = existing,
            After = updated,
            Path = agentsMdPath
        };
        // Caller shows Before/After diff to user via CLI/UI, then calls CommitAsync on confirm.
    }

    public Task CommitAsync(ReflectResult result, CancellationToken ct)
        => _fs.WriteAllTextAsync(result.Path, result.After, ct);
}
```

Slot this in wherever LitosAiAgent already parses slash commands (`/help`, `/clear`, etc.) — `/reflect` is just another command in that dispatcher, mapped to `ReflectCommand.ExecuteAsync`.

**Reading it back in:** at session start, check for `AGENTS.md` in the project root and, if present, inject its full contents into the system prompt (it should typically be small — a few KB at most — so no chunked retrieval needed at this stage). If it grows large over time, that's the trigger to add section-level retrieval later, not before.

---

## 7. Guardrails

- **User can always edit/delete.** `AGENTS.md` is a plain file in their repo — no special "memory database" they can't see or touch.
- **Filter secrets before reflecting.** Strip anything that looks like an API key, connection string, or credential from the transcript before it reaches the reflector prompt.
- **Confirm before write.** Never silently overwrite `AGENTS.md` — always diff-and-confirm (see §4).
- **Consolidate, don't just append.** Since the reflector always outputs the full file, de-duplication happens naturally each time — but watch for the file growing unbounded over many months; a periodic "tidy" pass (same reflector prompt, called with an empty transcript) can compact it.
- **Git-friendly.** Since it's markdown in the repo, normal git history/diff/blame gives you an audit trail for free — no extra versioning system needed.

---

