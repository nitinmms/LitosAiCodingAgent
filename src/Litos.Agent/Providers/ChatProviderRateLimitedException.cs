namespace Litos.Agent.Providers;

/// <summary>
/// Thrown by an IChatProvider when the upstream API itself rate-limits a request (HTTP 429) —
/// distinct from ToolInvocationException (a tool failing), this is the chat provider's own call
/// failing before any reply is produced. Message is written to be shown to a human as-is (see
/// AgentEvent.ErrorOccurred consumers, e.g. TelegramSessionDriver.StreamRepliesAsync), not a raw
/// exception dump, since retrying is outside this app's control — the fix is waiting or switching
/// providers/models, not anything the caller can programmatically resolve.
/// </summary>
public sealed class ChatProviderRateLimitedException(string message) : Exception(message);
