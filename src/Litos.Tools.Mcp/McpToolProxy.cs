using System.Text.Json;
using Litos.Agent.Tools;
using Litos.Tools.Shell;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Litos.Tools.Mcp;

/// <summary>
/// ITool wrapper around one tool discovered from a connected MCP server. Name is
/// mcp__{serverName}__{toolName} (Claude Code's own convention) — the double-underscore-delimited
/// name doubles as the approval-permission-rule key, so McpAwareApprovalGate needs no separate
/// "which server owns this tool" lookup once the proxy is constructed.
/// </summary>
public sealed class McpToolProxy(
    string serverName, McpClientTool clientTool, McpServerConnection connection, IToolApprovalGate approvalGate)
    : ITool
{
    public string Name { get; } = $"mcp__{serverName}__{clientTool.Name}";

    public string Description => clientTool.Description ?? string.Empty;

    public JsonElement ParameterSchema => clientTool.JsonSchema;

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var argsPreview = JsonSerializer.Serialize(arguments, new JsonSerializerOptions { WriteIndented = true });
        var decision = await approvalGate.RequestAsync(
            new ToolInvocationPreview(Name, $"Call MCP tool '{clientTool.Name}' on server '{serverName}'", argsPreview),
            ct);
        if (decision == ApprovalDecision.Deny)
            return ToolResult.Error($"User denied this MCP tool call ({Name}).");

        return await InvokeDirectAsync(serverName, clientTool.Name, connection, arguments, ct);
    }

    /// <summary>
    /// The actual MCP call, with no IToolApprovalGate check — extracted so a kernel-mode script's
    /// tool bridge (KernelSession.ServiceToolCallAsync) can reach it without going through
    /// InvokeAsync's gate, per ReadMe_PTCPersistentKernel.md §7/§8.2's flagged bug: routing a kernel
    /// script's MCP call through McpToolProxy.InvokeAsync unmodified would silently re-gate MCP
    /// tools from inside a supposedly ungated kernel. One invocation path, two callers — gated for
    /// the direct/sequential path (InvokeAsync above), ungated for KernelSession — no duplicated
    /// protocol-translation logic between them.
    /// </summary>
    public static async Task<ToolResult> InvokeDirectAsync(
        string serverName, string toolName, McpServerConnection connection, JsonElement arguments, CancellationToken ct)
    {
        var argumentsDict = ToArgumentsDictionary(arguments);

        CallToolResult result;
        try
        {
            result = await connection.CallToolAsync(toolName, argumentsDict, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"MCP server '{serverName}' failed to handle '{toolName}': {ex.Message}");
        }

        var text = string.Join(
            "\n",
            result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        return result.IsError == true
            ? ToolResult.Error(text)
            : ToolResult.Ok(text);
    }

    private static IReadOnlyDictionary<string, object?> ToArgumentsDictionary(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, object?>();

        var dict = new Dictionary<string, object?>();
        foreach (var property in arguments.EnumerateObject())
            dict[property.Name] = property.Value;
        return dict;
    }
}
