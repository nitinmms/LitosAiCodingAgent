import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ApiError, createApi, type FactoryApi } from './api/client';
import { resubscribeDelay, subscribeToBoard, type EventSourceFactory } from './api/events';
import type { CurrentUser, Project, Settings, Thread } from './api/types';
import { BoardPage } from './components/BoardPage';
import { InvitePage } from './components/InvitePage';
import { Login } from './components/Login';
import { PeoplePage } from './components/PeoplePage';
import { ProjectsPage } from './components/ProjectsPage';
import { ThreadsPage } from './components/ThreadsPage';
import { TopBar } from './components/TopBar';
import { navigate, useRoute, type Route } from './domain/route';
import { awaitsYou, newer } from './domain/task';

const NOTICE_MS = 6_000;

export interface AppProps {
  /** Builds the API client; tests pass one backed by a fake host. */
  createClient?: (onSignedOut: () => void) => FactoryApi;
  openEvents?: EventSourceFactory;
}

export function App({ createClient, openEvents }: AppProps) {
  // undefined: not known yet. null: signed out.
  const [user, setUser] = useState<CurrentUser | null | undefined>(undefined);
  const [settings, setSettings] = useState<Settings | null>(null);
  const [projects, setProjects] = useState<Project[]>([]);
  const [threads, setThreads] = useState<Thread[]>([]);
  /** Owner names by user id: only owners of threads this user can see (GET /api/directory). */
  const [names, setNames] = useState<Record<string, string>>({});
  const [loaded, setLoaded] = useState(false);
  /** Where the board's live stream starts: read by the host before the thread list. */
  const [boardCursor, setBoardCursor] = useState<number | null>(null);
  /** Bumped to open the board's stream again from a fresh snapshot after the host closed it. */
  const [boardSnapshot, setBoardSnapshot] = useState(0);
  const [boardReconnecting, setBoardReconnecting] = useState(false);
  /** Bumped when a fresh snapshot sets the cursor, so the stream reopens even at the same cursor. */
  const [boardOpened, setBoardOpened] = useState(0);
  /** How many fresh snapshots in a row have been tried since the board's stream was last live. */
  const boardAttempt = useRef(0);
  const [problem, setProblem] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const route = useRoute();

  const api = useMemo(() => {
    const signedOut = () => setUser(null);
    return createClient ? createClient(signedOut) : createApi(undefined, signedOut);
  }, [createClient]);

  const noticeTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const say = useCallback((text: string) => {
    setNotice(text);
    clearTimeout(noticeTimer.current);
    noticeTimer.current = setTimeout(() => setNotice(null), NOTICE_MS);
  }, []);
  useEffect(() => () => clearTimeout(noticeTimer.current), []);

  useEffect(() => {
    let stopped = false;
    api
      .me()
      .then((me) => !stopped && setUser(me))
      .catch((failure: unknown) => {
        if (stopped) return;
        setProblem(failure instanceof ApiError ? failure.message : 'The factory host could not be reached.');
        setUser(null);
      });
    return () => {
      stopped = true;
    };
  }, [api]);

  /** Takes a thread from anywhere — the list, a creation, a live event — keeping the newer copy. */
  const mergeThread = useCallback((thread: Thread) => {
    setThreads((current) => {
      const index = current.findIndex((t) => t.id === thread.id);
      if (index < 0) return [thread, ...current];
      const merged = newer(current[index], thread);
      if (merged === current[index]) return current;
      const next = [...current];
      next[index] = merged;
      return next;
    });
  }, []);

  const signedIn = !!user;
  useEffect(() => {
    if (!signedIn) {
      setLoaded(false);
      setThreads([]);
      setProjects([]);
      return;
    }

    let stopped = false;
    const fail = (failure: unknown) => {
      // A 401 has already sent the user to the sign-in screen.
      if (!stopped && !(failure instanceof ApiError && failure.status === 401))
        setProblem(failure instanceof ApiError ? failure.message : 'The factory could not be loaded.');
    };

    Promise.all([api.settings(), api.projects(), api.threadList(), api.directory()])
      .then(([s, p, list, d]) => {
        if (stopped) return;
        setSettings(s);
        setProjects(p);
        setThreads(list.threads);
        setBoardCursor(list.cursor);
        setNames(Object.fromEntries(d.map((e) => [e.id, e.name])));
        setProblem(null);
        setLoaded(true);
      })
      .catch(fail);

    return () => {
      stopped = true;
      setBoardCursor(null);
      setBoardSnapshot(0);
      setBoardReconnecting(false);
      boardAttempt.current = 0;
    };
  }, [api, signedIn]);

  // Every thread the user can see stays live (GET /api/events): a change made anywhere, or a new
  // thread, arrives as the thread's whole view and replaces an older copy.
  useEffect(() => {
    if (!signedIn || boardCursor === null) return;
    let retry: ReturnType<typeof setTimeout> | undefined;
    const stop = subscribeToBoard(boardCursor, mergeThread, openEvents, (status) => {
      if (status === 'live') boardAttempt.current = 0;
      setBoardReconnecting(status !== 'live');
      // The host refused the stream; the fresh list below finds out why (a 401 signs out).
      if (status === 'closed') retry = setTimeout(() => setBoardSnapshot((n) => n + 1), resubscribeDelay(boardAttempt.current++));
    });
    return () => {
      clearTimeout(retry);
      stop();
    };
  }, [signedIn, boardCursor, boardOpened, mergeThread, openEvents]);

  // After the board's stream was closed: the list again, and a new stream from its cursor. The
  // list replaces the old one, so a thread in a project the user has left goes away.
  useEffect(() => {
    if (!signedIn || boardSnapshot === 0) return;
    let stopped = false;
    let retry: ReturnType<typeof setTimeout> | undefined;
    api
      .threadList()
      .then((list) => {
        if (stopped) return;
        setThreads((current) =>
          list.threads.map((t) => {
            const shown = current.find((c) => c.id === t.id);
            return shown ? newer(shown, t) : t;
          }),
        );
        setBoardCursor(list.cursor);
        setBoardOpened((n) => n + 1);
      })
      .catch((failure: unknown) => {
        if (stopped || (failure instanceof ApiError && failure.status === 401)) return;
        retry = setTimeout(() => setBoardSnapshot((n) => n + 1), resubscribeDelay(boardAttempt.current++));
      });
    return () => {
      stopped = true;
      clearTimeout(retry);
    };
  }, [api, signedIn, boardSnapshot]);

  // A thread from someone not seen before (a new thread, a newly joined project) needs their name.
  const unknownOwner = threads.some((t) => !(t.ownerId in names));
  useEffect(() => {
    if (!signedIn || !loaded || !unknownOwner) return;
    let stopped = false;
    api
      .directory()
      .then((d) => {
        if (!stopped) setNames((current) => ({ ...current, ...Object.fromEntries(d.map((e) => [e.id, e.name])) }));
      })
      .catch(() => {});
    return () => {
      stopped = true;
    };
  }, [api, signedIn, loaded, unknownOwner]);

  // An invitation's link works for someone who has no account yet.
  if (route.view === 'invite') {
    return (
      <InvitePage
        api={api}
        token={route.token}
        onSignedIn={(me) => {
          setProblem(null);
          setUser(me);
          navigate({ view: 'threads', threadId: null });
        }}
      />
    );
  }

  if (user === undefined) return <p className="note">Loading…</p>;

  if (user === null) {
    return (
      <>
        {problem ? (
          <p className="banner" role="alert">
            {problem}
          </p>
        ) : null}
        <Login
          api={api}
          onSignedIn={(me) => {
            setProblem(null);
            setUser(me);
          }}
        />
      </>
    );
  }

  // "Awaiting you" is personal (§7.1): only what this user owns and must act on.
  const waiting = threads.filter((t) => awaitsYou(t, user.id));

  return (
    <>
      <TopBar
        user={user}
        view={route.view}
        awaitingYou={waiting.length}
        reconnecting={boardReconnecting}
        onNavigate={(view) => navigate(view === 'threads' ? { view, threadId: null } : ({ view } as Route))}
        onAwaitingYou={() => waiting[0] && navigate({ view: 'threads', threadId: waiting[0].id })}
        onSignOut={() => {
          api
            .logout()
            .catch(() => {})
            .finally(() => setUser(null));
        }}
      />
      {problem ? (
        <p className="banner" role="alert">
          {problem}
        </p>
      ) : null}
      {!loaded ? (
        <p className="note">Loading…</p>
      ) : route.view === 'board' ? (
        <BoardPage
          threads={threads}
          projects={projects}
          names={names}
          userId={user.id}
          taskTypes={settings?.taskTypes ?? []}
          onOpen={(threadId) => navigate({ view: 'threads', threadId })}
        />
      ) : route.view === 'people' ? (
        user.roles.includes('Admin') ? (
          <PeoplePage api={api} projects={projects} currentUserId={user.id} onNotice={say} />
        ) : (
          <p className="note">Only an admin can manage people.</p>
        )
      ) : route.view === 'projects' ? (
        <ProjectsPage
          api={api}
          settings={settings}
          projects={projects}
          isAdmin={user.roles.includes('Admin')}
          onRegistered={(project) => {
            setProjects((current) => [...current, project]);
            say(`${project.name} is registered. Create a thread to delegate work on it.`);
          }}
        />
      ) : (
        <ThreadsPage
          api={api}
          user={user}
          settings={settings}
          projects={projects}
          threads={threads}
          selectedId={route.threadId}
          openEvents={openEvents}
          onSelect={(threadId) => navigate({ view: 'threads', threadId })}
          onThread={mergeThread}
          onNotice={say}
          onProjects={() => navigate({ view: 'projects' })}
        />
      )}
      {notice ? (
        <div className="toast" role="status">
          {notice}
        </div>
      ) : null}
    </>
  );
}
