import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, type FactoryApi } from './api/client';
import { resubscribeDelay, subscribeToThread, type EventSourceFactory, type StreamStatus, type ThreadEvent } from './api/events';
import type { Thread, ThreadDetails, UsageCall } from './api/types';
import { applyChange, newer } from './domain/task';

export interface ThreadView {
  details: ThreadDetails | null;
  usage: UsageCall[];
  /** Why the thread could not be loaded; null while it is loading or shown. */
  error: string | null;
  /** Whether live updates are arriving: "reconnecting" while the stream is down. */
  connection: 'live' | 'reconnecting';
  reload(): Promise<void>;
}

/** How long to wait for more events before asking the host again. A model call writes two
 * usage events and a turn can write several messages at once; one request covers them. */
const RELOAD_DELAY_MS = 120;
const USAGE_DELAY_MS = 500;

/** Failures a fresh snapshot will not fix: signed out (the client has already gone to sign-in),
 * no longer allowed, or gone. */
const FINAL = new Set([401, 403, 404, 410]);

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
  const [connection, setConnection] = useState<'live' | 'reconnecting'>('live');
  // Why the last load failed, so a reconnect knows whether trying again can help.
  const lastFailure = useRef<number | null>(null);

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
        lastFailure.current = null;
        return loaded;
      } catch (failure) {
        lastFailure.current = failure instanceof ApiError ? failure.status : 0;
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
    setConnection('live');
    if (!threadId) return;

    let stopped = false;
    let unsubscribe = () => {};
    let reloadTimer: ReturnType<typeof setTimeout> | undefined;
    let usageTimer: ReturnType<typeof setTimeout> | undefined;
    let retryTimer: ReturnType<typeof setTimeout> | undefined;
    let attempt = 0;

    const reloadSoon = () => {
      clearTimeout(reloadTimer);
      reloadTimer = setTimeout(() => void load(threadId), RELOAD_DELAY_MS);
    };
    const usageSoon = () => {
      clearTimeout(usageTimer);
      usageTimer = setTimeout(() => void loadUsage(threadId), USAGE_DELAY_MS);
    };

    const onEvent = (event: ThreadEvent) => {
      if (event.type === 'message') {
        reloadSoon();
        return;
      }

      const previous = shown.current;
      // What a chat answer is doing changes nothing else, so it is shown as it comes.
      if (event.type === 'chat') {
        if (previous && previous.thread.id === threadId && previous.chatPending) show({ ...previous, chatProgress: event.progress });
        return;
      }

      if (previous && previous.thread.id === threadId) {
        const thread = applyChange(previous.thread, event.change);
        if (thread !== previous.thread) show({ ...previous, thread });
      }
      // A change of state brings decisions, verification and the handoff with it.
      if (event.type === 'state') reloadSoon();
      usageSoon();
    };

    const retry = () => {
      retryTimer = setTimeout(() => void connect(), resubscribeDelay(attempt++));
    };

    const onStatus = (status: StreamStatus) => {
      if (stopped) return;
      if (status === 'live') {
        attempt = 0;
        setConnection('live');
        return;
      }
      setConnection('reconnecting');
      // The browser has given up on this stream: a fresh snapshot, then a new stream from it.
      if (status === 'closed') {
        unsubscribe();
        unsubscribe = () => {};
        retry();
      }
    };

    // Loads the thread, then listens from the point the snapshot was taken.
    const connect = async (): Promise<void> => {
      const loaded = await load(threadId);
      if (stopped) return;
      void loadUsage(threadId);
      if (!loaded) {
        // A first load that fails is shown as an error; a reconnect keeps trying unless the
        // failure is one that trying again cannot fix.
        if (attempt > 0 && !FINAL.has(lastFailure.current ?? 0)) {
          retry();
          return;
        }
        // Nothing more will arrive. A thread the user can no longer see goes, so the reason shows.
        if (attempt > 0) {
          shown.current = null;
          setDetails(null);
        }
        setConnection('live');
        return;
      }
      unsubscribe = subscribeToThread(threadId, loaded.eventCursor, onEvent, openEvents, onStatus);
    };

    void connect();

    return () => {
      stopped = true;
      clearTimeout(reloadTimer);
      clearTimeout(usageTimer);
      clearTimeout(retryTimer);
      unsubscribe();
    };
  }, [threadId, load, loadUsage, show, openEvents]);

  const reload = useCallback(async () => {
    if (!threadId) return;
    await Promise.all([load(threadId), loadUsage(threadId)]);
  }, [threadId, load, loadUsage]);

  return { details, usage, error, connection, reload };
}
