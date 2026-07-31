namespace Litos.Api.Channels.Telegram;

/// <summary>
/// Boundary-marker for externally-sourced content entering a turn — adopted verbatim from
/// OpenClaw's own marker shape (ReadMe_TelegramIntegrationTool.md §10.3), chosen there over this
/// codebase's own steering-message framing specifically because OpenClaw's docs describe it as
/// effective against chat-template-token role-forging attacks. Applied here to Telegram document
/// attachments (a remote chat participant's file, not the local keyboard user's) — the same class
/// of risk WebSearchTool's results carry, per that section's own scope note. This signals to the
/// model that content is data, not instructions; it is not a guarantee.
/// </summary>
public static class UntrustedContent
{
    public static string Wrap(string source, string content) =>
        $"""
        <<<EXTERNAL_UNTRUSTED_CONTENT source="{source}">>>
        {content}
        <<<END_EXTERNAL_UNTRUSTED_CONTENT>>>
        """;
}
