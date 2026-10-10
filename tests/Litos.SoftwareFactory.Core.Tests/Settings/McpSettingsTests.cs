using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Settings;

namespace Litos.SoftwareFactory.Core.Tests.Settings;

/// <summary>The MCP servers tab, and what a run snapshots of it (m3-architecture.md §7.1, §7.4).</summary>
public class McpSettingsTests
{
    private static McpServerSettings Stdio(string name = "github") =>
        new() { Name = name, Command = "npx", Args = ["-y", "@modelcontextprotocol/server-github"], SecretVariables = ["GITHUB_TOKEN"] };

    [Fact]
    public void TheDefaults_HaveNoServer_AndAreValid()
    {
        Assert.Empty(new McpSettings().Servers);
        Assert.Empty(new McpSettings().Validate());
        Assert.Empty(new McpSettings { Servers = [Stdio(), new McpServerSettings { Name = "docs", Transport = McpTransport.Http, Url = "https://mcp.example.com/" }] }.Validate());
    }

    [Fact]
    public void ANewServer_IsOn_AndFull()
    {
        var server = Stdio();

        Assert.True(server.Enabled);
        Assert.Equal(McpAccess.Full, server.Permission);
    }

    public static TheoryData<McpServerSettings, string> Invalid => new()
    {
        { new() { Name = "git__hub", Command = "x" }, "is not a usable server name" },
        { new() { Name = "has space", Command = "x" }, "is not a usable server name" },
        { new() { Name = "-dash", Command = "x" }, "is not a usable server name" },
        { new() { Name = "github" }, "github: give the command that starts it." },
        { new() { Name = "docs", Transport = McpTransport.Http, Url = "ftp://x" }, "docs: its address must be an http or https URL." },
        { new() { Name = "github", Command = "x", SecretVariables = ["GITHUB-TOKEN"] }, "\"GITHUB-TOKEN\" is not an environment variable name." },
        { new() { Name = "github", Command = "x", SecretVariables = ["A", "A"] }, "github: A is listed twice." },
        { new() { Name = "github", Command = "x", ToolOverrides = new Dictionary<string, McpAccess> { [" "] = McpAccess.Deny } }, "is not a tool name" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void AServerThatCannotWork_IsRefused_InWords(McpServerSettings server, string expected)
    {
        Assert.Contains(new McpSettings { Servers = [server] }.Validate(), e => e.Contains(expected));
    }

    [Fact]
    public void TwoServersWithOneName_AreRefused_WhateverTheirCase()
    {
        Assert.Contains("There are two MCP servers named \"github\".", new McpSettings { Servers = [Stdio(), Stdio("GitHub")] }.Validate());
    }

    [Fact]
    public void TooManyServers_AreRefused()
    {
        var servers = Enumerable.Range(0, McpSettings.MaxServers + 1).Select(i => Stdio($"s{i}")).ToList();

        Assert.Contains($"At most {McpSettings.MaxServers} MCP servers can be set up.", new McpSettings { Servers = servers }.Validate());
    }

    [Fact]
    public void AToolsAccess_IsItsOverride_OrTheServers()
    {
        var full = Stdio() with { ToolOverrides = new Dictionary<string, McpAccess> { ["delete_repository"] = McpAccess.Deny } };
        var deny = Stdio() with { Permission = McpAccess.Deny, ToolOverrides = new Dictionary<string, McpAccess> { ["search_code"] = McpAccess.Full } };

        Assert.Equal((McpAccess.Full, McpAccess.Deny), (full.AccessTo("create_issue"), full.AccessTo("delete_repository")));
        Assert.Equal((McpAccess.Deny, McpAccess.Full), (deny.AccessTo("create_issue"), deny.AccessTo("search_code")));
    }

    // ---- The run's snapshot ----

    [Fact]
    public void ASnapshot_TakesTheToolSettings_AndOnlyTheServersThatAreOn()
    {
        var mcp = new McpSettings { Servers = [Stdio(), Stdio("off") with { Enabled = false }] };

        var snapshot = RunCapabilities.From(new ToolSettings { ShellTimeoutSeconds = 900 }, WebSearchAccess.AllTurns, mcp, ptc: false);

        Assert.Equal((false, 900, WebSearchAccess.AllTurns), (snapshot.Ptc, snapshot.ShellTimeoutSeconds, snapshot.WebSearch));
        Assert.Equal(["github"], snapshot.McpServers.Select(s => s.Name));
        Assert.Null(snapshot.McpStatus);
    }

    [Fact]
    public void ARework_KeepsTheSnapshot_ButItsServersConnectAfresh()
    {
        var reported = RunCapabilities.From(new ToolSettings(), WebSearchAccess.WorkTurns, new McpSettings { Servers = [Stdio()] }, ptc: true)
            with { McpStatus = [new McpServerReport("github", true, ["create_issue"], null)] };

        var rework = reported.ForRework();

        Assert.Equal(reported.McpServers, rework.McpServers);
        Assert.Equal(WebSearchAccess.WorkTurns, rework.WebSearch);
        Assert.Null(rework.McpStatus);
    }
}
