using System.Text;
using System.Text.Json;
using Litos.Agent.Messages;
using Litos.Agent.Tools;
using Litos.Hosting.Approvals;
using Litos.Hosting.Turns;
using Litos.Tools.Mcp;
using Litos.Tools.Shell;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Litos.Hosting.Tests;

public class TurnRequestReaderTests
{
    private static HttpRequest JsonRequest(string json)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return context.Request;
    }

    [Fact]
    public async Task ReadTurnRequestAsync_TextOnly_YieldsOneTextBlockAndNoTurnKind()
    {
        var turn = await TurnsEndpoints.ReadTurnRequestAsync(JsonRequest("""{"input":"hello"}"""), CancellationToken.None);

        Assert.Equal("hello", Assert.IsType<TextBlock>(Assert.Single(turn.Content)).Text);
        Assert.Null(turn.TurnKind);
    }

    [Fact]
    public async Task ReadTurnRequestAsync_WithTurnKind_CarriesItThrough()
    {
        var turn = await TurnsEndpoints.ReadTurnRequestAsync(
            JsonRequest("""{"input":"review this","turnKind":"Review"}"""), CancellationToken.None);

        Assert.Equal("Review", turn.TurnKind);
    }

    /// <summary>The VS Code extension still posts an "attachments" array; the default reader must
    /// tolerate fields it doesn't know rather than reject the request.</summary>
    [Fact]
    public async Task ReadTurnRequestAsync_UnknownFields_AreIgnored()
    {
        var turn = await TurnsEndpoints.ReadTurnRequestAsync(
            JsonRequest("""{"input":"hi","attachments":[]}"""), CancellationToken.None);

        Assert.Equal("hi", Assert.IsType<TextBlock>(Assert.Single(turn.Content)).Text);
    }

    [Fact]
    public async Task ReadTurnRequestAsync_NullBody_IsABadRequest()
    {
        await Assert.ThrowsAsync<BadHttpRequestException>(async () =>
            await TurnsEndpoints.ReadTurnRequestAsync(JsonRequest("null"), CancellationToken.None));
    }
}

public class LoopbackHostTests
{
    [Fact]
    public void FormatHandshake_IsTheSingleLineJsonTheParentParses()
    {
        // hostProcess.ts in the VS Code extension reads exactly this off the first stdout line.
        Assert.Equal("""{"port":51234}""", LoopbackHost.FormatHandshake(51234));
    }

    [Fact]
    public async Task ConfigureLoopback_ThenStart_BindsAnOsAssignedLoopbackPort()
    {
        var builder = WebApplication.CreateBuilder();
        LoopbackHost.ConfigureLoopback(builder);
        await using var app = builder.Build();

        var original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        int port;
        int? portSeenBeforeAnnounce = null;
        var announcedAtCallback = "not called";
        try
        {
            port = await LoopbackHost.StartAndAnnounceAsync(app, p =>
            {
                portSeenBeforeAnnounce = p;
                announcedAtCallback = captured.ToString();
            });
        }
        finally
        {
            Console.SetOut(original);
        }

        try
        {
            var url = new Uri(Assert.Single(app.Urls));
            Assert.Equal("127.0.0.1", url.Host);
            Assert.NotEqual(0, port);
            Assert.Equal(url.Port, port);

            // The callback ran with the real port, and before anything was announced.
            Assert.Equal(port, portSeenBeforeAnnounce);
            Assert.Equal("", announcedAtCallback);

            var firstLine = captured.ToString().Split('\n')[0].Trim();
            Assert.Equal(port, JsonDocument.Parse(firstLine).RootElement.GetProperty("port").GetInt32());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public void ConfigureLoopback_DisablesKestrelsResponseDataRateWatchdog()
    {
        var builder = WebApplication.CreateBuilder();
        LoopbackHost.ConfigureLoopback(builder);
        using var app = builder.Build();

        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>>();

        Assert.Null(options.Value.Limits.MinResponseDataRate);
    }
}

public class HostingServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddLitosAutoApproval_RegistersAGateThatApprovesEverything()
    {
        var services = new ServiceCollection().AddLitosAutoApproval().BuildServiceProvider();

        var gate = services.GetRequiredService<IToolApprovalGate>();
        var decision = await gate.RequestAsync(new ToolInvocationPreview("shell", "run", "echo hi"), CancellationToken.None);

        Assert.IsType<AutoApprovalGate>(gate);
        Assert.Equal(ApprovalDecision.Approve, decision);
    }

    [Fact]
    public void AddLitosAutoApproval_RegistersTheStoreAndRelayTheTurnEndpointsNeed()
    {
        var services = new ServiceCollection().AddLitosAutoApproval().BuildServiceProvider();

        Assert.NotNull(services.GetRequiredService<PendingApprovalStore>());
        Assert.NotNull(services.GetRequiredService<PendingApprovalRelay>());
    }

    [Fact]
    public void AddLitosMcp_NoServersConfigured_RegistersGateProviderSourceAndRefreshService()
    {
        var stateFile = Path.Combine(Path.GetTempPath(), $"litos-hosting-mcp-{Guid.NewGuid():n}.json");
        try
        {
            var collection = new ServiceCollection();
            collection.AddLogging();
            var configStore = new McpConfigStore(stateFile);

            var returned = collection.AddLitosMcp(new LitosMcpOptions { ConfigStore = configStore });
            using var services = collection.BuildServiceProvider();

            Assert.Same(configStore, services.GetRequiredService<McpConfigStore>());
            Assert.Same(returned, services.GetRequiredService<McpToolProvider>());
            Assert.IsType<McpAwareApprovalGate>(services.GetRequiredService<IToolApprovalGate>());
            Assert.IsType<McpToolSource>(services.GetRequiredService<IToolSource>());
            Assert.NotNull(services.GetRequiredService<PendingApprovalStore>());
            Assert.NotNull(services.GetRequiredService<PendingApprovalRelay>());
            Assert.Contains(services.GetServices<IHostedService>(), s => s is McpToolRefreshService);
        }
        finally
        {
            if (File.Exists(stateFile))
                File.Delete(stateFile);
        }
    }

    [Fact]
    public void LitosMcpOptions_DefaultsMatchWhatVsCodeHostAlwaysUsed()
    {
        var options = new LitosMcpOptions();

        Assert.Null(options.ConfigStore);
        Assert.Equal(TimeSpan.FromSeconds(30), options.HandshakeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), options.ReconcilePollInterval);
    }

    /// <summary>The relay registered by AddLitosAutoApproval is started: an approval raised inside
    /// a turn's ChannelContext reaches that session's subscriber.</summary>
    [Fact]
    public async Task AddLitosAutoApproval_RelayIsStarted_AndRoutesBySession()
    {
        var services = new ServiceCollection().AddLitosAutoApproval().BuildServiceProvider();
        var store = services.GetRequiredService<PendingApprovalStore>();
        var relay = services.GetRequiredService<PendingApprovalRelay>();
        var requested = new List<PendingApprovalRequestedWireEvent>();
        using var subscription = relay.Subscribe("session-a", requested.Add, _ => { });

        Task<ApprovalDecision>? pending = null;
        await ChannelContext.RunAsAsync(Litos.Agent.Session.SessionOwner.Local, "session-a", () =>
        {
            pending = store.Add(new ToolInvocationPreview("mcp__s__t", "summary", null));
            return Task.CompletedTask;
        });

        var evt = Assert.Single(requested);
        Assert.Equal("mcp__s__t", evt.ToolName);
        store.Resolve(evt.ApprovalId, ApprovalDecision.Deny);
        Assert.Equal(ApprovalDecision.Deny, await pending!);
    }
}
