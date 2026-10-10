import { useState, type FormEvent } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { McpAccess, McpServerSettings, McpSettings, McpTestResult, SecretStatus, SettingsSection } from '../api/types';
import { ErrorNote, SaveBar } from './bits';
import { SecretField } from './ProvidersTab';

/** The secret one of a server's environment variables is kept under (Core/Settings/SecretNames.cs). */
export const mcpSecret = (server: string, variable: string) => `mcp:${server}:${variable}`;

/** A usable server name, as Core/Settings/McpSettings.cs checks it. */
export const isServerName = (name: string) => /^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$/.test(name) && !name.includes('__');

/** One server as the form holds it: arguments are typed one per line. */
export interface ServerForm {
  /** The form's own id, stable while the name is edited. */
  key: string;
  name: string;
  transport: 'Stdio' | 'Http';
  command: string;
  argsText: string;
  url: string;
  enabled: boolean;
  permission: McpAccess;
  toolOverrides: Record<string, McpAccess>;
  secretVariables: string[];
}

let nextKey = 0;
const newKey = () => `server-${++nextKey}`;

export const toServerForm = (s: McpServerSettings): ServerForm => ({
  key: newKey(),
  name: s.name,
  transport: s.transport,
  command: s.command ?? '',
  argsText: s.args.join('\n'),
  url: s.url ?? '',
  enabled: s.enabled,
  permission: s.permission,
  toolOverrides: { ...s.toolOverrides },
  secretVariables: [...s.secretVariables],
});

/** The server the form describes; the host checks it. */
export const fromServerForm = (f: ServerForm): McpServerSettings => ({
  name: f.name.trim(),
  transport: f.transport,
  command: f.transport === 'Stdio' && f.command.trim() ? f.command.trim() : null,
  args: f.transport === 'Stdio' ? f.argsText.split('\n').map((a) => a.trim()).filter(Boolean) : [],
  url: f.transport === 'Http' && f.url.trim() ? f.url.trim() : null,
  enabled: f.enabled,
  permission: f.permission,
  toolOverrides: f.toolOverrides,
  secretVariables: f.secretVariables,
});

const comparable = (servers: ServerForm[]) => JSON.stringify(servers.map(fromServerForm));

/**
 * The MCP servers tab (blueprint §8.1, m3-architecture.md §7.1). Each server is a command the
 * worker starts, or a URL; its permission is Full or Deny, with exceptions per tool; its
 * variables' values are secrets, set at once and never shown again. Test connection starts it
 * once, as the form describes it, and lists its tools. Servers apply to runs that start
 * afterwards, and only implement, repair and rework turns get their tools.
 */
export function McpTab({
  api,
  section,
  secrets,
  onSaved,
  onSecretChanged,
  onReload,
}: {
  api: FactoryApi;
  section: SettingsSection<McpSettings>;
  secrets: SecretStatus[];
  onSaved: (saved: SettingsSection<McpSettings>) => void;
  onSecretChanged: (text: string) => Promise<void>;
  onReload: () => Promise<void>;
}) {
  const [servers, setServers] = useState(() => section.settings.servers.map(toServerForm));
  const [error, setError] = useState<string | null>(null);
  const [stale, setStale] = useState(false);
  const [busy, setBusy] = useState(false);

  const changed = comparable(servers) !== JSON.stringify(section.settings.servers);
  const update = (key: string, change: (s: ServerForm) => ServerForm) => setServers((all) => all.map((s) => (s.key === key ? change(s) : s)));

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      onSaved(await api.saveMcp(section.revision, { servers: servers.map(fromServerForm) }));
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The MCP servers could not be saved.');
      setStale(failure instanceof ApiError && failure.status === 409);
      setBusy(false);
    }
  };

  const add = () =>
    setServers((all) => [
      ...all,
      {
        key: newKey(),
        name: '',
        transport: 'Stdio',
        command: '',
        argsText: '',
        url: '',
        enabled: true,
        permission: 'Full',
        toolOverrides: {},
        secretVariables: [],
      },
    ]);

  return (
    <form className="stack" onSubmit={submit} aria-label="MCP servers" autoComplete="off">
      <section className="section">
        <h2>MCP servers</h2>
        <p className="small muted">
          Every project's runs get these servers. Each run starts them afresh, and its implement, repair and rework turns get
          their tools; turns that only read never do. A run's servers, with their secrets, are in a file in its folder while it
          runs, which the agent's shell could read: give them only to people you trust.
        </p>
        {servers.length === 0 ? <p className="small muted">No server is set up.</p> : null}
        <div className="btn-row">
          <button className="btn" type="button" onClick={add}>
            Add a server
          </button>
        </div>
      </section>

      {servers.map((server) => (
        <ServerCard
          key={server.key}
          api={api}
          server={server}
          saved={section.settings.servers.find((s) => s.name === server.name) ?? null}
          secrets={secrets}
          onChange={(change) => update(server.key, change)}
          onRemove={() => setServers((all) => all.filter((s) => s.key !== server.key))}
          onSecretChanged={onSecretChanged}
        />
      ))}

      <ErrorNote message={error} />
      <SaveBar changed={changed}>
        {stale ? (
          <button className="btn primary" type="button" onClick={() => void onReload()}>
            Load their change
          </button>
        ) : (
          <button className="btn primary" type="submit" disabled={busy || !changed}>
            Save servers
          </button>
        )}
        <button
          className="btn"
          type="button"
          disabled={busy || !changed}
          onClick={() => {
            setServers(section.settings.servers.map(toServerForm));
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

function ServerCard({
  api,
  server,
  saved,
  secrets,
  onChange,
  onRemove,
  onSecretChanged,
}: {
  api: FactoryApi;
  server: ServerForm;
  /** The server as saved under this name; null for a new or renamed one. */
  saved: McpServerSettings | null;
  secrets: SecretStatus[];
  onChange: (change: (s: ServerForm) => ServerForm) => void;
  onRemove: () => void;
  onSecretChanged: (text: string) => Promise<void>;
}) {
  const [variable, setVariable] = useState('');
  const [testing, setTesting] = useState(false);
  const [tested, setTested] = useState<McpTestResult | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const label = server.name.trim() || 'New server';
  const id = (part: string) => `mcp-${server.key}-${part}`;
  const named = isServerName(server.name.trim());

  const addVariable = () => {
    const name = variable.trim();
    if (!name) return;
    if (!/^[A-Za-z_][A-Za-z0-9_]{0,99}$/.test(name)) {
      setProblem(`"${name}" is not an environment variable name.`);
      return;
    }
    if (server.secretVariables.includes(name)) {
      setProblem(`${name} is listed already.`);
      return;
    }
    onChange((s) => ({ ...s, secretVariables: [...s.secretVariables, name] }));
    setVariable('');
    setProblem(null);
  };

  const test = async () => {
    setTesting(true);
    setProblem(null);
    setTested(null);
    try {
      setTested(await api.testMcp(fromServerForm(server)));
    } catch (failure) {
      setProblem(failure instanceof ApiError ? failure.message : 'The server could not be tested.');
    } finally {
      setTesting(false);
    }
  };

  /** A tool's access: an exception is kept only where it differs from the server's own. */
  const setAccess = (tool: string, access: McpAccess) =>
    onChange((s) => {
      const toolOverrides = { ...s.toolOverrides };
      if (access === s.permission) delete toolOverrides[tool];
      else toolOverrides[tool] = access;
      return { ...s, toolOverrides };
    });

  const exceptions = Object.entries(server.toolOverrides);

  return (
    <section className="section" aria-label={label}>
      <div className="section-head">
        <h2>{label}</h2>
        <label className="check">
          <input type="checkbox" checked={server.enabled} onChange={(e) => onChange((s) => ({ ...s, enabled: e.target.checked }))} />
          <span>Given to runs</span>
        </label>
        <button className="btn ghost small" type="button" onClick={onRemove} aria-label={`Remove ${label}`}>
          Remove
        </button>
      </div>

      <div className="form-grid">
        <div className="field">
          <label htmlFor={id('name')}>Name</label>
          <input id={id('name')} className="mono" value={server.name} onChange={(e) => onChange((s) => ({ ...s, name: e.target.value }))} placeholder="github" />
          <span className="hint">Its tools are named mcp__{server.name.trim() || 'name'}__tool. Renaming it clears its secrets when saved.</span>
        </div>
        <div className="field">
          <label htmlFor={id('transport')}>Connection</label>
          <select
            id={id('transport')}
            value={server.transport}
            onChange={(e) => onChange((s) => ({ ...s, transport: e.target.value as ServerForm['transport'] }))}
          >
            <option value="Stdio">A command the worker starts</option>
            <option value="Http">A server at a URL</option>
          </select>
        </div>
        <div className="field">
          <label htmlFor={id('permission')}>Permission</label>
          <select
            id={id('permission')}
            value={server.permission}
            onChange={(e) => onChange((s) => ({ ...s, permission: e.target.value as McpAccess }))}
          >
            <option value="Full">Full: runs may use its tools</option>
            <option value="Deny">Deny: runs may use none, except those allowed below</option>
          </select>
        </div>
      </div>

      {server.transport === 'Stdio' ? (
        <div className="form-grid">
          <div className="field">
            <label htmlFor={id('command')}>Command</label>
            <input id={id('command')} className="mono" value={server.command} onChange={(e) => onChange((s) => ({ ...s, command: e.target.value }))} placeholder="npx" />
            <span className="hint">Found on the worker's PATH, the toolchain allowlist's.</span>
          </div>
          <div className="field">
            <label htmlFor={id('args')}>Arguments</label>
            <textarea
              id={id('args')}
              className="mono"
              rows={3}
              value={server.argsText}
              onChange={(e) => onChange((s) => ({ ...s, argsText: e.target.value }))}
              placeholder={'-y\n@modelcontextprotocol/server-github'}
            />
            <span className="hint">One per line.</span>
          </div>
        </div>
      ) : (
        <div className="field">
          <label htmlFor={id('url')}>URL</label>
          <input id={id('url')} className="mono" value={server.url} onChange={(e) => onChange((s) => ({ ...s, url: e.target.value }))} placeholder="https://mcp.example.com/mcp" />
        </div>
      )}

      <div className="stack">
        <h3>Secret variables</h3>
        {server.secretVariables.length === 0 ? <p className="small muted">None. A server that needs a token reads it from one.</p> : null}
        {server.secretVariables.map((name) => (
          <div key={name} className="mcp-variable">
            {named ? (
              <SecretField
                api={api}
                name={mcpSecret(server.name.trim(), name)}
                label={name}
                what={`${name} of ${server.name.trim()}`}
                secret={secrets.find((s) => s.name === mcpSecret(server.name.trim(), name)) ?? null}
                onSecretChanged={onSecretChanged}
              />
            ) : (
              <p className="small mono">{name}</p>
            )}
            <button
              className="btn ghost small"
              type="button"
              aria-label={`Remove ${name}`}
              onClick={() => onChange((s) => ({ ...s, secretVariables: s.secretVariables.filter((v) => v !== name) }))}
            >
              Remove
            </button>
          </div>
        ))}
        {!named && server.secretVariables.length ? <p className="small muted">Name the server to set its variables.</p> : null}
        <div className="with-unit">
          <input
            aria-label={`New variable of ${label}`}
            className="mono"
            placeholder="GITHUB_TOKEN"
            value={variable}
            onChange={(e) => setVariable(e.target.value)}
            onKeyDown={(e) => {
              if (e.key !== 'Enter') return;
              e.preventDefault();
              addVariable();
            }}
          />
          <button className="btn small" type="button" onClick={addVariable} disabled={!variable.trim()}>
            Add variable
          </button>
        </div>
        <span className="hint">A variable is kept when the servers are saved; removing it clears its value.</span>
      </div>

      <div className="stack">
        <div className="btn-row">
          <button className="btn" type="button" onClick={() => void test()} disabled={testing || !named}>
            {testing ? 'Testing…' : 'Test connection'}
          </button>
        </div>
        <ErrorNote message={problem} />
        {tested ? (
          tested.connected ? (
            <div className="stack">
              <p className="small" role="status">
                Connected: {tested.tools.length === 1 ? '1 tool' : `${tested.tools.length} tools`}.
              </p>
              {tested.tools.length ? (
                <div className="table-wrap">
                  <table>
                    <thead>
                      <tr>
                        <th>Tool</th>
                        <th>Runs may</th>
                      </tr>
                    </thead>
                    <tbody>
                      {tested.tools.map((t) => (
                        <tr key={t.name}>
                          <td>
                            <span className="mono">{t.name}</span>
                            {t.description ? <div className="small muted">{t.description}</div> : null}
                          </td>
                          <td>
                            <select
                              aria-label={`Access to ${t.name}`}
                              value={server.toolOverrides[t.name] ?? server.permission}
                              onChange={(e) => setAccess(t.name, e.target.value as McpAccess)}
                            >
                              <option value="Full">Use it</option>
                              <option value="Deny">Not use it</option>
                            </select>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              ) : null}
            </div>
          ) : (
            <p className="small error" role="alert">
              It could not be reached: {tested.error}
            </p>
          )
        ) : null}
        {exceptions.length && !tested ? (
          <p className="small muted">
            Exceptions:{' '}
            {exceptions.map(([tool, access], i) => (
              <span key={tool}>
                {i ? ', ' : ''}
                <span className="mono">{tool}</span> {access === 'Full' ? 'allowed' : 'denied'}
              </span>
            ))}
            . Test the connection to change them.
          </p>
        ) : null}
        {saved === null && server.name.trim() ? <p className="small muted">Not saved yet.</p> : null}
      </div>
    </section>
  );
}
