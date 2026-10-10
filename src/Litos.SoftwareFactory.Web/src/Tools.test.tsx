import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import type { ToolSettings } from './api/types';
import { App } from './App';
import { fromToolsForm, toToolsForm } from './components/ToolsTab';
import { FakeHost } from './test/fakeHost';

function start(host: FakeHost) {
  host.signedIn = true;
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  return userEvent.setup();
}

async function onTools(host: FakeHost) {
  window.location.hash = '#/settings/tools';
  const user = start(host);
  await screen.findByRole('form', { name: 'Tools' });
  return user;
}

async function openNewThread(host: FakeHost) {
  window.location.hash = '#/threads';
  const user = start(host);
  await user.click(await screen.findByRole('button', { name: 'New thread' }));
  return { user, dialog: within(screen.getByRole('dialog', { name: 'New thread' })) };
}

describe('the tools tab', () => {
  it('shows the tools as stored, with web search off', async () => {
    const host = new FakeHost();
    await onTools(host);

    expect(screen.getByRole('tab', { name: 'Tools' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('checkbox', { name: 'New threads start with it on' })).toBeChecked();
    expect(screen.getByLabelText('Command time limit')).toHaveValue('300');
    expect(screen.getByText(/^5 minutes\./)).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: /search the web while it implements/ })).not.toBeChecked();
    // Read-only turns can search only once web search is on.
    expect(screen.getByRole('checkbox', { name: /Also while it only reads/ })).toBeDisabled();
  });

  it('saves the section whole, with the revision it was read at', async () => {
    const host = new FakeHost();
    const user = await onTools(host);

    await user.click(screen.getByRole('checkbox', { name: 'New threads start with it on' }));
    await user.clear(screen.getByLabelText('Command time limit'));
    await user.type(screen.getByLabelText('Command time limit'), '900');
    await user.click(screen.getByRole('checkbox', { name: /search the web while it implements/ }));
    await user.click(screen.getByRole('checkbox', { name: /Also while it only reads/ }));
    expect(screen.getByText('Unsaved changes')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Save tools' }));

    expect(await screen.findByText('The tools are saved.')).toBeInTheDocument();
    const sent = host.sent('PUT', '/api/admin/settings/tools')[0]!.body as { revision: number; settings: ToolSettings };
    expect(sent).toEqual({
      revision: 1,
      settings: { ptcByDefault: false, membersMayChoosePtc: true, shellTimeoutSeconds: 900, webSearchEnabled: true, webSearchOnReadOnlyTurns: true },
    });
  });

  it("shows the host's reasons for refusing a change", async () => {
    const host = new FakeHost();
    const user = await onTools(host);

    await user.clear(screen.getByLabelText('Command time limit'));
    await user.type(screen.getByLabelText('Command time limit'), '5');
    await user.click(screen.getByRole('button', { name: 'Save tools' }));

    expect(await screen.findByText(/between 30 and 3600 seconds/)).toBeInTheDocument();
    expect(host.tools.revision).toBe(1);
  });

  it('sets the web search key at once, and warns while web search is on without one', async () => {
    const host = new FakeHost();
    const user = await onTools(host);
    const webSearch = within(screen.getByRole('region', { name: 'Web search' }));

    await user.click(webSearch.getByRole('checkbox', { name: /search the web while it implements/ }));
    expect(webSearch.getByRole('note')).toHaveTextContent('Web search is on but has no key');

    await user.type(webSearch.getByLabelText('Tavily key'), 'tvly-secret{Enter}');

    expect(await screen.findByRole('status')).toHaveTextContent('The web search key is set.');
    expect(host.secretValues.get('websearch:tavily')).toBe('tvly-secret');
    expect(webSearch.queryByRole('note')).toBeNull();
    expect(document.body.textContent).not.toContain('tvly-secret');
    // Setting the key kept the unsaved tick.
    expect(webSearch.getByRole('checkbox', { name: /search the web while it implements/ })).toBeChecked();
  });
});

describe('the tools form', () => {
  const tools: ToolSettings = { ptcByDefault: true, membersMayChoosePtc: false, shellTimeoutSeconds: 600, webSearchEnabled: true, webSearchOnReadOnlyTurns: true };

  it('reads back what it was given', () => {
    expect(fromToolsForm(toToolsForm(tools))).toEqual({ settings: tools });
  });

  it('refuses a shell limit that is not a whole number, and accepts one with separators', () => {
    expect(fromToolsForm({ ...toToolsForm(tools), shellTimeoutSeconds: 'ten minutes' })).toEqual({
      error: 'The shell command time limit must be a whole number of seconds.',
    });
    expect((fromToolsForm({ ...toToolsForm(tools), shellTimeoutSeconds: '1,200' }) as { settings: ToolSettings }).settings.shellTimeoutSeconds).toBe(1200);
  });

  it('drops read-only searching when web search is off', () => {
    const off = fromToolsForm({ ...toToolsForm(tools), webSearchEnabled: false }) as { settings: ToolSettings };
    expect(off.settings.webSearchOnReadOnlyTurns).toBe(false);
  });
});

describe('PTC for a new thread', () => {
  it('starts at the default, and sends a choice only when it differs', async () => {
    const host = new FakeHost();
    host.addProject();
    const { user, dialog } = await openNewThread(host);
    const ptc = dialog.getByRole('checkbox', { name: /Programmatic Tool Calling/ });

    expect(ptc).toBeChecked();
    await user.type(dialog.getByLabelText('What do you want changed?'), 'Add CSV export');
    await user.click(dialog.getByRole('button', { name: 'Create thread' }));

    await screen.findByRole('heading', { level: 1, name: 'Add CSV export' });
    expect(host.sent('POST', '/api/threads')[0]!.body).not.toHaveProperty('ptcEnabled');
    expect(screen.getByText(/PTC on\./)).toBeInTheDocument();
  });

  it('sends PTC off when the person turns it off, and the thread says so', async () => {
    const host = new FakeHost();
    host.addProject();
    const { user, dialog } = await openNewThread(host);

    await user.click(dialog.getByRole('checkbox', { name: /Programmatic Tool Calling/ }));
    await user.type(dialog.getByLabelText('What do you want changed?'), 'Add CSV export');
    await user.click(dialog.getByRole('button', { name: 'Create thread' }));

    await screen.findByRole('heading', { level: 1, name: 'Add CSV export' });
    expect(host.sent('POST', '/api/threads')[0]!.body).toMatchObject({ ptcEnabled: false });
    expect(screen.getByText(/PTC off\./)).toBeInTheDocument();
  });

  it('is not offered to someone who may not choose it', async () => {
    const host = new FakeHost();
    host.addProject();
    host.settings.canChoosePtc = false;
    const { dialog } = await openNewThread(host);

    expect(dialog.queryByRole('checkbox', { name: /Programmatic Tool Calling/ })).toBeNull();
  });
});
