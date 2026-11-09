import type { BlockerLink, QueuePlacement, RunOrderEntry, Thread } from '../api/types';

/** A move as the host takes it: where to, and the task it is placed against. */
export interface Move {
  placement: QueuePlacement;
  otherThreadId?: string;
}

/**
 * Where ↑ or ↓ takes a task (run-order-architecture.md). An Admin moves it one place; a Member
 * swaps it with their own nearest task that way, so nobody else's task changes place. Null when
 * there is nowhere to go.
 */
export function stepMove(queue: Thread[], threadId: string, direction: -1 | 1, userId: string, isAdmin: boolean): Move | null {
  const index = queue.findIndex((t) => t.id === threadId);
  if (index < 0) return null;
  if (isAdmin) {
    if (direction < 0) {
      if (index === 0) return null;
      return index === 1 ? { placement: 'first' } : { placement: 'after', otherThreadId: queue[index - 2]!.id };
    }
    if (index === queue.length - 1) return null;
    return { placement: 'after', otherThreadId: queue[index + 1]!.id };
  }

  if (queue[index]!.ownerId !== userId) return null;
  const mine = queue.filter((t) => t.ownerId === userId);
  const other = mine[mine.findIndex((t) => t.id === threadId) + direction];
  return other ? { placement: 'swap', otherThreadId: other.id } : null;
}

/** Where dropping a dragged task in front of the task at `index` takes it; null for its own place. */
export function dropMove(queue: Thread[], draggedId: string, index: number): Move | null {
  const others = queue.filter((t) => t.id !== draggedId);
  const target = queue[index];
  if (!target || target.id === draggedId) {
    // Dropped past the end: after the last of the others.
    if (index >= queue.length && others.length) return { placement: 'end' };
    return null;
  }
  const at = others.findIndex((t) => t.id === target.id);
  if (at < 0) return null;
  // Where it is already: in front of the task after it.
  const own = queue.findIndex((t) => t.id === draggedId);
  if (own === index - 1) return null;
  return at === 0 ? { placement: 'first' } : { placement: 'after', otherThreadId: others[at - 1]!.id };
}

/** Whether the person may move a task, or change what it waits for: its owner or an Admin. */
export const canArrange = (thread: Thread, userId: string, isAdmin: boolean) => isAdmin || thread.ownerId === userId;

/** Whether a task has not started, so it can still be made to wait: a draft, or queued with no branch. */
export const notStarted = (thread: Thread) => thread.state === 'Draft' || (thread.state === 'Queued' && !thread.branch);

/** The tasks of a project a task may wait for: live, unmerged, its pull request not closed, not itself. */
export function blockerCandidates(threads: Thread[], projectId: string, except: string | null, linked: string[] = []): Thread[] {
  return threads.filter(
    (t) =>
      t.projectId === projectId &&
      t.id !== except &&
      !linked.includes(t.id) &&
      t.state !== 'Cancelled' &&
      !t.pullRequestMergedAt &&
      !t.pullRequestClosedAt,
  );
}

/** The project's queue in run order, from the thread list: what New thread offers as "Run after". */
export function queueOf(threads: Thread[], projectId: string): Thread[] {
  return threads
    .filter((t) => t.projectId === projectId && t.state === 'Queued' && t.stage !== 'Spec' && t.queueRank !== undefined)
    .sort((a, b) => (a.queueRank ?? -Infinity) - (b.queueRank ?? -Infinity));
}

/** Where a task a link waits for stands, in a few words. */
export function blockerStatus(link: BlockerLink): string {
  const pr = link.pullRequestNumber ? `PR #${link.pullRequestNumber}` : null;
  if (link.pullRequestMergedAt) return 'merged';
  if (link.pullRequestClosedAt) return `${pr ?? 'its pull request'} closed without merging`;
  switch (link.state) {
    case 'Accepted':
      return pr ? `accepted, ${pr} not merged yet` : 'accepted, not marked merged yet';
    case 'AwaitingHumanTesting':
      return pr ? `${pr} ready for testing` : 'ready for testing';
    case 'Running':
      return 'agent working';
    case 'Queued':
      return 'queued';
    case 'Draft':
      return 'not delegated yet';
    case 'Cancelled':
      return 'cancelled';
    default:
      return 'waiting on a person';
  }
}

/** Whole days a link has been open at `now`. */
export const daysWaited = (since: string, now: Date): number => Math.max(0, Math.floor((now.getTime() - new Date(since).getTime()) / 86_400_000));

/** From the number of days a link has been open, the factory reminds the blocking task's owner. */
export const LONG_WAIT_DAYS = 2;

/** Each queued task's place, 1 first, by thread id: what a Board card shows. */
export function placesOf(queue: RunOrderEntry[]): Record<string, number> {
  return Object.fromEntries(queue.map((e, i) => [e.thread.id, i + 1]));
}
