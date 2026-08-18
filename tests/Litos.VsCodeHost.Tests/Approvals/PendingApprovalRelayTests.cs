using Litos.Agent.Session;
using Litos.Tools.Shell;
using Litos.VsCodeHost.Approvals;

namespace Litos.VsCodeHost.Tests.Approvals;

public class PendingApprovalRelayTests
{
    [Fact]
    public async Task Added_WithinSessionScope_NotifiesThatSessionsSubscriberOnly()
    {
        var store = new PendingApprovalStore(TimeSpan.FromMinutes(10));
        var relay = new PendingApprovalRelay(store).Start();

        PendingApprovalRequestedWireEvent? receivedBySessionA = null;
        PendingApprovalRequestedWireEvent? receivedBySessionB = null;
        using var subA = relay.Subscribe("session-a", evt => receivedBySessionA = evt, _ => { });
        using var subB = relay.Subscribe("session-b", evt => receivedBySessionB = evt, _ => { });

        await ChannelContext.RunAsAsync(SessionOwner.Local, "session-a", () =>
        {
            store.Add(new ToolInvocationPreview("mcp__server__tool", "call a tool", null));
            return Task.CompletedTask;
        });

        Assert.NotNull(receivedBySessionA);
        Assert.Equal("mcp__server__tool", receivedBySessionA!.ToolName);
        Assert.Null(receivedBySessionB);
    }

    [Fact]
    public void Added_OutsideAnySessionScope_NotifiesNoOne()
    {
        var store = new PendingApprovalStore(TimeSpan.FromMinutes(10));
        var relay = new PendingApprovalRelay(store).Start();

        var received = false;
        using var sub = relay.Subscribe("session-a", _ => received = true, _ => { });

        // Added fires synchronously off Add itself, so no await/delay is needed to observe it.
        store.Add(new ToolInvocationPreview("mcp__server__tool", "call a tool", null));

        Assert.False(received);
    }

    [Fact]
    public async Task Resolved_RoutesToTheSameSessionThatSawTheRequest()
    {
        var store = new PendingApprovalStore(TimeSpan.FromMinutes(10));
        var relay = new PendingApprovalRelay(store).Start();

        Guid? requestedId = null;
        Guid? resolvedId = null;
        using var sub = relay.Subscribe("session-a", evt => requestedId = evt.ApprovalId, evt => resolvedId = evt.ApprovalId);

        await ChannelContext.RunAsAsync(SessionOwner.Local, "session-a", () =>
        {
            var (approval, _) = store.AddPending(new ToolInvocationPreview("mcp__server__tool", "call a tool", null));
            requestedId = approval.Id; // AddPending's own return value, independent of the relay's own callback.
            store.Resolve(approval.Id, ApprovalDecision.Approve);
            return Task.CompletedTask;
        });

        Assert.NotNull(requestedId);
        Assert.Equal(requestedId, resolvedId);
    }

    [Fact]
    public async Task Unsubscribe_StopsFurtherNotificationsForThatSession()
    {
        var store = new PendingApprovalStore(TimeSpan.FromMinutes(10));
        var relay = new PendingApprovalRelay(store).Start();

        var callCount = 0;
        var sub = relay.Subscribe("session-a", _ => callCount++, _ => { });
        sub.Dispose();

        await ChannelContext.RunAsAsync(SessionOwner.Local, "session-a", () =>
        {
            store.Add(new ToolInvocationPreview("mcp__server__tool", "call a tool", null));
            return Task.CompletedTask;
        });

        Assert.Equal(0, callCount);
    }
}
