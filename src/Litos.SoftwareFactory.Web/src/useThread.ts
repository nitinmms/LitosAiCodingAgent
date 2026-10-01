import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, type FactoryApi } from './api/client';
import { subscribeToThread, type EventSourceFactory } from './api/events';
import type { Thread, ThreadDetails, UsageCall } from './api/types';
import { applyChange, newer } from './domain/task';

export interface ThreadView {
  details: ThreadDetails | null;
  usage: UsageCall[];
  /** Why the thread could not be loaded; null while it is loading or shown. */
  error: string | null;
  reload(): Promise<void>;
}

/** How long to wait for more events before asking the host again. A model call writes two
 * usage events and a turn can write several messages at once; one request covers them. */
const RELOAD_DELAY_MS = 120;
const USAGE_DELAY_MS = 500;

/**
 * One thread, kept current. It loads the thread, then listens to its event stream from the
 * point the snapshot was taken: state and budget figures are applied straight from the events,
 * and anything that changes the conversation is fetched again.
 */
export function useThread(
  api: FactoryApi,
  threadId: string | null,
  openEvents: EventSourceFactory | undefined,
  onThread: (thread: Thread) => void,
): ThreadView {
  const [details, setDetails] = useState<ThreadDetails | null>(null);
  const [usage, setUsage] = useState<UsageCall[]>([]);
  const [error, setError] = useState<string | null>(null);

  const notify = useRef(onThread);
  notify.current = onThread;
  // Which thread the hook is on now, so an answer for a thread the user has left is dropped.
  const current = useRef<string | null>(threadId);
  current.current = threadId;

  // What is on screen, readable outside a render so events and reloads can be merged into it.
  const shown = useRef<ThreadDetails | null>(null);
  const show = useCallback((next: ThreadDetails) => {
    shown.current = next;
    setDetails(next);
    notify.current(next.thread);
  }, []);

  const load = useCallback(
    async (id: string): Promise<ThreadDetails | null> => {
      try {
        const loaded = await api.thread(id);
        if (current.current !== id) return null;
        // Events may already have moved the thread past this snapshot.
        const previous = shown.current;
        show(previous && previous.thread.id === id ? { ...loaded, thread: newer(previous.thread, loaded.thread) } : loaded);
        setError(null);
        return loaded;
      } catch (failure) {
        if (current.current === id) setError(failure instanceof ApiError ? failure.message : 'The thread could not be loaded.');
        return null;
      }
    },
    [api, show],
  );

  const loadUsage = useCallback(
    async (id: string) => {
      try {
        const calls = await api.usage(id);
        if (current.current === id) setUsage(calls);
      } catch {
        // The ledger is supporting detail; the budget figures themselves come with the thread.
      }
    },
    [api],
  );

  useEffect(() => {
    shown.current = null;
    setDetails(null);
    setUsage([]);
    setError(null);
    if (!threadId) return;

    let stopped = false;
    let unsubscribe = () => {};
    let reloadTimer: ReturnType<typeof setTimeout> | undefined;
    let usageTimer: ReturnType<typeof setTimeout> | undefined;

    const reloadSoon = () => {
      clearTimeout(reloadTimer);
      reloadTimer = setTimeout(() => void load(threadId), RELOAD_DELAY_MS);
    };
    const usageSoon = () => {
      clearTimeout(usageTimer);
      usageTimer = setTimeout(() => void loadUsage(threadId), USAGE_DELAY_MS);
    };

    void (async () => {
      const loaded = await load(threadId);
      void loadUsage(threadId);
      if (stopped || !loaded) return;

      unsubscribe = subscribeToThread(
        threadId,
        loaded.eventCursor,
        (event) => {
          if (event.type === 'message') {
            reloadSoon();
            return;
          }

          const previous = shown.current;
          if (previous && previous.thread.id === threadId) {
            const thread = applyChange(previous.thread, event.change);
            if (thread !== previous.thread) show({ ...previous, thread });
          }
          // A change of state brings decisions, verification and the handoff with it.
          if (event.type === 'state') reloadSoon();
          usageSoon();
        },
        openEvents,
      );
    })();

    return () => {
      stopped = true;
      clearTimeout(reloadTimer);
      clearTimeout(usageTimer);
      unsubscribe();
    };
  }, [threadId, load, loadUsage, show, openEvents]);

  const reload = useCallback(async () => {
    if (!threadId) return;
    await Promise.all([load(threadId), loadUsage(threadId)]);
  }, [threadId, load, loadUsage]);

  return { details, usage, error, reload };
}
