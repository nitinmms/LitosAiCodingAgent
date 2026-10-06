import { useRef, useState, type FormEvent } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { EventSourceFactory } from '../api/events';
import type { CurrentUser, Project, Settings, Thread } from '../api/types';
import { fmt } from '../domain/format';
import { useThread } from '../useThread';
import { ErrorNote, TurnPill } from './bits';
import { Details } from './Details';
import { ThreadMain } from './ThreadMain';

export function ThreadsPage({
  api,
  user,
  settings,
  projects,
  threads,
  selectedId,
  openEvents,
  onSelect,
  onThread,
  onNotice,
  onProjects,
}: {
  api: FactoryApi;
  user: CurrentUser;
  settings: Settings | null;
  projects: Project[];
  threads: Thread[];
  /** The thread in the address bar; when it names none, the first thread is shown. */
  selectedId: string | null;
  openEvents?: EventSourceFactory;
  onSelect: (threadId: string) => void;
  /** A thread was created or changed; the app keeps its list current from these. */
  onThread: (thread: Thread) => void;
  onNotice: (text: string) => void;
  onProjects: () => void;
}) {
  const [creating, setCreating] = useState(false);
  const [listOpen, setListOpen] = useState(false);
  const shownId = (selectedId && threads.some((t) => t.id === selectedId) ? selectedId : threads[0]?.id) ?? null;
  const view = useThread(api, shownId, openEvents, onThread);

  return (
    <div className="page">
      <div className="tv">
        <aside className={`list${listOpen ? '' : ' collapsed'}`} aria-label="Threads">
          <div className="btn-row">
            <button className="btn primary" onClick={() => setCreating(true)} disabled={projects.length === 0}>
              New thread
            </button>
            <button className="btn mobile-only" onClick={() => setListOpen(!listOpen)} aria-expanded={listOpen}>
              {listOpen ? 'Hide threads' : 'Show threads'}
            </button>
          </div>
          {projects.map((project) => {
            const mine = threads.filter((t) => t.projectId === project.id);
            return (
              <div className="list-proj" key={project.id}>
                <h3>{project.name}</h3>
                {mine.map((t) => (
                  <button
                    key={t.id}
                    className="list-item"
                    aria-current={t.id === shownId ? 'true' : 'false'}
                    onClick={() => {
                      onSelect(t.id);
                      setListOpen(false);
                    }}
                  >
                    <span>{t.title}</span>
                    <span>
                      <TurnPill state={t.state} />
                    </span>
                  </button>
                ))}
                {mine.length === 0 ? <p className="small muted list-empty">No threads yet.</p> : null}
              </div>
            );
          })}
        </aside>

        {projects.length === 0 ? (
          <div className="center">
            <div className="panel">
              <h2>Register a project first</h2>
              <p>The factory works on GitHub repositories you register. Add one, then create a thread for a change.</p>
              <div className="btn-row">
                <button className="btn primary" onClick={onProjects}>
                  Go to Projects
                </button>
              </div>
            </div>
          </div>
        ) : !shownId ? (
          <div className="center">
            <div className="panel">
              <h2>No threads yet</h2>
              <p>Create a thread for one change, then delegate it with @factory.</p>
            </div>
          </div>
        ) : view.details ? (
          <>
            <ThreadMain
              key={view.details.thread.id}
              api={api}
              details={view.details}
              user={user}
              settings={settings}
              reload={view.reload}
              onNotice={onNotice}
            />
            <Details
              details={view.details}
              usage={view.usage}
              cachedInputWeight={settings?.cachedInputWeight}
              onVerdict={(findingId, verdict) =>
                void api.setFindingVerdict(findingId, verdict).then(
                  () => view.reload(),
                  (error: unknown) => onNotice(error instanceof Error ? error.message : 'The verdict could not be saved.'),
                )
              }
            />
          </>
        ) : (
          <div className="center">
            <div className="panel">{view.error ? <ErrorNote message={view.error} /> : <p className="muted">Loading the thread…</p>}</div>
          </div>
        )}
      </div>

      {creating ? (
        <NewThread
          api={api}
          settings={settings}
          projects={projects}
          defaultProjectId={threads.find((t) => t.id === shownId)?.projectId ?? projects[0]?.id ?? ''}
          close={() => setCreating(false)}
          onCreated={(thread) => {
            onThread(thread);
            onSelect(thread.id);
            setCreating(false);
          }}
        />
      ) : null}
    </div>
  );
}

function NewThread({
  api,
  settings,
  projects,
  defaultProjectId,
  close,
  onCreated,
}: {
  api: FactoryApi;
  settings: Settings | null;
  projects: Project[];
  defaultProjectId: string;
  close: () => void;
  onCreated: (thread: Thread) => void;
}) {
  const types = settings?.taskTypes ?? ['feature', 'bug', 'refactor', 'chore'];
  const [title, setTitle] = useState('');
  const [projectId, setProjectId] = useState(defaultProjectId);
  const [typeLabel, setTypeLabel] = useState(types.includes('feature') ? 'feature' : (types[0] ?? 'feature'));
  const [cap, setCap] = useState(settings?.defaultBudget ? String(settings.defaultBudget) : '');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // The dialog closes on a click on the backdrop only if the press began there too. A browser
  // reports a click on the backdrop when a press inside a field ends outside it, and when an entry
  // is picked from its autofill list over the backdrop; either closed the dialog mid-edit.
  const pressedOnBackdrop = useRef(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy || !title.trim() || !projectId) return;

    const budgetCap = cap.trim() === '' ? undefined : Number(cap);
    if (budgetCap !== undefined && (!Number.isInteger(budgetCap) || budgetCap <= 0)) {
      setError('The token budget must be a whole number above zero.');
      return;
    }

    setBusy(true);
    setError(null);
    try {
      onCreated(await api.createThread({ projectId, title: title.trim(), typeLabel, budgetCap }));
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The thread could not be created.');
      setBusy(false);
    }
  };

  return (
    <div
      className="modal-bg"
      onMouseDown={(e) => {
        pressedOnBackdrop.current = e.target === e.currentTarget;
      }}
      onClick={(e) => {
        if (e.target === e.currentTarget && pressedOnBackdrop.current) close();
        pressedOnBackdrop.current = false;
      }}
    >
      <form
        className="modal"
        onSubmit={submit}
        role="dialog"
        aria-modal="true"
        aria-labelledby="nt-heading"
        autoComplete="off"
      >
        <h2 id="nt-heading">New thread</h2>
        <p className="small muted">One change per thread. Nothing runs until you delegate it with @factory.</p>
        <div className="field">
          <label htmlFor="nt-title">What do you want changed?</label>
          <input
            id="nt-title"
            required
            autoFocus
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            placeholder="Add CSV export to Orders"
          />
        </div>
        <div className="form-grid">
          <div className="field">
            <label htmlFor="nt-proj">Project</label>
            <select id="nt-proj" value={projectId} onChange={(e) => setProjectId(e.target.value)}>
              {projects.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
          </div>
          <div className="field">
            <label htmlFor="nt-type">Type</label>
            <select id="nt-type" value={typeLabel} onChange={(e) => setTypeLabel(e.target.value)}>
              {types.map((t) => (
                <option key={t} value={t}>
                  {t.charAt(0).toUpperCase() + t.slice(1)}
                </option>
              ))}
            </select>
          </div>
          <div className="field">
            <label htmlFor="nt-cap">Token budget</label>
            <input id="nt-cap" type="number" min={1} value={cap} onChange={(e) => setCap(e.target.value)} />
            <span className="hint">
              {settings?.defaultBudget ? `Left empty, it is ${fmt(settings.defaultBudget)}.` : 'Left empty, the task has no cap.'}
            </span>
          </div>
        </div>
        {settings ? (
          <p className="small muted">
            Runs on <span className="mono">{settings.model}</span> ({settings.provider}).
          </p>
        ) : null}
        <ErrorNote message={error} />
        <div className="btn-row">
          <button className="btn primary" type="submit" disabled={busy}>
            Create thread
          </button>
          <button className="btn" type="button" onClick={close}>
            Cancel
          </button>
        </div>
      </form>
    </div>
  );
}
