using System.Text.Json;

namespace Litos.Api.Channels.Telegram;

/// <summary>
/// Renders a short "🔧 tool_name arg" status line for a completed tool call — mirrors
/// Litos.Console's DescribeToolArgs (Program.cs), reused here so a Telegram chat sees the same
/// "what ran" signal without the full tool output flooding the chat (ReadMe_TelegramIntegrationTool.md
/// §6.3 point 4).
/// </summary>
public static class ToolCallSummary
{
    public static string Describe(string toolName, JsonElement arguments)
    {
        var propertyName = toolName switch
        {
            "read_file" or "write_file" or "edit_file" or "list_directory" => "path",
            "shell" => "command",
            "skill" => "name",
            _ => null,
        };

        var argText = propertyName is not null
            && arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? $" {value.GetString()}"
                : "";

        return $"🔧 {toolName}{argText}";
    }
}
