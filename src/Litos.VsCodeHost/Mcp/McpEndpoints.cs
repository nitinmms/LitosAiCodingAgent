using Litos.Tools.Mcp;
using Litos.Tools.Shell;

namespace Litos.VsCodeHost.Mcp;

/// <summary>
/// /mcp — server CRUD + live status + refresh, none of which has ever had a JSON API on any face
/// (the only prior art, Litos.Api's McpServers.razor, is a server-rendered Blazor/SignalR page).
/// McpConfigStore/McpToolProvider are the same face-agnostic Litos.Tools.Mcp types Litos.Api and
/// Litos.Gui already use, unmodified; the mutations themselves are McpServerAdmin's, shared with
/// Litos.Gui's McpServersWindow.
/// </summary>
public static class McpEndpoints
{
    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/mcp/servers", (McpConfigStore configStore, McpToolProvider toolProvider) =>
            Results.Ok(configStore.ListServers(toolProvider).Select(status => new
            {
                status.Definition.Name,
                status.Definition.Transport,
                status.Definition.Command,
                status.Definition.Args,
                status.Definition.Url,
                status.Definition.Enabled,
                status.Definition.DefaultPermission,
                status = status.Connection?.Status.ToString() ?? "Disconnected",
                error = status.Connection?.Error,
                toolCount = status.Connection?.Tools.Count ?? 0,
                promptCount = status.Connection?.Prompts.Count ?? 0,
            })));

        app.MapPost("/mcp/servers", (AddMcpServerRequest request, McpConfigStore configStore) =>
        {
            try
            {
                configStore.AddServer(new McpServerDefinition(
                    Name: request.Name,
                    Transport: request.Transport,
                    Command: request.Command,
                    Args: request.Args,
                    Env: request.Env,
                    Url: request.Url,
                    Enabled: true,
                    DefaultPermission: request.DefaultPermission,
                    ToolOverrides: null));
            }
            // McpConfigStore.Update's validation (duplicate name, "__" in a name) throws
            // McpConfigValidationException, not ArgumentException — catching only the latter
            // turned a duplicate name into a 500 instead of the 400 + message the panel shows.
            catch (Exception ex) when (ex is McpConfigValidationException or ArgumentException)
            {
                return Results.BadRequest(ex.Message);
            }
            return Results.Ok();
        });

        app.MapPost("/mcp/servers/{name}/enabled", (string name, SetEnabledRequest request, McpConfigStore configStore) =>
        {
            configStore.SetServerEnabled(name, request.Enabled);
            return Results.Ok();
        });

        app.MapPost("/mcp/servers/{name}/permission", (string name, SetDefaultPermissionRequest request, McpConfigStore configStore) =>
        {
            configStore.SetServerPermission(name, request.DefaultPermission);
            return Results.Ok();
        });

        app.MapDelete("/mcp/servers/{name}", (string name, McpConfigStore configStore) =>
        {
            configStore.RemoveServer(name);
            return Results.Ok();
        });

        app.MapPost("/mcp/refresh", async (McpToolProvider toolProvider, CancellationToken ct) =>
        {
            await toolProvider.RefreshAsync(TimeSpan.FromSeconds(30), ct);
            return Results.Ok();
        });

        return app;
    }
}

public sealed record AddMcpServerRequest(
    string Name, McpTransportKind Transport, string? Command, IReadOnlyList<string>? Args,
    IReadOnlyDictionary<string, string>? Env, string? Url, ToolPermission DefaultPermission);

public sealed record SetEnabledRequest(bool Enabled);

public sealed record SetDefaultPermissionRequest(ToolPermission DefaultPermission);
