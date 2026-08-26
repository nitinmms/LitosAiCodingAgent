using Litos.Agent.Messages;
using Litos.Agent.Streaming;

namespace Litos.Agent.Session;

public sealed record TranscriptEntry(
    string Kind,
    DateTimeOffset Timestamp,
    ChatMessage? Message,
    string? CallId,
    UsageInfo? Usage,
    string? WorkingDirectory = null,
    bool? KernelModeEnabled = null)
{
    public static TranscriptEntry FromMessage(ChatMessage message, UsageInfo? usage = null) =>
        new(
            Kind: message.Role == Role.Assistant ? "assistant" : "user",
            Timestamp: DateTimeOffset.UtcNow,
            Message: message,
            CallId: null,
            Usage: usage);

    /// <summary>
    /// Written once, as the first entry of a brand-new session, so the working directory
    /// travels with the transcript instead of being re-derived from the live process's
    /// ambient CWD on every resume — mirrors pi's SessionHeader.cwd.
    /// </summary>
    public static TranscriptEntry SessionHeader(string workingDirectory) =>
        new(
            Kind: "session",
            Timestamp: DateTimeOffset.UtcNow,
            Message: null,
            CallId: null,
            Usage: null,
            WorkingDirectory: workingDirectory);

    /// <summary>
    /// Written every time the kernel-mode toggle flips (ReadMe_PTCPersistentKernel.md §5.3) — a
    /// distinct entry per flip rather than baked into SessionHeader, since SessionHeader is written
    /// once at session start but the toggle can change any number of times afterward. On replay
    /// (Transcript.LoadAsync), the latest such entry wins — this is a per-session, persisted
    /// decision, not in-memory-only UI state that silently resets on /resume.
    /// </summary>
    public static TranscriptEntry KernelToggle(bool enabled) =>
        new(
            Kind: "kernel_toggle",
            Timestamp: DateTimeOffset.UtcNow,
            Message: null,
            CallId: null,
            Usage: null,
            KernelModeEnabled: enabled);
}
