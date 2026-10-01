import { useState, type FormEvent } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { Project, Settings } from '../api/types';
import { pct } from '../domain/format';
import { ErrorNote } from './bits';

const PRESET_NAMES: Record<string, string> = { dotnet: '.NET', 'node-react': 'Node / React' };

export function ProjectsPage({
  api,
  settings,
  projects,
  isAdmin,
  onRegistered,
}: {
  api: FactoryApi;
  settings: Settings | null;
  projects: Project[];
  isAdmin: boolean;
  onRegistered: (project: Project) => void;
}) {
  return (
    <div className="page">
      <div className="grid2">
        <section className="section" aria-label="Projects">
          <h2>Projects</h2>
          {projects.length ? (
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Project</th>
                    <th>Repository</th>
                    <th>Default branch</th>
                    <th>Coverage</th>
                    <th>Draft PR</th>
                  </tr>
                </thead>
                <tbody>
                  {projects.map((p) => (
                    <tr key={p.id}>
                      <td>{p.name}</td>
                      <td className="mono">{p.gitHub}</td>
                      <td className="mono">{p.defaultBranch}</td>
                      <td className="num">{p.coverageThresholdPercent === null ? 'Not measured' : pct(p.coverageThresholdPercent)}</td>
                      <td>{p.pullRequestEnabled ? 'Yes' : 'No'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            <p className="muted">No project is registered yet.</p>
          )}
          <p className="small muted">
            The factory keeps its own clone of each repository and pushes only <span className="mono">factory/*</span> branches.
            It never merges or pushes to the default branch.
          </p>
        </section>

        {isAdmin ? (
          <RegisterProject api={api} settings={settings} onRegistered={onRegistered} />
        ) : (
          <section className="section">
            <h2>Register a GitHub project</h2>
            <p className="muted">Only an admin can register a project.</p>
          </section>
        )}
      </div>
    </div>
  );
}

function RegisterProject({
  api,
  settings,
  onRegistered,
}: {
  api: FactoryApi;
  settings: Settings | null;
  onRegistered: (project: Project) => void;
}) {
  const presets = settings?.presets ?? ['dotnet', 'node-react'];
  const [name, setName] = useState('');
  const [gitHubUrl, setGitHubUrl] = useState('');
  const [defaultBranch, setDefaultBranch] = useState('main');
  const [preset, setPreset] = useState(presets[0] ?? 'dotnet');
  const [coverage, setCoverage] = useState('');
  const [pullRequestEnabled, setPullRequestEnabled] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy) return;

    const threshold = coverage.trim() === '' ? undefined : Number(coverage);
    if (threshold !== undefined && !(threshold >= 0 && threshold <= 100)) {
      setError('The coverage threshold must be between 0 and 100.');
      return;
    }

    setBusy(true);
    setError(null);
    try {
      const project = await api.registerProject({
        gitHubUrl: gitHubUrl.trim(),
        name: name.trim() || undefined,
        defaultBranch: defaultBranch.trim(),
        preset,
        coverageThresholdPercent: threshold,
        pullRequestEnabled,
      });
      onRegistered(project);
      setName('');
      setGitHubUrl('');
      setCoverage('');
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The project could not be registered.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="section" aria-label="Register a GitHub project">
      <form className="stack" onSubmit={submit}>
        <h2>Register a GitHub project</h2>
        <div className="form-grid">
          <div className="field">
            <label htmlFor="rp-repo">GitHub URL</label>
            <input
              id="rp-repo"
              required
              value={gitHubUrl}
              onChange={(e) => setGitHubUrl(e.target.value)}
              placeholder="https://github.com/owner/repo"
            />
          </div>
          <div className="field">
            <label htmlFor="rp-name">Name</label>
            <input id="rp-name" value={name} onChange={(e) => setName(e.target.value)} />
            <span className="hint">Left empty, the repository name is used.</span>
          </div>
          <div className="field">
            <label htmlFor="rp-branch">Default branch</label>
            <input id="rp-branch" required value={defaultBranch} onChange={(e) => setDefaultBranch(e.target.value)} />
          </div>
          <div className="field">
            <label htmlFor="rp-preset">Verification preset</label>
            <select id="rp-preset" value={preset} onChange={(e) => setPreset(e.target.value)}>
              {presets.map((p) => (
                <option key={p} value={p}>
                  {PRESET_NAMES[p] ?? p}
                </option>
              ))}
            </select>
            <span className="hint">The commands the host runs to build and test, and the reports it reads.</span>
          </div>
          <div className="field">
            <label htmlFor="rp-cov">Changed-line coverage threshold (%)</label>
            <input id="rp-cov" type="number" min={0} max={100} value={coverage} onChange={(e) => setCoverage(e.target.value)} />
            <span className="hint">Left empty, the preset decides.</span>
          </div>
        </div>
        <label className="check">
          <input type="checkbox" checked={pullRequestEnabled} onChange={(e) => setPullRequestEnabled(e.target.checked)} />
          <span>Open a draft pull request at each handoff</span>
        </label>
        <p className="small muted">
          The factory reaches GitHub with the token in the host’s own settings. It is never given to a worker.
        </p>
        <ErrorNote message={error} />
        <div className="btn-row">
          <button className="btn primary" type="submit" disabled={busy}>
            Register project
          </button>
        </div>
      </form>
    </section>
  );
}
