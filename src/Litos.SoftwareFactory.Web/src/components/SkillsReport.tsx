import type { SkillReport, SkillScore, SkillStanding } from '../api/types';
import { fmt } from '../domain/format';
import { percent, pointsText, tokens } from '../domain/metrics';
import { niceScale, tickValues, useWidth } from './charts';

/** How each standing reads and is coloured; the name is always shown beside the colour. */
export const SKILL_STANDING: Record<SkillStanding, { name: string; color: string; pill: string }> = {
  EarnsItsPlace: { name: 'Earns its place', color: 'var(--ok)', pill: 'done' },
  NoClearEffect: { name: 'No clear effect', color: 'var(--idle)', pill: 'neutral' },
  CostlyNoGain: { name: 'Costly, no clear gain', color: 'var(--amber)', pill: 'you' },
  WorseWithIt: { name: 'Worse with it', color: 'var(--bad)', pill: 'bad' },
  NeverLoaded: { name: 'Never loaded', color: 'var(--amber)', pill: 'you' },
  TooFewTasks: { name: 'Too few tasks', color: 'var(--queue)', pill: 'neutral' },
};

/** How a run gets the skill, which the chart draws as its shape. */
export const howOf = (s: SkillScore): 'chosen' | 'onDemand' | 'repository' =>
  s.runsChosen > 0 ? 'chosen' : s.origin === 'Repository' ? 'repository' : 'onDemand';
const HOW = { chosen: 'chosen for the task', onDemand: 'loaded on demand', repository: 'repository skill' } as const;

/** The marks' cost line: a chosen skill above it, with no clear gain, should load on demand instead (SkillMetrics.CostlyFrom). */
const COSTLY_FROM = 5_000;

/**
 * Whether each skill pays for the tokens it adds (m4-architecture.md §12.5). Skills get no verdicts:
 * a skill is judged by how runs use it, what it costs and how comparable tasks with it fared.
 */
export function Skills({ r }: { r: SkillReport }) {
  return (
    <section className="section" aria-label="Skills">
      <div className="section-head">
        <h3>Skills</h3>
      </div>
      <p className="small muted">
        Whether each skill pays for the tokens it adds. Up the chart: how many percentage points more often tasks with the skill were accepted at
        their first handoff than comparable tasks (same project and task type) without it; +20 means, say, 80% against 60%. Each side needs{' '}
        {r.minTasksToCompare} tasks.
      </p>
      <p className="small">
        {fmt(r.runsWithSkills)} of {fmt(r.runs)} runs had skills; the agent loaded one {fmt(r.loads)} times.
      </p>
      {r.skills.length === 0 ? (
        <p className="small muted">No run in the period had a skill.</p>
      ) : (
        <>
          <SkillChart r={r} />
          <SkillTable skills={r.skills} />
        </>
      )}
    </section>
  );
}

function SkillChart({ r }: { r: SkillReport }) {
  const [ref, width] = useWidth<HTMLDivElement>();
  const points = r.skills.filter((s) => s.firstHandoff && s.tokensPerRun !== null);
  const off = r.skills.filter((s) => !points.includes(s));
  const height = 300;
  const [left, right, top, bottom] = [52, 18, 14, 38];
  const lift = (s: SkillScore) => s.firstHandoff!.with - s.firstHandoff!.without;
  const scale = niceScale(Math.max(10_000, ...points.map((s) => s.tokensPerRun!)) * 1.05);
  const lo = Math.min(-0.2, ...points.map((s) => lift(s) - 0.05));
  const hi = Math.max(0.25, ...points.map((s) => lift(s) + 0.05));
  const x = (v: number) => left + (v / scale.max) * (width - left - right);
  const y = (v: number) => top + ((hi - v) / (hi - lo)) * (height - top - bottom);
  const yTicks: number[] = [];
  for (let v = Math.ceil(lo * 10) / 10; v <= hi + 1e-9; v += 0.1) yTicks.push(+v.toFixed(1));

  const mark = (s: SkillScore, cx: number, cy: number, radius: number, fill: string, stroke: string) => {
    const how = howOf(s);
    if (how === 'onDemand') return <rect x={cx - radius} y={cy - radius} width={radius * 2} height={radius * 2} rx={2} fill={fill} stroke={stroke} strokeWidth={2} />;
    if (how === 'repository') {
      const d = radius * 1.3;
      return <path d={`M${cx} ${cy - d} L${cx + d} ${cy} L${cx} ${cy + d} L${cx - d} ${cy}Z`} fill={fill} stroke={stroke} strokeWidth={2} />;
    }
    return <circle cx={cx} cy={cy} r={radius} fill={fill} stroke={stroke} strokeWidth={2} />;
  };

  return (
    <div className="viz-only stack" ref={ref}>
      <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`} role="img" aria-label="Skills by what they cost a run and the change in first-time acceptance">
        <rect x={left} y={y(hi)} width={width - left - right} height={y(0.05) - y(hi)} fill="var(--ok)" opacity={0.08} />
        <rect x={left} y={y(-0.05)} width={width - left - right} height={y(lo) - y(-0.05)} fill="var(--bad)" opacity={0.08} />
        {yTicks.map((v) => (
          <g key={v}>
            <line className={Math.abs(v) < 1e-9 ? 'axis-line' : 'grid-line'} x1={left} x2={width - right} y1={y(v)} y2={y(v)} />
            <text x={left - 6} y={y(v) + 4} textAnchor="end">
              {v > 0 ? '+' : ''}
              {Math.round(v * 100)}
            </text>
          </g>
        ))}
        {tickValues(scale).map((t) => (
          <g key={t}>
            <line className="grid-line" x1={x(t)} x2={x(t)} y1={top} y2={height - bottom} />
            <text x={x(t)} y={height - bottom + 15} textAnchor="middle">
              {tokens(t)}
            </text>
          </g>
        ))}
        <line x1={x(COSTLY_FROM)} x2={x(COSTLY_FROM)} y1={top} y2={height - bottom} stroke="var(--muted)" strokeDasharray="3 3" />
        <text x={(left + width - right) / 2} y={height - 4} textAnchor="middle">
          tokens a run is charged for the skill's text
        </text>
        <text x={12} y={(top + height - bottom) / 2} textAnchor="middle" transform={`rotate(-90 12 ${(top + height - bottom) / 2})`}>
          accepted first time: with − without (points)
        </text>
        <text className="zone ok" x={left + 6} y={y(hi) + 14}>
          Earns its place
        </text>
        <text className="zone" x={width - right - 6} y={y(0) - 6} textAnchor="end">
          Costly, no clear gain · load on demand
        </text>
        <text className="zone bad" x={left + 6} y={y(lo) - 8}>
          Worse with it · review
        </text>
        {points.map((s) => {
          const f = s.firstHandoff!;
          const radius = 5 + Math.sqrt(f.withTasks) * 0.9;
          const color = SKILL_STANDING[s.standing].color;
          const [cx, cy] = [x(s.tokensPerRun!), y(lift(s))];
          return (
            <g
              key={s.name}
              data-tip={`${s.name} · ${HOW[howOf(s)]}\n${f.withTasks} tasks with it · about ${fmt(s.tokensPerRun!)} tokens a run\nfirst time: ${percent(f.with)} with, ${percent(f.without)} of ${f.withoutTasks} without`}
            >
              <circle className="hit" cx={cx} cy={cy} r={radius + 8} />
              {mark(s, cx, cy, radius, color, 'var(--surface)')}
              <text className="chart-label mono" x={cx + radius + 5} y={cy + 4}>
                {s.name}
              </text>
            </g>
          );
        })}
      </svg>
      <div className="chart-key">
        <span>● chosen for the task</span>
        <span>■ loaded on demand</span>
        <span>◆ repository skill</span>
        <span>dashed line: {tokens(COSTLY_FROM)} tokens a run</span>
      </div>
      {off.length ? (
        <div className="chips">
          <span className="small muted">Not on the chart:</span>
          {off.map((s) => (
            <span key={s.name} className="chip">
              <b>{s.name}</b> {s.changedSinceApproval ? 'changed since its approval' : SKILL_STANDING[s.standing].name.toLowerCase()}
            </span>
          ))}
        </div>
      ) : null}
    </div>
  );
}

function SkillTable({ skills }: { skills: SkillScore[] }) {
  return (
    <div className="table-scroll">
      <table className="small score" aria-label="Skills">
        <thead>
          <tr>
            <th scope="col">Skill</th>
            <th scope="col">How runs get it</th>
            <th scope="col">Chosen</th>
            <th scope="col">Offered</th>
            <th scope="col">Loaded</th>
            <th scope="col">Tokens a run</th>
            <th scope="col">With vs without</th>
            <th scope="col">Standing</th>
          </tr>
        </thead>
        <tbody>
          {skills.map((s) => (
            <tr key={s.name}>
              <td>
                <a className="mono strong" href="#/settings/skills">
                  {s.name}
                </a>
              </td>
              <td>{HOW[howOf(s)]}</td>
              <td>{s.runsChosen}</td>
              <td>{s.runsOffered}</td>
              <td>
                {s.runsLoaded}
                {s.loads > s.runsLoaded ? <span className="muted"> ({s.loads} loads)</span> : null}
              </td>
              <td>{s.tokensPerRun === null ? <span className="muted">—</span> : `about ${tokens(s.tokensPerRun)}`}</td>
              <td>
                {s.firstHandoff ? (
                  <span
                    className={`lift ${s.firstHandoff.with > s.firstHandoff.without ? 'up' : s.firstHandoff.with < s.firstHandoff.without ? 'down' : ''}`}
                    data-tip={`${percent(s.firstHandoff.with)} of ${s.firstHandoff.withTasks} tasks with it, ${percent(s.firstHandoff.without)} of ${s.firstHandoff.withoutTasks} without`}
                  >
                    {pointsText(s.firstHandoff)}
                  </span>
                ) : (
                  <span className="muted">too few tasks</span>
                )}
              </td>
              <td>
                <span className={`pill ${SKILL_STANDING[s.standing].pill}`}>{SKILL_STANDING[s.standing].name}</span>
                {s.changedSinceApproval ? <span className="pill you">changed since approval</span> : null}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
