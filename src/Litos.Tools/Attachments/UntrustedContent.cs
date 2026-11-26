namespace Litos.Tools.Attachments;

/// <summary>
/// Boundary-marker for externally-sourced content entering a turn — a remote chat participant's
/// document (Litos.Api's Telegram channel), an HTTP-uploaded or locally attached file converted by
/// MarkItDown (Litos.Api, Litos.VsCodeHost, Litos.Gui), or a fetched page. Any of these can carry
/// prompt-injection text, the same class of risk WebSearchTool's results carry. The marker shape is
/// adopted verbatim from OpenClaw's (ReadMe_TelegramIntegrationTool.md §10.3), chosen over this
/// codebase's own steering-message framing because OpenClaw's docs describe it as effective against
/// chat-template-token role-forging attacks. It signals to the model that wrapped content is data,
/// not instructions; it is not a guarantee. One shared copy: each face used to keep its own.
/// </summary>
public static class UntrustedContent
{
    /// <summary>
    /// Written with explicit '\n' rather than a raw string literal on purpose. A raw literal's line
    /// breaks are whatever the source file's line endings are, so on a CRLF checkout the markers came
    /// out with "\r\n" and on an LF checkout with "\n" — the same helper producing two different wire
    /// formats depending on how the repository was cloned, which is exactly the kind of difference
    /// nobody would think to look for. The bytes a model sees are now the same everywhere.
    /// </summary>
    public static string Wrap(string source, string content) =>
        $"<<<EXTERNAL_UNTRUSTED_CONTENT source=\"{source}\">>>\n{content}\n<<<END_EXTERNAL_UNTRUSTED_CONTENT>>>";
}
