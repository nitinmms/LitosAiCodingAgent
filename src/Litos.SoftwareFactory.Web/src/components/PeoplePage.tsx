import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { AccountRole, CreatedInvitation, Invitation, Person, Project } from '../api/types';
import { ErrorNote } from './bits';

const STATUS_TONE: Record<Invitation['status'], string> = { Pending: 'you', Accepted: 'done', Revoked: 'neutral', Expired: 'neutral' };

const day = (iso: string) => new Date(iso).toLocaleDateString();

/** The full address of an invitation's link, from the `#/invite/<token>` the host returns. */
export const invitationUrl = (link: string, location: Pick<Location, 'origin' | 'pathname'> = window.location) =>
  `${location.origin}${location.pathname}${link}`;

/**
 * The Admin's people screen (m2-architecture.md §4): invite someone with a one-time link, see the
 * invitations and revoke one still waiting, and for each person change their role, disable or
 * re-enable them, and choose the projects they belong to.
 */
export function PeoplePage({
  api,
  projects,
  currentUserId,
  onNotice,
}: {
  api: FactoryApi;
  projects: Project[];
  currentUserId: string;
  onNotice: (text: string) => void;
}) {
  const [people, setPeople] = useState<Person[]>([]);
  const [invitations, setInvitations] = useState<Invitation[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loaded, setLoaded] = useState(false);

  const reload = useCallback(async () => {
    const [p, i] = await Promise.all([api.people(), api.invitations()]);
    setPeople(p);
    setInvitations(i);
    setLoaded(true);
  }, [api]);

  useEffect(() => {
    reload().catch((failure: unknown) => setError(failure instanceof ApiError ? failure.message : 'People could not be loaded.'));
  }, [reload]);

  /** Runs one change, then shows the host's own reason if it was refused, and the new state. */
  const act = async (change: () => Promise<unknown>, done: string) => {
    setError(null);
    try {
      await change();
      onNotice(done);
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'That change could not be made.');
    }
    await reload().catch(() => {});
  };

  const projectName = (id: string) => projects.find((p) => p.id === id)?.name ?? 'A removed project';

  return (
    <div className="page">
      <div className="grid2">
        <InviteSomeone api={api} projects={projects} onCreated={() => reload().catch(() => {})} />
        <section className="section" aria-label="Invitations">
          <h2>Invitations</h2>
          {invitations.length ? (
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>For</th>
                    <th>Role</th>
                    <th>Projects</th>
                    <th>Status</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {invitations.map((i) => (
                    <tr key={i.id}>
                      <td className="mono">{i.userName}</td>
                      <td>{i.role}</td>
                      <td className="small">{i.projectIds.map(projectName).join(', ') || '—'}</td>
                      <td>
                        <span className={`pill ${STATUS_TONE[i.status]}`}>{i.status}</span>
                        {i.status === 'Pending' ? <span className="small muted"> until {day(i.expiresAt)}</span> : null}
                      </td>
                      <td>
                        {i.status === 'Pending' ? (
                          <button
                            className="btn small"
                            onClick={() => act(() => api.revokeInvitation(i.id), `The invitation for ${i.userName} is revoked.`)}
                          >
                            Revoke
                          </button>
                        ) : null}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            <p className="muted">{loaded ? 'No invitation has been sent.' : 'Loading…'}</p>
          )}
        </section>
      </div>

      <section className="section" aria-label="People">
        <h2>People</h2>
        <ErrorNote message={error} />
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Person</th>
                <th>Role</th>
                <th>Projects</th>
                <th>Access</th>
              </tr>
            </thead>
            <tbody>
              {people.map((person) => (
                <PersonRow
                  key={person.id}
                  person={person}
                  isYou={person.id === currentUserId}
                  projects={projects}
                  projectName={projectName}
                  onRole={(role) => act(() => api.setRole(person.id, role), `${person.userName} is now ${role === 'Admin' ? 'an admin' : 'a member'}.`)}
                  onDisable={() => act(() => api.disablePerson(person.id), `${person.userName} is disabled. Their open sessions end within a minute.`)}
                  onEnable={() => act(() => api.enablePerson(person.id), `${person.userName} can sign in again.`)}
                  onJoin={(projectId) => act(() => api.addMember(projectId, person.id), `${person.userName} joined ${projectName(projectId)}.`)}
                  onLeave={(projectId) => act(() => api.removeMember(projectId, person.id), `${person.userName} left ${projectName(projectId)}.`)}
                />
              ))}
            </tbody>
          </table>
        </div>
        <p className="small muted">
          People are disabled, never deleted, so every task keeps who did what. Admins see every project, whatever their
          project list says.
        </p>
      </section>
    </div>
  );
}

function PersonRow({
  person,
  isYou,
  projects,
  projectName,
  onRole,
  onDisable,
  onEnable,
  onJoin,
  onLeave,
}: {
  person: Person;
  isYou: boolean;
  projects: Project[];
  projectName: (id: string) => string;
  onRole: (role: AccountRole) => void;
  onDisable: () => void;
  onEnable: () => void;
  onJoin: (projectId: string) => void;
  onLeave: (projectId: string) => void;
}) {
  const name = person.displayName || person.userName;
  const others = projects.filter((p) => !person.projectIds.includes(p.id));

  return (
    <tr aria-label={name}>
      <td>
        {name}
        {isYou ? <span className="muted"> (you)</span> : null}
        <div className="small muted mono">{person.userName}</div>
      </td>
      <td>
        <select aria-label={`Role of ${name}`} value={person.role} onChange={(e) => onRole(e.target.value as AccountRole)}>
          <option value="Member">Member</option>
          <option value="Admin">Admin</option>
        </select>
      </td>
      <td>
        <div className="btn-row">
          {person.projectIds.map((id) => (
            <span key={id} className="tag">
              {projectName(id)}{' '}
              <button className="btn ghost small" aria-label={`Remove ${name} from ${projectName(id)}`} onClick={() => onLeave(id)}>
                ×
              </button>
            </span>
          ))}
          {others.length ? (
            <select
              aria-label={`Add ${name} to a project`}
              value=""
              onChange={(e) => {
                if (e.target.value) onJoin(e.target.value);
              }}
            >
              <option value="">Add to…</option>
              {others.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
          ) : null}
        </div>
      </td>
      <td>
        {person.disabled ? (
          <>
            <span className="pill bad">Disabled</span>{' '}
            <button className="btn small" onClick={onEnable}>
              Enable
            </button>
          </>
        ) : (
          <>
            <span className="pill done">Active</span>{' '}
            {isYou ? null : (
              <button className="btn small" onClick={onDisable}>
                Disable
              </button>
            )}
          </>
        )}
      </td>
    </tr>
  );
}

function InviteSomeone({ api, projects, onCreated }: { api: FactoryApi; projects: Project[]; onCreated: () => void }) {
  const [userName, setUserName] = useState('');
  const [role, setRole] = useState<AccountRole>('Member');
  const [chosen, setChosen] = useState<string[]>([]);
  const [created, setCreated] = useState<CreatedInvitation | null>(null);
  const [copied, setCopied] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      const result = await api.createInvitation({ userName: userName.trim(), role, projectIds: chosen });
      setCreated(result);
      setCopied(false);
      setUserName('');
      setChosen([]);
      onCreated();
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The invitation could not be created.');
    } finally {
      setBusy(false);
    }
  };

  const url = created ? invitationUrl(created.link) : '';
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(url);
      setCopied(true);
    } catch {
      document.getElementById('invite-link')?.focus();
    }
  };

  return (
    <section className="section" aria-label="Invite someone">
      <form className="stack" onSubmit={submit}>
        <h2>Invite someone</h2>
        <div className="form-grid">
          <div className="field">
            <label htmlFor="iv-user">Username</label>
            <input id="iv-user" required value={userName} onChange={(e) => setUserName(e.target.value)} autoComplete="off" />
            <span className="hint">What they will sign in with.</span>
          </div>
          <div className="field">
            <label htmlFor="iv-role">Role</label>
            <select id="iv-role" value={role} onChange={(e) => setRole(e.target.value as AccountRole)}>
              <option value="Member">Member</option>
              <option value="Admin">Admin</option>
            </select>
          </div>
        </div>
        {projects.length ? (
          <fieldset className="checks">
            <legend className="small">Projects they join</legend>
            {projects.map((p) => (
              <label key={p.id} className="check">
                <input
                  type="checkbox"
                  checked={chosen.includes(p.id)}
                  onChange={(e) => setChosen((c) => (e.target.checked ? [...c, p.id] : c.filter((id) => id !== p.id)))}
                />
                <span>{p.name}</span>
              </label>
            ))}
          </fieldset>
        ) : null}
        <ErrorNote message={error} />
        <div className="btn-row">
          <button className="btn primary" type="submit" disabled={busy}>
            Create invitation link
          </button>
        </div>
      </form>

      {created ? (
        <div className="stack" role="status" aria-label="New invitation">
          <p className="small">
            Send this link to <span className="mono">{created.invitation.userName}</span> by any channel. It works once, until{' '}
            {day(created.invitation.expiresAt)}. <b>It is shown only now</b>: if it is lost, revoke the invitation and create another.
          </p>
          <div className="btn-row">
            <input
              id="invite-link"
              className="inline-input mono"
              aria-label="Invitation link"
              readOnly
              value={url}
              onFocus={(e) => e.target.select()}
            />
            <button className="btn" type="button" onClick={copy}>
              {copied ? 'Copied' : 'Copy link'}
            </button>
          </div>
        </div>
      ) : null}
    </section>
  );
}
