import { useEffect, useState } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { ChatMetrics, Project } from '../api/types';
import { fmt } from '../domain/format';
import { percent, PROJECT_COLORS, tokens } from '../domain/metrics';
import { ErrorNote } from './bits';
import { Columns, Key, Meter, Sparkline, type Segment } from './charts';

/** The two kinds of answer, and task work beside them; each keeps its colour on every chart here. */
const ARTIFACT = 'var(--amber)';
const PLAIN = 'var(--idle)';
const TASK = 'var(--petrol)';

type Split = 'project' | 'kind';

const stretch = (b: ChatMetrics['series'][number]) =>
  `${new Date(b.start).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })}${b.days > 1 ? `, ${b.days} days` : ''}`;

/**
 * The Metrics tab's Chat view: what chat answers spend, in all, by project and over the period,
 * with the answers that published an artifact told apart from plain ones. Chat is charged to each
 * answer's own allowance, never to a task's budget, so Flow leaves it out and it is reported here.
 */
export function ChatReport({ api, projects, projectId, days }: { api: FactoryApi; projects: Project[]; projectId: string | null; days: number }) {
  const [report, setReport] = useState<ChatMetrics | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let current = true;
    setReport(null);
    api.chatMetrics(projectId, days).then(
      (r) => current && (setReport(r), setError(null)),
      (failure: unknown) => current && setError(failure instanceof ApiError ? failure.message : 'The chat metrics could not be loaded.'),
    );
    return () => {
      current = false;
    };
  }, [api, projectId, days]);

  if (error) return <ErrorNote message={error} />;
  if (!report) return <p className="small muted">Loading…</p>;

  // Projects keep the colour of their place in the person's list, whichever are shown.
  const colorOf = (id: string) => PROJECT_COLORS[Math.max(0, projects.findIndex((p) => p.id === id)) % PROJECT_COLORS.length]!;
  const nameOf = (id: string) => projects.find((p) => p.id === id)?.name ?? 'Another project';

  return (
    <>
      <Summary r={report} />
      {report.answers === 0 ? (
        <section className="section">
          <p className="small muted">No chat answers in the period.</p>
        </section>
      ) : (
        <>
          <Trend r={report} colorOf={colorOf} nameOf={nameOf} />
          <div className="metrics-pair">
            <ByProject r={report} colorOf={colorOf} nameOf={nameOf} />
            <BesideTaskWork r={report} />
          </div>
          <TopTasks r={report} nameOf={nameOf} />
        </>
      )}
    </>
  );
}

function Summary({ r }: { r: ChatMetrics }) {
  const kinds: Segment[] = [
    { label: 'Plain', value: r.answers - r.artifactAnswers, color: PLAIN },
    { label: 'Artifact', value: r.artifactAnswers, color: ARTIFACT },
  ];
  const tokenKinds: Segment[] = [
    { label: 'Artifact answers', value: r.artifactTokens, color: ARTIFACT },
    { label: 'Plain answers', value: r.plainTokens, color: PLAIN },
  ];
  return (
    <section className="tiles" aria-label="Chat summary">
      <div className="section tile">
        <span className="eyebrow">Chat tokens</span>
        <div className="tile-row">
          <span className="big">{tokens(r.tokens)}</span>
          <Sparkline
            values={r.series.map((b) => b.tokens)}
            color={TASK}
            label="Chat tokens over the period"
            tip={(v, i) => `${tokens(v)} tokens · ${stretch(r.series[i]!)}`}
          />
        </div>
        <Meter label="Chat beside task work" segments={[{ label: 'Chat', value: r.tokens, color: TASK }, { label: 'Task work', value: r.taskTokens, color: 'var(--grid)' }]} />
        <p className="small muted">
          {percent(r.share)} of the {tokens(r.tokens + r.taskTokens)} tokens in the period
        </p>
      </div>
      <div className="section tile">
        <span className="eyebrow">Answers</span>
        <span className="big">{fmt(r.answers)}</span>
        <Meter label="Answers, plain or with an artifact" segments={kinds} />
        <p className="small muted">
          {fmt(r.answers - r.artifactAnswers)} plain · {fmt(r.artifactAnswers)} published an artifact
        </p>
      </div>
      <div className="section tile">
        <span className="eyebrow">Tokens per answer</span>
        <span className="big">
          {r.perAnswer.count ? tokens(r.perAnswer.median) : '—'}
          {r.perAnswer.count ? <small> median</small> : null}
        </span>
        <p className="small muted">
          {r.perAnswer.count ? `90th percentile ${tokens(r.perAnswer.p90)}` : 'No answer spent anything.'}
        </p>
        <p className="small muted">
          Plain {r.plainMedian ? tokens(r.plainMedian) : '—'} median · with an artifact {r.artifactMedian ? tokens(r.artifactMedian) : '—'}
        </p>
      </div>
      <div className="section tile">
        <span className="eyebrow">Spent on artifacts</span>
        <span className="big">
          {tokens(r.artifactTokens)}
          <small> {percent(r.tokens ? r.artifactTokens / r.tokens : null)}</small>
        </span>
        <Meter label="Chat tokens by kind of answer" segments={tokenKinds} />
        <p className="small muted">Of the chat tokens, what went on answers that published an artifact.</p>
      </div>
    </section>
  );
}

function Trend({ r, colorOf, nameOf }: { r: ChatMetrics; colorOf: (id: string) => string; nameOf: (id: string) => string }) {
  const [split, setSplit] = useState<Split>('project');
  const projectIds = r.byProject.map((p) => p.projectId);
  const columns = r.series.map((b) => {
    const segments: Segment[] =
      split === 'project'
        ? projectIds.map((id) => ({ label: nameOf(id), value: b.byProject.find((p) => p.projectId === id)?.tokens ?? 0, color: colorOf(id) }))
        : [
            { label: 'Artifact', value: b.artifactTokens, color: ARTIFACT },
            { label: 'Plain', value: b.tokens - b.artifactTokens, color: PLAIN },
          ];
    return {
      label: new Date(b.start).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' }),
      segments,
      tip: [`${stretch(b)} · ${tokens(b.tokens)} tokens`, ...segments.filter((s) => s.value > 0).map((s) => `${s.label} ${tokens(s.value)}`)].join('\n'),
    };
  });
  const key =
    split === 'project'
      ? r.byProject.map((p) => ({ label: nameOf(p.projectId), color: colorOf(p.projectId), value: tokens(p.tokens) }))
      : [
          { label: 'Artifact', color: ARTIFACT, value: tokens(r.artifactTokens) },
          { label: 'Plain', color: PLAIN, value: tokens(r.plainTokens) },
        ];
  return (
    <section className="section" aria-label="Chat tokens over the period">
      <div className="section-head">
        <h3>Chat tokens over the period</h3>
        <div className="seg viz-only" role="group" aria-label="Split columns by">
          <button type="button" aria-pressed={split === 'project'} onClick={() => setSplit('project')}>
            By project
          </button>
          <button type="button" aria-pressed={split === 'kind'} onClick={() => setSplit('kind')}>
            Plain or Artifact
          </button>
        </div>
      </div>
      <div className="viz-only stack">
        <Columns label="Chat tokens in each stretch of the period" caption="when the answers were given" columns={columns} format={tokens} />
        <Key items={key} />
      </div>
      <div className="table-scroll alt-only">
        <table className="small" aria-label="Chat tokens over the period">
          <thead>
            <tr>
              <th scope="col">From</th>
              <th scope="col">Chat tokens</th>
              <th scope="col">Artifact</th>
              <th scope="col">Plain</th>
            </tr>
          </thead>
          <tbody>
            {r.series.map((b) => (
              <tr key={b.start}>
                <td>{stretch(b)}</td>
                <td>{fmt(b.tokens)}</td>
                <td>{fmt(b.artifactTokens)}</td>
                <td>{fmt(b.tokens - b.artifactTokens)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function ByProject({ r, colorOf, nameOf }: { r: ChatMetrics; colorOf: (id: string) => string; nameOf: (id: string) => string }) {
  const most = Math.max(...r.byProject.map((p) => p.tokens), 0);
  return (
    <section className="section" aria-label="Chat by project">
      <div className="section-head">
        <h3>Chat by project</h3>
      </div>
      <p className="small muted">Bar length is the project's chat tokens, on one scale; the orange part went on artifacts.</p>
      <div className="viz-only stack">
        {r.byProject.map((p) => (
          <div
            key={p.projectId}
            className="hbar tall"
            data-tip={`${nameOf(p.projectId)}\n${fmt(p.tokens)} tokens · ${percent(p.share)} of chat\n${fmt(p.artifactTokens)} on artifacts · ${fmt(p.tokens - p.artifactTokens)} plain`}
          >
            <span>
              <i className="swatch" style={{ background: colorOf(p.projectId) }} aria-hidden="true" /> <b>{nameOf(p.projectId)}</b>
            </span>
            <span className="small">
              <b>{tokens(p.tokens)}</b>{' '}
              <span className="muted">
                {percent(p.share)} · {fmt(p.answers)} answer{p.answers === 1 ? '' : 's'} · {tokens(p.median)} median
              </span>
            </span>
            <span className="hbar-track">
              <span className="hbar-split" style={{ width: `${most ? (p.tokens / most) * 100 : 0}%` }}>
                <i style={{ flex: p.artifactTokens, background: ARTIFACT }} />
                <i style={{ flex: p.tokens - p.artifactTokens, background: PLAIN }} />
              </span>
            </span>
          </div>
        ))}
      </div>
      <div className="table-scroll alt-only">
        <table className="small" aria-label="Chat by project">
          <thead>
            <tr>
              <th scope="col">Project</th>
              <th scope="col">Answers</th>
              <th scope="col">Chat tokens</th>
              <th scope="col">On artifacts</th>
              <th scope="col">Median per answer</th>
              <th scope="col">Share</th>
            </tr>
          </thead>
          <tbody>
            {r.byProject.map((p) => (
              <tr key={p.projectId}>
                <td>{nameOf(p.projectId)}</td>
                <td>{fmt(p.answers)}</td>
                <td>{fmt(p.tokens)}</td>
                <td>{fmt(p.artifactTokens)}</td>
                <td>{fmt(p.median)}</td>
                <td>{percent(p.share)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function BesideTaskWork({ r }: { r: ChatMetrics }) {
  const all = r.tokens + r.taskTokens;
  const parts = [
    { label: 'Task work', value: r.taskTokens, color: TASK },
    { label: 'Chat answers with an artifact', value: r.artifactTokens, color: ARTIFACT },
    { label: 'Plain chat answers', value: r.plainTokens, color: PLAIN },
  ];
  return (
    <section className="section" aria-label="Chat beside task work">
      <div className="section-head">
        <h3>Chat beside task work</h3>
      </div>
      <p className="small muted">All tokens spent in the period. Task work is what the Flow tab counts; chat is not part of it.</p>
      <Meter tall label="All tokens in the period" segments={parts} />
      <Key items={parts.map((p) => ({ label: p.label, color: p.color, value: `${tokens(p.value)} · ${percent(all ? p.value / all : null)}` }))} />
    </section>
  );
}

function TopTasks({ r, nameOf }: { r: ChatMetrics; nameOf: (id: string) => string }) {
  return (
    <section className="section" aria-label="Tasks with the most chat">
      <div className="section-head">
        <h3>Tasks with the most chat</h3>
      </div>
      <p className="small muted">Where the chat tokens went, task by task, most first.</p>
      <div className="table-scroll">
        <table className="small" aria-label="Tasks with the most chat">
          <thead>
            <tr>
              <th scope="col">Task</th>
              <th scope="col">Project</th>
              <th scope="col">Answers</th>
              <th scope="col">Artifacts</th>
              <th scope="col">Chat tokens</th>
            </tr>
          </thead>
          <tbody>
            {r.topTasks.map((t) => (
              <tr key={t.threadId}>
                <td>
                  <a href={`#/threads/${t.threadId}`}>{t.title}</a>
                </td>
                <td>{nameOf(t.projectId)}</td>
                <td>{fmt(t.answers)}</td>
                <td>{fmt(t.artifactAnswers)}</td>
                <td>{fmt(t.tokens)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
