using global::OpenAI;
using global::OpenAI.Audio;

namespace Litos.Api.Channels.Telegram;

/// <summary>
/// Backed by the OpenAI SDK's AudioClient/Whisper — reuses the same OpenAIClient construction
/// shape OpenAiChatProvider already establishes (client.GetAudioClient(model) mirrors
/// client.GetOpenAIModelClient()/GetResponsesClient()). Registered in DI only when an OpenAI key
/// is configured (Program.cs) — TelegramSessionDriver accepts IAudioTranscriber as nullable and
/// falls back to a placeholder message when it isn't available, rather than this type having to
/// handle "no key" itself.
/// </summary>
public sealed class OpenAiAudioTranscriber(OpenAIClient client) : IAudioTranscriber
{
    private const string Model = "whisper-1";

    public async Task<string> TranscribeAsync(Stream audio, string mimeType, CancellationToken ct)
    {
        var audioClient = client.GetAudioClient(Model);
        var extension = mimeType switch
        {
            "audio/ogg" => "ogg",
            "audio/mpeg" => "mp3",
            "audio/wav" or "audio/x-wav" => "wav",
            _ => "ogg",
        };

        var result = await audioClient.TranscribeAudioAsync(audio, $"voice.{extension}", cancellationToken: ct);
        return result.Value.Text;
    }
}
