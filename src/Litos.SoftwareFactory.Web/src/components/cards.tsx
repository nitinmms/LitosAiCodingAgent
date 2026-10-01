import { useState } from 'react';
import type { Decision, HandoffEvidence, Message, Thread } from '../api/types';
import { fmt, pct, shortSha, words } from '../domain/format';
import { SafeLink } from './bits';

interface DecisionPayload {
  question?: string;
  whyItBlocks?: string;
  options?: string[];
  recommendation?: string | null;
  impact?: string | null;
}

/** A choice the agent could not reasonably make itself. Nothing is edited until it is answered. */
export function DecisionCard({
  message,
  decision,
  busy,
  onAnswer,
}: {
  message: Message;
  /** The decision's current record; absent only if the thread was loaded without it. */
  decision: Decision | undefined;
  busy: boolean;
  onAnswer: (decisionId: string, answer: string) => void;
}) {
  const payload = (message.payload ?? {}) as DecisionPayload;
  const question = decision?.question ?? payload.question ?? message.text;
  const why = decision?.whyItBlocks ?? payload.whyItBlocks;
  const options = decision?.options ?? payload.options ?? [];
  const recommendation = decision?.recommendation ?? payload.recommendation;
  const impact = decision?.impact ?? payload.impact;
  const open = decision ? decision.status === 'Open' : false;
  const decisionId = decision?.id ?? message.decisionId;

  return (
    <div className={`panel${open ? ' you' : ''}`} role="group" aria-label="Decision needed">
      <div className="panel-head">
        <h2>Decision needed</h2>
        <span className={`pill ${open ? 'you' : 'done'}`}>{open ? 'Awaiting you' : 'Answered'}</span>
      </div>
      <p>
        <b>{question}</b>
      </p>
      {why ? <p className="muted">{why}</p> : null}
      <div className="opts">
        {options.map((option) => (
          <button
            key={option}
            className={`opt${decision?.answer === option ? ' chosen' : ''}`}
            disabled={!open || busy || !decisionId}
            onClick={() => decisionId && onAnswer(decisionId, option)}
          >
            <span>{option}</span>
            {recommendation === option ? <span className="small muted">Recommended</span> : null}
          </button>
        ))}
      </div>
      {recommendation && !options.includes(recommendation) ? (
        <p className="small">
          <b>Recommendation:</b> {recommendation}
        </p>
      ) : null}
      {decision?.answer && !options.includes(decision.answer) ? (
        <p className="small">
          <b>Your answer:</b> {decision.answer}
        </p>
      ) : null}
      <p className="small muted">
        {impact ? `Affects ${impact.replace(/\.$/, '')}. ` : ''}
        {open
          ? 'No further edits run until this is answered. You can also reply in your own words.'
          : 'The answer is part of the task and is carried into every later run.'}
      </p>
    </div>
  );
}

const count = (n: number, noun: string) => `${fmt(n)} ${noun}${n === 1 ? '' : 's'}`;

function unitTestLine(e: HandoffEvidence): string {
  const figures: string[] = [];
  if (e.testsPassed || e.testsFailed) figures.push(`${fmt(e.testsPassed)} passed`);
  if (e.testsFailed) figures.push(`${fmt(e.testsFailed)} failed`);
  if (e.testsSkipped) figures.push(`${fmt(e.testsSkipped)} skipped`);
  if (e.newTests) figures.push(`${fmt(e.newTests)} new`);
  return figures.length ? `${words(e.unitTests)} (${figures.join(', ')})` : words(e.unitTests);
}

function coverageLine(e: HandoffEvidence): string {
  if (e.changedLineCoveragePercent === null) return words(e.coverage);
  const threshold = e.coverageThresholdPercent === null ? '' : ` (threshold ${pct(e.coverageThresholdPercent)})`;
  return `${pct(e.changedLineCoveragePercent)}${threshold}`;
}

function reviewLine(e: HandoffEvidence): string {
  const blocking = e.findings.filter((f) => f.severity === 'Blocking').length;
  const minor = e.findings.length - blocking;
  switch (e.review) {
    case 'Clean':
      return 'Clean';
    case 'FindingsFixed':
      return minor ? `${count(blocking, 'finding')} fixed, ${count(minor, 'minor finding')} open` : `${count(blocking, 'finding')} fixed`;
    case 'FindingsOpen':
      return `${count(e.findings.length, 'finding')} open`;
    default:
      return 'Not run';
  }
}

/**
 * The handoff: what was built, the evidence the host measured, and what is left for a person
 * to test. Only the newest handoff of a task waiting for testing can be accepted.
 */
export function HandoffCard({
  message,
  thread,
  latest,
  busy,
  onAccept,
  onRequestChanges,
}: {
  message: Message;
  thread: Thread;
  latest: boolean;
  busy: boolean;
  onAccept: () => void;
  onRequestChanges: () => void;
}) {
  const [checked, setChecked] = useState<Record<number, boolean>>({});
  const e = message.payload as HandoffEvidence | null;
  const active = latest && thread.state === 'AwaitingHumanTesting';
  const accepted = latest && thread.state === 'Accepted';

  if (!e) {
    return (
      <div className="panel">
        <h2>Handoff</h2>
        <p>{message.text}</p>
      </div>
    );
  }

  return (
    <div className={`panel${active ? ' you' : accepted ? ' okp' : ''}`} role="group" aria-label="Handoff">
      <div className="panel-head">
        <h2>{active ? 'Ready for human testing' : 'Handoff'}</h2>
        <span className={`pill ${active ? 'you' : accepted ? 'done' : 'neutral'}`}>
          {active ? 'Awaiting you' : accepted ? 'Accepted' : latest ? 'Closed' : 'Superseded'}
        </span>
      </div>
      {e.summary ? <p>{e.summary}</p> : null}
      <dl className="kv">
        <dt>Branch</dt>
        <dd className="mono">{e.branch}</dd>
        {e.commitSha ? (
          <>
            <dt>Commit</dt>
            <dd className="mono" title={e.commitSha}>
              {shortSha(e.commitSha)}
            </dd>
          </>
        ) : null}
        {e.pullRequestNumber !== null ? (
          <>
            <dt>Pull request</dt>
            <dd>
              <SafeLink href={e.pullRequestUrl}>Draft PR #{e.pullRequestNumber}</SafeLink>
            </dd>
          </>
        ) : null}
        <dt>Build</dt>
        <dd>{words(e.build)}</dd>
        <dt>Unit tests</dt>
        <dd>{unitTestLine(e)}</dd>
        <dt>Changed-line coverage</dt>
        <dd>{coverageLine(e)}</dd>
        <dt>Agent review</dt>
        <dd>{reviewLine(e)}</dd>
        <dt>Tokens</dt>
        <dd className="num">
          {fmt(e.tokensUsed)}
          {e.budgetCap === null ? ' used (no cap)' : ` of ${fmt(e.budgetCap)} used`}
        </dd>
      </dl>

      {e.criteria.length ? (
        <>
          <h3>Acceptance criteria and evidence</h3>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Criterion</th>
                  <th>Covered by</th>
                </tr>
              </thead>
              <tbody>
                {e.criteria.map((c, i) => (
                  <tr key={i}>
                    <td>{c.criterion}</td>
                    <td className={c.tests.length ? 'mono' : ''}>
                      {c.tests.length ? c.tests.join(', ') : c.manualOnly ? 'Manual testing only' : 'No test named'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      ) : null}

      {e.findings.length ? (
        <>
          <h3>Review findings</h3>
          <ul>
            {e.findings.map((f, i) => (
              <li key={i}>
                <span className={`tag ${f.severity === 'Blocking' ? 'bad' : ''}`}>{f.severity}</span>{' '}
                <span className="mono">
                  {f.file}
                  {f.line ? `:${f.line}` : ''}
                </span>{' '}
                {f.text}
              </li>
            ))}
          </ul>
        </>
      ) : null}

      {e.preExistingFailures.length ? (
        <p className="small">
          <b>Already failing before this task:</b> <span className="mono">{e.preExistingFailures.join(', ')}</span>
        </p>
      ) : null}
      {e.unmeasuredFiles.length ? (
        <p className="small">
          <b>Not measured for coverage:</b> <span className="mono">{e.unmeasuredFiles.join(', ')}</span>
        </p>
      ) : null}
      {e.knownLimitations.length ? (
        <p className="small">
          <b>Not verified:</b> {e.knownLimitations.map((l) => l.replace(/\.?$/, '.')).join(' ')}
        </p>
      ) : null}
      <p className="small muted">Unit tests are not application testing. That part is yours.</p>

      {e.manualTestSteps.length ? (
        <>
          <h3>Manual tests for you</h3>
          <div className="checks">
            {e.manualTestSteps.map((step, i) => (
              <label key={i} className="check">
                <input
                  type="checkbox"
                  disabled={!active}
                  checked={accepted || !!checked[i]}
                  onChange={(event) => setChecked({ ...checked, [i]: event.target.checked })}
                />
                <span>{step}</span>
              </label>
            ))}
          </div>
        </>
      ) : null}

      {e.commands.length ? (
        <details>
          <summary className="small">Commands the host ran</summary>
          <ul className="small mono">
            {e.commands.map((c, i) => (
              <li key={i}>{c}</li>
            ))}
          </ul>
        </details>
      ) : null}

      {active ? (
        <div className="btn-row">
          <button className="btn primary" onClick={onAccept} disabled={busy}>
            Accept
          </button>
          <button className="btn" onClick={onRequestChanges} disabled={busy}>
            Request changes
          </button>
        </div>
      ) : null}
    </div>
  );
}

/**
 * Shown when a task has stopped and only a person can restart it: the budget ran out, the run
 * was blocked, or the host lost its worker. It says why, and offers the way forward.
 */
export function StopPanel({
  thread,
  busy,
  onResume,
  onRaiseAndResume,
}: {
  thread: Thread;
  busy: boolean;
  onResume: () => void;
  onRaiseAndResume: (cap: number) => void;
}) {
  const committed = thread.tokensUsed + thread.tokensReserved;
  const [cap, setCap] = useState(() => String(Math.max(thread.budgetCap ?? 0, committed) + 100_000));

  if (thread.state === 'PausedBudget') {
    const next = Number(cap);
    const valid = Number.isInteger(next) && next > committed;
    return (
      <div className="panel you" role="group" aria-label="Budget paused">
        <div className="panel-head">
          <h2>Paused before the next model call</h2>
          <span className="pill you">Awaiting you</span>
        </div>
        <p>{thread.stateReason ?? 'The next request does not fit the remaining budget, so nothing was sent.'}</p>
        <p className="small muted">The changes made so far and the checkpoint are kept.</p>
        {thread.budgetCap === null ? (
          <div className="btn-row">
            <button className="btn attn" onClick={onResume} disabled={busy}>
              Resume
            </button>
          </div>
        ) : (
          <form
            className="raise"
            onSubmit={(event) => {
              event.preventDefault();
              if (valid) onRaiseAndResume(next);
            }}
          >
            <div className="field">
              <label htmlFor="raise-cap">New token budget</label>
              <input id="raise-cap" type="number" min={committed + 1} value={cap} onChange={(e) => setCap(e.target.value)} />
              <span className="hint">
                {fmt(thread.tokensUsed)} used of {fmt(thread.budgetCap)}. The new budget must be above {fmt(committed)}.
              </span>
            </div>
            <button className="btn attn" type="submit" disabled={busy || !valid}>
              Raise budget and resume
            </button>
          </form>
        )}
      </div>
    );
  }

  if (thread.state === 'Blocked' || thread.state === 'Interrupted') {
    const blocked = thread.state === 'Blocked';
    return (
      <div className="panel you" role="group" aria-label={blocked ? 'Blocked' : 'Interrupted'}>
        <div className="panel-head">
          <h2>{blocked ? 'Blocked' : 'Interrupted'}</h2>
          <span className="pill you">Awaiting you</span>
        </div>
        <p>
          {thread.stateReason ??
            (blocked ? 'The run stopped and cannot continue by itself.' : 'The run was cut off before it finished.')}
        </p>
        <p className="small muted">
          {blocked
            ? 'Fix what stopped it, then resume. Resuming gives the run a fresh repair allowance.'
            : 'The work on the branch is kept. Resuming continues from the last checkpoint.'}
        </p>
        <div className="btn-row">
          <button className="btn attn" onClick={onResume} disabled={busy}>
            {blocked ? 'Resume' : 'Recover and resume'}
          </button>
        </div>
      </div>
    );
  }

  return null;
}
