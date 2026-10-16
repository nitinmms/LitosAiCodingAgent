import { useEffect, useState } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { Counters } from '../api/types';
import { fmt } from '../domain/format';
import { ErrorNote } from './bits';
import { duration } from './MetricsPage';

const label = (name: string) => name.replace(/([a-z])([A-Z])/g, '$1 $2');
const share = (value: number | null) => (value === null ? '' : ` (${Math.round(value * 100)}%)`);

/**
 * The operational counters (ReadMe_LitosSoftwareFactory_V1.md §24, m4-architecture.md §7), for Admins:
 * read when shown, with Refresh. A run the host is not executing is what to look at first.
 */
export function Operations({ api }: { api: FactoryApi }) {
  const [counters, setCounters] = useState<Counters | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = () =>
    api.counters().then(
      (c) => (setCounters(c), setError(null)),
      (failure: unknown) => setError(failure instanceof ApiError ? failure.message : 'The counters could not be loaded.'),
    );

  useEffect(() => {
    void load();
  }, [api]);

  return (
    <section className="section" aria-label="Operations">
      <div className="section-head">
        <h3>Operations</h3>
        <span className="tag">Admins</span>
        <button className="btn ghost small" type="button" onClick={() => void load()}>
          Refresh
        </button>
      </div>
      <ErrorNote message={error} />
      {counters ? <CountersView c={counters} /> : !error ? <p className="small muted">Loading…</p> : null}
    </section>
  );
}

function CountersView({ c }: { c: Counters }) {
  const attention = c.runs.filter((r) => r.needsAttention);
  return (
    <div className="stack">
      <dl className="kv">
        <dt>Tasks</dt>
        <dd>
          {fmt(c.tasks.queued)} queued, {fmt(c.tasks.running)} running
          {c.tasks.stopped.length ? `; stopped: ${c.tasks.stopped.map((s) => `${fmt(s.count)} ${label(s.state).toLowerCase()}`).join(', ')}` : ''}
        </dd>
        <dt>Open decisions</dt>
        <dd>
          {fmt(c.openDecisions)}
          {c.oldestDecision ? (
            <>
              {'; the oldest has waited '}
              {duration(c.oldestDecision.ageHours)}: <a href={`#/threads/${c.oldestDecision.threadId}`}>{c.oldestDecision.title}</a>
            </>
          ) : null}
        </dd>
        <dt>Runs needing attention</dt>
        <dd className={attention.length ? 'error' : undefined}>
          {attention.length ? `${attention.length}: marked running, but this host is not executing them` : 'none'}
        </dd>
        <dt>Unsettled reservations</dt>
        <dd className={c.unsettled.count ? 'error' : undefined}>
          {c.unsettled.count ? `${fmt(c.unsettled.count)} calls holding ${fmt(c.unsettled.tokens)} tokens after their run stopped` : 'none'}
        </dd>
        <dt>Event streams</dt>
        <dd>
          {fmt(c.eventStreams.open)} open
          {c.eventStreams.open ? `; the slowest caught up ${fmt(c.eventStreams.slowestSecondsBehind)} s ago` : ''}
        </dd>
        <dt>Push and pull request failures</dt>
        <dd className={c.pushFailures.count ? 'error' : undefined}>
          {fmt(c.pushFailures.count)} in the last {c.pushFailures.days} days
          {c.pushFailures.latest.length ? (
            <>
              {', latest: '}
              <a href={`#/threads/${c.pushFailures.latest[0]!.threadId}`}>{c.pushFailures.latest[0]!.title}</a>
            </>
          ) : null}
        </dd>
        <dt>Lesson reflections</dt>
        <dd>
          {fmt(c.reflections.failed)} failed, {fmt(c.reflections.deferred)} deferred, {fmt(c.reflections.pending)} queued or running,{' '}
          {fmt(c.reflections.completed)} done
        </dd>
      </dl>

      {c.runs.length ? (
        <table className="small" aria-label="Running runs">
          <thead>
            <tr>
              <th scope="col">Task</th>
              <th scope="col">Run</th>
              <th scope="col">Running for</th>
              <th scope="col">Last checkpoint</th>
              <th scope="col">Executed here</th>
              <th scope="col">Worker</th>
            </tr>
          </thead>
          <tbody>
            {c.runs.map((r) => (
              <tr key={r.runId} className={r.needsAttention ? 'error' : undefined}>
                <td>
                  <a href={`#/threads/${r.threadId}`}>{r.title}</a>
                </td>
                <td>{r.kind}</td>
                <td>{r.minutesRunning === null ? '—' : duration(r.minutesRunning / 60)}</td>
                <td>{r.minutesSinceCheckpoint === null ? '—' : `${duration(r.minutesSinceCheckpoint / 60)} ago`}</td>
                <td>{r.executed ? 'yes' : 'no'}</td>
                <td>{r.workerAlive ? 'alive' : 'none'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : null}

      <table className="small" aria-label="Use against the quotas">
        <thead>
          <tr>
            <th scope="col">Person</th>
            <th scope="col">Today{c.quotas.daily ? ` (quota ${fmt(c.quotas.daily)})` : ''}</th>
            <th scope="col">This month{c.quotas.monthly ? ` (quota ${fmt(c.quotas.monthly)})` : ''}</th>
          </tr>
        </thead>
        <tbody>
          {c.users.length === 0 ? (
            <tr>
              <td colSpan={3} className="muted">
                No use this month.
              </td>
            </tr>
          ) : (
            c.users.map((u) => (
              <tr key={u.userId}>
                <td>{u.name}</td>
                <td>
                  {fmt(u.day)}
                  {share(u.dayShare)}
                </td>
                <td>
                  {fmt(u.month)}
                  {share(u.monthShare)}
                </td>
              </tr>
            ))
          )}
        </tbody>
      </table>
    </div>
  );
}
