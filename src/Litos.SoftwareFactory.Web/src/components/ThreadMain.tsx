import { useEffect, useRef, useState, type FormEvent } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { ChatProgress, CurrentUser, Message, PullRequestState, Settings, ThreadDetails } from '../api/types';
import { elapsed, initials } from '../domain/format';
import {
  canCancel,
  canChat,
  canMessage,
  canPause,
  canResume,
  canWithdraw,
  chatWork,
  isBareMention,
  isClosed,
  isPlainMessage,
  parseMention,
  parseSpec,
  pullRequestLabel,
  stateName,
  withMention,
} from '../domain/task';
import { ErrorNote, Rail, Rich, SafeLink, TurnPill } from './bits';
import { DecisionCard, HandoffCard, SpecCard, StopPanel } from './cards';

/** How often GitHub is asked where the task's pull request stands; the host caches the answer too. */
const PULL_REQUEST_REFRESH_MS = 60_000;

/** A fresh id per message: the host uses it to make a retried send safe. */
const newMessageId = (): string =>
  typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;

export function ThreadMain({
  api,
  details,
  user,
  settings,
  reload,
  connection = 'live',
  onNotice,
}: {
  api: FactoryApi;
  details: ThreadDetails;
  user: CurrentUser;
  settings: Settings | null;
  reload: () => Promise<void>;
  /** Whether this thread's live updates are arriving. */
  connection?: 'live' | 'reconnecting';
  /** Tells the user something happened, or went wrong, outside the conversation. */
  onNotice: (text: string) => void;
}) {
  const { thread, project, messages, decisions, run, handoff } = details;
  const [draft, setDraft] = useState('');
  const [busy, setBusy] = useState(false);
  const [confirmingCancel, setConfirmingCancel] = useState(false);
  // Asked to stop while running (a 202): shown until the task leaves Running.
  const [stopping, setStopping] = useState<'pause' | 'cancel' | null>(null);
  const [editing, setEditing] = useState(false);
  // Renaming or re-filing a thread is its owner's, or an admin's.
  const canEdit = thread.ownerId === user.id || user.roles.includes('Admin');
  const textarea = useRef<HTMLTextAreaElement>(null);
  const end = useRef<HTMLDivElement>(null);
  // The id sent with the current draft, kept so that sending the same text again after a
  // failure is recognised by the host as the same message.
  const sending = useRef<{ text: string; id: string } | null>(null);

  const count = messages.length;
  useEffect(() => {
    if (count > 1) end.current?.scrollIntoView?.({ block: 'nearest', behavior: 'smooth' });
  }, [count]);

  useEffect(() => {
    setConfirmingCancel(false);
    setStopping(null);
  }, [thread.state]);
  // The state event can arrive before the 202 itself, so it counts only while still running.
  const stoppingNow = thread.state === 'Running' ? stopping : null;

  // The factory opens the draft; marking it ready, merging and closing happen on GitHub. So the
  // label comes from GitHub, asked again whenever the task changes state and once a minute.
  const [pullRequestState, setPullRequestState] = useState<PullRequestState | undefined>(undefined);
  const hasPullRequest = thread.pullRequestNumber !== null;
  useEffect(() => {
    if (!hasPullRequest) {
      setPullRequestState(undefined);
      return;
    }

    let stopped = false;
    const ask = () => {
      api
        .pullRequest(thread.id)
        .then((info) => !stopped && setPullRequestState(info.state))
        // Not knowing is fine: the label then says only "PR #n".
        .catch(() => {});
    };
    ask();
    const timer = setInterval(ask, PULL_REQUEST_REFRESH_MS);
    return () => {
      stopped = true;
      clearInterval(timer);
    };
  }, [api, thread.id, thread.state, hasPullRequest]);

  const openDecision = thread.state === 'AwaitingDecision' ? decisions.find((d) => d.status === 'Open') : undefined;
  const lastHandoff = [...messages].reverse().find((m) => m.kind === 'Handoff');
  const answering = !!openDecision;
  const chatting = canChat(thread.state);
  const working = thread.state === 'Queued' || thread.state === 'Running';
  // An accepted task takes no more work, but can still be asked about.
  const closed = isClosed(thread.state) && !chatting;
  const acceptsText = answering || canMessage(thread.state) || chatting;
  const request = parseMention(draft);
  // "@factory spec ...": a specification, not the work (m2-architecture.md §5).
  const specRequest = request === null ? null : parseSpec(request);
  const asksForSpec = specRequest !== null;
  const specWaiting = thread.state === 'Draft' && details.spec !== null && !details.spec.approved;
  const specApproved = thread.state === 'Draft' && details.spec?.approved === true;
  // Without @factory, a message is a question (or, to a working task, guidance for its agent).
  const asking = !answering && request === null && draft.trim().length > 0 && !isBareMention(draft);
  const waitingForAnswer = details.chatPending && chatting;
  const sendable = answering
    ? draft.trim().length > 0
    : asksForSpec
      ? thread.state === 'Draft' && specRequest.length > 0
      : request !== null
        ? canMessage(thread.state) && !specWaiting
        : asking && (working || (chatting && !waitingForAnswer));
  // After a handoff an @factory message is a change request: it starts a rework run.
  const requestingChanges = !answering && thread.state === 'AwaitingHumanTesting' && request !== null && !asksForSpec;
  // A rework run that has not produced its own handoff yet can be taken back.
  const reworkUnderWay = run?.kind === 'Rework' && run.status !== 'Finished' && handoff !== null;

  /** Runs one action against the host, then shows whatever it changed. */
  const act = async (action: () => Promise<unknown>, done?: string) => {
    if (busy) return false;
    setBusy(true);
    try {
      await action();
      if (done) onNotice(done);
      await reload();
      return true;
    } catch (failure) {
      onNotice(failure instanceof ApiError ? failure.message : 'That did not work. Try again.');
      // The refusal usually means the task moved on; show where it is now.
      await reload();
      return false;
    } finally {
      setBusy(false);
    }
  };

  const send = async () => {
    if (!sendable || busy) return;
    const text = draft.trim();

    if (openDecision) {
      if (await act(() => api.answerDecision(openDecision.id, text))) setDraft('');
      return;
    }

    if (sending.current?.text !== text) sending.current = { text, id: newMessageId() };
    const { id } = sending.current;
    const sent = await act(async () => {
      const result = await api.postMessage(thread.id, id, text);
      if (result.outcome === 'FollowUp') onNotice('Sent to the agent. It reads the message at its next safe point.');
      else if (result.outcome === 'Chat') onNotice('Litos is reading the code to answer. Nothing is changed.');
      else if (requestingChanges && result.outcome === 'Queued')
        onNotice('Change request sent: a rework run starts on the same branch. You can withdraw it if that is not what you meant.');
    });
    if (sent) {
      sending.current = null;
      setDraft('');
    }
  };

  /** Pause or cancel. A running task answers before it has stopped, so that is shown until it has. */
  const stop = (kind: 'pause' | 'cancel') =>
    act(async () => {
      const result = await (kind === 'pause' ? api.pause(thread.id) : api.cancel(thread.id));
      if (result.stopping) setStopping(kind);
    });

  const startRequest = () => {
    setDraft((current) => withMention(current));
    textarea.current?.focus();
  };

  /** Puts "@factory spec " in front of the draft, to ask for a revision of the specification. */
  const startRevision = () => {
    setDraft((current) => {
      const rest = current.trim();
      return parseMention(current) !== null ? current : `@factory spec ${rest}`.trimEnd() + ' ';
    });
    textarea.current?.focus();
  };

  /** After approval: the request that builds it, ready to send. */
  const startBuild = () => {
    setDraft('@factory Build the approved specification.');
    textarea.current?.focus();
  };

  const renderMessage = (m: Message) => {
    switch (m.kind) {
      case 'Decision':
        return (
          <DecisionCard
            key={m.id}
            message={m}
            decision={decisions.find((d) => d.id === m.decisionId)}
            busy={busy}
            onAnswer={(decisionId, answer) => void act(() => api.answerDecision(decisionId, answer))}
          />
        );
      case 'Spec':
        return (
          <SpecCard
            key={m.id}
            message={m}
            status={details.spec}
            thread={thread}
            busy={busy}
            onApprove={(revision) => void act(() => api.approveSpec(thread.id, revision), `Specification revision ${revision} approved. Send @factory to build it.`)}
            onRevise={startRevision}
            onBuild={startBuild}
          />
        );
      case 'Handoff':
        return (
          <HandoffCard
            key={m.id}
            message={m}
            thread={thread}
            latest={m === lastHandoff}
            pullRequestState={pullRequestState}
            busy={busy}
            onAccept={() => void act(() => api.accept(thread.id), 'Accepted. The branch is yours to merge.')}
            onRequestChanges={startRequest}
          />
        );
      case 'Status':
        return (
          // The marker is the row's ::before, which takes the grid's first column.
          <div key={m.id} className={`ev ${m.author === 'User' ? 'sys' : ''}`}>
            <div className="ev-text">
              <Rich text={m.text} />
            </div>
            <span />
          </div>
        );
      default:
        if (m.author === 'Factory') {
          return (
            <div key={m.id} className="m">
              <div className="m-who">
                <b>Litos</b>
              </div>
              <div className="bubble">
                <Rich text={m.text} />
              </div>
            </div>
          );
        }

        return (
          <div key={m.id} className="m user">
            <div className="m-who">
              <span className="avatar" aria-hidden="true">
                {initials(m.authorName ?? '')}
              </span>
              {/* Whoever wrote it: a thread has more than one person on it, not only the viewer. */}
              <b>{m.authorName ?? 'Someone'}</b>
              {m.kind === 'DecisionAnswer' ? <span>answered the decision</span> : null}
            </div>
            <div className="bubble">
              {/* The host stores a request without its leading mention, and marks a plain message. */}
              {m.kind === 'Text' && !isPlainMessage(m) ? <span className="mention">@factory </span> : null}
              <Rich text={m.text} />
            </div>
          </div>
        );
    }
  };

  return (
    <div className="center">
      <section className="th-head">
        {editing ? (
          <EditThread
            api={api}
            threadId={thread.id}
            title={thread.title}
            typeLabel={thread.typeLabel}
            types={settings?.taskTypes ?? [thread.typeLabel]}
            onDone={async (changed) => {
              setEditing(false);
              if (changed) await reload();
            }}
          />
        ) : (
          <div className="th-title">
            <h1>{thread.title}</h1>
            <TurnPill turn={thread.turn} state={thread.state} />
            <span className="tag">{thread.typeLabel}</span>
            {connection === 'reconnecting' ? (
              <span className="pill you" role="status" title="Live updates stopped. What is shown may be out of date.">
                Reconnecting…
              </span>
            ) : null}
            {canEdit ? (
              <button className="btn ghost small" onClick={() => setEditing(true)}>
                Rename or change type
              </button>
            ) : null}
          </div>
        )}
        <div className="th-sub">
          <span>
            {project.name} ({project.gitHub})
          </span>
          {thread.branch ? <span className="mono">{thread.branch}</span> : <span>Branch created on delegation</span>}
          {thread.pullRequestNumber !== null ? (
            <span>
              <SafeLink href={thread.pullRequestUrl}>{pullRequestLabel(thread.pullRequestNumber, pullRequestState)}</SafeLink>
            </span>
          ) : null}
          <span>{stateName(thread.state)}</span>
        </div>
        <Rail stage={thread.stage} state={thread.state} turn={thread.turn} />
      </section>

      <div className="convo" aria-live="polite">
        {messages.length === 0 ? (
          <div className="panel">
            <h2>Delegate this task</h2>
            <p>
              Describe the change, starting with <span className="mention">@factory</span>. Litos works on its own branch of{' '}
              {project.gitHub}, builds, tests and reviews the change, then hands it back for you to test. It never merges or
              pushes to <span className="mono">{project.defaultBranch}</span>.
            </p>
            <p>
              Not sure yet? Ask a question without <span className="mention">@factory</span>: Litos reads the code and answers,
              and changes nothing. Or start with <span className="mention">@factory spec</span> to have Litos write a
              specification you approve before anything is built.
            </p>
          </div>
        ) : null}
        {messages.map(renderMessage)}
        {waitingForAnswer ? <ChatWaiting progress={details.chatProgress} /> : null}
        <StopPanel
          key={`${thread.state}-${thread.budgetCap}`}
          thread={thread}
          busy={busy}
          onResume={() => void act(() => api.resume(thread.id))}
          onRaiseAndResume={(cap) =>
            void act(async () => {
              await api.setBudget(thread.id, cap);
              await api.resume(thread.id);
            })
          }
        />
        <div ref={end} />
      </div>

      <div className="composer">
        <div className="controls">
          {stoppingNow ? (
            <span className="pill neutral" role="status">
              {stoppingNow === 'pause' ? 'Pausing…' : 'Cancelling…'}
            </span>
          ) : canPause(thread.state) ? (
            <button className="btn" onClick={() => void stop('pause')} disabled={busy}>
              Pause
            </button>
          ) : null}
          {/* A budget-paused task resumes from its panel, which offers raising the cap too. */}
          {canResume(thread.state) && thread.state !== 'PausedBudget' ? (
            <button className="btn primary" onClick={() => void act(() => api.resume(thread.id))} disabled={busy}>
              {thread.state === 'Interrupted' ? 'Recover and resume' : 'Resume'}
            </button>
          ) : null}
          {thread.state === 'AwaitingHumanTesting' ? (
            <button className="btn" onClick={startRequest} disabled={busy}>
              Request changes
            </button>
          ) : null}
          {reworkUnderWay && canWithdraw(thread.state) ? (
            <button
              className="btn"
              onClick={() => void act(() => api.withdraw(thread.id), 'Change request withdrawn. The task is back at its last handoff.')}
              disabled={busy}
              title="Drop this change request, discard what it had edited, and go back to the last handoff"
            >
              Withdraw change request
            </button>
          ) : null}
          {canCancel(thread.state) && !stoppingNow ? (
            confirmingCancel ? (
              <>
                <button
                  className="btn danger"
                  onClick={() => void stop('cancel').then(() => setConfirmingCancel(false))}
                  disabled={busy}
                >
                  {thread.state === 'Draft' ? 'Yes, cancel this draft' : 'Yes, cancel this task'}
                </button>
                <button className="btn ghost" onClick={() => setConfirmingCancel(false)}>
                  Keep it
                </button>
                <span className="small muted">
                  {thread.state === 'Draft'
                    ? 'A cancelled draft cannot be delegated later.'
                    : 'A cancelled task cannot be restarted. Its branch is kept.'}
                </span>
              </>
            ) : (
              <button className="btn" onClick={() => setConfirmingCancel(true)} disabled={busy}>
                Cancel task
              </button>
            )
          ) : null}
          {confirmingCancel ? null : stoppingNow ? (
            <span className="small muted">
              Litos stops at its next safe point, usually within a minute.{' '}
              {stoppingNow === 'pause' ? 'The work so far is kept.' : 'The branch is kept.'}
            </span>
          ) : reworkUnderWay && thread.state === 'Running' ? (
            <span className="small muted">Litos is working on your change request. Pause it if you want to withdraw it.</span>
          ) : (
            <StateHint state={thread.state} branch={thread.branch} />
          )}
        </div>

        {closed ? null : (
          <>
            <div className="compose-box">
              <button className="btn ghost" title="Delegate to the factory" onClick={startRequest} disabled={answering || !canMessage(thread.state)}>
                <span className="mention">@factory</span>
              </button>
              <textarea
                id="composer"
                ref={textarea}
                rows={2}
                aria-label="Message"
                value={draft}
                disabled={!acceptsText}
                placeholder={
                  answering
                    ? 'Answer the decision in your own words, or pick an option above'
                    : working
                      ? 'Guidance for the agent, with or without @factory'
                      : chatting && !canMessage(thread.state)
                        ? 'Ask a question about the code'
                        : acceptsText
                          ? 'Ask a question, or start with @factory to say what you want done'
                          : 'Resume the task before sending it anything'
                }
                onChange={(e) => setDraft(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' && !e.shiftKey) {
                    e.preventDefault();
                    void send();
                  }
                }}
              />
              <button className="btn primary" onClick={() => void send()} disabled={!sendable || busy}>
                {answering ? 'Answer' : asksForSpec ? 'Ask for a spec' : requestingChanges ? 'Send change request' : asking && chatting ? 'Ask' : 'Send'}
              </button>
            </div>
            <p className="compose-hint">
              {!answering && isBareMention(draft) ? <span className="warn">Say what you want done after @factory. </span> : null}
              {!answering && asksForSpec && !specRequest && thread.state === 'Draft' ? (
                <span className="warn">Say what the specification is for after @factory spec. </span>
              ) : null}
              {!answering && asksForSpec && thread.state !== 'Draft' ? (
                <span className="warn">A specification is written before the work starts; this task has been delegated. </span>
              ) : null}
              {!answering && request !== null && !asksForSpec && specWaiting ? (
                <span className="warn">
                  Approve specification revision {details.spec?.revision}, or ask for changes with @factory spec, before delegating.{' '}
                </span>
              ) : null}
              {!answering && request !== null && !asksForSpec && !canMessage(thread.state) ? (
                <span className="warn">This task is closed to new work; ask without @factory. </span>
              ) : null}
              {specApproved && !draft.trim() ? (
                <span>
                  Specification revision {details.spec?.revision} is approved: <b>@factory</b> builds it.{' '}
                </span>
              ) : null}
              {waitingForAnswer ? <span className="warn">Litos is still answering your last question. </span> : null}
              {working ? (
                <>Anything you send now reaches the agent at its next safe point. </>
              ) : thread.state === 'AwaitingHumanTesting' ? (
                <>
                  After a handoff, <b>@factory</b> asks for changes: it starts a rework run on this branch. Without it, Litos
                  answers questions about the branch and changes nothing.{' '}
                </>
              ) : chatting && !canMessage(thread.state) ? (
                <>Litos answers questions about the code. Nothing is changed. </>
              ) : (
                <>
                  <b>@factory</b> delegates within the task budget; <b>@factory spec</b> asks for a specification first. Without
                  it, Litos answers questions about the code and changes nothing.{' '}
                </>
              )}
              Model: <span className="mono">{thread.model}</span> on{' '}
              {thread.provider}
              {thread.ptcEnabled === null ? '' : `. PTC ${thread.ptcEnabled ? 'on' : 'off'}`}.
            </p>
          </>
        )}
      </div>
    </div>
  );
}

/**
 * The answer on its way: what Litos is doing now, what it has done, and for how long. The host
 * reports at most once a second; the clock runs here between reports.
 */
function ChatWaiting({ progress }: { progress: ChatProgress | null }) {
  // Until the first report, the time counts from when this appeared.
  const [shownAt] = useState(() => Date.now());
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);

  const started = progress ? Date.parse(progress.startedAt) : shownAt;
  const work = chatWork(progress);
  return (
    <div className="m" aria-label="Litos is answering">
      <div className="m-who">
        <b>Litos</b>
        <span className="small muted">{elapsed((now - (Number.isNaN(started) ? shownAt : started)) / 1000)}</span>
      </div>
      <div className="bubble muted">
        <div>{progress ? `${progress.activity}...` : 'Reading the code to answer...'}</div>
        {work ? <div className="small">{work} so far</div> : null}
      </div>
    </div>
  );
}

function StateHint({ state, branch }: { state: ThreadDetails['thread']['state']; branch: string | null }) {
  switch (state) {
    case 'Running':
      return <span className="small muted">Litos is working. Messages you send now reach it at its next safe point.</span>;
    case 'Queued':
      return <span className="small muted">Queued. It starts when a run slot is free.</span>;
    case 'PausedUser':
      return <span className="small muted">Paused. The work so far is kept.</span>;
    case 'AwaitingHumanTesting':
      return (
        <span className="small muted">
          Pull <span className="mono">{branch}</span>, test it, then accept or request changes.
        </span>
      );
    case 'Accepted':
      return <span className="small muted">Accepted. This task is closed; the branch is yours to merge.</span>;
    case 'Cancelled':
      return <span className="small muted">Cancelled. This task is closed.</span>;
    default:
      return null;
  }
}

/** Renames the thread or files it under another task type (PATCH /api/threads/{id}). */
function EditThread({
  api,
  threadId,
  title,
  typeLabel,
  types,
  onDone,
}: {
  api: FactoryApi;
  threadId: string;
  title: string;
  typeLabel: string;
  types: string[];
  onDone: (changed: boolean) => void | Promise<void>;
}) {
  const [newTitle, setNewTitle] = useState(title);
  const [newType, setNewType] = useState(typeLabel);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const save = async (event: FormEvent) => {
    event.preventDefault();
    if (busy) return;
    const trimmed = newTitle.trim();
    if (!trimmed) {
      setError('The title cannot be empty.');
      return;
    }
    if (trimmed === title && newType === typeLabel) {
      await onDone(false);
      return;
    }

    setBusy(true);
    setError(null);
    try {
      await api.editThread(threadId, { title: trimmed === title ? undefined : trimmed, typeLabel: newType === typeLabel ? undefined : newType });
      await onDone(true);
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The thread could not be changed.');
      setBusy(false);
    }
  };

  return (
    <form className="th-edit" onSubmit={save} aria-label="Rename or change type">
      <div className="field">
        <label htmlFor="te-title">Title</label>
        <input id="te-title" value={newTitle} onChange={(e) => setNewTitle(e.target.value)} maxLength={300} autoFocus />
      </div>
      <div className="field">
        <label htmlFor="te-type">Type</label>
        <select id="te-type" value={newType} onChange={(e) => setNewType(e.target.value)}>
          {types.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
      </div>
      <ErrorNote message={error} />
      <div className="btn-row">
        <button className="btn primary small" type="submit" disabled={busy}>
          Save
        </button>
        <button className="btn ghost small" type="button" onClick={() => void onDone(false)}>
          Cancel
        </button>
      </div>
    </form>
  );
}
