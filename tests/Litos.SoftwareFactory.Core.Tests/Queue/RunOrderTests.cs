using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Queue;

namespace Litos.SoftwareFactory.Core.Tests.Queue;

/// <summary>The order a project's queued tasks run in (run-order-architecture.md).</summary>
public class RunOrderTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid D = Guid.Parse("00000000-0000-0000-0000-00000000000d");
    private static readonly Guid New = Guid.Parse("00000000-0000-0000-0000-0000000000ff");

    private static List<QueuedTask> Queue(params (Guid Id, double Rank)[] tasks) => tasks.Select(t => new QueuedTask(t.Id, t.Rank)).ToList();

    /// <summary>The queue as it stands once the new ranks are applied, lowest first.</summary>
    private static List<Guid> After(List<QueuedTask> queue, Guid mover, IReadOnlyDictionary<Guid, double> ranks)
    {
        var all = queue.Select(t => (t.ThreadId, Rank: ranks.TryGetValue(t.ThreadId, out var r) ? r : t.Rank)).ToList();
        if (queue.All(t => t.ThreadId != mover))
            all.Add((mover, ranks[mover]));
        return all.OrderBy(t => t.Rank).Select(t => t.ThreadId).ToList();
    }

    // ---- Placing a task ----

    [Fact]
    public void TheFirstTaskInAnEmptyQueue_TakesOneStep()
    {
        var ranks = RunOrder.Place([], New, Placement.End);
        Assert.Equal(RunOrder.Step, ranks[New]);
    }

    /// <summary>With nobody arranging anything, the queue is first in, first out.</summary>
    [Fact]
    public void ATaskDelegatedWithNoPlacement_GoesToTheEnd()
    {
        var queue = Queue((A, 1000), (B, 2000));
        var ranks = RunOrder.Place(queue, New, Placement.End);
        Assert.Equal(3000, ranks[New]);
        Assert.Single(ranks);
    }

    [Fact]
    public void RunFirst_GoesAheadOfEveryTask()
    {
        var queue = Queue((A, 1000), (B, 2000));
        var ranks = RunOrder.Place(queue, New, Placement.First);
        Assert.Equal(0, ranks[New]);
        Assert.Equal([New, A, B], After(queue, New, ranks));
    }

    /// <summary>A move writes one row: the mover's, halfway between its new neighbours.</summary>
    [Fact]
    public void RunAfter_GoesHalfwayToTheNextTask_AndMovesNobodyElse()
    {
        var queue = Queue((A, 1000), (B, 2000), (C, 3000));
        var ranks = RunOrder.Place(queue, New, Placement.After(A));
        Assert.Equal(1500, ranks[New]);
        Assert.Single(ranks);
        Assert.Equal([A, New, B, C], After(queue, New, ranks));
    }

    [Fact]
    public void RunAfterTheLastTask_IsTheEnd()
    {
        var queue = Queue((A, 1000), (B, 2000));
        Assert.Equal(3000, RunOrder.Place(queue, New, Placement.After(B))[New]);
    }

    [Fact]
    public void MovingAQueuedTask_IgnoresItsOwnPlace()
    {
        var queue = Queue((A, 1000), (B, 2000), (C, 3000));
        var ranks = RunOrder.Place(queue, C, Placement.After(A));
        Assert.Equal(1500, ranks[C]);
        Assert.Equal([A, C, B], After(queue, C, ranks));

        // To the front, to the end, and after the task it already follows.
        Assert.Equal([C, A, B], After(queue, C, RunOrder.Place(queue, C, Placement.First)));
        Assert.Equal([B, C, A], After(queue, A, RunOrder.Place(queue, A, Placement.End)));
        Assert.Equal([A, B, C], After(queue, C, RunOrder.Place(queue, C, Placement.After(B))));
    }

    /// <summary>A Member's move: two tasks trade places, and everyone else's stay put.</summary>
    [Fact]
    public void Swapping_TradesTheTwoRanks()
    {
        var queue = Queue((A, 1000), (B, 2000), (C, 3000));
        var ranks = RunOrder.Place(queue, A, Placement.SwapWith(C));
        Assert.Equal(3000, ranks[A]);
        Assert.Equal(1000, ranks[C]);
        Assert.Equal([C, B, A], After(queue, A, ranks));
    }

    [Fact]
    public void Swapping_NeedsBothTasksQueued()
    {
        var queue = Queue((A, 1000), (B, 2000));
        Assert.Throws<ArgumentException>(() => RunOrder.Place(queue, New, Placement.SwapWith(A)));
        Assert.Throws<ArgumentException>(() => RunOrder.Place(queue, A, Placement.SwapWith(New)));
    }

    [Fact]
    public void PlacingAfterATaskNotInTheQueue_IsRefused()
    {
        var queue = Queue((A, 1000));
        Assert.Throws<ArgumentException>(() => RunOrder.Place(queue, New, Placement.After(D)));
        Assert.Throws<ArgumentException>(() => RunOrder.Place(queue, New, new Placement(PlacementKind.After)));
    }

    /// <summary>Halving the same gap again and again would run out of precision: the queue is laid out afresh.</summary>
    [Fact]
    public void WhenTwoNeighboursHaveNoRoomBetweenThem_TheQueueIsRenumbered()
    {
        var queue = Queue((A, 1000), (B, 1000 + RunOrder.MinimumGap), (C, 3000));
        var ranks = RunOrder.Place(queue, New, Placement.After(A));
        Assert.Equal(4, ranks.Count);
        Assert.Equal([A, New, B, C], After(queue, New, ranks));
        Assert.Equal([1000, 2000, 3000, 4000], new[] { A, New, B, C }.Select(id => ranks[id]));
    }

    [Fact]
    public void RepeatedMovesIntoTheSameGap_KeepTheOrderTheyMean()
    {
        var queue = Queue((A, 1000), (B, 2000));
        // Each new task goes straight after A, so the newest is nearest A.
        var placed = new List<Guid>();
        for (var i = 0; i < 80; i++)
        {
            var id = Guid.NewGuid();
            var ranks = RunOrder.Place(queue, id, Placement.After(A));
            foreach (var (thread, rank) in ranks)
            {
                var at = queue.FindIndex(t => t.ThreadId == thread);
                if (at >= 0)
                    queue[at] = new QueuedTask(thread, rank);
                else
                    queue.Add(new QueuedTask(thread, rank));
            }

            queue = queue.OrderBy(t => t.Rank).ToList();
            placed.Insert(0, id);
        }

        Assert.Equal([A, .. placed, B], queue.Select(t => t.ThreadId));
        Assert.Equal(queue.Count, queue.Select(t => t.Rank).Distinct().Count());
    }

    // ---- Arranging the claim ----

    private sealed record Run(string Name, Guid? Project, double? Rank);

    private static readonly Guid P = Guid.NewGuid();
    private static readonly Guid Q = Guid.NewGuid();

    private static List<string> Arrange(params Run[] inArrivalOrder) =>
        RunOrder.Arrange(inArrivalOrder, r => r.Project, r => r.Rank).Select(r => r.Name).ToList();

    [Fact]
    public void WithoutRanksToChangeIt_ArrivalOrderStands()
    {
        Assert.Equal(["p1", "q1", "p2"], Arrange(new("p1", P, 1000), new("q1", Q, 1000), new("p2", P, 2000)));
    }

    [Fact]
    public void WithinAProject_TheLowestRankFillsItsEarliestPlace()
    {
        Assert.Equal(["p2", "p1"], Arrange(new("p1", P, 2000), new("p2", P, 1000)));
    }

    /// <summary>Ranks reorder a project's own runs; its turn among other projects still comes from when its work arrived.</summary>
    [Fact]
    public void AProjectKeepsItsPlacesAmongOtherProjects()
    {
        var order = Arrange(
            new("q-first", Q, 1000),
            new("p-old", P, 3000),
            new("chat", null, null),
            new("q-second", Q, 2000),
            new("p-promoted", P, 1000));

        Assert.Equal(["q-first", "p-promoted", "chat", "q-second", "p-old"], order);
    }

    [Fact]
    public void UnorderedRuns_KeepTheirOwnPlace()
    {
        Assert.Equal(["spec", "p2", "reflection", "p1"],
            Arrange(new("spec", null, null), new("p1", P, 2000), new("reflection", null, null), new("p2", P, 1000)));
    }

    /// <summary>A run without a rank is work already under way (a resume), or queued before ranks existed.</summary>
    [Fact]
    public void ARunWithoutARank_GoesFirstInItsProject_InArrivalOrder()
    {
        Assert.Equal(["resumed", "legacy", "ranked"],
            Arrange(new("ranked", P, 1000), new("resumed", P, null), new("legacy", P, null)));
    }

    [Fact]
    public void EqualRanks_KeepArrivalOrder()
    {
        Assert.Equal(["first", "second"], Arrange(new("first", P, 1000), new("second", P, 1000)));
    }

    [Fact]
    public void AnEmptyQueue_ArrangesToNothing() => Assert.Empty(Arrange());

    // ---- Loops ----

    private static Func<Guid, IEnumerable<Guid>> Links(params (Guid Thread, Guid WaitsFor)[] links) =>
        id => links.Where(l => l.Thread == id).Select(l => l.WaitsFor);

    [Fact]
    public void ATaskCannotWaitForItself() => Assert.Equal([A], RunOrder.FindLoop(A, A, Links()));

    [Fact]
    public void ALinkBackToAWaitingTask_IsALoop()
    {
        // B waits for A; now A would wait for B.
        Assert.Equal([B, A], RunOrder.FindLoop(A, B, Links((B, A))));
    }

    [Fact]
    public void ALongLoop_IsNamedTaskByTask()
    {
        // C waits for B, B waits for A; A waiting for C closes the loop C → B → A.
        Assert.Equal([C, B, A], RunOrder.FindLoop(A, C, Links((C, B), (B, A))));
    }

    [Fact]
    public void AChainOrADiamond_IsNoLoop()
    {
        var links = Links((B, A), (C, A), (D, B), (D, C));
        Assert.Null(RunOrder.FindLoop(D, A, links));
        Assert.Null(RunOrder.FindLoop(C, B, links));
    }

    [Fact]
    public void AnExistingLoopElsewhere_DoesNotHangTheSearch()
    {
        // B and C already wait for each other (they cannot, but the search must still end).
        Assert.Null(RunOrder.FindLoop(A, B, Links((B, C), (C, B))));
    }

    // ---- What a waiting task says ----

    [Theory]
    [InlineData(LifecycleState.Accepted, 44, false, "Waiting for \"Add column\" to be merged (PR #44).")]
    [InlineData(LifecycleState.Accepted, null, false, "Waiting for \"Add column\" to be marked as merged.")]
    [InlineData(LifecycleState.AwaitingHumanTesting, 44, false, "Waiting for \"Add column\" to be tested, accepted and merged (PR #44).")]
    [InlineData(LifecycleState.Running, null, false, "Waiting for \"Add column\" to be finished and merged.")]
    [InlineData(LifecycleState.Queued, null, false, "Waiting for \"Add column\" to be finished and merged.")]
    [InlineData(LifecycleState.Draft, null, false, "Waiting for \"Add column\" to be finished and merged.")]
    [InlineData(LifecycleState.Accepted, 44, true, "\"Add column\"'s pull request (PR #44) was closed without merging. Release this task to run without that work, or cancel it.")]
    public void AWaitingTask_SaysWhatItWaitsFor(LifecycleState state, int? pr, bool closed, string expected) =>
        Assert.Equal(expected, RunOrder.WaitingReason(new OpenBlocker("Add column", state, pr, closed)));
}
