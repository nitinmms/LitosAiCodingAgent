using Litos.Api.Channels.Telegram;

namespace Litos.Api.Tests.Channels.Telegram;

public class TelegramMessageChunkerTests
{
    [Fact]
    public void Chunk_ShortText_ReturnsSingleChunk()
    {
        var chunks = TelegramMessageChunker.Chunk("hello world");

        Assert.Equal(["hello world"], chunks);
    }

    [Fact]
    public void Chunk_EmptyText_ReturnsNoChunks()
    {
        Assert.Empty(TelegramMessageChunker.Chunk(""));
    }

    [Fact]
    public void Chunk_LongTextWithParagraphs_SplitsAtParagraphBoundaries()
    {
        var paragraph = new string('a', 3000);
        var text = string.Join("\n\n", Enumerable.Repeat(paragraph, 3));

        var chunks = TelegramMessageChunker.Chunk(text);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 3500));
        // Every chunk boundary falls on a paragraph, not mid-word — no chunk should end with a
        // truncated "a...a" run shorter than a full paragraph except possibly a legitimate combine.
        Assert.All(chunks, c => Assert.True(c.Split("\n\n").All(p => p.Length == 3000 || p.Length == 0)));
    }

    [Fact]
    public void Chunk_SingleParagraphExceedingCap_HardSplits()
    {
        var text = new string('x', 8000);

        var chunks = TelegramMessageChunker.Chunk(text);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.True(c.Length <= 3500));
        Assert.Equal(text, string.Concat(chunks));
    }

    [Fact]
    public void Chunk_ReassemblesToOriginalContent_ForParagraphSplitCase()
    {
        var paragraph = new string('a', 3000);
        var text = string.Join("\n\n", Enumerable.Repeat(paragraph, 3));

        var chunks = TelegramMessageChunker.Chunk(text);
        var rejoined = string.Join("\n\n", chunks);

        Assert.Equal(text, rejoined);
    }
}
