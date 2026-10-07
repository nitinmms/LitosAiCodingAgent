import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ApiError, createApi, type FactoryApi } from './api/client';
import type { EventSourceFactory } from './api/events';
import type { CurrentUser, Project, Settings, Thread } from './api/types';
import { InvitePage } from './components/InvitePage';
import { Login } from './components/Login';
import { PeoplePage } from './components/PeoplePage';
import { ProjectsPage } from './components/ProjectsPage';
import { ThreadsPage } from './components/ThreadsPage';
import { TopBar } from './components/TopBar';
import { navigate, useRoute, type Route } from './domain/route';
import { newer, turnLabel } from './domain/task';

/** How often the thread list is refreshed; the open thread itself is kept live by its stream. */
const LIST_REFRESH_MS = 15_000;
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
  const [loaded, setLoaded] = useState(false);
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

    Promise.all([api.settings(), api.projects(), api.threads()])
      .then(([s, p, t]) => {
        if (stopped) return;
        setSettings(s);
        setProjects(p);
        setThreads(t);
        setProblem(null);
        setLoaded(true);
      })
      .catch(fail);

    const timer = setInterval(() => {
      api
        .threads()
        .then((list) => {
          if (stopped) return;
          setThreads((current) => list.map((t) => newer(current.find((c) => c.id === t.id), t)));
          setProblem(null);
        })
        .catch(fail);
    }, LIST_REFRESH_MS);

    return () => {
      stopped = true;
      clearInterval(timer);
    };
  }, [api, signedIn]);

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

  const waiting = threads.filter((t) => turnLabel(t.state).cls === 'you');

  return (
    <>
      <TopBar
        user={user}
        view={route.view}
        awaitingYou={waiting.length}
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
