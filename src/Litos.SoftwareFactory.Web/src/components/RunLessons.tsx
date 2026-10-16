import { useState } from 'react';
import type { Lesson, LessonVerdict, RunLesson } from '../api/types';

/** How each verdict on a lesson is offered (m4-architecture.md §4.5). */
const VERDICTS: [LessonVerdict, string][] = [
  ['Helped', 'Helped'],
  ['DidNotMatter', "Didn't matter"],
  ['Misled', 'Misled'],
];

/** What the run-details panel needs to show and act on a run's lessons. */
export interface RunLessonsActions {
  /** A person's verdict on an included lesson; pressing the chosen one again clears it. */
  onVerdict: (lessonId: string, verdict: LessonVerdict | null) => void;
  /** The project's approved, enabled lessons, for picking one for the task's next run. */
  loadApproved: () => Promise<Lesson[]>;
  /** The lessons picked for the task's next runs. */
  onPick: (lessonIds: string[]) => Promise<void>;
}

/**
 * The lessons the latest run was given, and those left out past the limit (m4-architecture.md §4.4),
 * with a verdict per lesson given; and the lessons a person picked for the task's next runs (§4.1).
 */
export function RunLessonsBox({ lessons, manual, actions }: { lessons: RunLesson[]; manual: string[]; actions?: RunLessonsActions }) {
  const given = lessons.filter((l) => l.inclusion !== 'NotIncluded');
  const left = lessons.filter((l) => l.inclusion === 'NotIncluded');
  const [approved, setApproved] = useState<Lesson[] | null>(null);
  const [choice, setChoice] = useState('');
  const [busy, setBusy] = useState(false);

  const picked = (approved ?? []).filter((l) => manual.includes(l.id));
  const pickable = (approved ?? []).filter((l) => !manual.includes(l.id));
  const pick = async (ids: string[]) => {
    if (!actions || busy) return;
    setBusy(true);
    try {
      await actions.onPick(ids);
      setChoice('');
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="box" aria-label="Lessons">
      <h3>Lessons</h3>
      {given.length === 0 && left.length === 0 ? <p className="small muted">This run was given no project lessons.</p> : null}
      {given.length ? (
        <ul className="small findings" aria-label="Lessons given to this run">
          {given.map((lesson) => (
            <li key={lesson.lessonId}>
              <span className="mono">
                L-{lesson.number} r{lesson.revision}
              </span>{' '}
              {lesson.rule}
              {lesson.manual ? <span className="muted"> (picked)</span> : null}
              {actions ? (
                <span className="verdicts" role="group" aria-label={`Did L-${lesson.number} help?`}>
                  {VERDICTS.map(([value, label]) => (
                    <button
                      key={value}
                      type="button"
                      className="verdict"
                      aria-pressed={lesson.verdict === value}
                      onClick={() => actions.onVerdict(lesson.lessonId, lesson.verdict === value ? null : value)}
                    >
                      {label}
                    </button>
                  ))}
                </span>
              ) : null}
            </li>
          ))}
        </ul>
      ) : null}
      {left.length ? (
        <p className="small muted">Left out, past the limit: {left.map((l) => `L-${l.number}`).join(', ')}.</p>
      ) : null}

      {actions ? (
        approved === null ? (
          <button className="btn ghost small" type="button" onClick={() => void actions.loadApproved().then(setApproved)}>
            Pick a lesson for the next run
          </button>
        ) : (
          <div className="stack">
            {picked.length ? (
              <p className="small">
                Picked for the next run:{' '}
                {picked.map((l, i) => (
                  <span key={l.id}>
                    {i ? ', ' : ''}L-{l.number}{' '}
                    <button
                      className="btn ghost small"
                      type="button"
                      aria-label={`Unpick L-${l.number}`}
                      disabled={busy}
                      onClick={() => void pick(manual.filter((id) => id !== l.id))}
                    >
                      ×
                    </button>
                  </span>
                ))}
              </p>
            ) : null}
            {pickable.length ? (
              <div className="raise">
                <div className="field">
                  <label htmlFor="pick-lesson">Lesson</label>
                  <select id="pick-lesson" value={choice} onChange={(e) => setChoice(e.target.value)}>
                    <option value="">Choose an approved lesson</option>
                    {pickable.map((l) => (
                      <option key={l.id} value={l.id}>
                        L-{l.number}: {l.current.rule.slice(0, 80)}
                      </option>
                    ))}
                  </select>
                </div>
                <button className="btn small" type="button" disabled={!choice || busy} onClick={() => void pick([...manual, choice])}>
                  Pick
                </button>
              </div>
            ) : (
              <p className="small muted">No other approved lesson to pick.</p>
            )}
          </div>
        )
      ) : null}
    </section>
  );
}
