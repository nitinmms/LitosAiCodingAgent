import { useState, type FormEvent } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { CurrentUser } from '../api/types';
import { STAGES } from '../domain/task';
import { ErrorNote, Mark } from './bits';

export function Login({ api, onSignedIn }: { api: FactoryApi; onSignedIn: (user: CurrentUser) => void }) {
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      onSignedIn(await api.login(userName.trim(), password));
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'Sign-in failed.');
      setPassword('');
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
          <h1>Delegate a change. Check the evidence. You decide.</h1>
          <p>
            Describe a change in a thread and hand it to the factory. It works on a branch of your GitHub repository,
            builds, tests and reviews its own work, and hands the result back for you to test.
          </p>
        </div>
        <div className="login-line" aria-hidden="true">
          {STAGES.map((stage) => (
            <span key={stage} className={stage === 'Handoff' ? 'on' : ''}>
              {stage}
            </span>
          ))}
        </div>
      </section>
      <section className="login-form">
        <form onSubmit={submit}>
          <h2>Sign in</h2>
          <div className="field">
            <label htmlFor="login-user">Username</label>
            <input
              id="login-user"
              value={userName}
              onChange={(e) => setUserName(e.target.value)}
              autoComplete="username"
              autoFocus
              required
            />
          </div>
          <div className="field">
            <label htmlFor="login-pass">Password</label>
            <input
              id="login-pass"
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoComplete="current-password"
              required
            />
            <span className="hint">Accounts are created by your factory admin.</span>
          </div>
          <ErrorNote message={error} />
          <button className="btn primary" type="submit" disabled={busy}>
            {busy ? 'Signing in…' : 'Sign in'}
          </button>
        </form>
      </section>
    </div>
  );
}
