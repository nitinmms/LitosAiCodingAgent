import { useEffect, useState } from 'react';
import type { FactoryApi } from '../api/client';
import type { BlockerRelease, Thread, ThreadLinks } from '../api/types';
import { blockerCandidates, canArrange, notStarted } from '../domain/runOrder';
import { Links } from './RunOrderPanel';

const RELEASED: Record<BlockerRelease, string> = {
  Merged: 'merged',
  Manual: 'released by a person',
  BlockerCancelled: 'that task was cancelled',
  ThreadCancelled: 'this task was cancelled',
};

/**
 * A task's "Blocked by" links on its page (run-order-architecture.md): what it waits for, with
 * Release now; what waits for it; and, once accepted, Mark merged for work the factory cannot see
 * merged. Read again whenever the task changes.
 */
export function TaskLinks({
  api,
  thread,
  threads,
  userId,
  isAdmin,
  now = new Date(),
  onChanged,
  onNotice,
}: {
  api: FactoryApi;
  thread: Thread;
  /** What "Blocked by" offers, from the task's project. */
  threads: Thread[];
  userId: string;
  isAdmin: boolean;
  now?: Date;
  onChanged: () => void;
  onNotice: (text: string) => void;
}) {
  const [links, setLinks] = useState<ThreadLinks | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let current = true;
    api.threadLinks(thread.id).then(
      (read) => current && setLinks(read),
      () => current && setLinks({ waitsFor: [], waitedOnBy: [] }),
    );
    return () => {
      current = false;
    };
  }, [api, thread.id, thread.revision]);

  const act = async (action: () => Promise<unknown>) => {
    if (busy) return;
    setBusy(true);
    try {
      await action();
      setLinks(await api.threadLinks(thread.id));
      onChanged();
    } catch (failure: unknown) {
      onNotice(failure instanceof Error ? failure.message : 'That could not be done.');
    } finally {
      setBusy(false);
    }
  };

  if (!links) return null;
  const open = links.waitsFor.filter((l) => !l.releasedAt);
  const released = links.waitsFor.filter((l) => l.releasedAt);
  const mine = canArrange(thread, userId, isAdmin);
  const candidates = mine && notStarted(thread) ? blockerCandidates(threads, thread.projectId, thread.id, open.map((l) => l.threadId)) : [];
  const unmerged = thread.state === 'Accepted' && !thread.pullRequestMergedAt;
  if (!open.length && !released.length && !links.waitedOnBy.length && !candidates.length && !unmerged) return null;

  return (
    <section className="box" aria-label="Blocked by">
      <h3>Blocked by</h3>
      {open.length ? (
        <Links links={open} now={now} canRelease={mine} busy={busy} onRelease={(link) => void act(() => api.releaseBlocker(thread.id, link.threadId))} />
      ) : (
        <p className="small muted">It waits for no other task.</p>
      )}
      {released.length ? (
        <ul className="small muted released" aria-label="No longer waits for">
          {released.map((l) => (
            <li key={`${l.threadId}-${l.releasedAt}`}>
              No longer waits for {l.title}: {l.releaseReason ? RELEASED[l.releaseReason] : 'released'}.
            </li>
          ))}
        </ul>
      ) : null}
      {candidates.length ? (
        <select
          className="add-blocker"
          aria-label="Make this task wait for"
          value=""
          disabled={busy}
          onChange={(e) => {
            const blocker = e.target.value;
            if (blocker) void act(() => api.addBlocker(thread.id, blocker));
          }}
        >
          <option value="">+ Blocked by…</option>
          {candidates.map((c) => (
            <option key={c.id} value={c.id}>
              {c.title}
            </option>
          ))}
        </select>
      ) : null}
      {links.waitedOnBy.length ? (
        <p className="small">
          {links.waitedOnBy.length === 1 ? '1 task waits' : `${links.waitedOnBy.length} tasks wait`} for this one to be merged:{' '}
          {links.waitedOnBy.map((l) => l.title).join(', ')}.
        </p>
      ) : null}
      {unmerged ? (
        <div className="btn-row">
          <button className="btn small" disabled={busy} onClick={() => void act(() => api.markMerged(thread.id))}>
            Mark merged
          </button>
          <span className="small muted">
            {thread.pullRequestNumber
              ? `The factory sees PR #${thread.pullRequestNumber} merged by itself. Use this if the work was merged another way.`
              : 'This task has no pull request the factory can watch. Say so here once its work is on the default branch.'}
          </span>
        </div>
      ) : null}
    </section>
  );
}
