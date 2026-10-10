import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import type { McpServerSettings, McpSettings } from './api/types';
import { App } from './App';
import { fromServerForm, isServerName, toServerForm } from './components/McpTab';
import { FakeHost } from './test/fakeHost';

function start(host: FakeHost) {
  host.signedIn = true;
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  return userEvent.setup();
}

async function onMcp(host: FakeHost) {
  window.location.hash = '#/settings/mcp';
  const user = start(host);
  await screen.findByRole('form', { name: 'MCP servers' });
  return user;
}

const github: McpServerSettings = {
  name: 'github',
  transport: 'Stdio',
  command: 'npx',
  args: ['-y', '@modelcontextprotocol/server-github'],
  url: null,
  enabled: true,
  permission: 'Full',
  toolOverrides: {},
  secretVariables: ['GITHUB_TOKEN'],
};

const withGitHub = (host: FakeHost) => {
  host.mcp = { revision: 1, settings: { servers: [github] } };
};

const card = (name: string) => within(screen.getByRole('region', { name }));

describe('the MCP servers tab', () => {
  it('starts with no server, and adds one that is saved whole', async () => {
    const host = new FakeHost();
    const user = await onMcp(host);
    expect(screen.getByRole('tab', { name: 'MCP servers' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByText('No server is set up.')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Add a server' }));
    const server = card('New server');
    await user.type(server.getByLabelText('Name'), 'github');
    const named = card('github');
    await user.type(named.getByLabelText('Command'), 'npx');
    await user.type(named.getByLabelText('Arguments'), '-y{Enter}@modelcontextprotocol/server-github');
    await user.type(named.getByLabelText('New variable of github'), 'GITHUB_TOKEN{Enter}');
    expect(screen.getByText('Unsaved changes')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Save servers' }));

    expect(await screen.findByText('The MCP servers are saved.')).toBeInTheDocument();
    const sent = host.sent('PUT', '/api/admin/settings/mcp')[0]!.body as { revision: number; settings: McpSettings };
    expect(sent).toEqual({ revision: 0, settings: { servers: [github] } });
  });

  it('sets a variable’s value at once, under its server’s name, and never shows it again', async () => {
    const host = new FakeHost();
    withGitHub(host);
    const user = await onMcp(host);

    await user.type(card('github').getByLabelText('GITHUB_TOKEN'), 'ghp_secret{Enter}');

    expect(await screen.findByRole('status')).toHaveTextContent('GITHUB_TOKEN of github is set.');
    expect(host.secretValues.get('mcp:github:GITHUB_TOKEN')).toBe('ghp_secret');
    expect(document.body.textContent).not.toContain('ghp_secret');
  });

  it('tests a server as the form describes it, and lets each tool be denied', async () => {
    const host = new FakeHost();
    withGitHub(host);
    host.mcpTestResult = {
      connected: true,
      tools: [
        { name: 'create_issue', description: 'Opens an issue.' },
        { name: 'delete_repository', description: 'Deletes a repository.' },
      ],
      error: null,
    };
    const user = await onMcp(host);
    const server = card('github');

    await user.click(server.getByRole('button', { name: 'Test connection' }));

    expect(await server.findByText('Connected: 2 tools.')).toBeInTheDocument();
    expect(host.sent('POST', '/api/admin/mcp/test')[0]!.body).toEqual(github);
    expect(server.getByLabelText('Access to create_issue')).toHaveValue('Full');
    await user.selectOptions(server.getByLabelText('Access to delete_repository'), 'Deny');
    await user.click(screen.getByRole('button', { name: 'Save servers' }));

    await screen.findByText('The MCP servers are saved.');
    const sent = host.sent('PUT', '/api/admin/settings/mcp')[0]!.body as { settings: McpSettings };
    expect(sent.settings.servers[0]!.toolOverrides).toEqual({ delete_repository: 'Deny' });
  });

  it('says why a server could not be reached', async () => {
    const host = new FakeHost();
    withGitHub(host);
    host.mcpTestResult = { connected: false, tools: [], error: 'Timed out connecting within 30s.' };
    const user = await onMcp(host);

    await user.click(card('github').getByRole('button', { name: 'Test connection' }));

    expect(await card('github').findByRole('alert')).toHaveTextContent('It could not be reached: Timed out connecting within 30s.');
  });

  it('asks for a URL for a server that runs elsewhere, and sends no command for it', async () => {
    const host = new FakeHost();
    const user = await onMcp(host);

    await user.click(screen.getByRole('button', { name: 'Add a server' }));
    await user.type(card('New server').getByLabelText('Name'), 'docs');
    await user.selectOptions(card('docs').getByLabelText('Connection'), 'Http');
    expect(card('docs').queryByLabelText('Command')).toBeNull();
    await user.type(card('docs').getByLabelText('URL'), 'https://mcp.example.com/mcp');
    await user.click(screen.getByRole('button', { name: 'Save servers' }));

    await screen.findByText('The MCP servers are saved.');
    const sent = host.sent('PUT', '/api/admin/settings/mcp')[0]!.body as { settings: McpSettings };
    expect(sent.settings.servers[0]).toMatchObject({ name: 'docs', transport: 'Http', command: null, args: [], url: 'https://mcp.example.com/mcp' });
  });

  it('removes a server, and its secrets go with it once saved', async () => {
    const host = new FakeHost();
    withGitHub(host);
    host.secrets.push({ name: 'mcp:github:GITHUB_TOKEN', setAt: '2026-10-10T09:00:00Z', setBy: null });
    const user = await onMcp(host);

    await user.click(card('github').getByRole('button', { name: 'Remove github' }));
    await user.click(screen.getByRole('button', { name: 'Save servers' }));

    await screen.findByText('The MCP servers are saved.');
    expect(host.secrets.some((s) => s.name.startsWith('mcp:'))).toBe(false);
    expect(screen.getByText('No server is set up.')).toBeInTheDocument();
  });

  it("shows the host's reasons for refusing a change", async () => {
    const host = new FakeHost();
    const user = await onMcp(host);

    await user.click(screen.getByRole('button', { name: 'Add a server' }));
    await user.type(card('New server').getByLabelText('Name'), 'git__hub');
    await user.click(screen.getByRole('button', { name: 'Save servers' }));

    expect(await screen.findByText(/is not a usable server name/)).toBeInTheDocument();
    // An unusable name cannot be tested either.
    expect(card('git__hub').getByRole('button', { name: 'Test connection' })).toBeDisabled();
  });
});

describe('the MCP server form', () => {
  it('reads back what it was given', () => {
    expect(fromServerForm(toServerForm(github))).toEqual(github);
  });

  it('reads arguments one per line, leaving out blank ones', () => {
    const form = { ...toServerForm(github), argsText: '  -y \n\n@modelcontextprotocol/server-github\n' };
    expect(fromServerForm(form).args).toEqual(['-y', '@modelcontextprotocol/server-github']);
  });

  it('knows a usable server name', () => {
    expect(isServerName('github')).toBe(true);
    expect(isServerName('my.server-2')).toBe(true);
    expect(isServerName('git__hub')).toBe(false);
    expect(isServerName('-x')).toBe(false);
    expect(isServerName('')).toBe(false);
  });
});

describe('what a run started with', () => {
  it('shows its tool settings and how each MCP server connected', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const thread = host.addThread(project, { state: 'Running', stage: 'Implement' });
    host.threads.get(thread.id)!.run = {
      id: 'r1',
      kind: 'Implement',
      status: 'Running',
      stopReason: null,
      baselineCommit: null,
      headCommit: null,
      promptRevision: 'm1.1',
      capabilities: {
        ptc: true,
        shellTimeoutSeconds: 600,
        webSearch: 'WorkTurns',
        mcpServers: [github, { ...github, name: 'docs' }],
        mcpStatus: [
          { name: 'github', connected: true, tools: ['create_issue', 'search_code'], error: null },
          { name: 'docs', connected: false, tools: [], error: 'Timed out connecting within 30s.' },
        ],
      },
    };
    window.location.hash = `#/threads/${thread.id}`;
    start(host);

    const box = within(await screen.findByRole('region', { name: 'Run' }));

    expect(box.getByText('PTC on; shell limit 10 min; web search while it changes code')).toBeInTheDocument();
    expect(box.getByText('(2 tools)')).toBeInTheDocument();
    expect(box.getByText('(not available)')).toHaveAttribute('title', 'Timed out connecting within 30s.');
  });
});
