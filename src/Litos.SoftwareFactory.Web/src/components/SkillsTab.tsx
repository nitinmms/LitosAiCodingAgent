import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { FoundRepositorySkill, LibrarySkill, RepositorySkillPolicy, SettingsSection, SkillSettings } from '../api/types';
import { ErrorNote, SaveBar } from './bits';

/** A usable skill name, as Core/Settings/SkillSettings.cs checks it. */
export const isSkillName = (name: string) => /^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$/.test(name);

/** One library skill as the form holds it. */
export interface SkillForm extends LibrarySkill {
  /** The form's own id, stable while the name is edited. */
  key: string;
}

let nextKey = 0;
const newKey = () => `skill-${++nextKey}`;

export const toSkillForm = (s: LibrarySkill): SkillForm => ({ ...s, key: newKey() });

/** The skill the form describes; the host checks it. */
export const fromSkillForm = ({ key: _key, ...s }: SkillForm): LibrarySkill => ({ ...s, name: s.name.trim(), description: s.description.trim() });

const comparable = (policy: RepositorySkillPolicy, library: SkillForm[]) =>
  JSON.stringify({ repositoryPolicy: policy, library: library.map(fromSkillForm) });

const POLICIES: Record<RepositorySkillPolicy, string> = {
  ApproveEach: 'Once an Admin approves each one, and each change to it',
  Always: 'Always, as found',
  Never: 'Never',
};

/** Where one repository skill stands, for the version a run would meet now. */
export type FoundStatus = 'Approved' | 'Waiting' | 'Changed';

/**
 * The rows the approvals list shows: each skill's latest version, and any earlier version that is
 * still approved, so its approval can be taken back. An earlier version nobody approved needs
 * nothing from anyone, and is left out.
 */
export function approvalRows(found: FoundRepositorySkill[]): { skill: FoundRepositorySkill; status: FoundStatus | 'Earlier' }[] {
  const approvedNames = new Set(found.filter((s) => s.approvedAt).map((s) => `${s.projectId}/${s.name}`));
  return found
    .filter((s) => s.latest || s.approvedAt)
    .map((skill) => ({
      skill,
      status: !skill.latest
        ? ('Earlier' as const)
        : skill.approvedAt
          ? ('Approved' as const)
          : approvedNames.has(`${skill.projectId}/${skill.name}`)
            ? ('Changed' as const)
            : ('Waiting' as const),
    }))
    .sort(
      (a, b) =>
        a.skill.project.localeCompare(b.skill.project) ||
        a.skill.name.localeCompare(b.skill.name) ||
        Number(b.skill.latest) - Number(a.skill.latest),
    );
}

/**
 * The Skills tab (blueprint §8.2, m3-architecture.md §7.3): the factory's own skill library, and
 * what runs do with the skills a repository keeps in its .litos/skills. Library skills and the
 * policy are saved together and apply to runs that start afterwards; an approval applies at once,
 * to the version of the skill that was read, so a later change waits for its own.
 */
export function SkillsTab({
  api,
  section,
  onSaved,
  onNotice,
  onReload,
}: {
  api: FactoryApi;
  section: SettingsSection<SkillSettings>;
  onSaved: (saved: SettingsSection<SkillSettings>) => void;
  onNotice: (text: string) => void;
  onReload: () => Promise<void>;
}) {
  const [policy, setPolicy] = useState(section.settings.repositoryPolicy);
  const [library, setLibrary] = useState(() => section.settings.library.map(toSkillForm));
  const [error, setError] = useState<string | null>(null);
  const [stale, setStale] = useState(false);
  const [busy, setBusy] = useState(false);

  const changed = comparable(policy, library) !== JSON.stringify(section.settings);
  const update = (key: string, change: Partial<LibrarySkill>) => setLibrary((all) => all.map((s) => (s.key === key ? { ...s, ...change } : s)));

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      onSaved(await api.saveSkills(section.revision, { repositoryPolicy: policy, library: library.map(fromSkillForm) }));
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The skills could not be saved.');
      setStale(failure instanceof ApiError && failure.status === 409);
      setBusy(false);
    }
  };

  return (
    <div className="stack">
      <form className="stack" onSubmit={submit} aria-label="Skills" autoComplete="off">
        <section className="section">
          <h2>Skill library</h2>
          <p className="small muted">
            Every project's runs get these skills. The agent sees each one's name and description, and reads its instructions
            when it decides it needs them. Skills on the computer that runs the factory are never used.
          </p>
          {library.length === 0 ? <p className="small muted">The library is empty.</p> : null}
          <div className="btn-row">
            <button
              className="btn"
              type="button"
              onClick={() => setLibrary((all) => [...all, { key: newKey(), name: '', description: '', body: '', enabled: true }])}
            >
              Add a skill
            </button>
          </div>
        </section>

        {library.map((skill) => (
          <SkillCard
            key={skill.key}
            skill={skill}
            onChange={(change) => update(skill.key, change)}
            onRemove={() => setLibrary((all) => all.filter((s) => s.key !== skill.key))}
          />
        ))}

        <section className="section">
          <h2>Repository skills</h2>
          <div className="field">
            <label htmlFor="skills-policy">Runs use the skills a repository keeps in .litos/skills</label>
            <select id="skills-policy" value={policy} onChange={(e) => setPolicy(e.target.value as RepositorySkillPolicy)}>
              {(Object.keys(POLICIES) as RepositorySkillPolicy[]).map((p) => (
                <option key={p} value={p}>
                  {POLICIES[p]}
                </option>
              ))}
            </select>
            <span className="hint">
              A repository's skills are written by whoever can push to it, and the agent follows them. A skill skipped by a run
              is named in its thread. A repository skill with a library skill's name is used in its place.
            </span>
          </div>
        </section>

        <ErrorNote message={error} />
        <SaveBar changed={changed}>
          {stale ? (
            <button className="btn primary" type="button" onClick={() => void onReload()}>
              Load their change
            </button>
          ) : (
            <button className="btn primary" type="submit" disabled={busy || !changed}>
              Save skills
            </button>
          )}
          <button
            className="btn"
            type="button"
            disabled={busy || !changed}
            onClick={() => {
              setPolicy(section.settings.repositoryPolicy);
              setLibrary(section.settings.library.map(toSkillForm));
              setError(null);
              setStale(false);
            }}
          >
            Discard changes
          </button>
        </SaveBar>
      </form>

      <Approvals api={api} policy={section.settings.repositoryPolicy} onNotice={onNotice} />
    </div>
  );
}

function SkillCard({
  skill,
  onChange,
  onRemove,
}: {
  skill: SkillForm;
  onChange: (change: Partial<LibrarySkill>) => void;
  onRemove: () => void;
}) {
  const label = skill.name.trim() || 'New skill';
  const id = (part: string) => `skill-${skill.key}-${part}`;
  const name = skill.name.trim();
  return (
    <section className="section" aria-label={label}>
      <div className="section-head">
        <h2>{label}</h2>
        <label className="check">
          <input type="checkbox" checked={skill.enabled} onChange={(e) => onChange({ enabled: e.target.checked })} />
          <span>Given to runs</span>
        </label>
        <button className="btn ghost small" type="button" onClick={onRemove} aria-label={`Remove ${label}`}>
          Remove
        </button>
      </div>
      <div className="form-grid">
        <div className="field">
          <label htmlFor={id('name')}>Name</label>
          <input id={id('name')} className="mono" value={skill.name} onChange={(e) => onChange({ name: e.target.value })} placeholder="release-notes" />
          <span className={name && !isSkillName(name) ? 'hint error' : 'hint'}>
            Letters, digits, '.', '-' and '_', up to 64. The agent asks for it by this name.
          </span>
        </div>
        <div className="field">
          <label htmlFor={id('description')}>Description</label>
          <input
            id={id('description')}
            value={skill.description}
            onChange={(e) => onChange({ description: e.target.value })}
            placeholder="Use when writing release notes for a change."
          />
          <span className="hint">One line saying when to use it: until the agent loads the skill, this is all it sees.</span>
        </div>
      </div>
      <div className="field">
        <label htmlFor={id('body')}>Instructions</label>
        <textarea id={id('body')} className="mono" rows={8} value={skill.body} onChange={(e) => onChange({ body: e.target.value })} />
        <span className="hint">Markdown, as a SKILL.md's body.</span>
      </div>
    </section>
  );
}

const STATUS_PILL: Record<FoundStatus | 'Earlier', [string, string]> = {
  Approved: ['done', 'Approved'],
  Waiting: ['you', 'Waiting for approval'],
  Changed: ['you', 'Changed since it was approved'],
  Earlier: ['neutral', 'An earlier version, approved'],
};

/** The skills found in projects' repositories, and their approvals, which apply at once. */
function Approvals({ api, policy, onNotice }: { api: FactoryApi; policy: RepositorySkillPolicy; onNotice: (text: string) => void }) {
  const [found, setFound] = useState<FoundRepositorySkill[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setFound(await api.repositorySkills());
      setError(null);
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The repository skills could not be loaded.');
    }
  }, [api]);

  useEffect(() => {
    void load();
  }, [load]);

  const decide = async (skill: FoundRepositorySkill, approved: boolean) => {
    setBusy(skill.hash);
    try {
      await api.setRepositorySkillApproval(skill.projectId, skill.name, skill.hash, approved);
      onNotice(approved ? `${skill.name} is approved for ${skill.project}.` : `${skill.name} is no longer approved for ${skill.project}.`);
      await load();
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The approval could not be changed.');
    } finally {
      setBusy(null);
    }
  };

  const rows = found ? approvalRows(found) : [];
  return (
    <section className="section" aria-label="Found in repositories">
      <h2>Found in repositories</h2>
      <p className="small muted">
        {policy === 'ApproveEach'
          ? 'A skill is listed here when a run first finds it. Runs skip it until it is approved, and approving it approves this version only.'
          : policy === 'Always'
            ? 'Runs use every repository skill as found, so approvals are kept but not needed while the policy is "Always".'
            : 'Runs use no repository skill while the policy is "Never"; approvals are kept for when it changes.'}
      </p>
      <ErrorNote message={error} />
      {!found ? (
        error ? null : (
          <p className="note">Loading…</p>
        )
      ) : rows.length === 0 ? (
        <p className="small muted">No repository has had a skill yet.</p>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Skill</th>
                <th>Project</th>
                <th>Status</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {rows.map(({ skill, status }) => (
                <tr key={`${skill.projectId}/${skill.name}/${skill.hash}`}>
                  <td>
                    <span className="mono">{skill.name}</span>
                    <div className="small muted">{skill.description}</div>
                    <details>
                      <summary className="small">Read {skill.folder}/SKILL.md</summary>
                      <pre className="diff">{skill.content}</pre>
                      {skill.otherFiles.length ? (
                        <p className="small muted">
                          Its folder also has {skill.otherFiles.join(', ')}; read those in the repository before approving.
                        </p>
                      ) : null}
                    </details>
                  </td>
                  <td>{skill.project}</td>
                  <td>
                    <span className={`pill ${STATUS_PILL[status][0]}`}>{STATUS_PILL[status][1]}</span>
                  </td>
                  <td>
                    {skill.approvedAt ? (
                      <button
                        className="btn small"
                        type="button"
                        disabled={busy !== null}
                        onClick={() => void decide(skill, false)}
                        aria-label={`Take back approval of ${skill.name} for ${skill.project}`}
                      >
                        Take back
                      </button>
                    ) : (
                      <button
                        className="btn small primary"
                        type="button"
                        disabled={busy !== null}
                        onClick={() => void decide(skill, true)}
                        aria-label={`Approve ${skill.name} for ${skill.project}`}
                      >
                        Approve
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
