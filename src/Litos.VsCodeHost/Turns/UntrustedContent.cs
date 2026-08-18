namespace Litos.VsCodeHost.Turns;

/// <summary>
/// Boundary-marker for externally-sourced content entering a turn — local copy of Litos.Api's
/// Channels/Telegram/UntrustedContent.cs. That file's own doc comment frames it around a remote
/// chat participant's document attachment specifically, but the underlying mechanism is generic:
/// signals to the model that wrapped content is data, not instructions (not a guarantee). Applied
/// here to /attach's document conversions the same way — a workspace file read via MarkItDown
/// carries the same class of prompt-injection risk any externally-sourced text does.
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
