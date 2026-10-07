import { Fragment } from 'react';
import type { LifecycleState, Stage, Turn } from '../api/types';
import { STAGES, turnLabel } from '../domain/task';

/** The factory's mark: a carrier on a line. */
export function Mark() {
  return (
    <svg width="26" height="26" viewBox="0 0 26 26" aria-hidden="true">
      <rect x="1" y="1" width="24" height="24" rx="4" fill="var(--ink)" />
      <path d="M5 17h16" stroke="var(--bg)" strokeWidth="2" strokeDasharray="3 2" />
      <rect x="7" y="8" width="5" height="6" rx="1" fill="var(--amber)" />
      <rect x="14" y="8" width="5" height="6" rx="1" fill="var(--bg)" />
    </svg>
  );
}

/** Whose turn it is, as a pill. */
export function TurnPill({ turn }: { turn: Turn }) {
  const label = turnLabel(turn);
  return <span className={`pill ${label.cls}`}>{label.text}</span>;
}

/** Text with `code` spans and @factory mentions marked up. Everything else is plain text. */
export function Rich({ text }: { text: string }) {
  const parts = text.split(/(`[^`\n]+`|@factory\b)/g);
  return (
    <>
      {parts.map((part, index) =>
        part.length > 2 && part.startsWith('`') && part.endsWith('`') ? (
          <code key={index}>{part.slice(1, -1)}</code>
        ) : part === '@factory' ? (
          <span key={index} className="mention">
            {part}
          </span>
        ) : (
          <Fragment key={index}>{part}</Fragment>
        ),
      )}
    </>
  );
}

/**
 * The stage rail: where the task is on the line. M1 has no spec stage, so once a task is past
 * it the station is drawn as skipped rather than as done.
 */
export function Rail({ stage, state, turn }: { stage: Stage; state: LifecycleState; turn: Turn }) {
  const index = STAGES.indexOf(stage);
  const you = turn === 'AwaitingYou';
  const finished = state === 'Accepted';
  // A cancelled task is not at any station: the rail shows how far it got, and nothing as current.
  const abandoned = state === 'Cancelled';

  return (
    <div className="rail" role="list" aria-label="Stage">
      {STAGES.map((name, i) => {
        const past = i < index || (finished && i === index);
        const skipped = past && name === 'Spec';
        const now = i === index && !finished && !abandoned;
        const classes = ['st'];
        if (past) classes.push(skipped ? 'skip' : 'done');
        if (now) {
          classes.push('now');
          if (you) classes.push('you');
          if (state === 'Running') classes.push('moving');
        }

        return (
          <div className={classes.join(' ')} role="listitem" key={name} aria-current={now ? 'step' : undefined}>
            <i>{skipped ? '–' : past ? '✓' : i + 1}</i>
            <span>
              {name}
              {skipped ? ' (skipped)' : ''}
            </span>
          </div>
        );
      })}
    </div>
  );
}

/** A link that only ever goes to an https address, whatever the data says. */
export function SafeLink({ href, children }: { href: string | null | undefined; children: React.ReactNode }) {
  if (!href || !/^https:\/\//i.test(href)) return <>{children}</>;
  return (
    <a href={href} target="_blank" rel="noreferrer noopener">
      {children}
    </a>
  );
}

export function ErrorNote({ message }: { message: string | null }) {
  return message ? (
    <p className="small error" role="alert">
      {message}
    </p>
  ) : null;
}
