using Litos.Tools.Mcp;
using Litos.Tools.Shell;

namespace Litos.VsCodeHost.Mcp;

/// <summary>
/// /mcp — server CRUD + live status + refresh, none of which has ever had a JSON API on any face
/// (the only prior art, Litos.Api's McpServers.razor, is a server-rendered Blazor/SignalR page).
/// McpConfigStore/McpToolProvider are the same face-agnostic Litos.Tools.Mcp types Litos.Api and
/// Litos.Gui already use, unmodified.
/// </summary>
public static class McpEndpoints
{
    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/mcp/servers", (McpConfigStore configStore, McpToolProvider toolProvider) =>
        {
            var connectionsByName = toolProvider.Connections.ToDictionary(c => c.ServerName);
            var servers = configStore.Current.Servers.Select(server =>
            {
                connectionsByName.TryGetValue(server.Name, out var connection);
                return new
                {
                    server.Name,
                    server.Transport,
                    server.Command,
                    server.Args,
                    server.Url,
                    server.Enabled,
                    server.DefaultPermission,
                    status = connection?.Status.ToString() ?? "Disconnected",
                    error = connection?.Error,
                    toolCount = connection?.Tools.Count ?? 0,
                    promptCount = connection?.Prompts.Count ?? 0,
                };
            });
            return Results.Ok(servers);
        });

        app.MapPost("/mcp/servers", (AddMcpServerRequest request, McpConfigStore configStore) =>
        {
            try
            {
                configStore.Update(cfg => cfg with
                {
                    Servers = [.. cfg.Servers, new McpServerDefinition(
                        Name: request.Name,
                        Transport: request.Transport,
                        Command: request.Command,
                        Args: request.Args,
                        Env: request.Env,
                        Url: request.Url,
                        Enabled: true,
                        DefaultPermission: request.DefaultPermission,
                        ToolOverrides: null)],
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            return Results.Ok();
        });

        app.MapPost("/mcp/servers/{name}/enabled", (string name, SetEnabledRequest request, McpConfigStore configStore) =>
        {
            configStore.Update(cfg => cfg with
            {
                Servers = [.. cfg.Servers.Select(s => s.Name == name ? s with { Enabled = request.Enabled } : s)],
            });
            return Results.Ok();
        });

        app.MapDelete("/mcp/servers/{name}", (string name, McpConfigStore configStore) =>
        {
            configStore.Update(cfg => cfg with { Servers = [.. cfg.Servers.Where(s => s.Name != name)] });
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
