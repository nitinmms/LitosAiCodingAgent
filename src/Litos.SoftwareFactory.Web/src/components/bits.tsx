import { Fragment, useEffect, type ReactNode } from 'react';
import type { LifecycleState, Stage, Turn } from '../api/types';
import { STAGES, taskLabel } from '../domain/task';

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

/** Whose turn it is, as a pill; a cancelled task's says Cancelled. */
export function TurnPill({ turn, state }: { turn: Turn; state: LifecycleState }) {
  const label = taskLabel(turn, state);
  return <span className={`pill ${label.cls}`}>{label.text}</span>;
}

/** One block of a message: a heading, a list, or a paragraph. */
export type Block = { kind: 'heading'; text: string } | { kind: 'bullets' | 'numbers'; items: string[] } | { kind: 'paragraph'; text: string };

/**
 * The few Markdown blocks the model writes in its answers and notes: `#` headings, `-`/`*` and
 * numbered lists, and paragraphs between blank lines. Anything else is paragraph text, as written.
 */
export function blocks(text: string): Block[] {
  const result: Block[] = [];
  let paragraph: string[] = [];
  const endParagraph = () => {
    if (paragraph.length > 0) result.push({ kind: 'paragraph', text: paragraph.join('\n') });
    paragraph = [];
  };
  const addItem = (kind: 'bullets' | 'numbers', item: string) => {
    endParagraph();
    const last = result[result.length - 1];
    if (last?.kind === kind) last.items.push(item);
    else result.push({ kind, items: [item] });
  };

  for (const line of text.split('\n')) {
    const heading = /^#{1,6}\s+(.+)$/.exec(line);
    const bullet = /^\s*[-*]\s+(.+)$/.exec(line);
    const numbered = /^\s*\d+[.)]\s+(.+)$/.exec(line);
    if (line.trim() === '') endParagraph();
    else if (heading) {
      endParagraph();
      result.push({ kind: 'heading', text: heading[1]!.trim() });
    } else if (bullet) addItem('bullets', bullet[1]!);
    else if (numbered) addItem('numbers', numbered[1]!);
    else paragraph.push(line);
  }
  endParagraph();
  return result;
}

/** A line's marks: `code`, **bold** and @factory mentions. Never HTML: the text stays text. */
function Inline({ text }: { text: string }) {
  const parts = text.split(/(`[^`\n]+`|\*\*[^*\n]+\*\*|@factory\b)/g);
  return (
    <>
      {parts.map((part, index) =>
        part.length > 2 && part.startsWith('`') && part.endsWith('`') ? (
          <code key={index}>{part.slice(1, -1)}</code>
        ) : part.length > 4 && part.startsWith('**') && part.endsWith('**') ? (
          <strong key={index}>{part.slice(2, -2)}</strong>
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
 * Message text: a single paragraph stays inline text, as it always was; headings, lists and
 * several paragraphs (a chat answer, the decision scan's assumptions) are laid out as blocks.
 */
export function Rich({ text }: { text: string }) {
  const parts = blocks(text);
  if (parts.length === 0 || (parts.length === 1 && parts[0]!.kind === 'paragraph')) return <Inline text={text} />;

  return (
    <>
      {parts.map((block, index) => {
        switch (block.kind) {
          case 'heading':
            return (
              <div key={index} className="md-h">
                <Inline text={block.text} />
              </div>
            );
          case 'paragraph':
            return (
              <div key={index} className="md-p">
                <Inline text={block.text} />
              </div>
            );
          default: {
            const List = block.kind === 'bullets' ? 'ul' : 'ol';
            return (
              <List key={index} className="md-list">
                {block.items.map((item, i) => (
                  <li key={i}>
                    <Inline text={item} />
                  </li>
                ))}
              </List>
            );
          }
        }
      })}
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

/**
 * Asks the browser to confirm leaving the page while a form has changes not yet saved. Browsers
 * show their own wording; the page can only ask for the prompt.
 */
export function useUnsavedWarning(changed: boolean) {
  useEffect(() => {
    if (!changed) return;
    const warn = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      // Older browsers prompt only when returnValue is set.
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [changed]);
}

/**
 * A settings form's save and discard buttons, kept in view at the bottom of the window, saying
 * when there are changes not yet saved. A change made far up a long form is otherwise easy to
 * leave unsaved, and lost on a reload.
 */
export function SaveBar({ changed, children }: { changed: boolean; children: ReactNode }) {
  useUnsavedWarning(changed);
  return (
    <div className={`save-bar${changed ? ' dirty' : ''}`}>
      <span className="small save-state" aria-live="polite">
        {changed ? 'Unsaved changes' : 'All changes saved'}
      </span>
      <div className="btn-row">{children}</div>
    </div>
  );
}
