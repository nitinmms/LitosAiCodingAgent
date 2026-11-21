import { useCallback, useEffect, useState } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { ProjectScenario, ProjectScenarios, ScenarioStep, ScenarioTrial } from '../api/types';
import { ErrorNote } from './bits';

/** What "New scenario" starts from: the W3.5 format, one of each common step. */
export const SCENARIO_TEMPLATE = `{
  "id": "my-scenario",
  "title": "What a person does",
  "criterion": "The acceptance criterion this checks.",
  "start": "/",
  "steps": [
    { "type": "click", "target": { "role": "button", "name": "Save" } },
    { "type": "expectText", "target": { "testId": "status" }, "text": "Saved" },
    { "type": "screenshot", "name": "saved" }
  ]
}`;

/** "Approved rev 2 · draft rev 3", for one scenario's badge. */
export function scenarioState(s: ProjectScenario): { text: string; tone: 'done' | 'bad' | 'neutral' } {
  if (s.retired) return { text: 'Retired', tone: 'neutral' };
  const approved = s.approvedRevision ? `Approved rev ${s.approvedRevision}` : 'Not approved';
  const draft = s.latest.status === 'Draft' ? ` · draft rev ${s.latest.revision}` : '';
  return { text: approved + draft, tone: s.approvedRevision ? 'done' : 'neutral' };
}

/**
 * A project's browser scenarios (README_Litos.Probe.Web.md W3.6): written here, checked against the
 * adopted contract, tried on the default branch, and approved by anyone on the project. Once
 * approved, every task runs them after its smoke. Saving never edits a revision: it makes a new draft.
 */
export function ScenariosSection({ api, projectId }: { api: FactoryApi; projectId: string }) {
  const [data, setData] = useState<ProjectScenarios | null>(null);
  const [editing, setEditing] = useState<string | null>(null);
  const [problems, setProblems] = useState<string[]>([]);
  const [trial, setTrial] = useState<{ scenarioId: string; result: ScenarioTrial } | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [showRetired, setShowRetired] = useState(false);

  const load = useCallback(async () => {
    try {
      setData(await api.scenarios(projectId));
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The scenarios could not be read.');
    }
  }, [api, projectId]);

  useEffect(() => {
    void load();
  }, [load]);

  const act = async (label: string, action: () => Promise<void>) => {
    setBusy(label);
    setError(null);
    setNotice(null);
    try {
      await action();
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'That did not work.');
    } finally {
      setBusy(null);
    }
  };

  const check = () =>
    act('check', async () => {
      if (editing === null) return;
      setProblems((await api.validateScenario(projectId, editing)).problems);
    });

  const save = () =>
    act('save', async () => {
      if (editing === null) return;
      const saved = await api.saveScenario(projectId, editing);
      setProblems(saved.problems);
      setNotice(`Saved ${saved.scenarioId} as draft revision ${saved.revision}${saved.problems.length ? ', with problems to fix before it can be approved' : ''}.`);
      if (!saved.problems.length) setEditing(null);
      await load();
    });

  const approve = (s: ProjectScenario) =>
    act(`approve:${s.scenarioId}`, async () => {
      await api.approveScenario(projectId, s.scenarioId, s.latest.revision);
      setNotice(`Approved ${s.scenarioId} revision ${s.latest.revision}. Every task runs it after its smoke; the Gate on the default branch is running it now.`);
      await load();
    });

  const retire = (s: ProjectScenario) =>
    act(`retire:${s.scenarioId}`, async () => {
      await api.retireScenario(projectId, s.scenarioId);
      setNotice(`Retired ${s.scenarioId}: tasks no longer run it.`);
      await load();
    });

  const run = (s: ProjectScenario) =>
    act(`run:${s.scenarioId}`, async () => {
      setTrial(null);
      setTrial({ scenarioId: s.scenarioId, result: await api.runScenario(projectId, s.scenarioId, s.latest.revision) });
    });

  if (!data) return null;
  if (!data.adopted) {
    return (
      <div role="group" aria-label="Scenarios">
        <h3>Scenarios</h3>
        <p className="small muted">Adopt a contract first: a scenario signs in and visits pages as the contract describes them.</p>
      </div>
    );
  }

  const shown = data.scenarios.filter((s) => showRetired || !s.retired);
  const retiredCount = data.scenarios.length - data.scenarios.filter((s) => !s.retired).length;
  return (
    <div role="group" aria-label="Scenarios">
      <h3>Scenarios</h3>
      <p className="small muted">
        Fixed browser steps that check what a person does. Once a revision is approved, every task runs it after its smoke, and a
        failure the task caused goes back to it for repair.
      </p>

      {shown.length ? (
        <ul className="checks" aria-label="Project scenarios">
          {shown.map((s) => {
            const state = scenarioState(s);
            const draft = s.latest.status === 'Draft';
            return (
              <li key={s.scenarioId}>
                <span className={`pill ${state.tone}`}>{state.text}</span> <span className="mono">{s.scenarioId}</span>
                {s.title ? <> · {s.title}</> : null}
                {s.role ? <span className="small muted"> · as {s.role}</span> : null}
                {s.criterion ? <div className="small">{s.criterion}</div> : null}
                {s.problems.length ? (
                  <ul aria-label={`Problems with ${s.scenarioId}`}>
                    {s.problems.map((p) => (
                      <li key={p} className="small error">{p}</li>
                    ))}
                  </ul>
                ) : null}
                {!s.retired ? (
                  <div className="btn-row">
                    <button className="btn small" type="button" onClick={() => { setEditing(s.latest.json); setProblems(s.problems); }}>
                      Edit
                    </button>
                    <button className="btn small" type="button" disabled={busy !== null} onClick={() => void run(s)}>
                      {busy === `run:${s.scenarioId}` ? 'Running on main…' : `Run rev ${s.latest.revision} on main`}
                    </button>
                    {draft ? (
                      <button
                        className="btn small primary"
                        type="button"
                        disabled={busy !== null || s.problems.length > 0}
                        onClick={() => void approve(s)}
                      >
                        Approve rev {s.latest.revision}
                      </button>
                    ) : null}
                    <button className="btn small" type="button" disabled={busy !== null} onClick={() => void retire(s)}>
                      Retire
                    </button>
                  </div>
                ) : null}
                {trial?.scenarioId === s.scenarioId ? <Trial result={trial.result} /> : null}
              </li>
            );
          })}
        </ul>
      ) : (
        <p className="small muted">No scenarios yet.</p>
      )}
      {retiredCount ? (
        <button className="btn small" type="button" onClick={() => setShowRetired(!showRetired)}>
          {showRetired ? 'Hide retired' : `Show ${retiredCount} retired`}
        </button>
      ) : null}

      {editing !== null ? (
        <div className="field">
          <label htmlFor={`scenario-json-${projectId}`}>Scenario (JSON)</label>
          <textarea
            id={`scenario-json-${projectId}`}
            className="mono"
            rows={16}
            spellCheck={false}
            value={editing}
            onChange={(e) => setEditing(e.target.value)}
          />
          {problems.length ? (
            <ul aria-label="Scenario problems">
              {problems.map((p) => (
                <li key={p} className="small error">{p}</li>
              ))}
            </ul>
          ) : null}
          <div className="btn-row">
            <button className="btn small" type="button" disabled={busy !== null} onClick={() => void check()}>
              Check
            </button>
            <button className="btn small primary" type="button" disabled={busy !== null} onClick={() => void save()}>
              Save as a new draft
            </button>
            <button className="btn small" type="button" onClick={() => { setEditing(null); setProblems([]); }}>
              Cancel
            </button>
          </div>
        </div>
      ) : (
        <button className="btn small" type="button" onClick={() => { setEditing(SCENARIO_TEMPLATE); setProblems([]); }}>
          New scenario
        </button>
      )}
      <ErrorNote message={error} />
      {notice ? <p className="small muted">{notice}</p> : null}
    </div>
  );
}

/** "Run on main"'s result: each step, and why the app did not come up if it did not. */
function Trial({ result }: { result: ScenarioTrial }) {
  const scenario = result.checks.find((c) => c.kind === 'Scenario');
  const failedBringUp = result.checks.filter((c) => c.kind !== 'Scenario' && c.status === 'Failed');
  return (
    <div aria-label="Run on main" role="group" className="stack-tight">
      <p className="small">
        On <span className="mono">{result.commit?.slice(0, 7) ?? 'the default branch'}</span>: {scenario?.status ?? result.outcome}
        {scenario?.classification ? ` (${scenario.classification})` : ''} in {result.wallSeconds.toFixed(0)}s.
      </p>
      {failedBringUp.map((c) => (
        <p key={c.id} className="small error">
          {c.id}: {c.message}
        </p>
      ))}
      {result.findings.map((f) => (
        <p key={f} className="small muted">{f}</p>
      ))}
      {scenario?.steps ? <Steps steps={scenario.steps} screenshot={(s) => s.screenshot} /> : null}
    </div>
  );
}

/** A scenario's steps, numbered, each with its result; a screenshot where the step took one. */
export function Steps({ steps, screenshot }: { steps: ScenarioStep[]; screenshot: (step: ScenarioStep) => string | null }) {
  return (
    <ol className="small" aria-label="Steps" start={0}>
      {steps.map((step) => {
        const url = screenshot(step);
        return (
          <li key={step.index}>
            <span className={`pill ${step.status === 'Passed' ? 'done' : step.status === 'Failed' ? 'bad' : 'neutral'}`}>{step.status}</span>{' '}
            {step.description}
            {step.message ? <div className="error">{step.message}</div> : null}
            {url ? (
              <a href={url} target="_blank" rel="noreferrer">
                <img className="att-thumb" src={url} alt={`Screenshot after ${step.description}`} loading="lazy" />
              </a>
            ) : null}
          </li>
        );
      })}
    </ol>
  );
}
