import { useState } from 'react';
import { ApiError } from '../api/client';
import type { Lesson, LessonCommand, Reflection } from '../api/types';
import { navigate } from '../domain/route';
import { ErrorNote } from './bits';

const STATUS: Record<Reflection['status'], string> = {
  Pending: 'Lesson reflection is queued.',
  Running: 'Lesson reflection is running.',
  DeferredBudget: 'Lesson reflection is deferred: the task has less than the reflection allowance left.',
  Completed: 'Lesson reflection is done.',
  Failed: 'Lesson reflection failed.',
  Skipped: 'Lesson reflection was skipped.',
};

/**
 * After acceptance (m4-architecture.md §5): where the task's lesson reflection stands, Retry and
 * Skip when it is deferred or failed, and a card per proposed lesson. Only an Admin remembers a
 * proposal for the project or dismisses it; anyone can open it on the Lessons page to edit it.
 */
export function ReflectionPanel({
  reflection,
  proposed,
  isAdmin,
  onRetry,
  onSkip,
  onCommand,
}: {
  reflection: Reflection;
  proposed: Lesson[];
  isAdmin: boolean;
  onRetry: () => Promise<void>;
  onSkip: () => Promise<void>;
  onCommand: (lesson: Lesson, command: LessonCommand) => Promise<void>;
}) {
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
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
  const stuck = reflection.status === 'DeferredBudget' || reflection.status === 'Failed';

  return (
    <div className="panel stack" aria-label="Lesson reflection">
      <h2>Lessons from this task</h2>
      <p className="small">
        {STATUS[reflection.status]}
        {reflection.status === 'Failed' && reflection.lastError ? ` (${reflection.lastError})` : ''}
      </p>
      {stuck ? (
        <div className="btn-row">
          <button className="btn small" type="button" disabled={busy} onClick={() => void act(onRetry)}>
            Retry the reflection
          </button>
          <button className="btn ghost small" type="button" disabled={busy} onClick={() => void act(onSkip)}>
            Skip it
          </button>
        </div>
      ) : null}
      {proposed.map((lesson) => (
        <section key={lesson.id} className="section" aria-label={`Proposed L-${lesson.number}`}>
          <div className="section-head">
            <h3>L-{lesson.number}</h3>
            <span className="tag">{lesson.status === 'Approved' ? 'remembered' : lesson.status === 'Dismissed' ? 'dismissed' : 'proposed'}</span>
          </div>
          <p>{lesson.current.rule}</p>
          <p className="small muted">
            Topics: {lesson.current.topics.join(', ')}
            {lesson.current.paths.length ? `. Paths: ${lesson.current.paths.join(', ')}` : ''}
            {lesson.current.reason ? `. Why: ${lesson.current.reason}` : ''}
          </p>
          {lesson.status === 'Suggested' ? (
            <div className="btn-row">
              {isAdmin ? (
                <button className="btn primary small" type="button" disabled={busy} onClick={() => void act(() => onCommand(lesson, 'approve'))}>
                  Remember for project
                </button>
              ) : null}
              <button className="btn small" type="button" onClick={() => navigate({ view: 'lessons', projectId: lesson.projectId })}>
                Edit
              </button>
              {isAdmin ? (
                <button className="btn ghost small" type="button" disabled={busy} onClick={() => void act(() => onCommand(lesson, 'dismiss'))}>
                  Dismiss
                </button>
              ) : null}
            </div>
          ) : null}
          {lesson.status === 'Suggested' && !isAdmin ? <p className="small muted">An Admin approves a lesson before any run is given it.</p> : null}
        </section>
      ))}
      <ErrorNote message={error} />
    </div>
  );
}
