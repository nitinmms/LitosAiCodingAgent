using System.Text.Json;

namespace Litos.SoftwareFactory.Tray;

/// <summary>Who the token belongs to, from GET /api/auth/me.</summary>
public sealed record TrayUser(Guid Id, string UserName, string? DisplayName)
{
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? UserName : DisplayName;

    internal static TrayUser Parse(JsonElement me) => new(
        me.GetProperty("id").GetGuid(),
        me.GetProperty("userName").GetString() ?? "",
        me.TryGetProperty("displayName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null);
}

/// <summary>
/// What the tray app needs of a task: the host's thread view (FactoryApi.ThreadView), as
/// GET /api/threads lists it and the board's event stream sends it, whole, on every change.
/// </summary>
public sealed record TrayTask(
    Guid Id,
    Guid OwnerId,
    string Title,
    string Turn,
    string State,
    string Stage,
    string? StateReason,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The host's turn label for "stopped until a person acts" (TurnLabels.cs).</summary>
    public const string AwaitingYou = "AwaitingYou";

    /// <summary>The rule the web app's beeps use (domain/task.ts awaitsYou): the user's own task, stopped until they act.</summary>
    public bool AwaitsUser(Guid userId) => Turn == AwaitingYou && OwnerId == userId;

    internal static TrayTask Parse(JsonElement thread) => new(
        thread.GetProperty("id").GetGuid(),
        thread.GetProperty("ownerId").GetGuid(),
        thread.GetProperty("title").GetString() ?? "",
        thread.GetProperty("turn").GetString() ?? "",
        thread.GetProperty("state").GetString() ?? "",
        thread.TryGetProperty("stage", out var stage) ? stage.GetString() ?? "" : "",
        thread.TryGetProperty("stateReason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null,
        thread.TryGetProperty("updatedAt", out var updated) && updated.ValueKind == JsonValueKind.String ? updated.GetDateTimeOffset() : default);

    internal static TrayTask Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Parse(document.RootElement);
    }
}
