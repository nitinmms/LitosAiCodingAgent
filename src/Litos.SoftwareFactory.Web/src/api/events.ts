import type { ChatProgress, Thread, ThreadChange } from './types';

export type ThreadEvent =
  | { type: 'message'; sequence: number }
  | { type: 'state'; sequence: number; change: ThreadChange }
  | { type: 'usage'; sequence: number; change: ThreadChange }
  | { type: 'chat'; sequence: number; progress: ChatProgress };

/**
 * Where a stream stands (m2-architecture.md §7). "reconnecting": it dropped and the browser is
 * trying again by itself, from the last event it saw. "closed": the host refused it — after the
 * session expired, or when the thread is no longer visible — and the browser has given up; the
 * caller takes a fresh snapshot, which also finds out which of those it was.
 */
export type StreamStatus = 'live' | 'reconnecting' | 'closed';

/** After the host closes a stream for good: the waits before each fresh snapshot in turn, the
 * last one repeating until the host answers. */
export const RESUBSCRIBE_DELAYS_MS = [1_000, 2_000, 5_000, 10_000, 30_000];

/** How long to wait before the given attempt (0 is the first) at a fresh snapshot. */
export const resubscribeDelay = (attempt: number): number =>
  RESUBSCRIBE_DELAYS_MS[Math.min(Math.max(attempt, 0), RESUBSCRIBE_DELAYS_MS.length - 1)]!;

/** EventSource.CLOSED: the browser will not reconnect by itself. */
const CLOSED = 2;

/** The part of EventSource this app uses, so tests can supply their own. */
export interface EventSourceLike {
  readonly readyState: number;
  addEventListener(type: string, listener: (event: MessageEvent) => void): void;
  close(): void;
}

export type EventSourceFactory = (url: string) => EventSourceLike;

export const browserEventSource: EventSourceFactory = (url) => new EventSource(url);

/**
 * Opens a stream and reports where it stands. Events at or before the last sequence handled are
 * dropped, so a replay after a reconnect, or a stream reopened from an older cursor, never
 * repeats one. Returns the source and a gate each listener passes its event through.
 */
function openStream(
  url: string,
  after: number,
  open: EventSourceFactory,
  onStatus: ((status: StreamStatus) => void) | undefined,
): { source: EventSourceLike; fresh: (event: MessageEvent) => number | null } {
  const source = open(url);
  let last = after;

  source.addEventListener('open', () => onStatus?.('live'));
  source.addEventListener('error', () => {
    if (source.readyState === CLOSED) {
      source.close();
      onStatus?.('closed');
    } else {
      onStatus?.('reconnecting');
    }
  });

  const fresh = (event: MessageEvent): number | null => {
    const sequence = Number(event.lastEventId) || 0;
    // An event without an id cannot be placed, so it is never mistaken for a repeat.
    if (sequence > 0) {
      if (sequence <= last) return null;
      last = sequence;
    }
    return sequence;
  };

  return { source, fresh };
}

/**
 * Listens to a thread's live events from `after` onward. The browser reconnects a dropped
 * stream by itself and sends the last event id it saw, so nothing is missed across a drop.
 * Returns the function that stops listening.
 */
export function subscribeToThread(
  threadId: string,
  after: number,
  onEvent: (event: ThreadEvent) => void,
  open: EventSourceFactory = browserEventSource,
  onStatus?: (status: StreamStatus) => void,
): () => void {
  const { source, fresh } = openStream(`/api/threads/${encodeURIComponent(threadId)}/events?after=${after}`, after, open, onStatus);

  const changeOf = (event: MessageEvent): ThreadChange | null => {
    try {
      return JSON.parse(String(event.data)) as ThreadChange;
    } catch {
      return null;
    }
  };

  source.addEventListener('message', (event) => {
    const sequence = fresh(event);
    if (sequence !== null) onEvent({ type: 'message', sequence });
  });
  for (const type of ['state', 'usage'] as const) {
    source.addEventListener(type, (event) => {
      const change = changeOf(event);
      if (!change) return;
      const sequence = fresh(event);
      if (sequence !== null) onEvent({ type, sequence, change });
    });
  }

  source.addEventListener('chat', (event) => {
    let progress: ChatProgress;
    try {
      progress = JSON.parse(String(event.data)) as ChatProgress;
    } catch {
      return; // A report that cannot be read is skipped; the next one replaces it.
    }
    const sequence = fresh(event);
    if (sequence !== null) onEvent({ type: 'chat', sequence, progress });
  });

  return () => source.close();
}

/**
 * Listens to the board's stream (GET /api/events) from `after` onward: every change to a thread
 * the user can see, as the thread's whole current view. The caller keeps the newer copy by
 * revision, so a repeat or a late event never undoes a newer one. Returns the function that stops.
 */
export function subscribeToBoard(
  after: number,
  onThread: (thread: Thread) => void,
  open: EventSourceFactory = browserEventSource,
  onStatus?: (status: StreamStatus) => void,
): () => void {
  const { source, fresh } = openStream(`/api/events?after=${after}`, after, open, onStatus);
  source.addEventListener('thread', (event) => {
    let thread: Thread;
    try {
      thread = JSON.parse(String(event.data)) as Thread;
    } catch {
      return; // Not a thread: ignored.
    }
    if (fresh(event) !== null) onThread(thread);
  });
  return () => source.close();
}
