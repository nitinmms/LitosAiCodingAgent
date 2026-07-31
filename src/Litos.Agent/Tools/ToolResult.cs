namespace Litos.Agent.Tools;

public sealed record ToolResult(string Text, bool IsError = false)
{
    public static ToolResult Ok(string text) => new(text);

    public static ToolResult Error(string message) => new(message, IsError: true);
}
