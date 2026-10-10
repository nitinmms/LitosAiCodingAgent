import { useState, type FormEvent, type KeyboardEvent } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { KnownProvider, ProviderCatalog, ProviderEntry, ProviderSettings, SecretStatus, SettingsSection } from '../api/types';
import { fmt } from '../domain/format';
import { ErrorNote, SaveBar } from './bits';
import { CatalogPicker } from './CatalogPicker';

/** The secret the factory-wide GitHub token is kept under (Core/Settings/SecretNames.cs). */
export const GITHUB_SECRET = 'github';

/** One provider as the form holds it: a context length is typed, so it is text until saved. */
interface EntryForm {
  name: string;
  enabled: boolean;
  baseUrl: string;
  models: { id: string; contextLength: string }[];
  defaultModel: string | null;
  allowEveryModel: boolean;
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
        allowEveryModel: entry?.allowEveryModel ?? false,
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
      allowEveryModel: entry.allowEveryModel,
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
 * the revision it was read at. Each provider's model catalog is fetched when its key is set, when
 * it is first enabled, and on Refresh.
 */
export function ProvidersTab({
  api,
  section,
  known,
  secrets,
  catalogs,
  onSaved,
  onSecretChanged,
  onRefreshCatalog,
  onReload,
}: {
  api: FactoryApi;
  section: SettingsSection<ProviderSettings>;
  known: KnownProvider[];
  secrets: SecretStatus[];
  catalogs: ProviderCatalog[];
  onSaved: (saved: SettingsSection<ProviderSettings>) => void;
  onSecretChanged: (text: string) => Promise<void>;
  onRefreshCatalog: (provider: string) => Promise<void>;
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
          catalog={catalogs.find((c) => c.name === kind.name) ?? null}
          onChange={(change) => update(kind.name, change)}
          onSecretChanged={onSecretChanged}
          onRefreshCatalog={() => onRefreshCatalog(kind.name)}
        />
      ))}

      <GitHubToken api={api} secret={secretOf(GITHUB_SECRET)} onSecretChanged={onSecretChanged} />

      <ErrorNote message={error} />
      <p className="small muted">
        A change applies to threads created afterwards: a thread keeps the provider and model it was created with. Keys apply from
        the next model call.
      </p>
      <SaveBar changed={changed}>
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
      </SaveBar>
    </form>
  );
}

function ProviderCard({
  api,
  kind,
  entry,
  secret,
  catalog,
  onChange,
  onSecretChanged,
  onRefreshCatalog,
}: {
  api: FactoryApi;
  kind: KnownProvider;
  entry: EntryForm;
  secret: SecretStatus | null;
  catalog: ProviderCatalog | null;
  onChange: (change: (entry: EntryForm) => EntryForm) => void;
  onSecretChanged: (text: string) => Promise<void>;
  onRefreshCatalog: () => Promise<void>;
}) {
  const [modelId, setModelId] = useState('');
  const [contextLength, setContextLength] = useState('');
  const [lookingUp, setLookingUp] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [picking, setPicking] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const id = (part: string) => `pv-${kind.name}-${part}`;
  const retired = new Set(catalog?.retired ?? []);

  const refresh = async () => {
    if (refreshing) return;
    setRefreshing(true);
    try {
      await onRefreshCatalog();
    } finally {
      setRefreshing(false);
    }
  };

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

  /** Adds models to the allowed list; the first allowed becomes the default until another is chosen. */
  const allow = (models: { id: string; contextLength: string }[]) =>
    onChange((e) => {
      const added = models.filter((m) => !e.models.some((x) => x.id === m.id));
      return { ...e, models: [...e.models, ...added], defaultModel: e.defaultModel ?? added[0]?.id ?? null };
    });

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
    allow([{ id: model, contextLength: contextLength.replace(/[\s,_]/g, '') }]);
    setModelId('');
    setContextLength('');
    setProblem(null);
  };

  const openPicker = () => {
    setPicking(true);
    // Nothing to pick from yet: fetch the list first.
    if (!catalog?.attemptedAt) void refresh();
  };

  return (
    <section className="section" aria-label={kind.displayName}>
      <div className="section-head">
        <h2>{kind.displayName}</h2>
        <span className={`pill ${kind.precision === 'Strict' ? 'done' : 'you'}`}>{kind.precision}</span>
        <label className="check">
          <input
            type="checkbox"
            checked={entry.enabled}
            onChange={(e) => {
              const on = e.target.checked;
              onChange((x) => ({ ...x, enabled: on }));
              // Turned on for the first time: fetch its list, so its models can be picked.
              if (on && !catalog?.attemptedAt && (secret || kind.usesBaseUrl)) void refresh();
            }}
          />
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
          <span className="hint">The server's OpenAI-compatible endpoint, as the host reaches it. Save it before fetching the server's models.</span>
        </div>
      ) : null}

      <SecretField
        api={api}
        name={kind.keySecret}
        label={kind.usesBaseUrl ? 'Key (optional)' : 'Key'}
        what={`The ${kind.displayName} key`}
        secret={secret}
        onSecretChanged={onSecretChanged}
        // A new key may see different models: fetch the list again.
        onSet={() => void refresh()}
      />

      <CatalogStatus catalog={catalog} refreshing={refreshing} onRefresh={() => void refresh()} />

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
                    <td>
                      <span className="mono">{m.id}</span>
                      {retired.has(m.id) ? (
                        <span
                          className="pill bad retired"
                          title="The provider no longer lists it. Members cannot choose it; tasks already on it carry on."
                        >
                          not offered by provider
                        </span>
                      ) : null}
                    </td>
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

        {picking ? (
          catalog?.models.length ? (
            <CatalogPicker
              providerName={kind.displayName}
              models={catalog.models}
              allowed={entry.models.map((m) => m.id)}
              onAllow={(models) => allow(models.map((m) => ({ id: m.id, contextLength: String(m.contextLength) })))}
              onClose={() => setPicking(false)}
            />
          ) : (
            <div className="btn-row">
              <p className="small muted">{refreshing ? 'Fetching its models…' : 'There is no list to choose from yet: refresh it above.'}</p>
              <button className="btn ghost small" type="button" onClick={() => setPicking(false)}>
                Close
              </button>
            </div>
          )
        ) : (
          <div className="btn-row">
            <button className="btn" type="button" onClick={openPicker}>
              Choose from catalog
            </button>
          </div>
        )}

        <details className="add-by-id">
          <summary className="small">Or add a model by its id</summary>
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
        </details>
        <p className="small muted">Allowed models are kept when you save the providers, below.</p>

        <label className="check">
          <input
            type="checkbox"
            checked={entry.allowEveryModel}
            onChange={(e) => onChange((x) => ({ ...x, allowEveryModel: e.target.checked }))}
          />
          <span>Allow every model in its catalog that takes tools</span>
        </label>
        {entry.allowEveryModel ? (
          <p className="small error" role="note">
            Anyone offered {kind.displayName} can then start a task on any of its models, whatever it costs. The allowed models
            above still come first, and the default stays the default.
          </p>
        ) : null}
      </div>
    </section>
  );
}

/** How many models the provider listed and when, or why the list could not be fetched; and Refresh. */
function CatalogStatus({ catalog, refreshing, onRefresh }: { catalog: ProviderCatalog | null; refreshing: boolean; onRefresh: () => void }) {
  return (
    <div className="catalog-status">
      <span className="small muted">
        {refreshing
          ? 'Fetching its models…'
          : catalog?.fetchedAt
            ? `Catalog: ${fmt(catalog.models.length)} models, fetched ${when(catalog.fetchedAt)}.`
            : 'Catalog: not fetched yet.'}
      </span>
      <button className="btn ghost small" type="button" disabled={refreshing} onClick={onRefresh}>
        Refresh
      </button>
      {!refreshing && catalog?.error ? (
        <p className="small error" role="alert">
          The last fetch failed{catalog.attemptedAt ? ` (${when(catalog.attemptedAt)})` : ''}: {catalog.error}
        </p>
      ) : null}
    </div>
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
export function SecretField({
  api,
  name,
  label,
  what,
  secret,
  onSecretChanged,
  onSet,
}: {
  api: FactoryApi;
  name: string;
  label: string;
  /** "The OpenRouter key": how notices name it. */
  what: string;
  secret: SecretStatus | null;
  onSecretChanged: (text: string) => Promise<void>;
  /** After it is set or replaced, not cleared. */
  onSet?: () => void;
}) {
  const [value, setValue] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const inputId = `secret-${name.replace(/[^a-z0-9]/gi, '-')}`;
  const set = () => {
    if (!busy && value.trim()) void change(() => api.setSecret(name, value.trim()), `${what} is ${secret ? 'replaced' : 'set'}.`, onSet);
  };

  const change = async (action: () => Promise<void>, done: string, after?: () => void) => {
    setBusy(true);
    setError(null);
    try {
      await action();
      setValue('');
      await onSecretChanged(done);
      after?.();
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
