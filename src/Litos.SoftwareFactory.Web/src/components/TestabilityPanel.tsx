import { useCallback, useEffect, useState } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { AdoptionPreview, Project, ProjectSecrets, TestabilitySummary, TestabilityView } from '../api/types';
import { ErrorNote } from './bits';

/** How often the panel looks again while a check is under way. */
export const TESTABILITY_POLL_MS = 3_000;

/** What a Make-testable task is asked to do: the request it is delegated with. */
export function makeTestableRequest(view: TestabilityView): string {
  const found = view.gaps.length ? `\n\nThe factory's testability check found:\n${view.gaps.map((g) => `- ${g}`).join('\n')}` : '';
  return `@factory Make this app testable by the factory: write litos.test.yaml at the repository root, and make the smallest changes to the app so it can be built, started on a given port, reset, signed into as each role and walked in a browser.${found}`;
}

/** A secret name as a contract writes it after {secret:: TEST_ADMIN_PASS. */
const SECRET_NAME = /^[A-Za-z_][A-Za-z0-9_]{0,99}$/;

/** What a project's testability says, in a few words, and whether it needs someone's attention. */
export function testabilitySummary(t: TestabilitySummary | null | undefined, revision?: number): { text: string; bad: boolean } {
  if (!t) return { text: '', bad: false };
  switch (t.status) {
    case 'NotChecked':
      return { text: 'Not checked', bad: false };
    case 'Checking':
      return { text: 'Checking…', bad: false };
    case 'NotAWebApp':
      return { text: 'Not a web app', bad: false };
    case 'StackNotSupported':
      return { text: 'Stack not supported yet', bad: false };
    case 'Gaps':
      return { text: 'Not testable yet', bad: true };
    case 'ContractFound':
      return { text: 'Contract found, not adopted', bad: false };
    case 'Adopted':
      return { text: revision ? `Contract adopted (rev ${revision})` : 'Contract adopted', bad: false };
    case 'TestReady':
      return { text: revision ? `Test-ready (rev ${revision})` : 'Test-ready', bad: false };
    case 'BrokenOnMain':
      return { text: 'Broken on main', bad: true };
    case 'CouldNotCheck':
      return { text: 'Could not check', bad: true };
  }
}

/**
 * A project's browser testing (testability-architecture.md §6.1, §9 Web): what the check found,
 * the contract on the default branch, the dry run's checks with their screenshots, and the
 * project's role secrets, which anyone on the project may set but no one can read back.
 */
export function TestabilityPanel({ api, project, onClose }: { api: FactoryApi; project: Project; onClose: () => void }) {
  const [view, setView] = useState<TestabilityView | null>(null);
  const [secrets, setSecrets] = useState<ProjectSecrets | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [v, s] = await Promise.all([api.testability(project.id), api.projectSecrets(project.id)]);
      setView(v);
      setSecrets(s);
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The testability check could not be read.');
    }
  }, [api, project.id]);

  useEffect(() => {
    void load();
  }, [load]);

  const checking = view?.status === 'Checking';
  useEffect(() => {
    if (!checking) return;
    const timer = setInterval(() => void load(), TESTABILITY_POLL_MS);
    return () => clearInterval(timer);
  }, [checking, load]);

  const checkAgain = async () => {
    setError(null);
    try {
      await api.checkTestability(project.id);
      setNotice('Checking again. This runs the project on the factory host and takes a minute or two.');
      await load();
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The check could not be started.');
    }
  };

  const startMakeTestable = async () => {
    if (!view) return;
    setError(null);
    try {
      const thread = await api.createThread({ projectId: project.id, title: `Make ${project.name} testable`, typeLabel: 'testability' });
      await api.postMessage(thread.id, crypto.randomUUID(), makeTestableRequest(view));
      window.location.hash = `#/threads/${thread.id}`;
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The task could not be started.');
    }
  };

  const summary = testabilitySummary(view, view?.profileRevision);
  return (
    <section className="section" aria-label={`Testability of ${project.name}`}>
      <h2>Testability: {project.name}</h2>
      <ErrorNote message={error} />
      {view ? (
        <>
          <div className="vrow">
            <span>UI testing</span>
            <span className={`pill ${summary.bad ? 'bad' : view.status === 'TestReady' ? 'done' : 'neutral'}`}>{summary.text}</span>
          </div>
          <p className="small muted">
            {view.stack ? <>Stack <span className="mono">{view.stack}</span>. </> : null}
            {view.checkedAt ? `Checked ${new Date(view.checkedAt).toLocaleString()}` : 'Never checked'}
            {view.commit ? <> on <span className="mono">{view.commit.slice(0, 7)}</span></> : null}.
            {view.stale ? ' The profile changed since; check again.' : null}
          </p>
          {view.problem ? <p className="error">{view.problem}</p> : null}

          {view.gaps.length ? (
            <>
              <h3>What to fix</h3>
              <ul aria-label="Gaps">
                {view.gaps.map((gap) => (
                  <li key={gap}>{gap}</li>
                ))}
              </ul>
            </>
          ) : null}

          {view.contract ? (
            <>
              <h3>Contract on the default branch</h3>
              <p>
                <span className="mono">{view.contract.path}</span> at <span className="mono">{view.contract.commit.slice(0, 7)}</span>:{' '}
                {view.contract.valid ? 'ready to adopt.' : 'does not run as written.'}
              </p>
              {view.contract.problems.length ? (
                <ul aria-label="Contract problems">
                  {view.contract.problems.map((p) => (
                    <li key={p} className="small">{p}</li>
                  ))}
                </ul>
              ) : null}
            </>
          ) : null}

          {view.contract ? <Adoption api={api} projectId={project.id} onAdopted={async (revision) => {
            setNotice(`Adopted as profile revision ${revision}. The Gate is running on the default branch.`);
            await load();
          }} /> : null}

          {view.run ? (
            <>
              <h3>{view.run.kind === 'gate' ? 'Gate on the default branch' : "Dry run of the stack's defaults"}</h3>
              <ul className="checks" aria-label="Dry run checks">
                {view.run.checks.map((check) => (
                  <li key={check.id}>
                    <span className={`pill ${check.status === 'Passed' ? 'done' : check.status === 'Failed' ? 'bad' : 'neutral'}`}>{check.status}</span>{' '}
                    <span className="mono">{check.id}</span>
                    {check.message && check.status !== 'Passed' ? <div className="small muted">{check.message}</div> : null}
                    {check.screenshots.map((url) => (
                      <a key={url} href={url} target="_blank" rel="noreferrer">
                        <img className="att-thumb" src={url} alt={`Screenshot of ${check.route ?? check.id}`} loading="lazy" />
                      </a>
                    ))}
                  </li>
                ))}
              </ul>
              {view.run.emails.length ? (
                <>
                  <h4>Emails the app sent</h4>
                  <ul aria-label="Emails">
                    {view.run.emails.map((email, i) => (
                      <li key={i}>
                        To {email.to.join(', ')}: {email.url ? <a href={email.url}>{email.subject}</a> : email.subject}
                      </li>
                    ))}
                  </ul>
                </>
              ) : null}
              <p className="small muted">No model calls; {view.run.wallSeconds.toFixed(0)}s on the factory host.</p>
            </>
          ) : null}

          {!view.adopted && (view.status === 'Gaps' || view.status === 'CouldNotCheck') ? (
            <div className="stack-tight">
              <p className="small">
                A Make-testable task writes the contract and the smallest app changes it needs, checks them with the factory's Gate, and
                ends in a pull request. After it is merged, adopt the contract here.
              </p>
              <button className="btn primary" type="button" onClick={() => void startMakeTestable()}>
                Start a Make-testable task
              </button>
            </div>
          ) : null}

          <div className="btn-row">
            <button className="btn" type="button" disabled={checking} onClick={() => void checkAgain()}>
              {checking ? 'Checking…' : 'Check again'}
            </button>
            <button className="btn" type="button" onClick={onClose}>
              Close
            </button>
          </div>
          {notice ? <p className="small muted">{notice}</p> : null}
        </>
      ) : (
        <p className="muted">Loading…</p>
      )}

      {secrets ? <RoleSecrets api={api} projectId={project.id} secrets={secrets} onChanged={load} /> : null}
    </section>
  );
}

/**
 * Adoption (testability-architecture.md §5): the default branch's litos.test.yaml, shown as a
 * diff against the adopted contract, then saved as a new profile revision. Only the commit the
 * person reviewed is adopted.
 */
function Adoption({ api, projectId, onAdopted }: { api: FactoryApi; projectId: string; onAdopted: (revision: number) => Promise<void> }) {
  const [preview, setPreview] = useState<AdoptionPreview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const review = async () => {
    setError(null);
    try {
      setPreview(await api.adoptionPreview(projectId));
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The contract could not be read.');
    }
  };

  const adopt = async () => {
    if (!preview) return;
    setBusy(true);
    setError(null);
    try {
      const { profileRevision } = await api.adoptContract(projectId, preview.commit);
      setPreview(null);
      await onAdopted(profileRevision);
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The contract could not be adopted.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div role="group" aria-label="Adopt the contract">
      {preview ? (
        <>
          <h3>Contract at {preview.commit.slice(0, 7)}</h3>
          {preview.changes ? (
            <pre className="mono small" aria-label="Contract changes">
              {preview.diff.map((line, i) => (
                <div key={i} className={line.kind === 'Added' ? 'diff-add' : line.kind === 'Removed' ? 'diff-del' : undefined}>
                  {line.kind === 'Added' ? '+ ' : line.kind === 'Removed' ? '- ' : '  '}
                  {line.text}
                </div>
              ))}
            </pre>
          ) : (
            <p className="small muted">It is the contract already adopted.</p>
          )}
          {preview.problems.length ? <p className="error">It does not run as written: {preview.problems.join(' ')}</p> : null}
          {preview.missingSecrets.length ? (
            <p className="error">Set {preview.missingSecrets.join(', ')} below first: the contract uses them.</p>
          ) : null}
          <div className="btn-row">
            <button className="btn primary" type="button" disabled={busy || !preview.canAdopt} onClick={() => void adopt()}>
              Adopt this contract
            </button>
            <button className="btn" type="button" onClick={() => setPreview(null)}>
              Cancel
            </button>
          </div>
        </>
      ) : (
        <button className="btn small" type="button" onClick={() => void review()}>
          Review contract on main
        </button>
      )}
      <ErrorNote message={error} />
    </div>
  );
}

/**
 * The project's secrets, by name: test-role passwords a contract names as {secret:NAME}. They go
 * only to the app and the browser's sign-in, never to the agent; once set, never shown again.
 */
function RoleSecrets({ api, projectId, secrets, onChanged }: { api: FactoryApi; projectId: string; secrets: ProjectSecrets; onChanged: () => Promise<void> }) {
  const [name, setName] = useState('');
  const [value, setValue] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const names = [...new Set([...secrets.missing, ...secrets.secrets.map((s) => s.name)])].sort();
  const isSet = (n: string) => secrets.secrets.some((s) => s.name === n);

  const act = async (action: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    try {
      await action();
      setValue('');
      await onChanged();
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'That could not be changed.');
    } finally {
      setBusy(false);
    }
  };

  const nameValid = SECRET_NAME.test(name);
  return (
    <div aria-label="Role secrets" role="group">
      <h3>Role secrets</h3>
      <p className="small muted">
        Test users' passwords, named in the contract as <span className="mono">{'{secret:NAME}'}</span>. They reach only the app
        and the browser's sign-in, never the agent, and are never shown again once set.
      </p>
      {names.length ? (
        <ul aria-label="Secret names">
          {names.map((n) => (
            <li key={n}>
              <span className="mono">{n}</span>{' '}
              <span className={`pill ${isSet(n) ? 'done' : 'bad'}`}>{isSet(n) ? 'Set' : 'Missing'}</span>{' '}
              {isSet(n) ? (
                <button className="btn small" type="button" disabled={busy} onClick={() => void act(() => api.clearProjectSecret(projectId, n))}>
                  Clear {n}
                </button>
              ) : (
                <button className="btn small" type="button" onClick={() => setName(n)}>
                  Set {n}
                </button>
              )}
            </li>
          ))}
        </ul>
      ) : (
        <p className="small muted">No secret is set, and no adopted contract needs one.</p>
      )}
      <div className="field">
        <label htmlFor={`secret-name-${projectId}`}>Name</label>
        <input id={`secret-name-${projectId}`} value={name} placeholder="TEST_ADMIN_PASS" onChange={(e) => setName(e.target.value)} />
        <label htmlFor={`secret-value-${projectId}`}>Value</label>
        <input
          id={`secret-value-${projectId}`}
          type="password"
          autoComplete="new-password"
          value={value}
          onChange={(e) => setValue(e.target.value)}
        />
        <button
          className="btn small"
          type="button"
          disabled={busy || !nameValid || !value}
          onClick={() => void act(() => api.setProjectSecret(projectId, name, value))}
        >
          {isSet(name) ? 'Replace' : 'Set'} secret
        </button>
        {name && !nameValid ? <span className="hint error">Use letters, digits and _, as in TEST_ADMIN_PASS.</span> : null}
      </div>
      <ErrorNote message={error} />
    </div>
  );
}
