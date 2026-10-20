import { useEffect, useState } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { ImprovementSeverity, LessonReport, LessonScore, LessonStanding, Project, SkillReport } from '../api/types';
import { fmt } from '../domain/format';
import { ErrorNote } from './bits';
import { Columns, Key, Meter, niceScale, tickValues, useWidth, type Segment } from './charts';
import { SOURCE } from './LessonsPage';
import { Skills } from './SkillsReport';
import { percent, pointsText } from '../domain/metrics';

/** How each standing reads and is coloured. Colour is never alone: the name is always shown beside it. */
export const STANDING: Record<LessonStanding, { name: string; color: string }> = {
  Working: { name: 'Working', color: 'var(--ok)' },
  RarelyMatched: { name: 'Helps, rarely matched', color: 'var(--petrol)' },
  LittleEffect: { name: 'Little effect', color: 'var(--idle)' },
  Misleading: { name: 'Misleading', color: 'var(--bad)' },
  TooFewVerdicts: { name: 'Too few verdicts', color: 'var(--queue)' },
  NeverMatched: { name: 'Never matched', color: 'var(--amber)' },
  New: { name: 'New', color: 'var(--queue)' },
};

export const SEVERITY: Record<ImprovementSeverity, { name: string; pill: string; color: string }> = {
  FixNow: { name: 'Fix now', pill: 'bad', color: 'var(--bad)' },
  Improve: { name: 'Improve', pill: 'you', color: 'var(--amber)' },
  Opportunity: { name: 'Opportunity', pill: 'agent', color: 'var(--petrol)' },
  MissingData: { name: 'Missing data', pill: 'neutral', color: 'var(--idle)' },
};

const VERDICTS = [
  { key: 'helped', name: 'Helped', color: 'var(--ok)' },
  { key: 'didNotMatter', name: 'Did not matter', color: 'var(--idle)' },
  { key: 'misled', name: 'Misled', color: 'var(--bad)' },
  { key: 'unjudged', name: 'Not judged', color: 'var(--queue)' },
] as const;

const verdictParts = (v: { helped: number; didNotMatter: number; misled: number; unjudged: number }): Segment[] =>
  VERDICTS.map((x) => ({ label: x.name, value: v[x.key], color: x.color }));

export { pointsText } from '../domain/metrics';

/** One change to make, to a lesson or a skill, with where it is made. */
export interface Fix {
  severity: ImprovementSeverity;
  subject: string;
  problem: string;
  action: string;
  rule: string;
  /** The page the change is made on: the lesson's card, or the Skills settings. */
  href: string;
}

const SEVERITY_ORDER: ImprovementSeverity[] = ['FixNow', 'Improve', 'Opportunity', 'MissingData'];

/** The lessons' and the skills' improvements as one list, most urgent first; each report's own order holds within a severity. */
export function fixesOf(lessons: LessonReport | null, skills: SkillReport | null): Fix[] {
  const fromLessons: Fix[] = (lessons?.improvements ?? []).map((i) => ({
    severity: i.severity,
    subject: i.subject,
    problem: i.problem,
    action: i.action,
    rule: i.rule,
    href: i.projectId ? `#/lessons/${encodeURIComponent(i.projectId)}/${i.subject}` : '#/lessons',
  }));
  const fromSkills: Fix[] = (skills?.improvements ?? []).map((i) => ({ ...i, rule: '', href: '#/settings/skills' }));
  return SEVERITY_ORDER.flatMap((severity) => [...fromLessons, ...fromSkills].filter((f) => f.severity === severity));
}

/**
 * How the project lessons and the factory's skills are doing, so they can be improved
 * (m4-architecture.md §12): what to change first, each lesson by use and verdict, whether ranking
 * puts the useful ones first, which sources make lessons that help, and each skill by cost and
 * outcome. Each improvement opens the page where the change is made.
 */
export function Guidance({ api, projects, projectId, days }: { api: FactoryApi; projects: Project[]; projectId: string | null; days: number }) {
  const [report, setReport] = useState<LessonReport | null>(null);
  const [skills, setSkills] = useState<SkillReport | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let current = true;
    setReport(null);
    setSkills(null);
    const failed = (what: string) => (failure: unknown) =>
      current && setError(failure instanceof ApiError ? failure.message : `The ${what} report could not be loaded.`);
    api.lessonMetrics(projectId, days).then((r) => current && (setReport(r), setError(null)), failed('lesson'));
    api.skillMetrics(projectId, days).then((r) => current && setSkills(r), failed('skill'));
    return () => {
      current = false;
    };
  }, [api, projectId, days]);

  const nameOf = (id: string) => projects.find((p) => p.id === id)?.name ?? '';
  const loading = (report === null || skills === null) && !error;
  return (
    <>
      <ErrorNote message={error} />
      {loading ? <p className="small muted">Loading…</p> : null}
      {!loading ? <Improvements fixes={fixesOf(report, skills)} /> : null}
      {report && report.lessons.length === 0 ? (
        <section className="section" aria-label="Lesson summary">
          <p className="small muted">No approved lessons yet. Lessons come from accepted tasks' reflections, or are written on the Lessons page.</p>
        </section>
      ) : null}
      {report && report.lessons.length > 0 ? (
        <>
          <Summary r={report} />
          <LessonMap r={report} />
          <Scorecard r={report} projectName={projects.length > 1 && !projectId ? nameOf : null} />
          <div className="metrics-pair">
            <Ranking r={report} />
            <Sources r={report} />
          </div>
        </>
      ) : null}
      {skills ? <Skills r={skills} /> : null}
    </>
  );
}

function Summary({ r }: { r: LessonReport }) {
  const coverage = r.given ? r.judged / r.given : null;
  const byStanding = (Object.keys(STANDING) as LessonStanding[])
    .map((s) => ({ s, n: r.lessons.filter((l) => l.standing === s).length }))
    .filter((x) => x.n > 0);
  return (
    <section className="tiles three" aria-label="Lesson summary">
      <div className="section tile">
        <span className="eyebrow">Verdict coverage</span>
        <div className="tile-row">
          <span className="big">{percent(coverage)}</span>
          {coverage === null ? null : <span className={`pill ${coverage >= 0.6 ? 'done' : 'you'}`}>{coverage >= 0.6 ? 'enough to go on' : 'too few to go on'}</span>}
        </div>
        <Meter
          label="Uses judged"
          segments={[
            { label: 'Judged', value: r.judged, color: 'var(--petrol)' },
            { label: 'Not judged', value: r.given - r.judged, color: 'var(--queue)' },
          ]}
        />
        <p className="small muted">
          {fmt(r.judged)} of the {fmt(r.given)} times a lesson was given, a person said what it did. Everything below rests on these verdicts.
        </p>
      </div>
      <div className="section tile">
        <span className="eyebrow">Lessons that helped</span>
        <div className="tile-row">
          <span className="big">{percent(r.judged ? r.helped / r.judged : null)}</span>
          {r.misled ? <span className="pill bad">{r.misled} misled</span> : <span className="pill done">none misled</span>}
        </div>
        <p className="small muted">
          Of {fmt(r.judged)} judged uses, {fmt(r.helped)} helped and {fmt(r.misled)} sent the agent the wrong way.
        </p>
      </div>
      <div className="section tile">
        <span className="eyebrow">Lessons</span>
        <span className="big">{fmt(r.lessons.length)}</span>
        <div className="chips">
          {byStanding.map(({ s, n }) => (
            <span key={s} className="chip" style={{ borderColor: STANDING[s].color }}>
              <b>{n}</b> {STANDING[s].name.toLowerCase()}
            </span>
          ))}
        </div>
      </div>
    </section>
  );
}

function Improvements({ fixes }: { fixes: Fix[] }) {
  return (
    <section className="section" aria-label="What to improve">
      <div className="section-head">
        <h3>What to improve</h3>
      </div>
      <p className="small muted">Ranked from the verdicts, matching and outcomes below. Each names the change to make, and opens the page to make it on.</p>
      {fixes.length === 0 ? (
        <p className="small muted">Nothing to change in this period.</p>
      ) : (
        <ul className="fixes">
          {fixes.map((i) => (
            <li key={`${i.subject}-${i.problem}`} className="fix" style={{ ['--sev' as string]: SEVERITY[i.severity].color }}>
              <div className="fix-what">
                <span className={`pill ${SEVERITY[i.severity].pill}`}>{SEVERITY[i.severity].name}</span>
                <span className="mono strong">{i.subject}</span>
                <span>{i.problem}</span>
                {i.rule ? (
                  <span className="fix-rule" title={i.rule}>
                    “{i.rule}”
                  </span>
                ) : null}
              </div>
              <a className="btn small" href={i.href}>
                {i.action} →
              </a>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/**
 * Each lesson by how often runs were given it (across) and its net verdict (up), in four zones that
 * say what to do: keep it, widen its topics, narrow or disable it, fix it now.
 */
function LessonMap({ r }: { r: LessonReport }) {
  const [ref, width] = useWidth<HTMLDivElement>();
  const points = r.lessons.filter((l) => l.net !== null && l.given > 0);
  const off = r.lessons.filter((l) => !points.includes(l));
  const height = 340;
  const [left, right, top, bottom] = [46, 18, 14, 38];
  const scale = niceScale(Math.max(r.rarelyBelow * 2, ...points.map((p) => p.given)) * 1.08);
  const x = (v: number) => left + (v / scale.max) * (width - left - right);
  const y = (v: number) => top + (1 - (v + 1) / 2) * (height - top - bottom);
  const band = (y1: number, y2: number, x1: number, x2: number, color: string) => (
    <rect x={x(x1)} y={y(y1)} width={x(x2) - x(x1)} height={y(y2) - y(y1)} fill={color} opacity={0.09} />
  );
  return (
    <section className="section" aria-label="Lesson map">
      <div className="section-head">
        <h3>Lesson map</h3>
      </div>
      <p className="small muted">
        Each lesson by how often runs were given it, and what people said it did: helped less misled, over judged uses. Bigger dots have more
        verdicts; hollow ones fewer than three.
      </p>
      <div className="viz-only" ref={ref}>
        <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`} role="img" aria-label="Lessons by how often they were given and how people judged them">
          {band(1, 0.4, r.rarelyBelow, scale.max, 'var(--ok)')}
          {band(1, 0.4, 0, r.rarelyBelow, 'var(--petrol)')}
          {band(0.4, 0, 0, scale.max, 'var(--idle)')}
          {band(0, -1, 0, scale.max, 'var(--bad)')}
          {tickValues(scale).map((t) => (
            <g key={t}>
              <line className="grid-line" x1={x(t)} x2={x(t)} y1={top} y2={height - bottom} />
              <text x={x(t)} y={height - bottom + 15} textAnchor="middle">
                {t}
              </text>
            </g>
          ))}
          {[-1, -0.5, 0, 0.4, 1].map((v) => (
            <g key={v}>
              <line className={v === 0 ? 'axis-line' : 'grid-line'} x1={left} x2={width - right} y1={y(v)} y2={y(v)} />
              <text x={left - 6} y={y(v) + 4} textAnchor="end">
                {v > 0 ? `+${v}` : v}
              </text>
            </g>
          ))}
          <text x={(left + width - right) / 2} y={height - 4} textAnchor="middle">
            times given to a run in the period
          </text>
          <text x={12} y={(top + height - bottom) / 2} textAnchor="middle" transform={`rotate(-90 12 ${(top + height - bottom) / 2})`}>
            net verdict
          </text>
          <text className="zone ok" x={width - right - 6} y={y(1) + 14} textAnchor="end">
            Working · keep
          </text>
          <text className="zone petrol" x={left + 6} y={y(1) + 14}>
            Helps, rarely matched · widen topics
          </text>
          <text className="zone" x={width - right - 6} y={y(0.4) + 14} textAnchor="end">
            Little effect · narrow or disable
          </text>
          <text className="zone bad" x={width - right - 6} y={y(-1) - 8} textAnchor="end">
            Misleading · fix now
          </text>
          {points.map((p) => {
            const judged = p.helped + p.didNotMatter + p.misled;
            const radius = 5 + Math.sqrt(judged) * 1.6;
            const hollow = judged < 3;
            const color = STANDING[p.standing].color;
            return (
              <g
                key={p.lessonId}
                data-tip={`L-${p.number} · ${STANDING[p.standing].name}\n${p.rule}\ngiven ${p.given} · helped ${p.helped} · did not matter ${p.didNotMatter} · misled ${p.misled} · not judged ${p.unjudged}`}
              >
                <circle className="hit" cx={x(p.given)} cy={y(p.net!)} r={radius + 6} />
                <circle cx={x(p.given)} cy={y(p.net!)} r={radius} fill={hollow ? 'var(--surface)' : color} stroke={hollow ? color : 'var(--surface)'} strokeWidth={2} />
                <text className="chart-label mono" x={x(p.given) + radius + 4} y={y(p.net!) + 4}>
                  L-{p.number}
                </text>
              </g>
            );
          })}
        </svg>
        {off.length ? (
          <div className="chips">
            <span className="small muted">Not on the map:</span>
            {off.map((l) => (
              <span key={l.lessonId} className="chip" data-tip={l.rule}>
                <b>L-{l.number}</b> {l.given ? 'not judged yet' : STANDING[l.standing].name.toLowerCase()}
              </span>
            ))}
          </div>
        ) : null}
      </div>
    </section>
  );
}

type SortKey = 'number' | 'given' | 'leftOut' | 'helpRate' | 'points' | 'lastGiven';

/** Every lesson with its figures; select a column to sort by it. */
function Scorecard({ r, projectName }: { r: LessonReport; projectName: ((id: string) => string) | null }) {
  const [sort, setSort] = useState<{ key: SortKey; descending: boolean }>({ key: 'given', descending: true });
  const value = (l: LessonScore, key: SortKey): number => {
    const judged = l.helped + l.didNotMatter + l.misled;
    switch (key) {
      case 'number':
        return l.number;
      case 'given':
        return l.given;
      case 'leftOut':
        return l.leftOut;
      case 'helpRate':
        return judged ? l.helped / judged : -1;
      case 'points':
        return l.firstHandoff ? l.firstHandoff.with - l.firstHandoff.without : -2;
      case 'lastGiven':
        return l.lastGiven ? Date.parse(l.lastGiven) : 0;
    }
  };
  const rows = [...r.lessons].sort((a, b) => (value(a, sort.key) - value(b, sort.key)) * (sort.descending ? -1 : 1) || a.number - b.number);
  const header = (key: SortKey, name: string) => (
    <th scope="col" aria-sort={sort.key === key ? (sort.descending ? 'descending' : 'ascending') : undefined}>
      <button type="button" className="sort" onClick={() => setSort({ key, descending: sort.key === key ? !sort.descending : true })}>
        {name}
        {sort.key === key ? (sort.descending ? ' ↓' : ' ↑') : ''}
      </button>
    </th>
  );
  return (
    <section className="section" aria-label="Lesson scorecard">
      <div className="section-head">
        <h3>Lesson scorecard</h3>
      </div>
      <p className="small muted">
        With vs without compares first-handoff acceptance of the project's accepted tasks given the lesson against those not given it, once each side has
        five.
      </p>
      <div className="table-scroll">
        <table className="small score" aria-label="Lesson scorecard">
          <thead>
            <tr>
              {header('number', 'Lesson')}
              <th scope="col">Rule</th>
              {header('given', 'Given')}
              {header('leftOut', 'Left out')}
              <th scope="col">Verdicts</th>
              {header('helpRate', 'Helped')}
              {header('points', 'With vs without')}
              {header('lastGiven', 'Last given')}
            </tr>
          </thead>
          <tbody>
            {rows.map((l) => {
              const judged = l.helped + l.didNotMatter + l.misled;
              return (
                <tr key={l.lessonId}>
                  <td>
                    <a className="mono strong" href={`#/lessons/${encodeURIComponent(l.projectId)}/L-${l.number}`}>
                      L-{l.number}
                    </a>
                    <br />
                    <span className="small strong" style={{ color: STANDING[l.standing].color }}>
                      {STANDING[l.standing].name}
                    </span>
                  </td>
                  <td className="rule-cell">
                    {l.rule}
                    <br />
                    <span className="muted">
                      {projectName ? `${projectName(l.projectId)} · ` : ''}
                      {SOURCE[l.source]} · revision {l.revision}
                      {l.usable ? '' : ' · not given to runs now'}
                    </span>
                  </td>
                  <td>
                    {l.given}
                    {l.manual ? <span className="muted"> ({l.manual} picked)</span> : null}
                  </td>
                  <td>{l.leftOut}</td>
                  <td data-tip={VERDICTS.map((v) => `${v.name}: ${l[v.key]}`).join('\n')}>
                    {l.given ? <Meter label={`Verdicts on L-${l.number}`} segments={verdictParts(l)} /> : <span className="muted">—</span>}
                  </td>
                  <td>{judged ? percent(l.helped / judged) : '—'}</td>
                  <td>
                    {l.firstHandoff ? (
                      <span
                        className={`lift ${l.firstHandoff.with > l.firstHandoff.without ? 'up' : l.firstHandoff.with < l.firstHandoff.without ? 'down' : ''}`}
                        data-tip={`${percent(l.firstHandoff.with)} of ${l.firstHandoff.withTasks} tasks with it, ${percent(l.firstHandoff.without)} of ${l.firstHandoff.withoutTasks} without`}
                      >
                        {pointsText(l.firstHandoff)}
                      </span>
                    ) : (
                      <span className="muted">too few tasks</span>
                    )}
                  </td>
                  <td>{l.lastGiven ? new Date(l.lastGiven).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' }) : <span className="muted">not given</span>}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <Key items={VERDICTS.map((v) => ({ label: v.name, color: v.color }))} />
    </section>
  );
}

function Ranking({ r }: { r: LessonReport }) {
  return (
    <section className="section" aria-label="Ranking">
      <div className="section-head">
        <h3>Does ranking pick the right lessons?</h3>
      </div>
      <p className="small muted">
        Verdicts by the rank a lesson had in its run. Helped should fall off with rank; a tall "left out" column says the limit is cutting lessons.
      </p>
      <div className="viz-only">
        <Columns
          label="Verdicts by the rank retrieval gave the lesson"
          caption="rank retrieval gave the lesson"
          columns={[
            ...r.byRank.map((b) => ({
              label: String(b.rank),
              segments: verdictParts(b),
              tip: `Rank ${b.rank}\n${VERDICTS.map((v) => `${v.name}: ${b[v.key]}`).join('\n')}`,
            })),
            { label: 'left out', segments: [{ label: 'Left out', value: r.leftOut, color: 'var(--queue)' }], tip: `${r.leftOut} matched, but past the limit`, outlined: true },
          ]}
        />
        <Key items={VERDICTS.map((v) => ({ label: v.name, color: v.color }))} />
      </div>
      <div className="table-scroll alt-only">
        <table className="small" aria-label="Verdicts by rank">
          <thead>
            <tr>
              <th scope="col">Rank</th>
              {VERDICTS.map((v) => (
                <th key={v.key} scope="col">
                  {v.name}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {r.byRank.map((b) => (
              <tr key={b.rank}>
                <td>{b.rank}</td>
                {VERDICTS.map((v) => (
                  <td key={v.key}>{b[v.key]}</td>
                ))}
              </tr>
            ))}
            <tr>
              <td>Left out</td>
              <td colSpan={4}>{r.leftOut}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>
  );
}

function Sources({ r }: { r: LessonReport }) {
  const p = r.proposals;
  const proposed = p.approvedAsWritten + p.approvedAfterEdit + p.dismissed + p.waiting;
  const outcomes: Segment[] = [
    { label: 'Approved as written', value: p.approvedAsWritten, color: 'var(--ok)' },
    { label: 'Approved after an edit', value: p.approvedAfterEdit, color: 'var(--petrol)' },
    { label: 'Dismissed', value: p.dismissed, color: 'var(--idle)' },
    { label: 'Waiting for review', value: p.waiting, color: 'var(--queue)' },
  ];
  return (
    <section className="section" aria-label="Where good lessons come from">
      <div className="section-head">
        <h3>Where good lessons come from</h3>
      </div>
      <p className="small muted">What reviewers made of reflection's proposals, and how often lessons from each source helped once given.</p>
      <span className="eyebrow">
        {proposed} proposed by reflection · {p.writtenByPeople} written by people
      </span>
      <Meter label="Reflection's proposals" segments={outcomes} />
      <Key items={outcomes.map((o) => ({ label: o.label, color: o.color, value: o.value }))} />
      <span className="eyebrow">Helped, by source</span>
      {r.bySource.length === 0 ? <p className="small muted">No lesson was judged in the period.</p> : null}
      {r.bySource.map((s) => (
        <div key={s.source} className="hbar" data-tip={`${SOURCE[s.source]}\n${s.lessons} lessons · helped ${s.helped} of ${s.judged} judged uses`}>
          <span>{SOURCE[s.source]}</span>
          <span className="small">
            <b>{percent(s.judged ? s.helped / s.judged : null)}</b> <span className="muted">of {s.judged}</span>
          </span>
          <span className="hbar-track">
            <span style={{ width: `${s.judged ? (s.helped / s.judged) * 100 : 0}%`, background: 'var(--ok)' }} />
          </span>
        </div>
      ))}
    </section>
  );
}
