using Litos.Tools.Shell;

namespace Litos.Api.Tests;

public class PendingApprovalStoreTests
{
    private static readonly ToolInvocationPreview Preview = new("shell", "Run shell command", "echo hi");

    [Fact]
    public async Task Add_ReturnsUnresolvedTask_UntilResolved()
    {
        var store = new PendingApprovalStore();

        var pending = store.Add(Preview);

        Assert.False(pending.IsCompleted);
        var (id, _) = SingleEntry(store);
        Assert.True(store.Resolve(id, ApprovalDecision.Approve));
        var decision = await pending;
        Assert.Equal(ApprovalDecision.Approve, decision);
    }

    [Fact]
    public async Task Resolve_UnknownId_ReturnsFalse_AndDoesNotThrow()
    {
        var store = new PendingApprovalStore();

        Assert.False(store.Resolve(Guid.NewGuid(), ApprovalDecision.Deny));
        await Task.CompletedTask;
    }

    [Fact]
    public void Resolve_RemovesFromList()
    {
        var store = new PendingApprovalStore();
        store.Add(Preview);
        var (id, _) = SingleEntry(store);

        store.Resolve(id, ApprovalDecision.Deny);

        Assert.Empty(store.List());
    }

    [Fact]
    public void Add_RaisesAddedEvent()
    {
        var store = new PendingApprovalStore();
        PendingApproval? raised = null;
        store.Added += a => raised = a;

        store.Add(Preview);

        Assert.NotNull(raised);
        Assert.Equal(Preview, raised!.Preview);
    }

    [Fact]
    public void Resolve_RaisesResolvedEvent_WithMatchingId()
    {
        var store = new PendingApprovalStore();
        store.Add(Preview);
        var (id, _) = SingleEntry(store);
        Guid? raised = null;
        store.Resolved += resolvedId => raised = resolvedId;

        store.Resolve(id, ApprovalDecision.Approve);

        Assert.Equal(id, raised);
    }

    [Fact]
    public void Resolve_CalledTwice_SecondCallReturnsFalse()
    {
        var store = new PendingApprovalStore();
        store.Add(Preview);
        var (id, _) = SingleEntry(store);

        Assert.True(store.Resolve(id, ApprovalDecision.Approve));
        Assert.False(store.Resolve(id, ApprovalDecision.Approve));
    }

    [Fact]
    public async Task Add_NobodyResolves_AutoDeniesAfterTimeout()
    {
        // Regression test for the incident this timeout exists to prevent: a Telegram write_file
        // call sat with the turn "active" for 25+ minutes because nothing ever bounded the wait —
        // the approval must resolve on its own once the timeout elapses, not hang forever.
        var store = new PendingApprovalStore(timeout: TimeSpan.FromMilliseconds(20));

        var pending = store.Add(Preview);
        var decision = await pending;

        Assert.Equal(ApprovalDecision.Deny, decision);
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Add_NobodyResolves_RaisesTimedOutAndResolvedEvents()
    {
        var store = new PendingApprovalStore(timeout: TimeSpan.FromMilliseconds(20));
        Guid? timedOutId = null;
        Guid? resolvedId = null;
        store.TimedOut += id => timedOutId = id;
        store.Resolved += id => resolvedId = id;

        var (approval, task) = store.AddPending(Preview);
        await task;

        Assert.Equal(approval.Id, timedOutId);
        Assert.Equal(approval.Id, resolvedId);
    }

    [Fact]
    public async Task Resolve_BeforeTimeoutElapses_PreventsTheTimeoutFromFiring()
    {
        var store = new PendingApprovalStore(timeout: TimeSpan.FromMilliseconds(50));
        var timedOut = false;
        store.TimedOut += _ => timedOut = true;

        var (approval, task) = store.AddPending(Preview);
        store.Resolve(approval.Id, ApprovalDecision.Approve);

        Assert.Equal(ApprovalDecision.Approve, await task);
        await Task.Delay(100); // let the timeout's own Task.Delay elapse, to prove it's a no-op now
        Assert.False(timedOut);
    }

    private static (Guid Id, PendingApproval Approval) SingleEntry(PendingApprovalStore store)
    {
        var approval = Assert.Single(store.List());
        return (approval.Id, approval);
    }
}
