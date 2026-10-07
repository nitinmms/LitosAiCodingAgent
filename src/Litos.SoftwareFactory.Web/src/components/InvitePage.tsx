import { useEffect, useState, type FormEvent } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { CurrentUser, InvitationPreview } from '../api/types';
import { ErrorNote, Mark } from './bits';

const MIN_PASSWORD = 12;

/**
 * Where an invitation's one-time link lands (m2-architecture.md §4). The invitee is not signed in:
 * the page says who the link is for, they choose a password, and they are signed in with the
 * invitation's role and projects. A link that no longer works says why.
 */
export function InvitePage({ api, token, onSignedIn }: { api: FactoryApi; token: string; onSignedIn: (user: CurrentUser) => void }) {
  const [preview, setPreview] = useState<InvitationPreview | null>(null);
  const [unusable, setUnusable] = useState<string | null>(null);
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [repeat, setRepeat] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let stopped = false;
    api
      .lookupInvitation(token)
      .then((found) => !stopped && setPreview(found))
      .catch((failure: unknown) => {
        if (!stopped) setUnusable(failure instanceof ApiError ? failure.message : 'This invitation could not be checked. Try the link again.');
      });
    return () => {
      stopped = true;
    };
  }, [api, token]);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy) return;
    if (password.length < MIN_PASSWORD) {
      setError(`Use at least ${MIN_PASSWORD} characters.`);
      return;
    }
    if (password !== repeat) {
      setError('The two passwords are different.');
      return;
    }

    setBusy(true);
    setError(null);
    try {
      onSignedIn(await api.acceptInvitation(token, password, displayName.trim() || undefined));
    } catch (failure) {
      if (failure instanceof ApiError && failure.status === 410) setUnusable(failure.message);
      else setError(failure instanceof ApiError ? failure.message : 'The invitation could not be accepted.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="login">
      <section className="login-art">
        <div className="brand">
          <Mark /> Litos Software Factory
        </div>
        <div className="login-pitch">
          <h1>You are invited to the factory.</h1>
          <p>
            Describe a change in a thread and hand it to the factory. It works on a branch of your GitHub repository, builds,
            tests and reviews its own work, and hands the result back for you to test.
          </p>
        </div>
      </section>
      <section className="login-form">
        {unusable ? (
          <div className="stack">
            <h2>This link does not work</h2>
            <p className="error" role="alert">
              {unusable}
            </p>
            <a className="btn" href="#/threads">
              Go to sign-in
            </a>
          </div>
        ) : !preview ? (
          <p className="note">Checking your invitation…</p>
        ) : (
          <form onSubmit={submit}>
            <h2>Create your account</h2>
            <p className="small muted">
              You will sign in as <span className="mono">{preview.userName}</span>
              {preview.role === 'Admin' ? ', as an admin' : ''}. This link works once, until {new Date(preview.expiresAt).toLocaleDateString()}.
            </p>
            <div className="field">
              <label htmlFor="inv-name">Your name</label>
              <input id="inv-name" value={displayName} onChange={(e) => setDisplayName(e.target.value)} autoComplete="name" />
              <span className="hint">Shown beside what you do. Left empty, your username is used.</span>
            </div>
            <div className="field">
              <label htmlFor="inv-pass">Choose a password</label>
              <input
                id="inv-pass"
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                autoComplete="new-password"
                required
              />
              <span className="hint">At least {MIN_PASSWORD} characters.</span>
            </div>
            <div className="field">
              <label htmlFor="inv-repeat">Repeat the password</label>
              <input
                id="inv-repeat"
                type="password"
                value={repeat}
                onChange={(e) => setRepeat(e.target.value)}
                autoComplete="new-password"
                required
              />
            </div>
            <ErrorNote message={error} />
            <button className="btn primary" type="submit" disabled={busy}>
              {busy ? 'Creating your account…' : 'Create account and sign in'}
            </button>
          </form>
        )}
      </section>
    </div>
  );
}
