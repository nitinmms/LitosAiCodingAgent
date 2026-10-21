import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { AccessToken, CreatedAccessToken } from '../api/types';
import { ErrorNote } from './bits';

const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : 'Never');

/**
 * Access tokens for the tray app (blueprint §26.3): a small program on someone's PC that rings and
 * shows a notification when one of their tasks starts waiting on them, whether the factory runs
 * on that PC or on a server. Anyone makes tokens for themselves here; a token is shown once, can
 * only read their tasks and the board's stream, and stops working when revoked. An Admin also
 * sees, and can revoke, everyone's.
 */
export function TrayPage({
  api,
  isAdmin,
  onNotice,
  address = `${window.location.origin}${window.location.pathname.replace(/\/$/, '')}`,
}: {
  api: FactoryApi;
  isAdmin: boolean;
  onNotice: (text: string) => void;
  /** What the tray app connects to: where this page was loaded from. */
  address?: string;
}) {
  const [mine, setMine] = useState<AccessToken[]>([]);
  const [everyone, setEveryone] = useState<AccessToken[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    try {
      const [own, all] = await Promise.all([api.accessTokens(), isAdmin ? api.accessTokens(true) : Promise.resolve([])]);
      setMine(own);
      setEveryone(all);
      setError(null);
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The access tokens could not be loaded.');
    } finally {
      setLoaded(true);
    }
  }, [api, isAdmin]);

  useEffect(() => {
    void reload();
  }, [reload]);

  const revoke = async (token: AccessToken) => {
    try {
      await api.revokeAccessToken(token.id);
      onNotice(`“${token.name}” is revoked. A tray app using it stops at once.`);
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The token could not be revoked.');
    }
    await reload();
  };

  return (
    <div className="page stack">
      <section className="section stack" aria-label="Connect the tray app">
        <h2>Tray app</h2>
        <p className="small">
          The tray app sits in the Windows taskbar and rings when one of your tasks starts waiting on you: a decision, a
          handoff to test, a block, a paused budget. It needs no open browser tab. To connect it, give it this address and a
          token from below.
        </p>
        <CopyField id="tray-address" label="Factory address" value={address} />
        <CreateToken api={api} onCreated={() => void reload()} />
      </section>

      <section className="section" aria-label="Your tokens">
        <h2>Your tokens</h2>
        <ErrorNote message={error} />
        <TokenTable tokens={mine} loaded={loaded} empty="You have no tokens yet." onRevoke={revoke} />
        <p className="small muted">
          A token can only read your tasks and their changes; it cannot change anything. Revoke one you no longer use, or
          one that may have been seen by someone else.
        </p>
      </section>

      {isAdmin ? (
        <section className="section" aria-label="Everyone's tokens">
          <h2>Everyone's tokens</h2>
          <TokenTable tokens={everyone} loaded={loaded} empty="Nobody has a token yet." showOwner onRevoke={revoke} />
        </section>
      ) : null}
    </div>
  );
}

function CreateToken({ api, onCreated }: { api: FactoryApi; onCreated: () => void }) {
  const [name, setName] = useState('');
  const [created, setCreated] = useState<CreatedAccessToken | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      setCreated(await api.createAccessToken(name.trim()));
      setName('');
      onCreated();
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The token could not be created.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="stack">
      <form className="btn-row" onSubmit={submit} aria-label="Create a token">
        <div className="field">
          <label htmlFor="tk-name">Token name</label>
          <input
            id="tk-name"
            required
            maxLength={100}
            placeholder="Work laptop"
            value={name}
            onChange={(e) => setName(e.target.value)}
            autoComplete="off"
          />
        </div>
        <button className="btn primary" type="submit" disabled={busy}>
          Create token
        </button>
      </form>
      <ErrorNote message={error} />
      {created ? (
        <div className="stack" role="status" aria-label="New token">
          <p className="small">
            Paste this into the tray app's settings for <b>{created.token.name}</b>. <b>It is shown only now</b>: if it is
            lost, revoke it and create another.
          </p>
          <CopyField id="tk-secret" label="Token" value={created.secret} />
        </div>
      ) : null}
    </div>
  );
}

function CopyField({ id, label, value }: { id: string; label: string; value: string }) {
  const [copied, setCopied] = useState(false);
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
    } catch {
      document.getElementById(id)?.focus();
    }
  };

  return (
    <div className="btn-row">
      <input id={id} className="inline-input mono" aria-label={label} readOnly value={value} onFocus={(e) => e.target.select()} />
      <button className="btn" type="button" onClick={copy}>
        {copied ? 'Copied' : `Copy ${label.toLowerCase()}`}
      </button>
    </div>
  );
}

function TokenTable({
  tokens,
  loaded,
  empty,
  showOwner = false,
  onRevoke,
}: {
  tokens: AccessToken[];
  loaded: boolean;
  empty: string;
  showOwner?: boolean;
  onRevoke: (token: AccessToken) => void;
}) {
  if (!tokens.length) return <p className="muted">{loaded ? empty : 'Loading…'}</p>;

  return (
    <div className="table-wrap">
      <table>
        <thead>
          <tr>
            {showOwner ? <th>Person</th> : null}
            <th>Name</th>
            <th>Created</th>
            <th>Last used</th>
            <th>Status</th>
          </tr>
        </thead>
        <tbody>
          {tokens.map((token) => (
            <tr key={token.id} aria-label={token.name}>
              {showOwner ? <td className="mono small">{token.userName ?? token.userId}</td> : null}
              <td>{token.name}</td>
              <td className="small">{when(token.createdAt)}</td>
              <td className="small">{when(token.lastUsedAt)}</td>
              <td>
                {token.status === 'Active' ? (
                  <>
                    <span className="pill done">Active</span>{' '}
                    <button className="btn small" aria-label={`Revoke ${token.name}`} onClick={() => onRevoke(token)}>
                      Revoke
                    </button>
                  </>
                ) : (
                  <span className="pill neutral">Revoked</span>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
