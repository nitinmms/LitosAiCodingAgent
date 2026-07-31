namespace Litos.Api.Channels.Telegram;

public sealed record TelegramCommand(string Name, string? Argument)
{
    /// <summary>
    /// Parses a `/command` or `/command argument` message into its name/argument split — pure,
    /// no Telegram.Bot dependency, so TelegramSessionDriver's dispatch logic can be unit tested
    /// without constructing live Message/Update objects. Returns null for anything not starting
    /// with '/', so callers can distinguish "not a command" from "a command with no argument".
    /// </summary>
    public static TelegramCommand? Parse(string text)
    {
        if (!text.StartsWith('/'))
            return null;

        var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries);
        var argument = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
        return new TelegramCommand(parts[0], argument);
    }
}
