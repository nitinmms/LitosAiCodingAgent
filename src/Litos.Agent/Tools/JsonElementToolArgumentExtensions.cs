using System.Text.Json;

namespace Litos.Agent.Tools;

/// <summary>
/// Tools read required string arguments with this instead of <see cref="JsonElement.GetProperty"/>,
/// which throws <see cref="KeyNotFoundException"/> when the model's tool-call JSON omits a property
/// — turning a plain "argument required" ToolResult.Error into a raw, unhelpful .NET exception
/// message. Local models in particular are more prone to malformed/incomplete tool-call arguments
/// than hosted ones, so they hit this path more often.
/// </summary>
public static class JsonElementToolArgumentExtensions
{
    public static string? GetStringOrNull(this JsonElement arguments, string propertyName) =>
        arguments.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
