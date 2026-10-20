import { useEffect, useState } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { Counters, FlowMetrics, Project, TimeIn } from '../api/types';
import { fmt } from '../domain/format';
import { duration, label, percent, tokens } from '../domain/metrics';
import { navigate } from '../domain/route';
import { ErrorNote } from './bits';
import { Bullet, Columns, DotStrip, Key, Meter, RangeRows, Sparkline, TipLayer, type Segment } from './charts';
import { Guidance } from './Guidance';
import { Operations } from './Operations';

export const PERIODS = [7, 30, 90] as const;
export type MetricsTab = 'flow' | 'guidance';

export { duration } from '../domain/metrics';

/** The M1 exit gate (ReadMe §18): 7 of 12 tasks accepted with at most one rework. */
export const FIRST_HANDOFF_MARK = 7 / 12;

/** Whose turn each state is (§7.1), for "where the time goes". */
export const WHOSE: Record<string, 'factory' | 'people' | 'stopped' | 'queued'> = {
  Running: 'factory',
  Draft: 'people',
  AwaitingDecision: 'people',
  AwaitingHumanTesting: 'people',
  Queued: 'queued',
  PausedBudget: 'stopped',
  PausedUser: 'stopped',
  Blocked: 'stopped',
  Interrupted: 'stopped',
};
export const GROUPS = [
  { id: 'factory', name: 'Factory working', color: 'var(--petrol)' },
  { id: 'people', name: 'Waiting on people', color: 'var(--amber)' },
  { id: 'stopped', name: 'Stopped (budget, paused, blocked)', color: 'var(--idle)' },
  { id: 'queued', name: 'Queued for a lock or slot', color: 'var(--queue)' },
] as const;

/** The stages in order (§5.1), and whose turn each mostly is. */
const STAGES: [string, 'factory' | 'people'][] = [
  ['Discuss', 'people'],
  ['Spec', 'people'],
  ['Implement', 'factory'],
  ['Verify', 'factory'],
  ['Review', 'factory'],
  ['Handoff', 'people'],
];

/** Each phase keeps its colour whatever else is shown; anything else is "Other". */
const PHASES: [string, string][] = [
  ['Implement', 'var(--s1)'],
  ['Repair', 'var(--s2)'],
  ['Rework', 'var(--s3)'],
  ['Review', 'var(--s4)'],
  ['Scan', 'var(--s5)'],
  ['Spec', 'var(--s6)'],
];
const phaseOf = (name: string) => (name === 'LightReview' || name === 'NoReview' ? 'Review' : PHASES.some(([p]) => p === name) ? name : 'Other');
export const PROJECT_COLORS = ['var(--s1)', 'var(--s2)', 'var(--s3)', 'var(--s4)', 'var(--s5)', 'var(--s6)'];

const VIEW_KEY = 'litos-metrics-view';
const readView = (): 'charts' | 'tables' => {
  try {
    return localStorage.getItem(VIEW_KEY) === 'tables' ? 'tables' : 'charts';
  } catch {
    return 'charts';
  }
};

/**
 * The Metrics tab (ReadMe_LitosSoftwareFactory_V1.md §12.1, m4-architecture.md §6 and §12), over the
 * projects the person belongs to: one project or all, for the last 7, 30 or 90 days of tasks. Flow
 * shows where tasks spend their time and tokens; Skills & lessons shows how the project lessons and
 * the skills are doing, so they can be improved. Charts or, for anyone who reads numbers better, the same figures as tables.
 */
export function MetricsPage({
  api,
  projects,
  tab = 'flow',
  isAdmin = false,
  onDispatchChanged,
}: {
  api: FactoryApi;
  projects: Project[];
  tab?: MetricsTab;
  isAdmin?: boolean;
  /** An Admin paused or resumed dispatch here: the banner asks again. */
  onDispatchChanged?: () => void;
}) {
  const [projectId, setProjectId] = useState<string>('');
  const [days, setDays] = useState<(typeof PERIODS)[number]>(30);
  const [view, setView] = useState(readView);

  const chooseView = (next: 'charts' | 'tables') => {
    setView(next);
    try {
      localStorage.setItem(VIEW_KEY, next);
    } catch {
      // A blocked store only means the choice is not remembered.
    }
  };

  return (
    <TipLayer>
      <div className={`page stack metrics${view === 'tables' ? ' tables' : ''}`}>
        <section className="section">
          <div className="section-head">
            <h2>Metrics</h2>
            <nav className="seg" aria-label="Metrics">
              <button type="button" aria-pressed={tab === 'flow'} onClick={() => navigate({ view: 'metrics' })}>
                Flow
              </button>
              <button type="button" aria-pressed={tab === 'guidance'} onClick={() => navigate({ view: 'metrics', tab: 'guidance' })}>
                Skills &amp; lessons
              </button>
            </nav>
            <select aria-label="Project" value={projectId} onChange={(e) => setProjectId(e.target.value)}>
              <option value="">All my projects</option>
              {projects.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
            <div className="seg" role="group" aria-label="Show as">
              {(['charts', 'tables'] as const).map((v) => (
                <button key={v} type="button" aria-pressed={view === v} onClick={() => chooseView(v)}>
                  {v === 'charts' ? 'Charts' : 'Tables'}
                </button>
              ))}
            </div>
          </div>
          <nav className="tabs" role="tablist" aria-label="Period">
            {PERIODS.map((p) => (
              <button key={p} role="tab" aria-selected={p === days} onClick={() => setDays(p)}>
                Last {p} days
              </button>
            ))}
          </nav>
          <p className="small muted">
            {tab === 'flow'
              ? 'Tasks created in the period. Open decisions are those waiting now, on any task.'
              : 'Lessons and skills given to runs in the period: what people said each lesson did, and how tasks with each skill fared.'}
          </p>
        </section>

        {tab === 'flow' ? (
          <Flow api={api} projects={projects} projectId={projectId || null} days={days} isAdmin={isAdmin} onDispatchChanged={onDispatchChanged} />
        ) : (
          <Guidance api={api} projects={projects} projectId={projectId || null} days={days} />
        )}
      </div>
    </TipLayer>
  );
}

function Flow({
  api,
  projects,
  projectId,
  days,
  isAdmin,
  onDispatchChanged,
}: {
  api: FactoryApi;
  projects: Project[];
  projectId: string | null;
  days: number;
  isAdmin: boolean;
  onDispatchChanged?: () => void;
}) {
  const [metrics, setMetrics] = useState<FlowMetrics | null>(null);
  const [counters, setCounters] = useState<Counters | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let current = true;
    setMetrics(null);
    api.metrics(projectId, days).then(
      (m) => current && (setMetrics(m), setError(null)),
      (failure: unknown) => current && setError(failure instanceof ApiError ? failure.message : 'The metrics could not be loaded.'),
    );
    return () => {
      current = false;
    };
  }, [api, projectId, days]);

  return (
    <>
      <ErrorNote message={error} />
      {metrics === null && !error ? <p className="small muted">Loading…</p> : null}
      {metrics ? <Attention m={metrics} counters={counters} /> : null}
      {metrics ? <Report m={metrics} projects={projectId ? projects.filter((p) => p.id === projectId) : projects} /> : null}
      {isAdmin ? <Operations api={api} onDispatchChanged={onDispatchChanged} onCounters={setCounters} /> : null}
    </>
  );
}

/** What needs someone now, first: a decision waiting over a day, and for Admins what the counters flag. */
function Attention({ m, counters }: { m: FlowMetrics; counters: Counters | null }) {
  const items: { severity: 'bad' | 'warn'; text: React.ReactNode }[] = [];
  const oldest = m.oldestOpenDecision;
  if (oldest)
    items.push({
      severity: oldest.ageHours > 24 ? 'bad' : 'warn',
      text: (
        <>
          <b>
            {m.openDecisions} decision{m.openDecisions === 1 ? '' : 's'} waiting.
          </b>{' '}
          The oldest has waited {duration(oldest.ageHours)}: <a href={`#/threads/${oldest.threadId}`}>{oldest.title}</a>
        </>
      ),
    });
  const orphaned = counters?.runs.filter((r) => r.needsAttention) ?? [];
  if (orphaned.length)
    items.push({
      severity: 'bad',
      text: (
        <>
          <b>{orphaned.length} run{orphaned.length === 1 ? '' : 's'} marked running</b> that this host is not executing:{' '}
          <a href={`#/threads/${orphaned[0]!.threadId}`}>{orphaned[0]!.title}</a>
        </>
      ),
    });
  if (counters?.pushFailures.count)
    items.push({
      severity: 'bad',
      text: (
        <>
          <b>
            {counters.pushFailures.count} push or pull-request failure{counters.pushFailures.count === 1 ? '' : 's'}
          </b>{' '}
          in {counters.pushFailures.days} days.
        </>
      ),
    });
  const nearQuota = counters?.users.filter((u) => (u.dayShare ?? 0) > 0.85 || (u.monthShare ?? 0) > 0.85) ?? [];
  if (nearQuota.length)
    items.push({ severity: 'warn', text: <>Close to a quota: {nearQuota.map((u) => u.name).join(', ')}.</> });

  if (items.length === 0) return null;
  return (
    <section className="attention" aria-label="Needs attention">
      {items.map((item, i) => (
        <div key={i} className={`alert ${item.severity}`}>
          <span className="alert-icon" aria-hidden="true">
            {item.severity === 'bad' ? '×' : '!'}
          </span>
          <span>{item.text}</span>
        </div>
      ))}
    </section>
  );
}

function Report({ m, projects }: { m: FlowMetrics; projects: Project[] }) {
  return (
    <>
      <Summary m={m} />
      <WhereTimeGoes m={m} />
      <div className="metrics-pair">
        <TimeInState rows={m.timeInState} />
        <Rework m={m} />
      </div>
      <div className="metrics-pair">
        <PerTask m={m} projects={projects} />
        <TokensBy m={m} />
      </div>
      <div className="metrics-trio">
        <Queues m={m} />
        <Decisions m={m} />
        <LessonsInBrief m={m} />
      </div>
    </>
  );
}

function Summary({ m }: { m: FlowMetrics }) {
  const parts: Segment[] = [
    { label: 'Accepted', value: m.accepted, color: 'var(--ok)' },
    { label: 'Open', value: m.open, color: 'var(--petrol)' },
    { label: 'Cancelled', value: m.cancelled, color: 'var(--idle)' },
  ];
  const fh = m.firstHandoffAcceptance;
  const oldest = m.oldestOpenDecision;
  const stretch = (i: number) => {
    const b = m.series[i]!;
    return `${new Date(b.start).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })}${b.days > 1 ? `, ${b.days} days` : ''}`;
  };
  return (
    <section className="tiles" aria-label="Summary">
      <div className="section tile">
        <span className="eyebrow">Tasks in the period</span>
        <div className="tile-row">
          <span className="big">{fmt(m.tasks)}</span>
          <Sparkline
            values={m.series.map((b) => b.accepted)}
            color="var(--ok)"
            label="Tasks accepted over the period"
            tip={(v, i) => `${v} accepted · ${stretch(i)}`}
          />
        </div>
        <Meter segments={parts} label="Tasks" />
        <Key items={parts.map((p) => ({ label: p.label, color: p.color, value: p.value }))} />
      </div>
      <div className="section tile">
        <span className="eyebrow">Accepted at the first handoff</span>
        <div className="tile-row">
          <span className="big">{percent(fh)}</span>
          {fh === null ? null : <span className={`pill ${fh >= FIRST_HANDOFF_MARK ? 'done' : 'you'}`}>{fh >= FIRST_HANDOFF_MARK ? 'above' : 'below'} the M1 gate</span>}
        </div>
        <Bullet
          value={fh ?? 0}
          mark={FIRST_HANDOFF_MARK}
          label={`First-handoff acceptance ${percent(fh)}, against the M1 gate of 58%`}
          valueTip={`${percent(fh)} of accepted tasks needed no change request`}
          markTip="The M1 gate: 7 of 12 tasks (58%)"
        />
        <p className="small muted">
          Of {fmt(m.accepted)} accepted, {fmt(Math.round((fh ?? 0) * m.accepted))} needed no change request. The tick is the M1 gate (7 of 12).
        </p>
      </div>
      <div className="section tile">
        <span className="eyebrow">Tokens per task</span>
        <span className="big">
          {m.tokensPerTask.count ? tokens(m.tokensPerTask.median) : '—'}
          {m.tokensPerTask.count ? <small> median</small> : null}
        </span>
        <p className="small muted">
          {m.tokensPerTask.count ? `90th percentile ${tokens(m.tokensPerTask.p90)} · ` : ''}
          {fmt(m.tokensTotal)} in all
        </p>
      </div>
      <div className="section tile">
        <span className="eyebrow">Waiting on people</span>
        <div className="tile-row">
          <span className="big">
            {fmt(m.openDecisions)}
            <small> open decision{m.openDecisions === 1 ? '' : 's'}</small>
          </span>
          {oldest ? <span className={`pill ${oldest.ageHours > 24 ? 'bad' : 'you'}`}>oldest {duration(oldest.ageHours)}</span> : <span className="pill done">none waiting</span>}
        </div>
        {oldest ? (
          <p className="small">
            <a href={`#/threads/${oldest.threadId}`}>{oldest.title}</a> — <span className="muted">{oldest.question}</span>
          </p>
        ) : (
          <p className="small muted">Every open task is with the factory or a tester.</p>
        )}
      </div>
    </section>
  );
}

function WhereTimeGoes({ m }: { m: FlowMetrics }) {
  const hours = Object.fromEntries(GROUPS.map((g) => [g.id, 0])) as Record<(typeof GROUPS)[number]['id'], number>;
  for (const r of m.timeInState) {
    const group = WHOSE[r.name];
    if (group) hours[group] += r.totalHours;
  }
  const total = Object.values(hours).reduce((a, b) => a + b, 0);
  const share = (id: keyof typeof hours) => (total ? hours[id] / total : 0);
  const testing = m.timeInState.find((r) => r.name === 'AwaitingHumanTesting')?.totalHours ?? 0;
  const stages = STAGES.map(([name, who]) => ({ who, row: m.timeInStage.find((r) => r.name === name) ?? { name, count: 0, totalHours: 0, medianHours: 0, p90Hours: 0 } }));
  const most = Math.max(...stages.map((s) => s.row.totalHours), 0);
  const busiest = stages.find((s) => s.row.totalHours === most && most > 0);

  return (
    <section className="section" aria-label="Where the time goes">
      <div className="section-head">
        <h3>Where the time goes</h3>
      </div>
      {total === 0 ? (
        <p className="small muted">No tasks in the period.</p>
      ) : (
        <>
          <p className="headline">
            Tasks spent <em>{percent(share('people'))}</em> of their time waiting on people
            {total && testing / total > 0.3 ? ', mostly in human testing' : ''}, and <strong>{percent(share('factory'))}</strong> with the factory working.
          </p>
          <div className="viz-only stack">
            <Meter
              tall
              label="Time by whose turn it was"
              segments={GROUPS.map((g) => ({ label: g.name, value: Math.round(hours[g.id] * 10) / 10, color: g.color }))}
            />
            <Key items={GROUPS.map((g) => ({ label: g.name, color: g.color, value: duration(hours[g.id]) }))} />
            <div className="conveyor-scroll">
              <ol className="conveyor" aria-label="Stages in order">
                {stages.map(({ who, row }) => (
                  <li
                    key={row.name}
                    className={`stage ${who}${row === busiest?.row ? ' busiest' : ''}`}
                    data-tip={`${row.name} · ${row.count} tasks\nmedian ${duration(row.medianHours)} · 90th percentile ${duration(row.p90Hours)}\n${duration(row.totalHours)} in all`}
                  >
                    <span className="who">{who === 'factory' ? 'Factory' : 'People'}</span>
                    <span className="name">{row.name}</span>
                    <span className="med">{row.count ? duration(row.medianHours) : '—'}</span>
                    <span className="meta">
                      median · p90 {row.count ? duration(row.p90Hours) : '—'} · {row.count} tasks
                    </span>
                    <span className="share" aria-hidden="true">
                      <span style={{ width: `${most ? (row.totalHours / most) * 100 : 0}%` }} />
                    </span>
                  </li>
                ))}
              </ol>
            </div>
            <p className="small muted">
              The bar under each stage is its total time, on one scale.
              {busiest ? ` ${busiest.row.name} holds the most (${duration(busiest.row.totalHours)}).` : ''}
            </p>
          </div>
          <TimeTable title="Time in each stage" name="Stage" rows={stages.map((s) => s.row).filter((r) => r.count > 0)} />
        </>
      )}
    </section>
  );
}

function TimeTable({ title, name, rows }: { title: string; name: string; rows: TimeIn[] }) {
  return (
    <div className="table-scroll alt-only">
      <table className="small" aria-label={title}>
        <thead>
          <tr>
            <th scope="col">{name}</th>
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
    </div>
  );
}

function TimeInState({ rows }: { rows: TimeIn[] }) {
  const shown = rows.filter((r) => WHOSE[r.name]);
  const color = (name: string) => GROUPS.find((g) => g.id === WHOSE[name])!.color;
  return (
    <section className="section" aria-label="Time in each state">
      <div className="section-head">
        <h3>Time in each state</h3>
      </div>
      <p className="small muted">The dot is the median per task, the tick the 90th percentile. The colour says whose turn it is.</p>
      {shown.length === 0 ? (
        <p className="small muted">No tasks in the period.</p>
      ) : (
        <>
          <div className="viz-only">
            <RangeRows
              label="Median and 90th percentile time per task in each state"
              unit={(h) => `${h}h`}
              rows={shown.map((r) => ({
                label: label(r.name),
                count: r.count,
                median: r.medianHours,
                p90: r.p90Hours,
                color: color(r.name),
                tip: `${label(r.name)} · ${r.count} tasks\nmedian ${duration(r.medianHours)} · 90th percentile ${duration(r.p90Hours)}\n${duration(r.totalHours)} in all`,
              }))}
            />
            <Key items={GROUPS.map((g) => ({ label: g.name, color: g.color }))} />
          </div>
          <TimeTable title="Time in each state" name="State" rows={shown} />
        </>
      )}
    </section>
  );
}

function Rework({ m }: { m: FlowMetrics }) {
  // Three or more change requests share a column.
  const counts = [0, 1, 2, 3].map((r) => m.reworkRounds.filter((x) => (r === 3 ? x.rounds >= 3 : x.rounds === r)).reduce((a, x) => a + x.tasks, 0));
  const name = (r: number) => (r === 0 ? 'none' : r === 3 ? '3+' : String(r));
  return (
    <section className="section" aria-label="Rework rounds">
      <div className="section-head">
        <h3>Rework rounds</h3>
      </div>
      <p className="small muted">How many times a tester sent each accepted task back.</p>
      <div className="viz-only">
        <Columns
          label="Accepted tasks by the number of change requests they needed"
          caption="change requests before acceptance"
          columns={counts.map((tasks, r) => ({
            label: name(r),
            segments: [{ label: 'Tasks', value: tasks, color: r === 0 ? 'var(--ok)' : 'var(--petrol)' }],
            tip: `${tasks} accepted task${tasks === 1 ? '' : 's'} with ${name(r)} change request${r === 1 ? '' : 's'}`,
          }))}
        />
      </div>
      <div className="table-scroll alt-only">
        <table className="small" aria-label="Rework rounds">
          <thead>
            <tr>
              <th scope="col">Change requests</th>
              <th scope="col">Accepted tasks</th>
            </tr>
          </thead>
          <tbody>
            {counts.map((tasks, r) => (
              <tr key={r}>
                <td>{name(r)}</td>
                <td>{tasks}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function PerTask({ m, projects }: { m: FlowMetrics; projects: Project[] }) {
  const charged = m.perTask.filter((t) => t.tokens > 0);
  const lanes = projects.filter((p) => charged.some((t) => t.projectId === p.id));
  const laneOf = (id: string) => Math.max(0, lanes.findIndex((p) => p.id === id));
  const colorOf = (id: string) => PROJECT_COLORS[laneOf(id) % PROJECT_COLORS.length]!;
  const nameOf = (id: string) => projects.find((p) => p.id === id)?.name ?? 'Another project';
  return (
    <section className="section" aria-label="Tokens, task by task">
      <div className="section-head">
        <h3>Tokens, task by task</h3>
      </div>
      <p className="small muted">Each dot is a task. Faded dots are not accepted yet; larger ones needed rework. The tasks to the right are where the budget goes.</p>
      {charged.length === 0 ? (
        <p className="small muted">No task was charged in the period.</p>
      ) : (
        <>
          <div className="viz-only">
            <DotStrip
              label="Tokens charged to each task, by project"
              lanes={lanes.map((p) => p.name)}
              median={m.tokensPerTask.median}
              p90={m.tokensPerTask.p90}
              unit={tokens}
              dots={charged.map((t) => ({
                key: t.threadId,
                value: t.tokens,
                lane: laneOf(t.projectId),
                color: colorOf(t.projectId),
                big: t.reworkRounds > 0,
                faded: t.state !== 'Accepted',
                tip: `${t.title}\n${nameOf(t.projectId)} · ${label(t.state)}\n${fmt(t.tokens)} tokens${t.reworkRounds ? ` · ${t.reworkRounds} rework` : ''}`,
              }))}
            />
            {lanes.length > 1 ? <Key items={lanes.map((p) => ({ label: p.name, color: colorOf(p.id) }))} /> : null}
          </div>
          <div className="table-scroll alt-only">
            <table className="small" aria-label="Tokens, task by task">
              <thead>
                <tr>
                  <th scope="col">Task</th>
                  <th scope="col">Project</th>
                  <th scope="col">State</th>
                  <th scope="col">Rework</th>
                  <th scope="col">Tokens</th>
                </tr>
              </thead>
              <tbody>
                {charged.map((t) => (
                  <tr key={t.threadId}>
                    <td>
                      <a href={`#/threads/${t.threadId}`}>{t.title}</a>
                    </td>
                    <td>{nameOf(t.projectId)}</td>
                    <td>{label(t.state)}</td>
                    <td>{t.reworkRounds}</td>
                    <td>{fmt(t.tokens)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </section>
  );
}

function TokensBy({ m }: { m: FlowMetrics }) {
  const phases = new Map<string, number>();
  for (const p of m.tokensByPhase) phases.set(phaseOf(p.name), (phases.get(phaseOf(p.name)) ?? 0) + p.tokens);
  const phaseRows = [...PHASES.map(([name]) => name), 'Other']
    .filter((name) => phases.get(name))
    .map((name) => ({ name, tokens: phases.get(name)!, color: PHASES.find(([p]) => p === name)?.[1] ?? 'var(--idle)' }));
  const share = (n: number) => (m.tokensTotal ? n / m.tokensTotal : 0);
  const topModel = Math.max(...m.tokensByModel.map((r) => r.share), 0);
  return (
    <section className="section" aria-label="Tokens by phase and model">
      <div className="section-head">
        <h3>Where tokens go</h3>
      </div>
      <p className="small muted">{fmt(m.tokensTotal)} charged in the period, cache reads at their discounted weight.</p>
      <div className="viz-only stack">
        <span className="eyebrow">By phase</span>
        <Meter tall label="Tokens by phase" segments={phaseRows.map((r) => ({ label: r.name, value: r.tokens, color: r.color }))} />
        <Key items={phaseRows.map((r) => ({ label: r.name, color: r.color, value: percent(share(r.tokens)) }))} />
        <span className="eyebrow">By model</span>
        {m.tokensByModel.map((r) => (
          <div key={r.name} className="hbar" data-tip={`${r.name}\n${fmt(r.tokens)} tokens · ${percent(r.share)}`}>
            <span className="mono">{r.name}</span>
            <span className="small">
              <b>{tokens(r.tokens)}</b> <span className="muted">{percent(r.share)}</span>
            </span>
            <span className="hbar-track">
              <span style={{ width: `${topModel ? (r.share / topModel) * 100 : 0}%` }} />
            </span>
          </div>
        ))}
      </div>
      <div className="form-grid alt-only">
        {[
          ['Phase', m.tokensByPhase],
          ['Model', m.tokensByModel],
        ].map(([name, rows]) => (
          <table key={name as string} className="small" aria-label={`Tokens by ${(name as string).toLowerCase()}`}>
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
  );
}

function Queues({ m }: { m: FlowMetrics }) {
  const rows = [
    { name: 'Repository lock', s: m.queueForRepository, color: 'var(--petrol)' },
    { name: 'Free slot', s: m.queueForSlot, color: 'var(--idle)' },
  ];
  const text = (s: FlowMetrics['queueForSlot']) =>
    s.count ? `${fmt(s.count)} waits, median ${s.median.toFixed(0)} min, 90th percentile ${s.p90.toFixed(0)} min` : 'none';
  return (
    <section className="section" aria-label="Queue waits">
      <div className="section-head">
        <h3>Queue waits</h3>
      </div>
      <p className="small muted">Minutes a task waited before it could run.</p>
      {rows.some((r) => r.s.count) ? (
        <div className="viz-only">
          <RangeRows
            label="Queue waits in minutes"
            labelWidth={120}
            unit={(v) => `${v}m`}
            rows={rows.filter((r) => r.s.count).map((r) => ({ label: r.name, count: r.s.count, median: r.s.median, p90: r.s.p90, color: r.color, tip: `${r.name}\n${text(r.s)}` }))}
          />
        </div>
      ) : null}
      <dl className="kv">
        {rows.map((r) => (
          <div key={r.name} style={{ display: 'contents' }}>
            <dt>{r.name}</dt>
            <dd>{text(r.s)}</dd>
          </div>
        ))}
      </dl>
    </section>
  );
}

function Decisions({ m }: { m: FlowMetrics }) {
  const oldest = m.oldestOpenDecision;
  const span = Math.max(48, (oldest?.ageHours ?? 0) * 1.15);
  return (
    <section className="section" aria-label="Open decisions" id="decisions">
      <div className="section-head">
        <h3>Open decisions</h3>
      </div>
      <p className="small muted">Tasks stopped until someone answers, on any task.</p>
      <span className="big">
        {fmt(m.openDecisions)}
        <small> waiting now</small>
      </span>
      {oldest ? (
        <div className="decision-oldest">
          <span className="eyebrow">Oldest · waited {duration(oldest.ageHours)}</span>
          <a href={`#/threads/${oldest.threadId}`}>{oldest.title}</a>
          <q>{oldest.question}</q>
          <div className="agebar" role="img" aria-label={`Waited ${duration(oldest.ageHours)}; the mark is one day`} data-tip={`Waited ${duration(oldest.ageHours)}. The mark is one day.`}>
            <span style={{ width: `${Math.min(100, (oldest.ageHours / span) * 100)}%` }} />
            <i style={{ left: `${(24 / span) * 100}%` }} />
          </div>
        </div>
      ) : null}
    </section>
  );
}

function LessonsInBrief({ m }: { m: FlowMetrics }) {
  const l = m.lessons;
  const parts: Segment[] = [
    { label: 'Helped', value: l.helped, color: 'var(--ok)' },
    { label: 'Did not matter', value: l.didNotMatter, color: 'var(--idle)' },
    { label: 'Misled', value: l.misled, color: 'var(--bad)' },
    { label: 'Not judged', value: l.unjudged, color: 'var(--queue)' },
  ];
  return (
    <section className="section" aria-label="Lessons">
      <div className="section-head">
        <h3>Lessons</h3>
      </div>
      <p className="small">
        Given to runs {fmt(l.included)} times, left out past the limit {fmt(l.leftOut)} times. Of those given, people said {fmt(l.helped)} helped,{' '}
        {fmt(l.didNotMatter)} did not matter and {fmt(l.misled)} misled; {fmt(l.unjudged)} were not judged.
      </p>
      <div className="viz-only stack">
        <Meter label="Verdicts on the lessons given" segments={parts} />
        <Key items={parts.map((p) => ({ label: p.label, color: p.color, value: p.value }))} />
      </div>
      <a href="#/metrics/guidance" className="small strong-link">
        See how each lesson is doing →
      </a>
    </section>
  );
}
