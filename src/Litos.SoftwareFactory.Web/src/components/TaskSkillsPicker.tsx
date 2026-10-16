import { useState } from 'react';
import { ApiError } from '../api/client';
import type { TaskSkillOption } from '../api/types';
import { fmt } from '../domain/format';
import { ErrorNote } from './bits';

/** The instruction text these chosen skills add to every session's system prompt. */
export const chosenSize = (options: TaskSkillOption[], chosen: string[]): number =>
  options.filter((o) => chosen.includes(o.name)).reduce((total, o) => total + o.chars, 0);

/** The skills a new task of this type starts with chosen, in the library's order. */
export const defaultsFor = (options: TaskSkillOption[], type: string): string[] =>
  options.filter((o) => o.defaultFor.includes(type)).map((o) => o.name);

/**
 * The library skills chosen for a task (m3-architecture.md §13): each one's whole text goes into
 * every session's system prompt, so the total is shown against the limit the host enforces.
 */
export function TaskSkillsPicker({
  options,
  max,
  value,
  onChange,
  legend = 'Skills for this task',
}: {
  options: TaskSkillOption[];
  max: number;
  value: string[];
  onChange: (chosen: string[]) => void;
  legend?: string;
}) {
  const size = chosenSize(options, value);
  const toggle = (name: string, on: boolean) => {
    const next = on ? [...value, name] : value.filter((n) => n !== name);
    onChange(options.filter((o) => next.includes(o.name)).map((o) => o.name));
  };

  return (
    <fieldset className="checks">
      <legend className="small">{legend}</legend>
      {options.map((o) => (
        <label key={o.name} className="check">
          <input type="checkbox" checked={value.includes(o.name)} onChange={(e) => toggle(o.name, e.target.checked)} />
          <span>
            <span className="mono">{o.name}</span> <span className="muted">{o.description}</span>
          </span>
        </label>
      ))}
      <span className={size > max ? 'hint error' : 'hint'}>
        {fmt(size)} of {fmt(max)} characters. Each chosen skill is given whole to every step of the task's runs, and kept through
        compaction.
      </span>
    </fieldset>
  );
}

/**
 * Changes a task's skills. A task that is working keeps the ones it started with until it is
 * paused and resumed; the host posts a note in the thread saying when the change applies.
 */
export function EditTaskSkills({
  options,
  max,
  current,
  working,
  onSave,
  onCancel,
}: {
  options: TaskSkillOption[];
  max: number;
  current: string[];
  /** Whether a run is under way, which keeps its skills until it is paused and resumed. */
  working: boolean;
  onSave: (chosen: string[]) => Promise<void>;
  onCancel: () => void;
}) {
  const [chosen, setChosen] = useState(() => current.filter((name) => options.some((o) => o.name === name)));
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const gone = current.filter((name) => !options.some((o) => o.name === name));

  const save = async () => {
    if (busy) return;
    if (chosenSize(options, chosen) > max) {
      setError(`A task's skills can come to at most ${fmt(max)} characters. Untick one.`);
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await onSave(chosen);
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : "The task's skills could not be changed.");
      setBusy(false);
    }
  };

  return (
    <div className="panel stack" aria-label="Change the task's skills">
      <TaskSkillsPicker options={options} max={max} value={chosen} onChange={setChosen} />
      {gone.length ? (
        <p className="small muted">No longer in the library, so not applied, and dropped on saving: {gone.join(', ')}.</p>
      ) : null}
      {working ? <p className="small muted">The task is working: the change is applied after it is paused and resumed.</p> : null}
      <ErrorNote message={error} />
      <div className="btn-row">
        <button className="btn primary small" type="button" onClick={() => void save()} disabled={busy}>
          Save skills
        </button>
        <button className="btn small" type="button" onClick={onCancel}>
          Cancel
        </button>
      </div>
    </div>
  );
}
