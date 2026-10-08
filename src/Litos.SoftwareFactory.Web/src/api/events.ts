import type { ChatProgress, Thread, ThreadChange } from './types';

export type ThreadEvent =
  | { type: 'message'; sequence: number }
  | { type: 'state'; sequence: number; change: ThreadChange }
  | { type: 'usage'; sequence: number; change: ThreadChange }
  | { type: 'chat'; sequence: number; progress: ChatProgress };

/** The part of EventSource this app uses, so tests can supply their own. */
export interface EventSourceLike {
  addEventListener(type: string, listener: (event: MessageEvent) => void): void;
  close(): void;
}

export type EventSourceFactory = (url: string) => EventSourceLike;

export const browserEventSource: EventSourceFactory = (url) => new EventSource(url);

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
): () => void {
  const source = open(`/api/threads/${encodeURIComponent(threadId)}/events?after=${after}`);

  const sequenceOf = (event: MessageEvent) => Number(event.lastEventId) || 0;
  const changeOf = (event: MessageEvent): ThreadChange | null => {
    try {
      return JSON.parse(String(event.data)) as ThreadChange;
    } catch {
      return null;
    }
  };

  source.addEventListener('message', (event) => onEvent({ type: 'message', sequence: sequenceOf(event) }));
  for (const type of ['state', 'usage'] as const) {
    source.addEventListener(type, (event) => {
      const change = changeOf(event);
      if (change) onEvent({ type, sequence: sequenceOf(event), change });
    });
  }

  source.addEventListener('chat', (event) => {
    try {
      onEvent({ type: 'chat', sequence: sequenceOf(event), progress: JSON.parse(String(event.data)) as ChatProgress });
    } catch {
      // A report that cannot be read is skipped; the next one replaces it.
    }
  });

  return () => source.close();
}

/**
 * Listens to the board's stream (GET /api/events) from `after` onward: every change to a thread
 * the user can see, as the thread's whole current view. The caller keeps the newer copy by
 * revision, so a repeat or a late event never undoes a newer one. Returns the function that stops.
 */
export function subscribeToBoard(after: number, onThread: (thread: Thread) => void, open: EventSourceFactory = browserEventSource): () => void {
  const source = open(`/api/events?after=${after}`);
  source.addEventListener('thread', (event) => {
    try {
      onThread(JSON.parse(String(event.data)) as Thread);
    } catch {
      // Not a thread: ignored.
    }
  });
  return () => source.close();
}
