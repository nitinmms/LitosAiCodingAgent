using System.Text.Json;
using Litos.Agent.Tools;

namespace Litos.Gui.Tests.Buzzies;

/// <summary>A throwaway folder per test, deleted afterwards.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "litos-buzzies-tests", Guid.NewGuid().ToString("n"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Litos.Gui.Buzzies.BuzzyStore.DeleteTreeWithoutFollowingLinks(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Records the arguments it was called with; its schema can mark <c>path</c> required.</summary>
internal sealed class RecordingTool(string name, bool pathRequired = true) : ITool
{
    public string Name { get; } = name;
    public string Description => "recording";
    public List<JsonElement> Calls { get; } = [];

    public JsonElement ParameterSchema { get; } = pathRequired
        ? JsonSerializer.SerializeToElement(new { type = "object", required = new[] { "path" } })
        : JsonSerializer.SerializeToElement(new { type = "object" });

    public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        Calls.Add(arguments.Clone());
        return Task.FromResult(ToolResult.Ok("ran"));
    }

    public string? LastPath => Calls.Count == 0 || !Calls[^1].TryGetProperty("path", out var p) ? null : p.GetString();
}

internal static class Args
{
    public static JsonElement Of(object value) => JsonSerializer.SerializeToElement(value);
}
