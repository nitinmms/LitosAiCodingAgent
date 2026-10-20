import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { Lesson, LessonCommand, LessonInput, Project } from '../api/types';
import { navigate } from '../domain/route';
import { ErrorNote } from './bits';

/** The page's tabs: a lesson's status, with approved lessons split by whether runs may use them. */
export const LESSON_TABS = ['Suggested', 'Approved', 'Disabled', 'Dismissed'] as const;
export type LessonTab = (typeof LESSON_TABS)[number];

export const tabOf = (lesson: Lesson): LessonTab =>
  lesson.status === 'Approved' ? (lesson.enabled ? 'Approved' : 'Disabled') : lesson.status;

/** Topics or paths typed as a comma-separated list. */
export const splitList = (text: string): string[] =>
  text
    .split(',')
    .map((t) => t.trim())
    .filter((t) => t.length > 0);

/** Where a lesson came from, as people say it. */
export const SOURCE: Record<Lesson['current']['source'], string> = {
  Manual: 'Written by hand',
  HumanCorrection: 'A change request',
  Decision: 'An answered decision',
  ReviewFinding: 'A review finding',
  Evidence: 'A handoff',
};

/**
 * A project's lessons (ReadMe_LitosSoftwareFactory_V1.md §21, m4-architecture.md §3.4): reviewed
 * guidance later tasks on the project are given. Anyone on the project writes and edits
 * suggestions; only an Admin approves, edits an approved lesson, dismisses, disables or replaces one.
 */
export function LessonsPage({
  api,
  projects,
  projectId,
  isAdmin,
  onProject,
  onNotice,
  focus,
}: {
  api: FactoryApi;
  projects: Project[];
  projectId: string | null;
  isAdmin: boolean;
  onProject: (projectId: string) => void;
  onNotice: (text: string) => void;
  /** A lesson's number to open on: its tab, scrolled into view and marked (the Metrics tab links here). */
  focus?: number;
}) {
  const project = projects.find((p) => p.id === projectId) ?? projects[0] ?? null;
  const [lessons, setLessons] = useState<Lesson[] | null>(null);
  const [tab, setTab] = useState<LessonTab>('Suggested');
  const [writing, setWriting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    if (!project) return;
    try {
      setLessons(await api.lessons(project.id));
      setError(null);
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The lessons could not be loaded.');
    }
  }, [api, project]);

  useEffect(() => {
    setLessons(null);
    void reload();
  }, [reload]);

  const focused = focus === undefined ? undefined : lessons?.find((l) => l.number === focus);
  useEffect(() => {
    if (focused) setTab(tabOf(focused));
  }, [focused]);
  useEffect(() => {
    // jsdom has no scrollIntoView; a browser always does.
    if (focused) document.getElementById(`lesson-${focused.number}`)?.scrollIntoView?.({ block: 'center' });
  }, [focused, tab]);

  /** Replaces one lesson with the host's answer, so the list shows what was saved. */
  const replaced = (lesson: Lesson) =>
    setLessons((current) => (current?.some((l) => l.id === lesson.id) ? current.map((l) => (l.id === lesson.id ? lesson : l)) : [lesson, ...(current ?? [])]));

  if (!project) return <div className="page"><p className="note">Register a project first; lessons belong to a project.</p></div>;

  const shown = (lessons ?? []).filter((l) => tabOf(l) === tab);
  const count = (t: LessonTab) => (lessons ?? []).filter((l) => tabOf(l) === t).length;
  const approvedOthers = (except: string) => (lessons ?? []).filter((l) => l.id !== except && l.status === 'Approved' && l.enabled);

  return (
    <div className="page stack">
      <section className="section">
        <div className="section-head">
          <h2>Lessons</h2>
          {projects.length > 1 ? (
            <select aria-label="Project" value={project.id} onChange={(e) => onProject(e.target.value)}>
              {projects.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
          ) : (
            <span className="tag">{project.name}</span>
          )}
          <button className="btn primary small" type="button" onClick={() => setWriting(true)} disabled={writing}>
            New lesson
          </button>
        </div>
        <p className="small muted">
          Guidance from earlier work on {project.name}. Approved, enabled lessons are given to later tasks on this project whose words match
          their topics. Suggestions are never given to a run until an Admin approves them.
        </p>
      </section>

      {writing ? (
        <LessonForm
          title="New lesson"
          isAdmin={isAdmin}
          onCancel={() => setWriting(false)}
          onSave={async (input) => {
            const lesson = await api.createLesson(project.id, input);
            replaced(lesson);
            setWriting(false);
            setTab(tabOf(lesson));
            onNotice(input.approve ? `L-${lesson.number} is approved.` : `L-${lesson.number} is saved as a suggestion.`);
          }}
        />
      ) : null}

      <ErrorNote message={error} />

      <nav className="tabs" role="tablist" aria-label="Lesson status">
        {LESSON_TABS.map((t) => (
          <button key={t} role="tab" aria-selected={t === tab} onClick={() => setTab(t)}>
            {t} ({count(t)})
          </button>
        ))}
      </nav>

      {lessons === null && !error ? <p className="small muted">Loading…</p> : null}
      {lessons !== null && shown.length === 0 ? <p className="small muted">No {tab.toLowerCase()} lessons.</p> : null}
      {shown.map((lesson) => (
        <LessonCard
          key={lesson.id}
          lesson={lesson}
          focused={lesson.number === focus}
          isAdmin={isAdmin}
          replacements={approvedOthers(lesson.id)}
          numberOf={(id) => lessons?.find((l) => l.id === id)?.number ?? null}
          onRevise={async (input) => {
            const saved = await api.reviseLesson(lesson.id, lesson.currentRevision, input);
            replaced(saved);
            onNotice(saved.pendingEdit ? `L-${saved.number}: the edit waits for an Admin's approval.` : `L-${saved.number} is saved.`);
          }}
          onCommand={async (command) => {
            const saved = await api.lessonCommand(lesson.id, command, lesson.currentRevision);
            replaced(saved);
            onNotice(`L-${saved.number} is ${command === 'approve' ? 'approved' : command === 'dismiss' ? 'dismissed' : command === 'disable' ? 'disabled' : 'enabled'}.`);
          }}
          onSupersede={async (replacementId) => {
            const saved = await api.supersedeLesson(lesson.id, lesson.currentRevision, replacementId);
            replaced(saved);
            onNotice(`L-${saved.number} is replaced and disabled.`);
          }}
        />
      ))}
    </div>
  );
}

function LessonCard({
  lesson,
  focused = false,
  isAdmin,
  replacements,
  numberOf,
  onRevise,
  onCommand,
  onSupersede,
}: {
  lesson: Lesson;
  focused?: boolean;
  isAdmin: boolean;
  replacements: Lesson[];
  numberOf: (id: string) => number | null;
  onRevise: (input: LessonInput) => Promise<void>;
  onCommand: (command: LessonCommand) => Promise<void>;
  onSupersede: (replacementId: string) => Promise<void>;
}) {
  const [editing, setEditing] = useState(false);
  const [replacing, setReplacing] = useState(false);
  const [replacement, setReplacement] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const { current, approved } = lesson;
  // A Member edits suggestions only; an approved or dismissed lesson is an Admin's.
  const canEdit = lesson.status === 'Suggested' || (isAdmin && lesson.status === 'Approved');
  const superseder = lesson.supersededById ? numberOf(lesson.supersededById) : null;

  const act = async (action: () => Promise<void>) => {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await action();
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'That could not be done.');
    } finally {
      setBusy(false);
    }
  };

  if (editing)
    return (
      <LessonForm
        title={`Edit L-${lesson.number}`}
        initial={current}
        isAdmin={isAdmin}
        approveLabel={lesson.status === 'Approved' ? 'Save and approve' : undefined}
        onCancel={() => setEditing(false)}
        onSave={async (input) => {
          await onRevise(input);
          setEditing(false);
        }}
      />
    );

  return (
    <section id={`lesson-${lesson.number}`} className={focused ? 'section focused' : 'section'} aria-label={`L-${lesson.number}`} aria-current={focused || undefined}>
      <div className="section-head">
        <h3>L-{lesson.number}</h3>
        <span className="tag">revision {lesson.currentRevision}</span>
        {lesson.pendingEdit ? <span className="pill you">Edit waiting for approval</span> : null}
        {superseder ? <span className="pill neutral">Replaced by L-{superseder}</span> : null}
        {lesson.proposedByJobId ? <span className="pill neutral">Proposed by a reflection</span> : null}
      </div>
      <p>{current.rule}</p>
      {approved ? (
        <p className="small muted">
          Runs are given the approved revision {approved.revision} until the edit is approved: “{approved.rule}”
        </p>
      ) : null}
      <dl className="small kv">
        <dt>Topics</dt>
        <dd>{current.topics.join(', ')}</dd>
        {current.paths.length ? (
          <>
            <dt>Paths</dt>
            <dd className="mono">{current.paths.join(', ')}</dd>
          </>
        ) : null}
        <dt>Source</dt>
        <dd>
          {SOURCE[current.source]}
          {current.sourceThreadId ? (
            <>
              {' '}
              (
              <a
                href={`#/threads/${current.sourceThreadId}`}
                onClick={(e) => {
                  e.preventDefault();
                  navigate({ view: 'threads', threadId: current.sourceThreadId });
                }}
              >
                its thread
              </a>
              )
            </>
          ) : null}
        </dd>
        {current.reason ? (
          <>
            <dt>Why</dt>
            <dd>{current.reason}</dd>
          </>
        ) : null}
        <dt>Written by</dt>
        <dd>{current.author ?? 'an agent'}</dd>
        {lesson.status === 'Approved' ? (
          <>
            <dt>Approved by</dt>
            <dd>{(approved ?? current).approvedBy ?? '—'}</dd>
          </>
        ) : null}
      </dl>

      {replacing ? (
        <div className="raise">
          <div className="field">
            <label htmlFor={`replace-${lesson.id}`}>Replace with</label>
            <select id={`replace-${lesson.id}`} value={replacement} onChange={(e) => setReplacement(e.target.value)}>
              <option value="">Choose an approved lesson</option>
              {replacements.map((l) => (
                <option key={l.id} value={l.id}>
                  L-{l.number}: {l.current.rule.slice(0, 80)}
                </option>
              ))}
            </select>
          </div>
          <button className="btn small" type="button" disabled={!replacement || busy} onClick={() => void act(() => onSupersede(replacement))}>
            Replace
          </button>
          <button className="btn ghost small" type="button" onClick={() => setReplacing(false)}>
            Cancel
          </button>
        </div>
      ) : null}

      <ErrorNote message={error} />
      <div className="btn-row">
        {canEdit ? (
          <button className="btn small" type="button" onClick={() => setEditing(true)} disabled={busy}>
            Edit
          </button>
        ) : null}
        {isAdmin && (lesson.status === 'Suggested' || lesson.pendingEdit) ? (
          <button className="btn primary small" type="button" onClick={() => void act(() => onCommand('approve'))} disabled={busy}>
            {lesson.pendingEdit ? 'Approve the edit' : 'Remember for project'}
          </button>
        ) : null}
        {isAdmin && lesson.status === 'Suggested' ? (
          <button className="btn small" type="button" onClick={() => void act(() => onCommand('dismiss'))} disabled={busy}>
            Dismiss
          </button>
        ) : null}
        {isAdmin && lesson.status === 'Approved' ? (
          <button className="btn small" type="button" onClick={() => void act(() => onCommand(lesson.enabled ? 'disable' : 'enable'))} disabled={busy}>
            {lesson.enabled ? 'Disable' : 'Enable'}
          </button>
        ) : null}
        {isAdmin && lesson.status === 'Approved' && lesson.enabled && replacements.length > 0 && !replacing ? (
          <button className="btn ghost small" type="button" onClick={() => setReplacing(true)} disabled={busy}>
            Replace with another lesson
          </button>
        ) : null}
      </div>
      {!isAdmin && lesson.status === 'Suggested' ? <p className="small muted">An Admin approves a lesson before any run is given it.</p> : null}
    </section>
  );
}

function LessonForm({
  title,
  initial,
  isAdmin,
  approveLabel = 'Save and approve',
  onSave,
  onCancel,
}: {
  title: string;
  initial?: { rule: string; topics: string[]; paths: string[]; reason: string };
  isAdmin: boolean;
  approveLabel?: string;
  onSave: (input: LessonInput) => Promise<void>;
  onCancel: () => void;
}) {
  const [rule, setRule] = useState(initial?.rule ?? '');
  const [topics, setTopics] = useState(initial?.topics.join(', ') ?? '');
  const [paths, setPaths] = useState(initial?.paths.join(', ') ?? '');
  const [reason, setReason] = useState(initial?.reason ?? '');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const id = (part: string) => `lesson-form-${part}`;

  const save = async (approve: boolean) => {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await onSave({ rule: rule.trim(), topics: splitList(topics), paths: splitList(paths), reason: reason.trim(), approve });
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The lesson could not be saved.');
      setBusy(false);
    }
  };

  return (
    <form
      className="section stack"
      aria-label={title}
      onSubmit={(e: FormEvent) => {
        e.preventDefault();
        void save(false);
      }}
    >
      <h3>{title}</h3>
      <div className="field">
        <label htmlFor={id('rule')}>Lesson</label>
        <textarea id={id('rule')} rows={3} maxLength={600} value={rule} onChange={(e) => setRule(e.target.value)} />
        <span className="hint">One instruction for later tasks, at most 600 characters. No secrets, logs or stack traces.</span>
      </div>
      <div className="form-grid">
        <div className="field">
          <label htmlFor={id('topics')}>Topics</label>
          <input id={id('topics')} value={topics} onChange={(e) => setTopics(e.target.value)} placeholder="money, invoices" />
          <span className="hint">Comma-separated. A task whose title or request mentions one is given the lesson.</span>
        </div>
        <div className="field">
          <label htmlFor={id('paths')}>Paths (optional)</label>
          <input id={id('paths')} className="mono" value={paths} onChange={(e) => setPaths(e.target.value)} placeholder="src/Billing/**" />
          <span className="hint">Comma-separated globs it applies to.</span>
        </div>
      </div>
      <div className="field">
        <label htmlFor={id('reason')}>Why (optional)</label>
        <input id={id('reason')} value={reason} onChange={(e) => setReason(e.target.value)} />
      </div>
      <ErrorNote message={error} />
      <div className="btn-row">
        <button className="btn" type="submit" disabled={busy}>
          {isAdmin ? 'Save as a suggestion' : 'Save'}
        </button>
        {isAdmin ? (
          <button className="btn primary" type="button" disabled={busy} onClick={() => void save(true)}>
            {approveLabel}
          </button>
        ) : null}
        <button className="btn ghost" type="button" onClick={onCancel}>
          Cancel
        </button>
      </div>
    </form>
  );
}
