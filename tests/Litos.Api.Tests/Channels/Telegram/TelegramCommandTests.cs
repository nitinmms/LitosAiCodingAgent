using Litos.Api.Channels.Telegram;

namespace Litos.Api.Tests.Channels.Telegram;

public class TelegramCommandTests
{
    [Fact]
    public void Parse_PlainText_ReturnsNull()
    {
        Assert.Null(TelegramCommand.Parse("hello there"));
    }

    [Fact]
    public void Parse_CommandWithNoArgument_HasNullArgument()
    {
        var command = TelegramCommand.Parse("/new");

        Assert.NotNull(command);
        Assert.Equal("/new", command.Name);
        Assert.Null(command.Argument);
    }

    [Fact]
    public void Parse_CommandWithArgument_SplitsOnFirstSpace()
    {
        var command = TelegramCommand.Parse("/skill research something else");

        Assert.Equal("/skill", command!.Name);
        Assert.Equal("research something else", command.Argument);
    }

    [Fact]
    public void Parse_CommandWithTrailingWhitespaceOnly_ArgumentIsNull()
    {
        var command = TelegramCommand.Parse("/skills   ");

        Assert.Equal("/skills", command!.Name);
        Assert.Null(command.Argument);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsNull()
    {
        Assert.Null(TelegramCommand.Parse(""));
    }
}
