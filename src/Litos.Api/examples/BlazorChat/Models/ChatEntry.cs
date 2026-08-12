namespace Litos.Examples.BlazorChat.Models;

/// <summary>One rendered line in the chat transcript — circuit-scoped, in-memory only (Chat.razor's @code block owns the list; nothing here survives a page refresh, matching this example's one-session-per-circuit design).</summary>
public abstract record ChatEntry
{
    public sealed record UserMessage(string Text, IReadOnlyList<string> AttachmentNames) : ChatEntry;

    /// <summary>Mutable Text so streamed TextDelta events can append into the same bubble instead of creating a new entry per delta.</summary>
    public sealed record AssistantMessage : ChatEntry
    {
        public string Text { get; set; } = "";
    }

    public sealed record ToolActivity(string ToolName, ToolActivityStatus Status, string? Detail) : ChatEntry;

    public sealed record SystemNote(string Text) : ChatEntry;
}

public enum ToolActivityStatus
{
    Running,
    Succeeded,
    Failed,
    Skipped,
}
