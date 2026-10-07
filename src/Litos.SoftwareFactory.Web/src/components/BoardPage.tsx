import { useMemo, useState } from 'react';
import type { Project, Thread, Turn } from '../api/types';
import { fmt } from '../domain/format';
import { STAGES, turnLabel } from '../domain/task';
import { TurnPill } from './bits';

const TURNS: Turn[] = ['AwaitingYou', 'AwaitingAgent', 'AgentWorking', 'Paused', 'NotStarted', 'Done'];

/** "81k of 300k": a card has room for the gist, not the exact count. */
const short = (tokens: number) => (tokens >= 1_000 ? `${fmt(Math.round(tokens / 1_000))}k` : fmt(tokens));

/**
 * The board (§7.1, m2-architecture.md §6): every task the user can see, in a column per stage,
 * each card saying whose move it is. Filters narrow it by project, type, owner and turn.
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

  const projectName = (id: string) => projects.find((p) => p.id === id)?.name ?? '';
  const ownerName = (id: string) => (id === userId ? 'You' : (names[id] ?? 'Someone'));
  const owners = useMemo(() => [...new Set(threads.map((t) => t.ownerId))], [threads]);

  const shown = threads
    .filter((t) => (!project || t.projectId === project) && (!type || t.typeLabel === type) && (!owner || t.ownerId === owner) && (!turn || t.turn === turn))
    .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
  const filtered = !!(project || type || owner || turn);

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
      </section>

      {threads.length === 0 ? (
        <p className="muted">No task yet. Create a thread to delegate work to the factory.</p>
      ) : (
        <div className="board" role="list" aria-label="Board">
          {STAGES.map((stage) => {
            const cards = shown.filter((t) => t.stage === stage);
            return (
              <section key={stage} className="board-col" role="listitem" aria-label={stage}>
                <h3>
                  {stage} <span className="muted">{cards.length}</span>
                </h3>
                {cards.map((t) => (
                  <button key={t.id} className={`card ${t.turn === 'AwaitingYou' ? 'card-you' : ''}`} onClick={() => onOpen(t.id)} aria-label={t.title}>
                    <span className="card-title">{t.title}</span>
                    <span className="card-row">
                      <TurnPill turn={t.turn} />
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
