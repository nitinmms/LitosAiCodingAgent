import { useEffect, useState, type FormEvent } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { Project, ProjectBaseline, Settings } from '../api/types';
import { fmt, pct } from '../domain/format';
import { ErrorNote } from './bits';
import { jsonProblem } from './PresetsTab';

/** How often the page looks again while a baseline is being measured. */
export const BASELINE_POLL_MS = 3_000;

/** What a project's baseline says, in a few words, and whether it is bad news. */
export function baselineSummary(baseline: ProjectBaseline | null | undefined): { text: string; bad: boolean } {
  if (!baseline) return { text: '', bad: false };
  switch (baseline.state) {
    case 'Measuring':
      return { text: 'Measuring…', bad: false };
    case 'NotMeasured':
      return { text: 'Not measured yet', bad: false };
    case 'CouldNotMeasure':
      return { text: 'Could not be measured', bad: true };
  }
  if (baseline.build === 'Failed') return { text: 'Build fails', bad: true };
  if (baseline.build === 'Unavailable' || baseline.unitTests === 'Unavailable') return { text: 'A command could not start', bad: true };
  const passed = baseline.passedCount ?? 0;
  const failed = baseline.failedCount ?? 0;
  if (baseline.unitTests === 'NoTests' || passed + failed === 0) return { text: 'Builds; no tests found', bad: true };
  if (failed > 0 || baseline.unitTests === 'Failed') return { text: `${fmt(failed)} of ${fmt(passed + failed)} tests fail`, bad: true };
  return { text: `${fmt(passed)} tests pass`, bad: false };
}

export function ProjectsPage({
  api,
  settings,
  projects,
  isAdmin,
  onRegistered,
  onUpdated,
  onReloaded,
}: {
  api: FactoryApi;
  settings: Settings | null;
  projects: Project[];
  isAdmin: boolean;
  onRegistered: (project: Project) => void;
  /** A project's profile was replaced. */
  onUpdated: (project: Project) => void;
  /** The projects as the host lists them now, read while a baseline is being measured. */
  onReloaded: (projects: Project[]) => void;
}) {
  const [editing, setEditing] = useState<string | null>(null);
  const measuring = projects.some((p) => p.baseline?.state === 'Measuring');

  useEffect(() => {
    if (!measuring) return;
    const timer = setInterval(() => {
      api.projects().then(onReloaded).catch(() => {});
    }, BASELINE_POLL_MS);
    return () => clearInterval(timer);
  }, [api, measuring, onReloaded]);

  const editedProject = projects.find((p) => p.id === editing) ?? null;

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
                    <th>Baseline</th>
                    {isAdmin ? <th /> : null}
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
                      <td>
                        <BaselineCell baseline={p.baseline} />
                      </td>
                      {isAdmin ? (
                        <td>
                          <button
                            className="btn small"
                            type="button"
                            aria-label={`Change the profile of ${p.name}`}
                            onClick={() => setEditing(editing === p.id ? null : p.id)}
                          >
                            Change profile
                          </button>
                        </td>
                      ) : null}
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
            It never merges or pushes to the default branch. A project's baseline is its profile's result on the default branch,
            so a task is never blamed for what already failed.
          </p>
        </section>

        {editedProject ? (
          <ProfileEditor
            key={`${editedProject.id}-${editedProject.profileRevision}`}
            api={api}
            settings={settings}
            project={editedProject}
            onSaved={(project) => {
              setEditing(null);
              onUpdated(project);
            }}
            onCancel={() => setEditing(null)}
          />
        ) : isAdmin ? (
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

function BaselineCell({ baseline }: { baseline: ProjectBaseline | null | undefined }) {
  const { text, bad } = baselineSummary(baseline);
  if (!baseline) return null;
  const why = baseline.state === 'CouldNotMeasure' ? baseline.problem : bad ? baseline.problems?.[0] : null;
  return (
    <div>
      <span className={bad ? 'error' : undefined}>{text}</span>
      {why ? <div className="small muted">{why}</div> : null}
    </div>
  );
}

type ProfileSource = 'preset' | 'written' | 'threshold';

/**
 * Replaces a registered project's profile (m3-architecture.md §8): with a preset's, with one
 * written out, or only its coverage threshold. Its revision moves on, so its next run measures
 * its baseline again; the host measures it at once.
 */
function ProfileEditor({
  api,
  settings,
  project,
  onSaved,
  onCancel,
}: {
  api: FactoryApi;
  settings: Settings | null;
  project: Project;
  onSaved: (project: Project) => void;
  onCancel: () => void;
}) {
  const presets = settings?.presets ?? [];
  const [source, setSource] = useState<ProfileSource>('written');
  const [preset, setPreset] = useState(presets[0]?.name ?? '');
  const [profile, setProfile] = useState(() => pretty(project.profile ?? ''));
  const [coverage, setCoverage] = useState(project.coverageThresholdPercent === null ? '' : String(project.coverageThresholdPercent));
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const problem = source === 'written' ? jsonProblem(profile) : null;

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy || problem) return;
    const threshold = coverage.trim() === '' ? undefined : Number(coverage);
    if (threshold !== undefined && !(threshold >= 0 && threshold <= 100)) {
      setError('The coverage threshold must be between 0 and 100.');
      return;
    }

    setBusy(true);
    setError(null);
    try {
      onSaved(
        await api.updateProjectProfile(project.id, {
          preset: source === 'preset' ? preset : undefined,
          profileJson: source === 'written' ? profile : undefined,
          coverageThresholdPercent: threshold,
        }),
      );
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The profile could not be replaced.');
      setBusy(false);
    }
  };

  return (
    <section className="section" aria-label={`Profile of ${project.name}`}>
      <form className="stack" onSubmit={submit}>
        <h2>Profile of {project.name}</h2>
        <p className="small muted">
          Revision {project.profileRevision}. Saving makes a new revision: runs that start afterwards use it, and its baseline
          is measured again straight away. A run already going keeps the profile it started with.
        </p>
        <div className="field">
          <label htmlFor="pe-source">Replace it with</label>
          <select id="pe-source" value={source} onChange={(e) => setSource(e.target.value as ProfileSource)}>
            <option value="written">The profile as edited below</option>
            <option value="preset">A preset</option>
            <option value="threshold">The same profile, with the coverage threshold below</option>
          </select>
        </div>
        {source === 'preset' ? (
          <div className="field">
            <label htmlFor="pe-preset">Preset</label>
            <select id="pe-preset" value={preset} onChange={(e) => setPreset(e.target.value)}>
              {presets.map((p) => (
                <option key={p.name} value={p.name}>
                  {p.displayName}
                </option>
              ))}
            </select>
          </div>
        ) : source === 'written' ? (
          <div className="field">
            <label htmlFor="pe-profile">Profile</label>
            <textarea
              id="pe-profile"
              className="mono"
              rows={16}
              spellCheck={false}
              value={profile}
              onChange={(e) => setProfile(e.target.value)}
              aria-describedby="pe-profile-hint"
            />
            <span className={problem ? 'hint error' : 'hint'} id="pe-profile-hint">
              {problem ?? 'The host checks it, and that every program it names is on its PATH, when you save.'}
            </span>
          </div>
        ) : null}
        <div className="field">
          <label htmlFor="pe-cov">Changed-line coverage threshold (%)</label>
          <input id="pe-cov" type="number" min={0} max={100} value={coverage} onChange={(e) => setCoverage(e.target.value)} />
          <span className="hint">Left empty, the profile's own threshold is kept.</span>
        </div>
        <ErrorNote message={error} />
        <div className="btn-row">
          <button className="btn primary" type="submit" disabled={busy || !!problem}>
            Save profile
          </button>
          <button className="btn" type="button" onClick={onCancel} disabled={busy}>
            Cancel
          </button>
        </div>
      </form>
    </section>
  );
}

/** The profile laid out for editing; text that is not JSON is shown as it is. */
function pretty(json: string): string {
  try {
    return JSON.stringify(JSON.parse(json), null, 2);
  } catch {
    return json;
  }
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
  const presets = settings?.presets ?? [];
  const [name, setName] = useState('');
  const [gitHubUrl, setGitHubUrl] = useState('');
  const [defaultBranch, setDefaultBranch] = useState('main');
  const [preset, setPreset] = useState(presets[0]?.name ?? '');
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
                <option key={p.name} value={p.name}>
                  {p.displayName}
                </option>
              ))}
            </select>
            <span className="hint">The commands the host runs to build and test, and the reports it reads. Admins edit presets in Settings.</span>
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
          The factory reaches GitHub with the token in the host’s own settings. It is never given to a worker. Once registered,
          the project's baseline is measured straight away.
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
