import type { Finding, FindingVerdict, RunCapabilities, ThreadDetails, UsageCall } from '../api/types';
import { fmt, pct, shortSha, toneOf, words } from '../domain/format';
import { RunLessonsBox, type RunLessonsActions } from './RunLessons';

function humanTesting(state: ThreadDetails['thread']['state']): { text: string; tone: string } {
  if (state === 'Accepted') return { text: 'Accepted', tone: 'done' };
  if (state === 'AwaitingHumanTesting') return { text: 'Awaiting you', tone: 'you' };
  return { text: 'Not started', tone: 'neutral' };
}

/** The gauges beside a thread: budget, verification, changed files and the run. */
/** How a call's phase is shown. */
const PHASE_NAMES: Record<string, string> = {
  Implement: 'implement',
  Rework: 'rework',
  Repair: 'repair',
  Nudge: 'reminder',
  Review: 'review',
  LightReview: 'light review',
  Scan: 'decision scan',
};

export const phaseName = (phase: string | null): string => (phase ? (PHASE_NAMES[phase] ?? phase.toLowerCase()) : 'other');

/** Implementation (implement, rework, repair, reminders) against review, in tokens charged. */
export function phaseTotals(calls: UsageCall[]): { implementation: number; review: number; other: number } {
  const totals = { implementation: 0, review: 0, other: 0 };
  for (const call of calls) {
    const tokens = call.charged;
    if (call.phase === 'Review' || call.phase === 'LightReview') totals.review += tokens;
    else if (call.phase === 'Implement' || call.phase === 'Rework' || call.phase === 'Repair' || call.phase === 'Nudge') totals.implementation += tokens;
    else totals.other += tokens;
  }
  return totals;
}

/** How each verdict is offered. */
const VERDICTS: [FindingVerdict, string][] = [
  ['Real', 'Real defect'],
  ['NotWorthFixing', 'Not worth fixing'],
  ['Wrong', 'Wrong'],
];

/**
 * One review finding, with buttons to judge it. Pressing the chosen verdict again clears it.
 * The verdicts are what tells the factory whether its reviews are worth what they cost.
 */
function FindingItem({ finding, onVerdict }: { finding: Finding; onVerdict?: (findingId: string, verdict: FindingVerdict | null) => void }) {
  return (
    <li>
      <span className="mono">
        {finding.file}
        {finding.line ? `:${finding.line}` : ''}
      </span>{' '}
      {finding.status === 'Fixed' ? <span className="pill done">fixed</span> : null} {finding.text}
      {finding.id && onVerdict ? (
        <span className="verdicts" role="group" aria-label="Was this finding right?">
          {VERDICTS.map(([value, label]) => (
            <button
              key={value}
              type="button"
              className="verdict"
              aria-pressed={finding.verdict === value}
              onClick={() => onVerdict(finding.id!, finding.verdict === value ? null : value)}
            >
              {label}
            </button>
          ))}
        </span>
      ) : null}
    </li>
  );
}

export function Details({
  details,
  usage,
  cachedInputWeight,
  onVerdict,
  lessonActions,
}: {
  details: ThreadDetails;
  usage: UsageCall[];
  /** The fraction of a cached input token that counts against the budget; unknown until settings load. */
  cachedInputWeight?: number;
  /** Judges a review finding; without it the findings are shown without buttons. */
  onVerdict?: (findingId: string, verdict: FindingVerdict | null) => void;
  /** Acts on the run's lessons; without it they are shown without buttons. */
  lessonActions?: RunLessonsActions;
}) {
  const { thread, verification, findings, handoff, run } = details;
  const evidence = handoff?.evidence ?? null;
  const cap = thread.budgetCap;
  const usedPct = cap ? Math.min(100, (thread.tokensUsed / cap) * 100) : 0;
  const reservedPct = cap ? Math.min(100 - usedPct, (thread.tokensReserved / cap) * 100) : 0;
  const left = cap === null ? null : Math.max(0, cap - thread.tokensUsed - thread.tokensReserved);

  // The run's own figures are newer than the last handoff's while a rework is under way.
  const build = verification?.build ?? evidence?.build ?? 'NotRun';
  const unitTests = verification?.unitTests ?? evidence?.unitTests ?? 'NotRun';
  const coverage = verification?.coverage ?? evidence?.coverage ?? 'NotMeasured';
  const coveragePercent = verification ? verification.changedLineCoveragePercent : (evidence?.changedLineCoveragePercent ?? null);
  const passed = verification?.passedCount ?? evidence?.testsPassed ?? 0;
  const failed = verification?.failedCount ?? evidence?.testsFailed ?? 0;
  const review = evidence?.review ?? (findings.length ? 'FindingsOpen' : 'NotRun');
  const human = humanTesting(thread.state);
  // Fixed findings stay listed: a finding that was fixed is the clearest case of a real one.
  const shownFindings = findings.filter((f) => f.status !== 'Dismissed');
  // An earlier round's findings stay open to a verdict after a rework has replaced its review.
  const earlierFindings = (details.earlierFindings ?? []).filter((f) => f.status !== 'Dismissed');

  const rows: [string, string, string][] = [
    ['Build', words(build), toneOf(build)],
    ['Unit tests', passed + failed > 0 ? `${fmt(passed)} passed${failed ? `, ${fmt(failed)} failed` : ''}` : words(unitTests), toneOf(unitTests)],
    ['Changed-line coverage', coveragePercent === null ? words(coverage) : pct(coveragePercent), toneOf(coverage)],
    ['Agent review', words(review), toneOf(review)],
    ['Human testing', human.text, human.tone],
  ];

  return (
    <aside className="details" aria-label="Task details">
      <section className="box" aria-label="Budget">
        <h3>Budget</h3>
        {cap !== null ? (
          <>
            <div className="gauge" role="img" aria-label={`${fmt(thread.tokensUsed)} of ${fmt(cap)} tokens used`}>
              <i className="u" style={{ width: `${usedPct}%` }} />
              <i className="d" style={{ width: `${reservedPct}%` }} />
            </div>
            <div className="vrow">
              <span className="num">{fmt(thread.tokensUsed)} used</span>
              <span className="num">
                {fmt(left ?? 0)} left of {fmt(cap)}
              </span>
            </div>
          </>
        ) : (
          <p className="num">{fmt(thread.tokensUsed)} tokens used. No cap set.</p>
        )}
        {thread.tokensReserved > 0 ? (
          <p className="small muted num">{fmt(thread.tokensReserved)} reserved for a call in flight.</p>
        ) : null}
        <div className="vrow">
          <span>
            {thread.provider} <span className="mono">{thread.model}</span>
          </span>
          <span className={`pill ${thread.budgetPrecision === 'strict' ? 'done' : 'you'}`}>{words(thread.budgetPrecision)}</span>
        </div>
        {usage.length ? (
          <>
            <PhaseSummary calls={usage} />
            <CacheMissSummary calls={usage} />
            <h3>Recent model calls</h3>
            <div className="ledger">
              {usage
                .map((call, index) => ({ call, n: index + 1 }))
                .slice(-5)
                .reverse()
                .map(({ call, n }) => (
                  <div key={call.id}>
                    <span>
                      Call {n} · {phaseName(call.phase)}
                      {call.servedBy && call.servedBy !== thread.provider ? <span className="muted"> · {call.servedBy}</span> : null}
                      {call.cacheMissTokens > 0 ? <span className="pill you"> cache miss</span> : null}
                    </span>
                    <span className="num">
                      up to {fmt(call.reserved)},{' '}
                      {call.status === 'Settled'
                        ? `used ${fmt(call.charged)}`
                        : call.status === 'Estimated'
                          ? `charged ${fmt(call.charged)} (estimate)`
                          : call.status === 'Reserved'
                            ? 'in flight'
                            : 'usage not reported'}
                    </span>
                  </div>
                ))}
            </div>
            <p className="small muted">
              Before a call is sent, the most it could cost is set aside: its input and its output limit. It is then
              charged what the provider reports, which is usually far less.
              {cachedInputWeight !== undefined && cachedInputWeight < 1
                ? ` Input the provider serves from its cache counts at ${pct(cachedInputWeight * 100)}.`
                : ''}
            </p>
          </>
        ) : null}
      </section>

      <section className="box" aria-label="Verification">
        <h3>Verification</h3>
        {rows.map(([label, text, tone]) => (
          <div className="vrow" key={label}>
            <span>{label}</span>
            <span className={`pill ${tone}`}>{text}</span>
          </div>
        ))}
        {shownFindings.length ? (
          <ul className="small findings" aria-label="Review findings">
            {shownFindings.map((f, i) => (
              <FindingItem key={f.id ?? i} finding={f} onVerdict={onVerdict} />
            ))}
          </ul>
        ) : null}
        {earlierFindings.length ? (
          <>
            <p className="small muted">From earlier rounds</p>
            <ul className="small findings" aria-label="Review findings from earlier rounds">
              {earlierFindings.map((f, i) => (
                <FindingItem key={f.id ?? i} finding={f} onVerdict={onVerdict} />
              ))}
            </ul>
          </>
        ) : null}
      </section>

      <RunLessonsBox lessons={details.runLessons ?? []} manual={thread.manualLessons ?? []} actions={lessonActions} />

      <section className="box" aria-label="Changed files">
        <h3>Changed files</h3>
        {evidence?.changedFiles.length ? (
          <div className="files">
            {evidence.changedFiles.map((path) => (
              <div key={path}>
                <span className="mono" title={path}>
                  {path}
                </span>
              </div>
            ))}
          </div>
        ) : (
          <p className="small muted">{handoff ? 'None recorded at the last handoff.' : 'Listed at handoff.'}</p>
        )}
      </section>

      {run ? (
        <section className="box" aria-label="Run">
          <h3>Run</h3>
          <dl className="kv small">
            <dt>Kind</dt>
            <dd>{run.kind}</dd>
            <dt>Status</dt>
            <dd>
              {run.status}
              {run.stopReason ? ` (${words(run.stopReason).toLowerCase()})` : ''}
            </dd>
            {run.baselineCommit ? (
              <>
                <dt>Started from</dt>
                <dd className="mono" title={run.baselineCommit}>
                  {shortSha(run.baselineCommit)}
                </dd>
              </>
            ) : null}
            {run.promptRevision ? (
              <>
                <dt>Prompts</dt>
                <dd className="mono">{run.promptRevision}</dd>
              </>
            ) : null}
            {run.capabilities ? <CapabilityRows capabilities={run.capabilities} /> : null}
          </dl>
        </section>
      ) : null}
    </aside>
  );
}

const WEB_SEARCH: Record<RunCapabilities['webSearch'], string> = { Off: 'off', WorkTurns: 'while it changes code', AllTurns: 'on every turn' };

const SKIPPED: Record<string, string> = {
  WaitingForApproval: 'waiting for approval',
  ChangedSinceApproved: 'changed since approved',
  NotUsedByPolicy: 'not used by policy',
};

/** What the run started with (m3-architecture.md §7.4): tool settings, each MCP server as it connected, and its skills. */
function CapabilityRows({ capabilities }: { capabilities: RunCapabilities }) {
  const { ptc, shellTimeoutSeconds, webSearch, mcpServers, mcpStatus } = capabilities;
  const repository = capabilities.repositorySkills ?? [];
  // A repository skill that is used hides the library's of the same name.
  const library = (capabilities.librarySkills ?? []).filter((l) => !repository.some((r) => r.replacesLibrarySkill && r.name === l.name));
  return (
    <>
      <dt>Tools</dt>
      <dd>
        PTC {ptc ? 'on' : 'off'}; shell limit {shellTimeoutSeconds % 60 === 0 ? `${shellTimeoutSeconds / 60} min` : `${shellTimeoutSeconds} s`}; web
        search {WEB_SEARCH[webSearch]}
      </dd>
      <dt>MCP servers</dt>
      <dd>
        {mcpServers.length === 0
          ? 'None'
          : mcpServers.map((server, i) => {
              const status = mcpStatus?.find((s) => s.name === server.name);
              return (
                <span key={server.name}>
                  {i ? ', ' : ''}
                  <span className="mono">{server.name}</span>{' '}
                  {!status ? (
                    <span className="muted">(connecting)</span>
                  ) : status.connected ? (
                    <span className="muted">({status.tools.length === 1 ? '1 tool' : `${status.tools.length} tools`})</span>
                  ) : (
                    <span className="error" title={status.error ?? undefined}>
                      (not available)
                    </span>
                  )}
                </span>
              );
            })}
      </dd>
      <dt>Skills</dt>
      <dd>
        {library.length === 0 && repository.length === 0
          ? 'None'
          : [
              ...library.map((skill) => (
                <span key={`l-${skill.name}`} className="mono" title={skill.description}>
                  {skill.name}
                </span>
              )),
              ...repository.map((skill) => (
                <span key={`r-${skill.name}`}>
                  <span className="mono">{skill.name}</span>{' '}
                  {skill.status === 'Used' ? (
                    <span className="muted">(repository)</span>
                  ) : (
                    <span className="muted">(repository, skipped: {SKIPPED[skill.status]})</span>
                  )}
                </span>
              )),
            ].map((item, i) => (
              <span key={i}>
                {i ? ', ' : ''}
                {item}
              </span>
            ))}
      </dd>
      {capabilities.taskSkills?.length ? (
        <>
          <dt>Task skills</dt>
          <dd>
            {capabilities.taskSkills.map((skill, i) => (
              <span key={skill.name}>
                {i ? ', ' : ''}
                <span className="mono" title={skill.text}>
                  {skill.name}
                </span>
              </span>
            ))}{' '}
            <span className="muted">(in every step's instructions)</span>
          </dd>
        </>
      ) : null}
    </>
  );
}

/** Where the task's tokens went: the work against its review (ReadMe_CodeVerifyOptimisations.md §11). */
/** Calls the provider's cache was expected to serve and did not, and what they cost above a hit. */
export function cacheMisses(calls: UsageCall[]): { calls: number; tokens: number } {
  const missed = calls.filter((c) => c.cacheMissTokens > 0);
  return { calls: missed.length, tokens: missed.reduce((sum, c) => sum + c.cacheMissTokens, 0) };
}

/** Shown only when there was a miss: it is the provider's cost, not the factory's own. */
function CacheMissSummary({ calls }: { calls: UsageCall[] }) {
  const misses = cacheMisses(calls);
  if (misses.calls === 0) return null;
  return (
    <p className="small muted num" aria-label="Cache misses">
      Cache misses: {fmt(misses.calls)} {misses.calls === 1 ? 'call' : 'calls'}, about {fmt(misses.tokens)} tokens more than
      had the provider's cache served them.
    </p>
  );
}

function PhaseSummary({ calls }: { calls: UsageCall[] }) {
  const totals = phaseTotals(calls);
  if (totals.implementation === 0 && totals.review === 0) return null;
  const ratio = totals.implementation > 0 ? Math.round((totals.review / totals.implementation) * 100) : null;
  return (
    <div className="ledger" aria-label="Tokens by phase">
      <div>
        <span>Implementation</span>
        <span className="num">{fmt(totals.implementation)}</span>
      </div>
      <div>
        <span>Review{ratio === null ? '' : ` (${ratio}% of implementation)`}</span>
        <span className="num">{fmt(totals.review)}</span>
      </div>
      {totals.other > 0 ? (
        <div>
          <span>Other</span>
          <span className="num">{fmt(totals.other)}</span>
        </div>
      ) : null}
    </div>
  );
}
