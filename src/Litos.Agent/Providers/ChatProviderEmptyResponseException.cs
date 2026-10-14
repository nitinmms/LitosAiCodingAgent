namespace Litos.Agent.Providers;

/// <summary>
/// Thrown by an IChatProvider when the upstream API answered but the answer holds neither text nor
/// a tool call, for a reason the API reports apart from the content (Gemini's finishReason, for
/// one). Passing such an answer on as an ordinary message would look, to the agent loop, like a
/// turn that chose to say nothing and stop. <see cref="Transient"/> says whether sending the same
/// request again may succeed (a malformed function call, an overloaded model) or not (a safety or
/// recitation block). Message names the reason and is written to be shown to a human as-is.
/// </summary>
public sealed class ChatProviderEmptyResponseException(string message, bool transient) : Exception(message)
{
    public bool Transient { get; } = transient;
}
