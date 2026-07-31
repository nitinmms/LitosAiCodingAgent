namespace Litos.Api.Channels.Telegram;

/// <summary>
/// Splits reply text into Telegram's 4096-character-per-message cap (ReadMe_TelegramIntegrationTool.md
/// §6.7) — chunked at paragraph boundaries where possible so a long response doesn't get cut
/// mid-sentence, falling back to a hard cut only when a single paragraph itself exceeds the cap.
/// Pure/static so it's unit-testable without a live ITelegramBotClient.
/// </summary>
public static class TelegramMessageChunker
{
    private const int MaxLength = 3500; // stays comfortably under Telegram's 4096 hard cap

    public static IReadOnlyList<string> Chunk(string text)
    {
        if (text.Length <= MaxLength)
            return text.Length == 0 ? [] : [text];

        var chunks = new List<string>();
        var current = "";

        foreach (var paragraph in text.Split("\n\n"))
        {
            var candidate = current.Length == 0 ? paragraph : current + "\n\n" + paragraph;
            if (candidate.Length <= MaxLength)
            {
                current = candidate;
                continue;
            }

            if (current.Length > 0)
            {
                chunks.Add(current);
                current = "";
            }

            if (paragraph.Length <= MaxLength)
            {
                current = paragraph;
            }
            else
            {
                for (var i = 0; i < paragraph.Length; i += MaxLength)
                    chunks.Add(paragraph.Substring(i, Math.Min(MaxLength, paragraph.Length - i)));
            }
        }

        if (current.Length > 0)
            chunks.Add(current);

        return chunks;
    }
}
