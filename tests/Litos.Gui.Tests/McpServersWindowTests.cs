using Litos.Gui;
using Litos.Tools.Mcp;
using Litos.Tools.Shell;

namespace Litos.Gui.Tests;

public class McpServersWindowTests
{
    [Fact]
    public void BuildDefinition_BlankName_ReturnsNullWithError()
    {
        var definition = McpServersWindow.BuildDefinition(
            name: "  ", isStdio: true, command: "npx", args: null, url: null, enabled: true, ToolPermission.Full, out var error);

        Assert.Null(definition);
        Assert.Equal("A server name is required.", error);
    }

    [Fact]
    public void BuildDefinition_NullName_ReturnsNullWithError()
    {
        var definition = McpServersWindow.BuildDefinition(
            name: null, isStdio: true, command: "npx", args: null, url: null, enabled: true, ToolPermission.Full, out var error);

        Assert.Null(definition);
        Assert.NotNull(error);
    }

    [Fact]
    public void BuildDefinition_Stdio_TrimsNameAndCommandAndSplitsArgs()
    {
        var definition = McpServersWindow.BuildDefinition(
            name: "  filesystem  ",
            isStdio: true,
            command: "  npx  ",
            args: "-y @modelcontextprotocol/server-filesystem /workspace",
            url: null,
            enabled: true,
            ToolPermission.Full,
            out var error);

        Assert.Null(error);
        Assert.NotNull(definition);
        Assert.Equal("filesystem", definition!.Name);
        Assert.Equal(McpTransportKind.Stdio, definition.Transport);
        Assert.Equal("npx", definition.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "/workspace"], definition.Args);
        Assert.Null(definition.Url);
        Assert.True(definition.Enabled);
    }

    [Fact]
    public void BuildDefinition_Stdio_EmptyArgsProducesEmptyList()
    {
        var definition = McpServersWindow.BuildDefinition(
            name: "srv", isStdio: true, command: "npx", args: "", url: null, enabled: true, ToolPermission.Full, out _);

        Assert.NotNull(definition);
        Assert.Empty(definition!.Args!);
    }

    [Fact]
    public void BuildDefinition_Http_SetsUrlAndLeavesCommandArgsNull()
    {
        var definition = McpServersWindow.BuildDefinition(
            name: "remote", isStdio: false, command: null, args: null, url: "  https://example.com/mcp  ",
            enabled: false, ToolPermission.Full, out var error);

        Assert.Null(error);
        Assert.NotNull(definition);
        Assert.Equal(McpTransportKind.Http, definition!.Transport);
        Assert.Equal("https://example.com/mcp", definition.Url);
        Assert.Null(definition.Command);
        Assert.Null(definition.Args);
        Assert.False(definition.Enabled);
    }

    [Theory]
    [InlineData(ToolPermission.Deny)]
    [InlineData(ToolPermission.Ask)]
    [InlineData(ToolPermission.Full)]
    public void BuildDefinition_UsesTheChosenPermission_AndNoOverrides(ToolPermission permission)
    {
        var definition = McpServersWindow.BuildDefinition(
            name: "srv", isStdio: true, command: "npx", args: null, url: null, enabled: true, permission, out _);

        Assert.NotNull(definition);
        Assert.Equal(permission, definition!.DefaultPermission);
        Assert.Null(definition.ToolOverrides);
    }

    [Fact]
    public void PermissionChoices_MatchTheVsCodePanelOrder() =>
        Assert.Equal([ToolPermission.Deny, ToolPermission.Ask, ToolPermission.Full], McpServersWindow.PermissionChoices);

    [Theory]
    [InlineData(ToolPermission.Deny, 0)]
    [InlineData(ToolPermission.Ask, 1)]
    [InlineData(ToolPermission.Full, 2)]
    public void PermissionIndex_AndPermissionAt_RoundTrip(ToolPermission permission, int index)
    {
        Assert.Equal(index, McpServersWindow.PermissionIndex(permission));
        Assert.Equal(permission, McpServersWindow.PermissionAt(index));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void PermissionAt_OutOfRange_FallsBackToFull(int index) =>
        Assert.Equal(ToolPermission.Full, McpServersWindow.PermissionAt(index));

    [Theory]
    [InlineData(McpConnectionStatus.Connected, "Connected")]
    [InlineData(McpConnectionStatus.Connecting, "Connecting…")]
    [InlineData(McpConnectionStatus.Unreachable, "Unreachable")]
    [InlineData(null, "Not started")]
    public void StatusLabel_MapsEachStatus(McpConnectionStatus? status, string expected)
    {
        Assert.Equal(expected, McpServersWindow.StatusLabel(status));
    }

    [Theory]
    [InlineData(0, false, "0 tools ▸")]
    [InlineData(1, false, "1 tool ▸")]
    [InlineData(2, false, "2 tools ▸")]
    [InlineData(5, true, "5 tools ▾")]
    [InlineData(1, true, "1 tool ▾")]
    public void ToolsSummaryLabel_FormatsCountPluralizationAndExpandGlyph(int toolCount, bool expanded, string expected)
    {
        Assert.Equal(expected, McpServersWindow.ToolsSummaryLabel(toolCount, expanded));
    }

    [Theory]
    [InlineData(0, false, "0 prompts ▸")]
    [InlineData(1, false, "1 prompt ▸")]
    [InlineData(2, false, "2 prompts ▸")]
    [InlineData(5, true, "5 prompts ▾")]
    [InlineData(1, true, "1 prompt ▾")]
    public void PromptsSummaryLabel_FormatsCountPluralizationAndExpandGlyph(int promptCount, bool expanded, string expected)
    {
        Assert.Equal(expected, McpServersWindow.PromptsSummaryLabel(promptCount, expanded));
    }
}
