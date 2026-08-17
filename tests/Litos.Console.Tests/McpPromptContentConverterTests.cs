using Litos.Console;
using ModelContextProtocol.Protocol;

namespace Litos.Console.Tests;

/// <summary>
/// Covers McpPromptContentConverter.Convert — the pure GetPromptResult-to-plain-text extraction
/// that lets a fetched MCP prompt be fed through the same turn-execution path a typed message
/// uses (see Program.cs's TryHandleMcpPromptCommandAsync). Mirrors Litos.Gui's
/// McpPromptContentConverterTests exactly, since the type was copied verbatim.
/// </summary>
public class McpPromptContentConverterTests
{
    private static PromptMessage TextMessage(string text) =>
        new() { Role = Role.User, Content = new TextContentBlock { Text = text } };

    private static PromptMessage NonTextMessage() =>
        new() { Role = Role.User, Content = new ImageContentBlock { Data = ReadOnlyMemory<byte>.Empty, MimeType = "image/png" } };

    [Fact]
    public void Convert_SingleTextMessage_ReturnsItsText()
    {
        var result = new GetPromptResult { Messages = [TextMessage("hello")] };

        var converted = McpPromptContentConverter.Convert(result);

        Assert.Equal("hello", converted.Text);
        Assert.Empty(converted.SkippedBlockIndexes);
    }

    [Fact]
    public void Convert_MultipleTextMessages_JoinsWithBlankLine_InOriginalOrder()
    {
        var result = new GetPromptResult { Messages = [TextMessage("first"), TextMessage("second")] };

        var converted = McpPromptContentConverter.Convert(result);

        Assert.Equal("first\n\nsecond", converted.Text);
    }

    [Fact]
    public void Convert_NonTextContentBlock_IsSkipped_IndexRecorded()
    {
        var result = new GetPromptResult { Messages = [NonTextMessage()] };

        var converted = McpPromptContentConverter.Convert(result);

        Assert.Equal("", converted.Text);
        Assert.Equal([0], converted.SkippedBlockIndexes);
    }

    [Fact]
    public void Convert_MixOfTextAndNonTextBlocks_ExtractsOnlyText_RecordsSkippedIndexesForTheOthers()
    {
        var result = new GetPromptResult
        {
            Messages = [TextMessage("keep"), NonTextMessage(), TextMessage("also keep")],
        };

        var converted = McpPromptContentConverter.Convert(result);

        Assert.Equal("keep\n\nalso keep", converted.Text);
        Assert.Equal([1], converted.SkippedBlockIndexes);
    }

    [Fact]
    public void Convert_AllBlocksNonText_ReturnsEmptyTextAndAllIndexesSkipped()
    {
        var result = new GetPromptResult { Messages = [NonTextMessage(), NonTextMessage()] };

        var converted = McpPromptContentConverter.Convert(result);

        Assert.Equal("", converted.Text);
        Assert.Equal([0, 1], converted.SkippedBlockIndexes);
    }

    [Fact]
    public void Convert_EmptyMessagesList_ReturnsEmptyTextNoSkips()
    {
        var converted = McpPromptContentConverter.Convert(new GetPromptResult { Messages = [] });

        Assert.Equal("", converted.Text);
        Assert.Empty(converted.SkippedBlockIndexes);
    }

    [Fact]
    public void Convert_MessageRoleIsIgnored_AssistantAndUserRoleBothContributeText()
    {
        var result = new GetPromptResult
        {
            Messages =
            [
                new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = "user text" } },
                new PromptMessage { Role = Role.Assistant, Content = new TextContentBlock { Text = "assistant text" } },
            ],
        };

        var converted = McpPromptContentConverter.Convert(result);

        Assert.Equal("user text\n\nassistant text", converted.Text);
    }
}
