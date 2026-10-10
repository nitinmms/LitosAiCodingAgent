import { useState, type FormEvent } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { SecretStatus, SettingsSection, ToolSettings } from '../api/types';
import { ErrorNote, SaveBar } from './bits';
import { SecretField } from './ProvidersTab';

/** The secret the web search key is kept under (Core/Settings/SecretNames.cs). */
export const WEB_SEARCH_SECRET = 'websearch:tavily';

/** The form as typed: the shell limit is text until saved. */
interface ToolsForm extends Omit<ToolSettings, 'shellTimeoutSeconds'> {
  shellTimeoutSeconds: string;
}

export const toToolsForm = (t: ToolSettings): ToolsForm => ({ ...t, shellTimeoutSeconds: String(t.shellTimeoutSeconds) });

/** The settings the form describes, or why the shell limit cannot be read; the host checks its range. */
export function fromToolsForm(form: ToolsForm): { settings: ToolSettings } | { error: string } {
  const seconds = form.shellTimeoutSeconds.replace(/[\s,_]/g, '');
  if (!/^\d+$/.test(seconds)) return { error: 'The shell command time limit must be a whole number of seconds.' };
  return {
    settings: {
      ...form,
      shellTimeoutSeconds: Number(seconds),
      // Read-only turns can search only while web search is on at all.
      webSearchOnReadOnlyTurns: form.webSearchEnabled && form.webSearchOnReadOnlyTurns,
    },
  };
}

/**
 * The Tools tab (blueprint §8.3, m3-architecture.md §6): Programmatic Tool Calling for new
 * threads, the shell's time limit, and web search with its key. PTC is chosen per thread when it
 * is created; the shell limit and web search apply to runs that start afterwards, and turning
 * web search off, or clearing its key, stops a run that is already searching.
 */
export function ToolsTab({
  api,
  section,
  secrets,
  onSaved,
  onSecretChanged,
  onReload,
}: {
  api: FactoryApi;
  section: SettingsSection<ToolSettings>;
  secrets: SecretStatus[];
  onSaved: (saved: SettingsSection<ToolSettings>) => void;
  onSecretChanged: (text: string) => Promise<void>;
  onReload: () => Promise<void>;
}) {
  const [form, setForm] = useState(() => toToolsForm(section.settings));
  const [error, setError] = useState<string | null>(null);
  const [stale, setStale] = useState(false);
  const [busy, setBusy] = useState(false);

  const original = toToolsForm(section.settings);
  const changed = JSON.stringify(form) !== JSON.stringify(original);
  const keySecret = secrets.find((s) => s.name === WEB_SEARCH_SECRET) ?? null;
  const set = <K extends keyof ToolsForm>(key: K, value: ToolsForm[K]) => setForm((f) => ({ ...f, [key]: value }));

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy) return;
    const parsed = fromToolsForm(form);
    if ('error' in parsed) {
      setError(parsed.error);
      return;
    }

    setBusy(true);
    setError(null);
    try {
      onSaved(await api.saveTools(section.revision, parsed.settings));
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The tools could not be saved.');
      setStale(failure instanceof ApiError && failure.status === 409);
      setBusy(false);
    }
  };

  const seconds = Number(form.shellTimeoutSeconds.replace(/[\s,_]/g, ''));

  return (
    <form className="stack" onSubmit={submit} aria-label="Tools" autoComplete="off">
      <section className="section">
        <h2>Programmatic Tool Calling</h2>
        <p className="small muted">
          The agent writes short scripts that call its tools, rather than calling them one at a time: fewer model calls, and
          fewer tokens, on most tasks. A thread keeps the choice it was created with.
        </p>
        <label className="check">
          <input type="checkbox" checked={form.ptcByDefault} onChange={(e) => set('ptcByDefault', e.target.checked)} />
          <span>New threads start with it on</span>
        </label>
        <label className="check">
          <input type="checkbox" checked={form.membersMayChoosePtc} onChange={(e) => set('membersMayChoosePtc', e.target.checked)} />
          <span>Members may choose otherwise when they create a thread. Admins always may.</span>
        </label>
      </section>

      <section className="section">
        <h2>Shell</h2>
        <div className="form-grid">
          <div className="field">
            <label htmlFor="tl-shell">Command time limit</label>
            <div className="with-unit">
              <input
                id="tl-shell"
                inputMode="numeric"
                value={form.shellTimeoutSeconds}
                onChange={(e) => set('shellTimeoutSeconds', e.target.value)}
              />
              <span className="small muted">seconds</span>
            </div>
            <span className="hint">
              {Number.isFinite(seconds) && seconds >= 60 ? `${Math.round((seconds / 60) * 10) / 10} minutes. ` : ''}A command
              still running after this is killed, and the agent is told. From 30 to 3,600.
            </span>
          </div>
        </div>
      </section>

      <section className="section" aria-label="Web search">
        <h2>Web search</h2>
        <p className="small muted">
          The agent can search the web through this host, which holds the key: no worker ever sees it. Every query, and the
          addresses it found, is written to the run's log.
        </p>
        <label className="check">
          <input type="checkbox" checked={form.webSearchEnabled} onChange={(e) => set('webSearchEnabled', e.target.checked)} />
          <span>Let the agent search the web while it implements and repairs</span>
        </label>
        <label className="check">
          <input
            type="checkbox"
            checked={form.webSearchEnabled && form.webSearchOnReadOnlyTurns}
            disabled={!form.webSearchEnabled}
            onChange={(e) => set('webSearchOnReadOnlyTurns', e.target.checked)}
          />
          <span>Also while it only reads: reviews, specifications, decision scans and chat</span>
        </label>
        <SecretField
          api={api}
          name={WEB_SEARCH_SECRET}
          label="Tavily key"
          what="The web search key"
          secret={keySecret}
          onSecretChanged={onSecretChanged}
        />
        {form.webSearchEnabled && !keySecret ? (
          <p className="small error" role="note">
            Web search is on but has no key, so the agent cannot search until one is set.
          </p>
        ) : null}
      </section>

      <ErrorNote message={error} />
      <p className="small muted">
        The shell limit and web search apply to runs that start afterwards; turning web search off, or clearing its key, also
        stops searches in runs already under way.
      </p>
      <SaveBar changed={changed}>
        {stale ? (
          <button className="btn primary" type="button" onClick={() => void onReload()}>
            Load their change
          </button>
        ) : (
          <button className="btn primary" type="submit" disabled={busy || !changed}>
            Save tools
          </button>
        )}
        <button
          className="btn"
          type="button"
          disabled={busy || !changed}
          onClick={() => {
            setForm(original);
            setError(null);
            setStale(false);
          }}
        >
          Discard changes
        </button>
      </SaveBar>
    </form>
  );
}
