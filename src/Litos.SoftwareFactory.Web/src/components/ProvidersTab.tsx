import { useState, type FormEvent, type KeyboardEvent } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { KnownProvider, ProviderEntry, ProviderSettings, SecretStatus, SettingsSection } from '../api/types';
import { fmt } from '../domain/format';
import { ErrorNote } from './bits';

/** The secret the factory-wide GitHub token is kept under (Core/Settings/SecretNames.cs). */
export const GITHUB_SECRET = 'github';

/** One provider as the form holds it: a context length is typed, so it is text until saved. */
interface EntryForm {
  name: string;
  enabled: boolean;
  baseUrl: string;
  models: { id: string; contextLength: string }[];
  defaultModel: string | null;
}

interface ProvidersForm {
  entries: EntryForm[];
  defaultProvider: string | null;
  strictOnly: boolean;
}

/** Every known provider gets a card, whether the settings list it yet or not. */
export function toProvidersForm(settings: ProviderSettings, known: KnownProvider[]): ProvidersForm {
  return {
    entries: known.map((k) => {
      const entry = settings.providers.find((p) => p.name === k.name);
      return {
        name: k.name,
        enabled: entry?.enabled ?? false,
        baseUrl: entry?.baseUrl ?? '',
        models: (entry?.models ?? []).map((m) => ({ id: m.id, contextLength: String(m.contextLength) })),
        defaultModel: entry?.defaultModel ?? null,
      };
    }),
    defaultProvider: settings.defaultProvider,
    strictOnly: settings.strictOnly,
  };
}

/**
 * The settings the form describes, or why they cannot be read. A provider that is off, with no
 * models and no address, is left out, so the stored section lists only what was set up.
 */
export function fromProvidersForm(form: ProvidersForm, known: KnownProvider[]): { settings: ProviderSettings } | { error: string } {
  const problems: string[] = [];
  const providers: ProviderEntry[] = [];
  for (const entry of form.entries) {
    const kind = known.find((k) => k.name === entry.name);
    const baseUrl = entry.baseUrl.trim();
    if (!entry.enabled && entry.models.length === 0 && !baseUrl) continue;

    const models = entry.models.map((m) => {
      const cleaned = m.contextLength.replace(/[\s,_]/g, '');
      if (!/^\d+$/.test(cleaned)) problems.push(`${kind?.displayName ?? entry.name}: ${m.id}'s context length must be a whole number.`);
      return { id: m.id, contextLength: Number(cleaned) };
    });
    providers.push({
      name: entry.name,
      enabled: entry.enabled,
      baseUrl: kind?.usesBaseUrl && baseUrl ? baseUrl : null,
      models,
      defaultModel: entry.defaultModel,
    });
  }

  return problems.length
    ? { error: problems.join(' ') }
    : { settings: { providers, defaultProvider: form.defaultProvider, strictOnly: form.strictOnly } };
}

const when = (iso: string) => new Date(iso).toLocaleString();

/** Enter in a field that has its own button does that button's job, rather than saving the whole form. */
function onEnter(event: KeyboardEvent<HTMLInputElement>, action: () => void) {
  if (event.key !== 'Enter') return;
  event.preventDefault();
  action();
}

/**
 * The Providers tab (blueprint §8.4, m3-architecture.md §4): which providers are on, their keys,
 * the models members may choose and the context window of each, the defaults, and the GitHub
 * token. Keys are set and cleared at once, apart from the form; the rest is saved together with
 * the revision it was read at.
 */
export function ProvidersTab({
  api,
  section,
  known,
  secrets,
  onSaved,
  onSecretChanged,
  onReload,
}: {
  api: FactoryApi;
  section: SettingsSection<ProviderSettings>;
  known: KnownProvider[];
  secrets: SecretStatus[];
  onSaved: (saved: SettingsSection<ProviderSettings>) => void;
  onSecretChanged: (text: string) => Promise<void>;
  onReload: () => Promise<void>;
}) {
  const [form, setForm] = useState(() => toProvidersForm(section.settings, known));
  const [error, setError] = useState<string | null>(null);
  const [stale, setStale] = useState(false);
  const [busy, setBusy] = useState(false);

  const original = toProvidersForm(section.settings, known);
  const changed = JSON.stringify(form) !== JSON.stringify(original);
  const secretOf = (name: string) => secrets.find((s) => s.name === name) ?? null;

  const update = (name: string, change: (entry: EntryForm) => EntryForm) =>
    setForm((f) => ({ ...f, entries: f.entries.map((e) => (e.name === name ? change(e) : e)) }));

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy) return;
    const parsed = fromProvidersForm(form, known);
    if ('error' in parsed) {
      setError(parsed.error);
      return;
    }

    setBusy(true);
    setError(null);
    try {
      onSaved(await api.saveProviders(section.revision, parsed.settings));
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The providers could not be saved.');
      setStale(failure instanceof ApiError && failure.status === 409);
      setBusy(false);
    }
  };

  const enabled = form.entries.filter((e) => e.enabled);

  return (
    <form className="stack" onSubmit={submit} aria-label="Providers" autoComplete="off">
      <section className="section">
        <h2>Defaults</h2>
        <div className="form-grid">
          <div className="field">
            <label htmlFor="pv-default">Default provider</label>
            <select
              id="pv-default"
              value={form.defaultProvider ?? ''}
              onChange={(e) => setForm((f) => ({ ...f, defaultProvider: e.target.value || null }))}
            >
              <option value="">None</option>
              {enabled.map((e) => (
                <option key={e.name} value={e.name}>
                  {known.find((k) => k.name === e.name)?.displayName ?? e.name}
                </option>
              ))}
            </select>
            <span className="hint">What a new thread runs on when its creator chooses nothing. Only an enabled provider.</span>
          </div>
        </div>
        <label className="check">
          <input type="checkbox" checked={form.strictOnly} onChange={(e) => setForm((f) => ({ ...f, strictOnly: e.target.checked }))} />
          <span>
            Offer members only providers whose budgets are strict. Admins still see every enabled provider.
          </span>
        </label>
      </section>

      {known.map((kind) => (
        <ProviderCard
          key={kind.name}
          api={api}
          kind={kind}
          entry={form.entries.find((e) => e.name === kind.name)!}
          secret={secretOf(kind.keySecret)}
          onChange={(change) => update(kind.name, change)}
          onSecretChanged={onSecretChanged}
        />
      ))}

      <GitHubToken api={api} secret={secretOf(GITHUB_SECRET)} onSecretChanged={onSecretChanged} />

      <ErrorNote message={error} />
      <div className="btn-row">
        {stale ? (
          <button className="btn primary" type="button" onClick={() => void onReload()}>
            Load their change
          </button>
        ) : (
          <button className="btn primary" type="submit" disabled={busy || !changed}>
            Save providers
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
      </div>
      <p className="small muted">
        A change applies to threads created afterwards: a thread keeps the provider and model it was created with. Keys apply from
        the next model call.
      </p>
    </form>
  );
}

function ProviderCard({
  api,
  kind,
  entry,
  secret,
  onChange,
  onSecretChanged,
}: {
  api: FactoryApi;
  kind: KnownProvider;
  entry: EntryForm;
  secret: SecretStatus | null;
  onChange: (change: (entry: EntryForm) => EntryForm) => void;
  onSecretChanged: (text: string) => Promise<void>;
}) {
  const [modelId, setModelId] = useState('');
  const [contextLength, setContextLength] = useState('');
  const [lookingUp, setLookingUp] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const id = (part: string) => `pv-${kind.name}-${part}`;

  const lookUp = async () => {
    const model = modelId.trim();
    if (!model || lookingUp) return;
    setLookingUp(true);
    setProblem(null);
    try {
      setContextLength(String(await api.contextLength(model)));
    } catch (failure) {
      setProblem(failure instanceof ApiError ? failure.message : 'The context length could not be looked up.');
    } finally {
      setLookingUp(false);
    }
  };

  const add = () => {
    const model = modelId.trim();
    if (!model) return;
    if (entry.models.some((m) => m.id === model)) {
      setProblem(`${model} is already allowed.`);
      return;
    }
    if (!/^\d+$/.test(contextLength.replace(/[\s,_]/g, ''))) {
      setProblem('Give its context length, or look it up.');
      return;
    }
    onChange((e) => ({
      ...e,
      models: [...e.models, { id: model, contextLength: contextLength.replace(/[\s,_]/g, '') }],
      // The first model allowed is the default until another is chosen.
      defaultModel: e.defaultModel ?? model,
    }));
    setModelId('');
    setContextLength('');
    setProblem(null);
  };

  return (
    <section className="section" aria-label={kind.displayName}>
      <div className="section-head">
        <h2>{kind.displayName}</h2>
        <span className={`pill ${kind.precision === 'Strict' ? 'done' : 'you'}`}>{kind.precision}</span>
        <label className="check">
          <input type="checkbox" checked={entry.enabled} onChange={(e) => onChange((x) => ({ ...x, enabled: e.target.checked }))} />
          <span>Enabled</span>
        </label>
      </div>
      {kind.precision === 'Estimated' ? (
        <p className="small muted">
          Its usage can come back missing or approximate, so the factory charges its own estimate: a budget may be overrun.
        </p>
      ) : null}

      {kind.usesBaseUrl ? (
        <div className="field">
          <label htmlFor={id('url')}>Address</label>
          <input
            id={id('url')}
            className="mono"
            placeholder="http://localhost:1234/v1"
            value={entry.baseUrl}
            onChange={(e) => onChange((x) => ({ ...x, baseUrl: e.target.value }))}
          />
          <span className="hint">The server's OpenAI-compatible endpoint, as the host reaches it.</span>
        </div>
      ) : null}

      <SecretField
        api={api}
        name={kind.keySecret}
        label={kind.usesBaseUrl ? 'Key (optional)' : 'Key'}
        what={`The ${kind.displayName} key`}
        secret={secret}
        onSecretChanged={onSecretChanged}
      />

      <div className="stack">
        <h3>Allowed models</h3>
        {entry.models.length ? (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Model</th>
                  <th>Context length</th>
                  <th>Default</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {entry.models.map((m) => (
                  <tr key={m.id}>
                    <td className="mono">{m.id}</td>
                    <td>
                      <input
                        className="inline-input"
                        inputMode="numeric"
                        aria-label={`Context length of ${m.id}`}
                        value={m.contextLength}
                        onChange={(e) =>
                          onChange((x) => ({ ...x, models: x.models.map((y) => (y.id === m.id ? { ...y, contextLength: e.target.value } : y)) }))
                        }
                      />
                    </td>
                    <td>
                      <input
                        type="radio"
                        name={id('default')}
                        aria-label={`${m.id} is the default`}
                        checked={entry.defaultModel === m.id}
                        onChange={() => onChange((x) => ({ ...x, defaultModel: m.id }))}
                      />
                    </td>
                    <td>
                      <button
                        className="btn ghost small"
                        type="button"
                        aria-label={`Remove ${m.id}`}
                        onClick={() =>
                          onChange((x) => {
                            const models = x.models.filter((y) => y.id !== m.id);
                            return { ...x, models, defaultModel: x.defaultModel === m.id ? (models[0]?.id ?? null) : x.defaultModel };
                          })
                        }
                      >
                        Remove
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <p className="small muted">No model is allowed yet.</p>
        )}
        <div className="form-grid">
          <div className="field">
            <label htmlFor={id('model')}>Model id</label>
            <input
              id={id('model')}
              className="mono"
              value={modelId}
              onChange={(e) => setModelId(e.target.value)}
              onKeyDown={(e) => onEnter(e, add)}
              placeholder={kind.name === 'openrouter' ? 'anthropic/claude-sonnet-5' : ''}
            />
          </div>
          <div className="field">
            <label htmlFor={id('context')}>Its context length</label>
            <div className="with-unit">
              <input
                id={id('context')}
                inputMode="numeric"
                value={contextLength}
                onChange={(e) => setContextLength(e.target.value)}
                onKeyDown={(e) => onEnter(e, add)}
              />
              <button className="btn small" type="button" onClick={() => void lookUp()} disabled={!modelId.trim() || lookingUp}>
                Look up
              </button>
            </div>
            <span className="hint">
              {contextLength && Number.isFinite(Number(contextLength)) ? `${fmt(Number(contextLength))} tokens. ` : ''}
              Workers compact against it.
            </span>
          </div>
        </div>
        <ErrorNote message={problem} />
        <div className="btn-row">
          <button className="btn" type="button" onClick={add} disabled={!modelId.trim()}>
            Allow model
          </button>
        </div>
      </div>
    </section>
  );
}

function GitHubToken({
  api,
  secret,
  onSecretChanged,
}: {
  api: FactoryApi;
  secret: SecretStatus | null;
  onSecretChanged: (text: string) => Promise<void>;
}) {
  return (
    <section className="section" aria-label="GitHub">
      <h2>GitHub</h2>
      <p className="small muted">
        One token for every project: it clones, pushes task branches and opens draft pull requests. Without one, branches cannot be
        pushed to private repositories and no pull request is opened.
      </p>
      <SecretField api={api} name={GITHUB_SECRET} label="Token" what="The GitHub token" secret={secret} onSecretChanged={onSecretChanged} />
    </section>
  );
}

/** A secret is set or replaced, and cleared, at once; its value is never shown again. */
function SecretField({
  api,
  name,
  label,
  what,
  secret,
  onSecretChanged,
}: {
  api: FactoryApi;
  name: string;
  label: string;
  /** "The OpenRouter key": how notices name it. */
  what: string;
  secret: SecretStatus | null;
  onSecretChanged: (text: string) => Promise<void>;
}) {
  const [value, setValue] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const inputId = `secret-${name.replace(/[^a-z0-9]/gi, '-')}`;
  const set = () => {
    if (!busy && value.trim()) void change(() => api.setSecret(name, value.trim()), `${what} is ${secret ? 'replaced' : 'set'}.`);
  };

  const change = async (action: () => Promise<void>, done: string) => {
    setBusy(true);
    setError(null);
    try {
      await action();
      setValue('');
      await onSecretChanged(done);
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'That could not be changed.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="field">
      <label htmlFor={inputId}>{label}</label>
      <div className="with-unit">
        <input
          id={inputId}
          type="password"
          autoComplete="new-password"
          placeholder={secret ? 'Set. Type a new one to replace it.' : 'Not set'}
          value={value}
          onChange={(e) => setValue(e.target.value)}
          onKeyDown={(e) => onEnter(e, set)}
        />
        <button className="btn small" type="button" disabled={busy || !value.trim()} onClick={set}>
          {secret ? 'Replace' : 'Set'}
        </button>
        {secret ? (
          <button className="btn small" type="button" disabled={busy} onClick={() => void change(() => api.clearSecret(name), `${what} is cleared.`)}>
            Clear
          </button>
        ) : null}
      </div>
      <span className="hint">{secret ? `Set ${when(secret.setAt)}${secret.setBy ? '' : ' from the host’s environment'}. It is never shown again.` : 'Kept encrypted; never shown again once set.'}</span>
      <ErrorNote message={error} />
    </div>
  );
}
