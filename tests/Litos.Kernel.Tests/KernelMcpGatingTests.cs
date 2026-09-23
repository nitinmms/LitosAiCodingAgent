using Litos.Agent.Tools;
using Litos.Tools.Mcp;
using Litos.Tools.Shell;
using Microsoft.Extensions.Logging.Abstractions;

namespace Litos.Kernel.Tests;

/// <summary>
/// The kernel's MCP permission rule ("Option C"): honor Deny, treat Ask as approved, allow Full.
///
/// Why the kernel needs a rule of its own at all: InvokeBridgedToolAsync routes MCP calls through
/// McpToolProvider.InvokeDirectAsync, which deliberately bypasses McpToolProxy — and McpToolProxy
/// is the ONLY place IToolApprovalGate (and therefore McpAwareApprovalGate) is consulted for an MCP
/// tool. Without an explicit check here, a user's configured Deny would silently stop applying the
/// moment kernel mode was switched on, with nothing in the UI saying so.
///
/// Built-in tools stay ungated inside the kernel by design (§5.1); only MCP is gated, because an
/// MCP server is often remote and holds credentials that local code execution does not otherwise
/// grant — so Deny there is not made redundant by the toggle's own consent.
///
/// These assert against a REAL subprocess through the full bridge path, so they cover the actual
/// wiring rather than a hand-called private method.
/// </summary>
public sealed class KernelMcpGatingTests
{
    private const string ServerName = "testsrv";
    private const string ToolName = "mcp__testsrv__do_thing";

    private static McpConfigStore StoreWith(ToolPermission permission, string? perToolOverrideFor = null, ToolPermission overrideValue = ToolPermission.Deny)
    {
        // A state file path under a fresh temp dir keeps each test's config isolated from the
        // developer's real ~/.litos MCP config.
        var stateFile = Path.Combine(Path.GetTempPath(), "litos-kernel-gating-tests", Guid.NewGuid().ToString("n"), "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        var store = new McpConfigStore(stateFile);
        store.Update(_ => new McpConfig([
            new McpServerDefinition(
                Name: ServerName,
                Transport: McpTransportKind.Stdio,
                Command: "noop",
                Args: null,
                Env: null,
                Url: null,
                Enabled: true,
                DefaultPermission: permission,
                ToolOverrides: perToolOverrideFor is null
                    ? null
                    : new Dictionary<string, ToolPermission> { [perToolOverrideFor] = overrideValue }),
        ]));
        return store;
    }

    private static KernelSession NewSession(McpConfigStore store)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "litos-kernel-gating-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);

        // A provider with no live connections: a Denied call must be refused BEFORE any attempt to
        // reach a server, so these tests never need a real MCP server running. An allowed call
        // falls through to InvokeDirectAsync and fails on the missing connection instead — which is
        // exactly how the tests below tell "refused by policy" apart from "allowed through".
        var provider = new McpToolProvider(store, NullLoggerFactory.Instance, new AlwaysApproveGate());

        // The bridged registry must advertise the MCP tool names under test: ToolWrapperCodeGen
        // emits one script-callable wrapper per registry entry, so a name absent here is simply not
        // a defined function inside the kernel and the eval fails to COMPILE — which would make
        // these tests pass or fail for reasons having nothing to do with permissions.
        var declared = new ToolRegistry([
            new DeclaredOnlyTool(ToolName),
            new DeclaredOnlyTool("mcp__ghost__do_thing"),
        ]);

        return new KernelSession(
            sessionId: "gating",
            workingDirectory: Path.GetTempPath(),
            scratchDirectory: scratch,
            bridgedToolsSource: () => declared,
            mcpToolProvider: provider,
            hardTimeout: TimeSpan.FromSeconds(60));
    }

    /// <summary>The call is refused by policy, naming the denial — never attempted.</summary>
    private static void AssertDeniedByPolicy(string text)
    {
        Assert.Contains("denied", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ToolName, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DenyPermission_IsHonoredInsideTheKernel_NotBypassed()
    {
        await using var session = NewSession(StoreWith(ToolPermission.Deny));

        var result = await session.RunAsync($"await {ToolName}(\"{{}}\")", CancellationToken.None);

        Assert.True(result.IsError);
        AssertDeniedByPolicy(result.Text);
    }

    [Fact]
    public async Task AskPermission_ProceedsWithoutPrompting_SoAnEvalNeverBlocksOnAPanelNobodyIsWatching()
    {
        await using var session = NewSession(StoreWith(ToolPermission.Ask));

        var result = await session.RunAsync($"await {ToolName}(\"{{}}\")", CancellationToken.None);

        // It gets PAST the permission check — the failure that follows is the absent connection,
        // not a refusal. If Ask were being denied, the text would name the denial instead.
        Assert.DoesNotContain("denied by this server's configured permission", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FullPermission_IsAllowedThrough()
    {
        await using var session = NewSession(StoreWith(ToolPermission.Full));

        var result = await session.RunAsync($"await {ToolName}(\"{{}}\")", CancellationToken.None);

        Assert.DoesNotContain("denied by this server's configured permission", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PerToolDenyOverride_BeatsAnOtherwisePermissiveServerDefault()
    {
        // PermissionFor resolves a per-tool override ahead of DefaultPermission; the kernel must
        // use that same resolution rather than reading DefaultPermission directly, or a targeted
        // "deny just this one dangerous tool" would quietly stop working in kernel mode.
        await using var session = NewSession(StoreWith(ToolPermission.Full, perToolOverrideFor: ToolName));

        var result = await session.RunAsync($"await {ToolName}(\"{{}}\")", CancellationToken.None);

        Assert.True(result.IsError);
        AssertDeniedByPolicy(result.Text);
    }

    [Fact]
    public async Task UnknownServer_FallsBackToDeny_MatchingMcpAwareApprovalGate()
    {
        var store = StoreWith(ToolPermission.Full);
        await using var session = NewSession(store);

        var result = await session.RunAsync("await mcp__ghost__do_thing(\"{}\")", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("denied", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class AlwaysApproveGate : IToolApprovalGate
    {
        public Task<ApprovalDecision> RequestAsync(ToolInvocationPreview preview, CancellationToken ct) =>
            Task.FromResult(ApprovalDecision.Approve);
    }

    /// <summary>
    /// Exists only so ToolWrapperCodeGen emits a callable wrapper under this name. An mcp__-prefixed
    /// call never reaches ITool.InvokeAsync — InvokeBridgedToolAsync branches to the MCP path first
    /// — so reaching this body would itself be the bug, and it says so.
    /// </summary>
    private sealed class DeclaredOnlyTool(string name) : ITool
    {
        public string Name => name;
        public string Description => "Declared for wrapper generation in tests; never invoked.";
        public System.Text.Json.JsonElement ParameterSchema =>
            System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" });

        public Task<ToolResult> InvokeAsync(System.Text.Json.JsonElement arguments, CancellationToken ct) =>
            throw new InvalidOperationException(
                $"'{name}' is mcp__-prefixed and must be routed through the MCP path, never ITool.InvokeAsync.");
    }
}
