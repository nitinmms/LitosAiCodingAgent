namespace Litos.Api.Channels.Telegram;

/// <summary>
/// Transcribes voice messages to text before they ever reach AgentLoop — see
/// ReadMe_TelegramIntegrationTool.md §7.2. Narrow and provider-independent from the configured
/// chat provider (Anthropic/Gemini users still get voice support as long as an OpenAI key
/// exists), the same reasoning WebSearchTool's Tavily key gets registered independent of the
/// chat provider. Colocated under Litos.Api.Channels.Telegram rather than a new
/// Litos.Providers.OpenAI type, since only the Telegram bridge needs this today.
/// </summary>
public interface IAudioTranscriber
{
    Task<string> TranscribeAsync(Stream audio, string mimeType, CancellationToken ct);
}
