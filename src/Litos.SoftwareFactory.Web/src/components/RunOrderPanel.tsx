import { useEffect, useState, type DragEvent } from 'react';
import type { FactoryApi } from '../api/client';
import type { BlockerLink, RunOrder, Thread } from '../api/types';
import {
  blockerCandidates,
  blockerStatus,
  canArrange,
  daysWaited,
  dropMove,
  LONG_WAIT_DAYS,
  notStarted,
  stepMove,
  type Move,
} from '../domain/runOrder';

/**
 * A project's run order (run-order-architecture.md): the task holding the project, then the queue
 * the coordinator takes in order, skipping a task that waits for another's merge. An Admin drags a
 * task, or uses ↑ ↓ and Run first; a Member swaps two of their own tasks with ↑ ↓. The order is
 * read again whenever one of the project's tasks changes.
 */
export function RunOrderPanel({
  api,
  projectId,
  threads,
  userId,
  isAdmin,
  names,
  now = new Date(),
  onOpen,
  onNotice,
  onOrder,
}: {
  api: FactoryApi;
  projectId: string;
  /** Every task the person can see; those of this project say when to read the order again. */
  threads: Thread[];
  userId: string;
  isAdmin: boolean;
  names: Record<string, string>;
  now?: Date;
  onOpen: (threadId: string) => void;
  onNotice: (text: string) => void;
  /** The order as last read, for the Board's cards. */
  onOrder?: (order: RunOrder | null) => void;
}) {
  const [order, setOrder] = useState<RunOrder | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [dragging, setDragging] = useState<string | null>(null);
  const [dropAt, setDropAt] = useState<number | null>(null);
  const changes = threads
    .filter((t) => t.projectId === projectId)
    .map((t) => `${t.id}:${t.revision}`)
    .join(',');

  useEffect(() => {
    let current = true;
    api.runOrder(projectId).then(
      (read) => {
        if (!current) return;
        setOrder(read);
        setError(null);
        onOrder?.(read);
      },
      (failure: unknown) => current && setError(failure instanceof Error ? failure.message : 'The run order could not be read.'),
    );
    return () => {
      current = false;
    };
    // Read again when any of the project's tasks changes; onOrder is the Board's setter, left out
    // so a new function each render does not read it again.
  }, [api, projectId, changes]);

  const act = async (action: () => Promise<unknown>) => {
    if (busy) return;
    setBusy(true);
    try {
      await action();
      const read = await api.runOrder(projectId);
      setOrder(read);
      onOrder?.(read);
    } catch (failure: unknown) {
      onNotice(failure instanceof Error ? failure.message : 'That could not be done.');
    } finally {
      setBusy(false);
    }
  };

  const move = (thread: Thread, to: Move | null) => {
    if (to) void act(() => api.moveInQueue(thread.id, to.placement, to.otherThreadId, thread.revision));
  };

  if (error) return <section className="run-order panel" aria-label="Run order"><p className="error small">{error}</p></section>;
  if (!order) return <section className="run-order panel" aria-label="Run order"><p className="muted small">Reading the run order…</p></section>;

  const queue = order.queue.map((e) => e.thread);
  const ownerName = (id: string) => (id === userId ? 'You' : (names[id] ?? 'Someone'));
  const onDrop = (event: DragEvent, index: number) => {
    event.preventDefault();
    const dragged = queue.find((t) => t.id === dragging);
    setDragging(null);
    setDropAt(null);
    if (dragged) move(dragged, dropMove(queue, dragged.id, index));
  };
  // The first task the coordinator would take: the first that waits for nothing.
  const next = order.queue.find((e) => e.waitsFor.length === 0)?.thread.id;

  return (
    <section className="run-order panel" aria-label="Run order">
      <div className="run-order-head">
        <h3>Run order</h3>
        <span className="small muted">
          {isAdmin ? 'Drag a task, or use ↑ ↓ and Run first.' : 'You can swap your own tasks with ↑ ↓.'} Blocked tasks keep their place
          and are skipped.
        </span>
      </div>
      <div className="holder">
        {order.holder ? (
          <>
            <span className="small muted">Holding the project</span>{' '}
            <button className="link" onClick={() => onOpen(order.holder!.id)}>
              {order.holder.title}
            </button>{' '}
            <span className="small muted">· {ownerName(order.holder.ownerId)}</span>
          </>
        ) : (
          <span className="small muted">Nothing holds the project: the next task that waits for nothing starts on the coordinator's next look.</span>
        )}
      </div>
      {order.queue.length === 0 ? (
        <p className="muted small">Nothing is queued.</p>
      ) : (
        <ol className="queue" aria-label="Queue">
          {order.queue.map((entry, index) => {
            const t = entry.thread;
            const mine = canArrange(t, userId, isAdmin);
            const up = mine ? stepMove(queue, t.id, -1, userId, isAdmin) : null;
            const down = mine ? stepMove(queue, t.id, 1, userId, isAdmin) : null;
            const linked = entry.waitsFor.map((l) => l.threadId);
            const candidates = mine && notStarted(t) ? blockerCandidates(threads, projectId, t.id, linked) : [];
            return (
              <li
                key={t.id}
                className={`queue-item${entry.waitsFor.length ? ' skipped' : ''}${t.id === next ? ' next' : ''}${dropAt === index ? ' drop-before' : ''}`}
                draggable={isAdmin && !busy}
                onDragStart={(e) => {
                  setDragging(t.id);
                  e.dataTransfer.effectAllowed = 'move';
                }}
                onDragEnd={() => {
                  setDragging(null);
                  setDropAt(null);
                }}
                onDragOver={(e) => {
                  if (!dragging) return;
                  e.preventDefault();
                  setDropAt(index);
                }}
                onDrop={(e) => onDrop(e, index)}
                aria-label={`${index + 1}. ${t.title}`}
              >
                <span className="pos num">{index + 1}</span>
                <div className="queue-body">
                  <button className="link queue-title" onClick={() => onOpen(t.id)}>
                    {t.title}
                  </button>
                  <span className="small muted">
                    {' '}
                    · {ownerName(t.ownerId)}
                    {entry.isRework ? ' · rework' : ''}
                    {entry.waitedOnBy ? ` · ${entry.waitedOnBy} ${entry.waitedOnBy === 1 ? 'task waits' : 'tasks wait'} for its merge` : ''}
                  </span>
                  <div className={`small ${entry.waitsFor.length ? 'skip' : t.id === next ? 'go' : 'muted'}`}>
                    {entry.waitsFor.length
                      ? (t.stateReason ?? `Waits for "${entry.waitsFor[0]!.title}" to be merged.`)
                      : t.id === next
                        ? order.holder
                          ? `Starts next, when "${order.holder.title}" frees the project.`
                          : 'Starts next.'
                        : 'Waits its turn.'}
                  </div>
                  <Links
                    links={entry.waitsFor}
                    now={now}
                    canRelease={mine}
                    busy={busy}
                    onRelease={(link) => void act(() => api.releaseBlocker(t.id, link.threadId))}
                  />
                  {candidates.length ? (
                    <select
                      className="add-blocker"
                      aria-label={`Make "${t.title}" wait for`}
                      value=""
                      disabled={busy}
                      onChange={(e) => {
                        const blocker = e.target.value;
                        if (blocker) void act(() => api.addBlocker(t.id, blocker));
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
                </div>
                <div className="queue-moves">
                  <button className="btn small" aria-label={`Move "${t.title}" up`} disabled={busy || !up} onClick={() => move(t, up)}>
                    ↑
                  </button>
                  <button className="btn small" aria-label={`Move "${t.title}" down`} disabled={busy || !down} onClick={() => move(t, down)}>
                    ↓
                  </button>
                  {isAdmin ? (
                    <button className="btn small" disabled={busy || index === 0} onClick={() => move(t, { placement: 'first' })}>
                      Run first
                    </button>
                  ) : null}
                </div>
              </li>
            );
          })}
          {isAdmin ? (
            <li
              className={`queue-end${dropAt === order.queue.length ? ' drop-before' : ''}`}
              aria-hidden="true"
              onDragOver={(e) => {
                if (!dragging) return;
                e.preventDefault();
                setDropAt(order.queue.length);
              }}
              onDrop={(e) => onDrop(e, order.queue.length)}
            />
          ) : null}
        </ol>
      )}
    </section>
  );
}

/** A task's open links: what each waits for, how long, and Release now for its owner or an Admin. */
export function Links({
  links,
  now,
  canRelease,
  busy,
  onRelease,
}: {
  links: BlockerLink[];
  now: Date;
  canRelease: boolean;
  busy: boolean;
  onRelease: (link: BlockerLink) => void;
}) {
  if (!links.length) return null;
  return (
    <ul className="links" aria-label="Waits for">
      {links.map((link) => {
        const days = daysWaited(link.since, now);
        return (
          <li key={link.threadId} className={`link-chip${link.pullRequestClosedAt ? ' you' : ''}`}>
            <span>
              Waits for <b>{link.title}</b> to be merged <span className="muted">· {blockerStatus(link)}</span>
            </span>
            {days >= 1 ? (
              <span className={`age${days >= LONG_WAIT_DAYS ? ' long' : ''}`}>
                blocked {days} {days === 1 ? 'day' : 'days'}
              </span>
            ) : null}
            {canRelease ? (
              <button
                className="btn small"
                disabled={busy}
                title="Stop waiting. It runs without that task's work. Recorded in the audit log."
                onClick={() => onRelease(link)}
              >
                Release now
              </button>
            ) : null}
          </li>
        );
      })}
    </ul>
  );
}
