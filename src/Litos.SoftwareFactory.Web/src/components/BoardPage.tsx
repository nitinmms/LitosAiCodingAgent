import { useMemo, useState } from 'react';
import type { Project, Thread, Turn } from '../api/types';
import { fmt } from '../domain/format';
import { awaitsYou, closedOrder, FINISHED_SHOWN, isClosed, STAGES, turnLabel } from '../domain/task';
import { FinishedPill, TurnPill } from './bits';

/** Finished work has its own Closed column, so neither its turn nor the Done stage is offered. */
const TURNS: Turn[] = ['AwaitingYou', 'AwaitingAgent', 'AgentWorking', 'Paused', 'NotStarted'];
const LIVE_STAGES = STAGES.filter((s) => s !== 'Done');

/** The strip's counts, in the order they are read. "Needs you" is personal (§7.1); the rest are turns. */
const STRIP: { turn: Turn; label: string; mine?: boolean }[] = [
  { turn: 'AwaitingYou', label: 'needs you', mine: true },
  { turn: 'AgentWorking', label: 'working' },
  { turn: 'AwaitingAgent', label: 'queued' },
  { turn: 'Paused', label: 'paused' },
  { turn: 'NotStarted', label: 'not started' },
];

/** "81k of 300k": a card has room for the gist, not the exact count. */
const short = (tokens: number) => (tokens >= 1_000 ? `${fmt(Math.round(tokens / 1_000))}k` : fmt(tokens));

/**
 * The board (§7.1, m2-architecture.md §6): every live task the user can see, in a column per stage,
 * each card saying whose move it is. Filters narrow it by project, type, owner and turn, and a
 * strip counts the live work. Finished tasks are hidden until asked for, then shown in one Closed
 * column rather than in the stage each stopped at (m3-architecture.md §11).
 */
export function BoardPage({
  threads,
  projects,
  names,
  userId,
  taskTypes,
  onOpen,
}: {
  threads: Thread[];
  projects: Project[];
  /** Owner names by user id, from the host's directory. */
  names: Record<string, string>;
  userId: string;
  taskTypes: string[];
  onOpen: (threadId: string) => void;
}) {
  const [project, setProject] = useState('');
  const [type, setType] = useState('');
  const [owner, setOwner] = useState('');
  const [turn, setTurn] = useState('');
  const [showFinished, setShowFinished] = useState(false);
  const [allClosed, setAllClosed] = useState(false);

  const projectName = (id: string) => projects.find((p) => p.id === id)?.name ?? '';
  const ownerName = (id: string) => (id === userId ? 'You' : (names[id] ?? 'Someone'));
  const owners = useMemo(() => [...new Set(threads.map((t) => t.ownerId))], [threads]);

  const inScope = threads.filter((t) => (!project || t.projectId === project) && (!type || t.typeLabel === type));
  const shown = inScope.filter((t) => (!owner || t.ownerId === owner) && (!turn || t.turn === turn));
  const live = shown.filter((t) => !isClosed(t.state)).sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
  // Every turn offered is a live one, so a turn filter leaves nothing finished to show.
  const closed = turn ? [] : closedOrder(shown.filter((t) => isClosed(t.state)));
  const filtered = !!(project || type || owner || turn);

  // Counted within the project and type filters only: the owner and turn filters are what a count sets.
  const counts = STRIP.map((s) => ({
    ...s,
    count: inScope.filter((t) => !isClosed(t.state) && (s.mine ? awaitsYou(t, userId) : t.turn === s.turn)).length,
  })).filter((s) => s.count > 0);
  const pressed = (s: (typeof STRIP)[number]) => turn === s.turn && owner === (s.mine ? userId : '');
  const pick = (s: (typeof STRIP)[number]) => {
    const on = pressed(s);
    setTurn(on ? '' : s.turn);
    setOwner(on || !s.mine ? '' : userId);
  };

  return (
    <div className="page">
      <section className="section board-head" aria-label="Filters">
        <h2>Board</h2>
        <div className="board-filters">
          <Filter label="Project" value={project} onChange={setProject} options={projects.map((p) => [p.id, p.name])} />
          <Filter label="Type" value={type} onChange={setType} options={taskTypes.map((t) => [t, t])} />
          <Filter
            label="Owner"
            value={owner}
            onChange={setOwner}
            options={[...owners.filter((id) => id === userId), ...owners.filter((id) => id !== userId)].map((id) => [id, ownerName(id)])}
          />
          <Filter label="Turn" value={turn} onChange={setTurn} options={TURNS.map((t) => [t, turnLabel(t).text])} />
          {filtered ? (
            <button
              className="btn ghost small"
              onClick={() => {
                setProject('');
                setType('');
                setOwner('');
                setTurn('');
              }}
            >
              Clear filters
            </button>
          ) : null}
        </div>
        <div className="board-strip">
          <div className="strip" role="group" aria-label="Live tasks">
            {counts.length === 0 ? (
              <span className="muted">Nothing live</span>
            ) : (
              counts.map((s, i) => (
                <span key={s.turn} className="strip-item">
                  {i > 0 ? (
                    <span className="muted" aria-hidden="true">
                      ·
                    </span>
                  ) : null}
                  <button className={`count${s.mine ? ' you' : ''}`} aria-pressed={pressed(s)} onClick={() => pick(s)}>
                    <span className="num">{s.count}</span> {s.label}
                  </button>
                </span>
              ))
            )}
          </div>
          <label className="check">
            <input type="checkbox" checked={showFinished} onChange={(e) => setShowFinished(e.target.checked)} />
            <span>
              Show finished (<span className="num">{closed.length}</span>)
            </span>
          </label>
        </div>
      </section>

      {threads.length === 0 ? (
        <p className="muted">No task yet. Create a thread to delegate work to the factory.</p>
      ) : (
        <div
          className="board"
          role="list"
          aria-label="Board"
          style={{ gridTemplateColumns: `repeat(${LIVE_STAGES.length + (showFinished ? 1 : 0)}, minmax(200px, 1fr))` }}
        >
          {LIVE_STAGES.map((stage) => {
            const cards = live.filter((t) => t.stage === stage);
            return (
              <section key={stage} className="board-col" role="listitem" aria-label={stage}>
                <h3>
                  {stage} <span className="muted">{cards.length}</span>
                </h3>
                {cards.map((t) => (
                  <button key={t.id} className={`card ${t.turn === 'AwaitingYou' ? 'card-you' : ''}`} onClick={() => onOpen(t.id)} aria-label={t.title}>
                    <span className="card-title">{t.title}</span>
                    <span className="card-row">
                      <TurnPill turn={t.turn} state={t.state} />
                      <span className="tag">{t.typeLabel}</span>
                    </span>
                    <span className="small muted">
                      {projectName(t.projectId)} · {ownerName(t.ownerId)}
                    </span>
                    {t.turn === 'AwaitingAgent' && t.stateReason ? <span className="small">{t.stateReason}</span> : null}
                    {t.budgetCap ? (
                      <span className="card-budget" title={`${fmt(t.tokensUsed)} of ${fmt(t.budgetCap)} tokens`}>
                        <span className="bar" aria-hidden="true">
                          <span style={{ width: `${Math.min(100, (t.tokensUsed / t.budgetCap) * 100)}%` }} />
                        </span>
                        <span className="small muted num">
                          {short(t.tokensUsed)} of {short(t.budgetCap)}
                        </span>
                      </span>
                    ) : null}
                    {t.pullRequestNumber ? <span className="small mono">PR #{t.pullRequestNumber}</span> : null}
                  </button>
                ))}
              </section>
            );
          })}
          {showFinished ? (
            <section className="board-col closed" role="listitem" aria-label="Closed">
              <h3>
                Closed <span className="muted">{closed.length}</span>
              </h3>
              {(allClosed ? closed : closed.slice(0, FINISHED_SHOWN)).map((t) => (
                <button key={t.id} className="card compact" onClick={() => onOpen(t.id)} aria-label={t.title}>
                  <span className="card-title">{t.title}</span>
                  <span className="card-row">
                    <FinishedPill state={t.state} />
                    <span className="small muted">
                      {projectName(t.projectId)} · {ownerName(t.ownerId)}
                    </span>
                  </span>
                  {t.state === 'Cancelled' ? <span className="small muted">Stopped in {t.stage}</span> : null}
                </button>
              ))}
              {closed.length > FINISHED_SHOWN ? (
                <button className="btn small" onClick={() => setAllClosed(!allClosed)}>
                  {allClosed ? `Show the last ${FINISHED_SHOWN}` : `Show all ${closed.length}`}
                </button>
              ) : null}
              {closed.length === 0 ? <p className="small muted">Nothing finished.</p> : null}
            </section>
          ) : null}
        </div>
      )}
    </div>
  );
}

function Filter({
  label,
  value,
  onChange,
  options,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  options: [string, string][];
}) {
  return (
    <label className="board-filter">
      <span className="small">{label}</span>
      <select value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">All</option>
        {options.map(([key, text]) => (
          <option key={key} value={key}>
            {text}
          </option>
        ))}
      </select>
    </label>
  );
}
