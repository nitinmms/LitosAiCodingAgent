using Litos.SoftwareFactory.Core.Lifecycle;

namespace Litos.SoftwareFactory.Core.Queue;

/// <summary>Where a task is to go in its project's queue.</summary>
public enum PlacementKind
{
    /// <summary>Ahead of every queued task.</summary>
    First,

    /// <summary>Straight after another queued task.</summary>
    After,

    /// <summary>Behind every queued task: first in, first out.</summary>
    End,

    /// <summary>Into another queued task's place, which takes the mover's: how a Member moves,
    /// so nobody else's task changes place.</summary>
    SwapWith,
}

public sealed record Placement(PlacementKind Kind, Guid? OtherThreadId = null)
{
    public static readonly Placement First = new(PlacementKind.First);
    public static readonly Placement End = new(PlacementKind.End);

    public static Placement After(Guid threadId) => new(PlacementKind.After, threadId);

    public static Placement SwapWith(Guid threadId) => new(PlacementKind.SwapWith, threadId);
}

/// <summary>A task in its project's queue, in queue order.</summary>
public sealed record QueuedTask(Guid ThreadId, double Rank);

/// <summary>What a task waits for: another task of its project, until that task's pull request is merged.</summary>
public sealed record OpenBlocker(string Title, LifecycleState State, int? PullRequestNumber, bool PullRequestClosed);

/// <summary>
/// The order a project's queued tasks run in (docs/software-factory/run-order-architecture.md).
/// Each queued task has a rank; the queue runs lowest first. A task delegated without a placement
/// goes to the end, so with nobody arranging anything the queue is first in, first out. Ranks are
/// sparse, so a move writes one row: the mover's, halfway between its new neighbours.
/// </summary>
public static class RunOrder
{
    /// <summary>The gap between neighbours a task joining the end or the front leaves.</summary>
    public const double Step = 1000;

    /// <summary>Below this gap, halving it again would run out of precision: the queue is renumbered.</summary>
    public const double MinimumGap = 1e-6;

    /// <summary>
    /// The new ranks a placement gives: the mover's, the other task's for a swap, or every queued
    /// task's when two neighbours have run out of room between them. <paramref name="queue"/> is the
    /// project's queue in order, with or without the mover in it (a draft being delegated is not).
    /// </summary>
    public static IReadOnlyDictionary<Guid, double> Place(IReadOnlyList<QueuedTask> queue, Guid mover, Placement placement)
    {
        if (placement.Kind == PlacementKind.SwapWith)
        {
            var moving = queue.FirstOrDefault(t => t.ThreadId == mover)
                ?? throw new ArgumentException("Only a queued task can swap places.", nameof(mover));
            var other = Find(queue, placement.OtherThreadId);
            return new Dictionary<Guid, double> { [mover] = other.Rank, [other.ThreadId] = moving.Rank };
        }

        var others = queue.Where(t => t.ThreadId != mover).ToList();
        var index = placement.Kind switch
        {
            PlacementKind.First => 0,
            PlacementKind.End => others.Count,
            PlacementKind.After => others.IndexOf(Find(others, placement.OtherThreadId)) + 1,
            _ => throw new ArgumentOutOfRangeException(nameof(placement), placement.Kind, "An unknown placement."),
        };

        var previous = index > 0 ? others[index - 1].Rank : (double?)null;
        var next = index < others.Count ? others[index].Rank : (double?)null;
        if (previous is { } p && next is { } n)
        {
            if (n - p >= MinimumGap * 2)
                return new Dictionary<Guid, double> { [mover] = p + (n - p) / 2 };

            // No room left between them: lay the whole queue out again, the mover in its place.
            var order = others.Select(t => t.ThreadId).ToList();
            order.Insert(index, mover);
            return order.Select((id, i) => (id, rank: (i + 1) * Step)).ToDictionary(x => x.id, x => x.rank);
        }

        return new Dictionary<Guid, double>
        {
            [mover] = previous is { } last ? last + Step : next is { } first ? first - Step : Step,
        };
    }

    /// <summary>
    /// The order the coordinator considers queued runs in. A project keeps the places its ordered
    /// runs hold in <paramref name="inArrivalOrder"/>, so its turn among other projects is still set
    /// by when its work arrived; the ranks decide which of its runs fills each of those places.
    /// <paramref name="orderedWithin"/> gives an ordered run's project, and null for a run that keeps
    /// its own place (a chat, a reflection, a spec run). A run without a rank goes first: it is work
    /// already under way, or was queued before ranks existed. Ties keep their arrival order.
    /// </summary>
    public static IReadOnlyList<T> Arrange<T>(IReadOnlyList<T> inArrivalOrder, Func<T, Guid?> orderedWithin, Func<T, double?> rank)
    {
        var byProject = inArrivalOrder
            .Select((item, arrival) => (item, arrival, project: orderedWithin(item)))
            .Where(x => x.project is not null)
            .GroupBy(x => x.project!.Value)
            .ToDictionary(
                g => g.Key,
                g => new Queue<T>(g.OrderBy(x => rank(x.item) is null ? 0 : 1).ThenBy(x => rank(x.item) ?? 0).ThenBy(x => x.arrival).Select(x => x.item)));

        return inArrivalOrder.Select(item => orderedWithin(item) is { } project ? byProject[project].Dequeue() : item).ToList();
    }

    /// <summary>
    /// The loop a new link "<paramref name="thread"/> waits for <paramref name="blocker"/>" would
    /// close, as the tasks from the blocker back round to the thread; null when there is none.
    /// <paramref name="waitsFor"/> gives each task's open links.
    /// </summary>
    public static IReadOnlyList<Guid>? FindLoop(Guid thread, Guid blocker, Func<Guid, IEnumerable<Guid>> waitsFor)
    {
        if (thread == blocker)
            return [thread];

        var seen = new HashSet<Guid>();
        var path = new List<Guid>();

        bool Reaches(Guid current)
        {
            path.Add(current);
            if (current == thread)
                return true;
            if (seen.Add(current))
            {
                foreach (var next in waitsFor(current))
                {
                    if (Reaches(next))
                        return true;
                }
            }

            path.RemoveAt(path.Count - 1);
            return false;
        }

        return Reaches(blocker) ? path : null;
    }

    /// <summary>What a queued task says while it waits for <paramref name="blocker"/>.</summary>
    public static string WaitingReason(OpenBlocker blocker)
    {
        var title = $"\"{blocker.Title}\"";
        var pr = blocker.PullRequestNumber is { } number ? $"PR #{number}" : null;
        if (blocker.PullRequestClosed)
            return $"{title}'s pull request{(pr is null ? "" : $" ({pr})")} was closed without merging. Release this task to run without that work, or cancel it.";

        return blocker.State switch
        {
            LifecycleState.Accepted when pr is null => $"Waiting for {title} to be marked as merged.",
            LifecycleState.Accepted => $"Waiting for {title} to be merged ({pr}).",
            LifecycleState.AwaitingHumanTesting when pr is not null => $"Waiting for {title} to be tested, accepted and merged ({pr}).",
            _ => $"Waiting for {title} to be finished and merged.",
        };
    }

    private static QueuedTask Find(IReadOnlyList<QueuedTask> queue, Guid? threadId) =>
        queue.FirstOrDefault(t => t.ThreadId == threadId)
        ?? throw new ArgumentException("The task to place it against is not in the queue.", nameof(threadId));
}
