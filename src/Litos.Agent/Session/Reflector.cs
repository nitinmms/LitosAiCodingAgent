using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;

namespace Litos.Agent.Session;

/// <summary>
/// Performs the /reflect distillation: a one-off, non-tool, non-streamed-to-user call to the
/// session's current provider/model that reads the full transcript plus the project's existing
/// AGENTS.md content (if any) and asks the model to produce the FULL updated file content, per
/// AGENTS.md's fixed five-section schema (Conventions, Decisions, Corrections, User preferences,
/// Open threads). Mirrors Compactor.SummarizeAsync exactly (Tools: [], TextDelta accumulation) —
/// kept here rather than in Litos.Gui so any front end can reuse it, the same way both
/// AgentLoopFactory and MainWindow already reuse Compactor.
///
/// Unlike Compactor, this never mutates the transcript or sends anything to the model beyond the
/// existing messages plus the reflector prompt — the caller is responsible for showing the result
/// to the user and writing it to disk only on explicit confirmation.
///
/// Known gap: the transcript is sent to the model verbatim, with no secret-scrubbing pass. Left
/// for a later iteration — the confirm-before-write step is the only guardrail today.
/// </summary>
public sealed class Reflector
{
    private const string ReflectorPrompt = """
        You are reviewing a coding session to extract durable knowledge for future sessions in
        this project. You are given the full message transcript of the session above, followed by
        the project's current AGENTS.md content (or a note that it does not exist yet).

        Extract ONLY facts that would still be true and useful weeks from now:
        - Conventions: codebase naming, structure, and patterns actually used or established this session.
        - Decisions: choices made and their rationale.
        - Corrections: cases where the user fixed or pushed back on something the agent did.
        - User preferences: how the user likes the agent to work.
        - Open threads: things noticed but not addressed this session.

        Do NOT include:
        - Session-specific details with no future relevance (e.g. "fixed a typo on line 42").
        - Anything already captured in the current AGENTS.md content below — merge and de-duplicate
          instead of repeating; update an existing entry in place if this session changed or refined it.
        - Speculation not grounded in what actually happened in the transcript above.

        Output the FULL updated AGENTS.md content as markdown, using exactly this structure and
        these five section headers, in this order:

        # AGENTS.md

        ## Conventions
        - ...

        ## Decisions
        - ...

        ## Corrections
        - ...

        ## User preferences
        - ...

        ## Open threads
        - ...

        Preserve existing entries that are still valid, update any that changed, and append
        genuinely new ones under the correct section. Keep entries terse — one line each where
        possible. If a section has no entries, leave it present with no bullets underneath. Output
        ONLY the file content — no commentary before or after it, no markdown code fence around
        the whole file.
        """;

    public Task<string> ReflectAsync(
        IReadOnlyList<ChatMessage> messages,
        string? existingAgentsMd,
        IChatProvider provider,
        string model,
        CancellationToken ct)
    {
        var existingSection = string.IsNullOrWhiteSpace(existingAgentsMd)
            ? "(AGENTS.md does not exist yet — produce a new file.)"
            : existingAgentsMd;

        var promptText = $"""
            {ReflectorPrompt}

            ## Current AGENTS.md content

            {existingSection}
            """;

        var request = new ChatRequest(
            Messages: [.. messages, ChatMessage.User(promptText)],
            Tools: [],
            Model: model);

        return StreamTextAsync(provider, request, ct);
    }

    private static async Task<string> StreamTextAsync(IChatProvider provider, ChatRequest request, CancellationToken ct)
    {
        var text = new System.Text.StringBuilder();
        await foreach (var evt in provider.StreamAsync(request, ct))
            if (evt is TextDelta delta)
                text.Append(delta.Text);

        return text.Length > 0 ? text.ToString() : "(reflection unavailable)";
    }
}
