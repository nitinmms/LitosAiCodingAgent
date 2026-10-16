import { useEffect, useState } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { FlowMetrics, Project } from '../api/types';
import { fmt } from '../domain/format';
import { ErrorNote } from './bits';
import { Operations } from './Operations';

export const PERIODS = [7, 30, 90] as const;

/** Hours as people read them: minutes under an hour, days over two. */
export const duration = (hours: number): string =>
  hours < 1 ? `${Math.round(hours * 60)} min` : hours < 48 ? `${hours.toFixed(1)} h` : `${(hours / 24).toFixed(1)} days`;

const percent = (share: number | null) => (share === null ? '—' : `${Math.round(share * 100)}%`);

/** State and stage names as the app shows them elsewhere. */
const label = (name: string) => name.replace(/([a-z])([A-Z])/g, '$1 $2');

/**
 * The flow metrics (ReadMe_LitosSoftwareFactory_V1.md §12.1, m4-architecture.md §6), over the projects
 * the person belongs to: one project or all, for the last 7, 30 or 90 days of tasks.
 */
export function MetricsPage({ api, projects, isAdmin = false }: { api: FactoryApi; projects: Project[]; isAdmin?: boolean }) {
  const [projectId, setProjectId] = useState<string>('');
  const [days, setDays] = useState<(typeof PERIODS)[number]>(30);
  const [metrics, setMetrics] = useState<FlowMetrics | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let current = true;
    setMetrics(null);
    api.metrics(projectId || null, days).then(
      (m) => current && (setMetrics(m), setError(null)),
      (failure: unknown) => current && setError(failure instanceof ApiError ? failure.message : 'The metrics could not be loaded.'),
    );
    return () => {
      current = false;
    };
  }, [api, projectId, days]);

  return (
    <div className="page stack">
      <section className="section">
        <div className="section-head">
          <h2>Metrics</h2>
          <select aria-label="Project" value={projectId} onChange={(e) => setProjectId(e.target.value)}>
            <option value="">All my projects</option>
            {projects.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
        </div>
        <nav className="tabs" role="tablist" aria-label="Period">
          {PERIODS.map((p) => (
            <button key={p} role="tab" aria-selected={p === days} onClick={() => setDays(p)}>
              Last {p} days
            </button>
          ))}
        </nav>
        <p className="small muted">Tasks created in the period. Open decisions are those waiting now, on any task.</p>
      </section>

      <ErrorNote message={error} />
      {metrics === null && !error ? <p className="small muted">Loading…</p> : null}
      {metrics ? <Report metrics={metrics} /> : null}
      {isAdmin ? <Operations api={api} /> : null}
    </div>
  );
}

function Report({ metrics: m }: { metrics: FlowMetrics }) {
  const median = (rounds: { rounds: number; tasks: number }[]) => {
    const all = rounds.flatMap((r) => Array<number>(r.tasks).fill(r.rounds));
    return all.length ? all[Math.ceil(all.length / 2) - 1]! : null;
  };
  const medianRounds = median(m.reworkRounds);

  return (
    <>
      <section className="section" aria-label="Summary">
        <dl className="kv">
          <dt>Tasks</dt>
          <dd>
            {fmt(m.tasks)} ({fmt(m.accepted)} accepted, {fmt(m.cancelled)} cancelled, {fmt(m.open)} open)
          </dd>
          <dt>Accepted at the first handoff</dt>
          <dd>{percent(m.firstHandoffAcceptance)}</dd>
          <dt>Change requests per accepted task</dt>
          <dd>
            {medianRounds === null ? '—' : `median ${medianRounds}`}
            {m.reworkRounds.length ? ` (${m.reworkRounds.map((r) => `${r.tasks} with ${r.rounds}`).join(', ')})` : ''}
          </dd>
          <dt>Tokens per task</dt>
          <dd>
            {m.tokensPerTask.count ? `median ${fmt(m.tokensPerTask.median)}, 90th percentile ${fmt(m.tokensPerTask.p90)}` : '—'} ({fmt(m.tokensTotal)} in
            all)
          </dd>
          <dt>Open decisions</dt>
          <dd>
            {fmt(m.openDecisions)}
            {m.oldestOpenDecision ? (
              <>
                {'; the oldest has waited '}
                {duration(m.oldestOpenDecision.ageHours)}:{' '}
                <a href={`#/threads/${m.oldestOpenDecision.threadId}`}>{m.oldestOpenDecision.title}</a> — {m.oldestOpenDecision.question}
              </>
            ) : null}
          </dd>
          <dt>Queue wait for a repository</dt>
          <dd>{m.queueForRepository.count ? `${fmt(m.queueForRepository.count)} waits, median ${m.queueForRepository.median.toFixed(0)} min, 90th percentile ${m.queueForRepository.p90.toFixed(0)} min` : 'none'}</dd>
          <dt>Queue wait for a slot</dt>
          <dd>{m.queueForSlot.count ? `${fmt(m.queueForSlot.count)} waits, median ${m.queueForSlot.median.toFixed(0)} min, 90th percentile ${m.queueForSlot.p90.toFixed(0)} min` : 'none'}</dd>
        </dl>
      </section>

      <TimeTable title="Time in each state" rows={m.timeInState} />
      <TimeTable title="Time in each stage" rows={m.timeInStage} />

      <section className="section" aria-label="Tokens by phase and model">
        <h3>Tokens by phase and model</h3>
        <div className="form-grid">
          {[
            ['Phase', m.tokensByPhase],
            ['Model', m.tokensByModel],
          ].map(([name, rows]) => (
            <table key={name as string} className="small">
              <thead>
                <tr>
                  <th scope="col">{name as string}</th>
                  <th scope="col">Tokens</th>
                  <th scope="col">Share</th>
                </tr>
              </thead>
              <tbody>
                {(rows as FlowMetrics['tokensByPhase']).map((r) => (
                  <tr key={r.name}>
                    <td className={name === 'Model' ? 'mono' : undefined}>{r.name}</td>
                    <td>{fmt(r.tokens)}</td>
                    <td>{percent(r.share)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ))}
        </div>
      </section>

      <section className="section" aria-label="Lessons">
        <h3>Lessons</h3>
        <p className="small">
          Given to runs {fmt(m.lessons.included)} times, left out past the limit {fmt(m.lessons.leftOut)} times. Of those given, people said{' '}
          {fmt(m.lessons.helped)} helped, {fmt(m.lessons.didNotMatter)} did not matter and {fmt(m.lessons.misled)} misled; {fmt(m.lessons.unjudged)} were not
          judged.
        </p>
      </section>
    </>
  );
}

function TimeTable({ title, rows }: { title: string; rows: FlowMetrics['timeInState'] }) {
  return (
    <section className="section" aria-label={title}>
      <h3>{title}</h3>
      {rows.length === 0 ? (
        <p className="small muted">No tasks in the period.</p>
      ) : (
        <table className="small">
          <thead>
            <tr>
              <th scope="col">{title.endsWith('stage') ? 'Stage' : 'State'}</th>
              <th scope="col">Tasks</th>
              <th scope="col">Median per task</th>
              <th scope="col">90th percentile</th>
              <th scope="col">In all</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.name}>
                <td>{label(r.name)}</td>
                <td>{fmt(r.count)}</td>
                <td>{duration(r.medianHours)}</td>
                <td>{duration(r.p90Hours)}</td>
                <td>{duration(r.totalHours)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
