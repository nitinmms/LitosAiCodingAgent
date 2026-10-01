import { useEffect, useRef, useState } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { CurrentUser, Message, Settings, ThreadDetails } from '../api/types';
import { initials } from '../domain/format';
import { canCancel, canMessage, canPause, isClosed, parseMention, stateName, withMention } from '../domain/task';
import { Rail, Rich, SafeLink, TurnPill } from './bits';
import { DecisionCard, HandoffCard, StopPanel } from './cards';

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
  onNotice,
}: {
  api: FactoryApi;
  details: ThreadDetails;
  user: CurrentUser;
  settings: Settings | null;
  reload: () => Promise<void>;
  /** Tells the user something happened, or went wrong, outside the conversation. */
  onNotice: (text: string) => void;
}) {
  const { thread, project, messages, decisions } = details;
  const [draft, setDraft] = useState('');
  const [busy, setBusy] = useState(false);
  const [confirmingCancel, setConfirmingCancel] = useState(false);
  const textarea = useRef<HTMLTextAreaElement>(null);
  const end = useRef<HTMLDivElement>(null);
  // The id sent with the current draft, kept so that sending the same text again after a
  // failure is recognised by the host as the same message.
  const sending = useRef<{ text: string; id: string } | null>(null);

  const count = messages.length;
  useEffect(() => {
    if (count > 1) end.current?.scrollIntoView?.({ block: 'nearest', behavior: 'smooth' });
  }, [count]);

  useEffect(() => setConfirmingCancel(false), [thread.state]);

  const openDecision = thread.state === 'AwaitingDecision' ? decisions.find((d) => d.status === 'Open') : undefined;
  const lastHandoff = [...messages].reverse().find((m) => m.kind === 'Handoff');
  const closed = isClosed(thread.state);
  const answering = !!openDecision;
  const acceptsText = answering || canMessage(thread.state);
  const request = parseMention(draft);
  const sendable = answering ? draft.trim().length > 0 : request !== null;
  const userName = user.displayName || user.userName;

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
    });
    if (sent) {
      sending.current = null;
      setDraft('');
    }
  };

  const startRequest = () => {
    setDraft((current) => withMention(current));
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
      case 'Handoff':
        return (
          <HandoffCard
            key={m.id}
            message={m}
            thread={thread}
            latest={m === lastHandoff}
            busy={busy}
            onAccept={() => void act(() => api.accept(thread.id), 'Accepted. The branch is yours to merge.')}
            onRequestChanges={startRequest}
          />
        );
      case 'Status':
        return (
          <div key={m.id} className={`ev ${m.author === 'User' ? 'sys' : ''}`}>
            <span />
            <span>
              <Rich text={m.text} />
            </span>
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
                {initials(userName)}
              </span>
              <b>{userName}</b>
              {m.kind === 'DecisionAnswer' ? <span>answered the decision</span> : null}
            </div>
            <div className="bubble">
              {/* The host stores the request without its leading mention. */}
              {m.kind === 'Text' ? <span className="mention">@factory </span> : null}
              <Rich text={m.text} />
            </div>
          </div>
        );
    }
  };

  return (
    <div className="center">
      <section className="th-head">
        <div className="th-title">
          <h1>{thread.title}</h1>
          <TurnPill state={thread.state} />
          <span className="tag">{thread.typeLabel}</span>
        </div>
        <div className="th-sub">
          <span>
            {project.name} ({project.gitHub})
          </span>
          {thread.branch ? <span className="mono">{thread.branch}</span> : <span>Branch created on delegation</span>}
          {thread.pullRequestNumber !== null ? (
            <span>
              <SafeLink href={thread.pullRequestUrl}>Draft PR #{thread.pullRequestNumber}</SafeLink>
            </span>
          ) : null}
          <span>{stateName(thread.state)}</span>
        </div>
        <Rail stage={thread.stage} state={thread.state} />
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
          </div>
        ) : null}
        {messages.map(renderMessage)}
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
          {canPause(thread.state) ? (
            <button className="btn" onClick={() => void act(() => api.pause(thread.id))} disabled={busy}>
              Pause
            </button>
          ) : null}
          {thread.state === 'PausedUser' ? (
            <button className="btn primary" onClick={() => void act(() => api.resume(thread.id))} disabled={busy}>
              Resume
            </button>
          ) : null}
          {thread.state === 'AwaitingHumanTesting' ? (
            <button className="btn" onClick={startRequest} disabled={busy}>
              Request changes
            </button>
          ) : null}
          {canCancel(thread.state) ? (
            confirmingCancel ? (
              <>
                <button
                  className="btn danger"
                  onClick={() => void act(() => api.cancel(thread.id)).then(() => setConfirmingCancel(false))}
                  disabled={busy}
                >
                  Yes, cancel this task
                </button>
                <button className="btn ghost" onClick={() => setConfirmingCancel(false)}>
                  Keep it
                </button>
                <span className="small muted">A cancelled task cannot be restarted. Its branch is kept.</span>
              </>
            ) : (
              <button className="btn" onClick={() => setConfirmingCancel(true)} disabled={busy}>
                Cancel task
              </button>
            )
          ) : null}
          {confirmingCancel ? null : <StateHint state={thread.state} branch={thread.branch} />}
        </div>

        {closed ? null : (
          <>
            <div className="compose-box">
              <button className="btn ghost" title="Delegate to the factory" onClick={startRequest} disabled={answering || !acceptsText}>
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
                    : acceptsText
                      ? 'Start with @factory, then say what you want done'
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
                {answering ? 'Answer' : 'Send'}
              </button>
            </div>
            <p className="compose-hint">
              {!answering && draft.trim() && request === null ? (
                <span className="warn">Start the message with @factory followed by what you want done. </span>
              ) : null}
              <b>@factory</b> delegates within the task budget. Model: <span className="mono">{thread.model}</span> on{' '}
              {thread.provider}
              {settings ? `. PTC ${settings.ptcEnabled ? 'on' : 'off'}` : ''}.
            </p>
          </>
        )}
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
