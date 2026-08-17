using Litos.Console.Terminal;
using Litos.Tools.Mcp;
using Litos.Tools.Shell;
using Microsoft.Extensions.Logging;

namespace Litos.Console.Tests;

/// <summary>
/// Tests for McpServersDialog's pure, Terminal.Gui-free logic (BuildDefinition, BuildRows,
/// StatusLabel, ToolsSummaryLabel, PromptsSummaryLabel) — mirrors Litos.Gui's
/// McpServersWindowTests exactly, since both dialogs share the same underlying BuildDefinition
/// shape and status/count label conventions.
/// </summary>
public class McpServersDialogTests
{
    private static readonly ILoggerFactory NoopLoggerFactory = LoggerFactory.Create(_ => { });

    private static McpServerDefinition StdioDefinition(string name, bool enabled = true) => new(
        Name: name, Transport: McpTransportKind.Stdio, Command: "npx", Args: ["-y", "server"],
        Env: null, Url: null, Enabled: enabled, DefaultPermission: ToolPermission.Full, ToolOverrides: null);

    [Fact]
    public void BuildDefinition_BlankName_ReturnsNullWithError()
    {
        var definition = McpServersDialog.BuildDefinition(
            name: "  ", isStdio: true, command: "npx", args: null, url: null, enabled: true, out var error);

        Assert.Null(definition);
        Assert.Equal("A server name is required.", error);
    }

    [Fact]
    public void BuildDefinition_Stdio_TrimsNameAndCommandAndSplitsArgs()
    {
        var definition = McpServersDialog.BuildDefinition(
            name: "  filesystem  ", isStdio: true, command: "  npx  ",
            args: "-y @modelcontextprotocol/server-filesystem /workspace", url: null, enabled: true, out var error);

        Assert.Null(error);
        Assert.NotNull(definition);
        Assert.Equal("filesystem", definition!.Name);
        Assert.Equal(McpTransportKind.Stdio, definition.Transport);
        Assert.Equal("npx", definition.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "/workspace"], definition.Args);
        Assert.Null(definition.Url);
    }

    [Fact]
    public void BuildDefinition_Http_SetsUrlAndLeavesCommandArgsNull()
    {
        var definition = McpServersDialog.BuildDefinition(
            name: "remote", isStdio: false, command: null, args: null, url: "  https://example.com/mcp  ",
            enabled: false, out var error);

        Assert.Null(error);
        Assert.NotNull(definition);
        Assert.Equal(McpTransportKind.Http, definition!.Transport);
        Assert.Equal("https://example.com/mcp", definition.Url);
        Assert.Null(definition.Command);
        Assert.Null(definition.Args);
        Assert.False(definition.Enabled);
    }

    [Fact]
    public void BuildDefinition_AlwaysSetsFullPermissionAndNoOverrides()
    {
        var definition = McpServersDialog.BuildDefinition(
            name: "srv", isStdio: true, command: "npx", args: null, url: null, enabled: true, out _);

        Assert.NotNull(definition);
        Assert.Equal(ToolPermission.Full, definition!.DefaultPermission);
        Assert.Null(definition.ToolOverrides);
    }

    [Theory]
    [InlineData(McpConnectionStatus.Connected, "Connected")]
    [InlineData(McpConnectionStatus.Connecting, "Connecting…")]
    [InlineData(McpConnectionStatus.Unreachable, "Unreachable")]
    [InlineData(McpConnectionStatus.Failed, "Failed")]
    [InlineData(null, "Not started")]
    public void StatusLabel_MapsEachStatus(McpConnectionStatus? status, string expected)
    {
        Assert.Equal(expected, McpServersDialog.StatusLabel(status));
    }

    [Theory]
    [InlineData(0, "0 tools")]
    [InlineData(1, "1 tool")]
    [InlineData(2, "2 tools")]
    public void ToolsSummaryLabel_FormatsPluralization(int count, string expected)
    {
        Assert.Equal(expected, McpServersDialog.ToolsSummaryLabel(count));
    }

    [Theory]
    [InlineData(0, "0 prompts")]
    [InlineData(1, "1 prompt")]
    [InlineData(2, "2 prompts")]
    public void PromptsSummaryLabel_FormatsPluralization(int count, string expected)
    {
        Assert.Equal(expected, McpServersDialog.PromptsSummaryLabel(count));
    }

    [Fact]
    public void BuildRows_NoServers_ReturnsSinglePlaceholderRow()
    {
        var rows = McpServersDialog.BuildRows(McpConfig.Empty, [], new HashSet<string>());

        var row = Assert.Single(rows);
        Assert.False(row.IsHeader);
        Assert.Contains("No MCP servers configured", row.Text);
    }

    [Fact]
    public void BuildRows_OneServer_NoConnectionYet_ShowsNotStartedHeaderRow()
    {
        var config = new McpConfig([StdioDefinition("filesystem")]);

        var rows = McpServersDialog.BuildRows(config, [], new HashSet<string>());

        var header = Assert.Single(rows);
        Assert.True(header.IsHeader);
        Assert.Equal("filesystem", header.ServerName);
        Assert.Contains("Not started", header.Text);
        Assert.Contains("Enabled", header.Text);
    }

    [Fact]
    public void BuildRows_DisabledServer_ShowsDisabledInHeaderRow()
    {
        var config = new McpConfig([StdioDefinition("filesystem", enabled: false)]);

        var rows = McpServersDialog.BuildRows(config, [], new HashSet<string>());

        Assert.Contains("Disabled", rows[0].Text);
    }

    [Fact]
    public void BuildRows_CollapsedServer_ShowsOnlyHeaderRow_EvenWithConnection()
    {
        var definition = StdioDefinition("filesystem");
        var config = new McpConfig([definition]);
        var connection = new McpServerConnection(definition, NoopLoggerFactory);

        var rows = McpServersDialog.BuildRows(config, [connection], new HashSet<string>());

        var header = Assert.Single(rows);
        Assert.True(header.IsHeader);
        Assert.Contains("Connecting…", header.Text);
    }

    [Fact]
    public void BuildRows_ExpandedServer_WithNoToolsYet_StillJustShowsHeader()
    {
        var definition = StdioDefinition("filesystem");
        var config = new McpConfig([definition]);
        var connection = new McpServerConnection(definition, NoopLoggerFactory);

        // A freshly-constructed (never-connected) McpServerConnection has empty Tools/Prompts —
        // expanding it produces no extra rows since there's nothing yet to list.
        var rows = McpServersDialog.BuildRows(config, [connection], new HashSet<string> { "filesystem" });

        var header = Assert.Single(rows);
        Assert.True(header.IsHeader);
        Assert.StartsWith("▾", header.Text);
    }

    [Fact]
    public void BuildRows_ExpandedFlag_ChangesHeaderGlyph()
    {
        var config = new McpConfig([StdioDefinition("filesystem")]);

        var collapsedRows = McpServersDialog.BuildRows(config, [], new HashSet<string>());
        var expandedRows = McpServersDialog.BuildRows(config, [], new HashSet<string> { "filesystem" });

        Assert.StartsWith("▸", collapsedRows[0].Text);
        Assert.StartsWith("▾", expandedRows[0].Text);
    }

    [Fact]
    public void BuildRows_MultipleServers_OneHeaderRowEach_WhenAllCollapsed()
    {
        var config = new McpConfig([StdioDefinition("alpha"), StdioDefinition("beta")]);

        var rows = McpServersDialog.BuildRows(config, [], new HashSet<string>());

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsHeader));
        Assert.Equal(["alpha", "beta"], rows.Select(r => r.ServerName));
    }
}
