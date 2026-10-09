import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import type { ProviderSettings } from './api/types';
import { App } from './App';
import { fromProvidersForm, toProvidersForm } from './components/ProvidersTab';
import { FakeHost } from './test/fakeHost';

function start(host: FakeHost) {
  host.signedIn = true;
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  return userEvent.setup();
}

async function onProviders(host: FakeHost) {
  const user = start(host);
  await user.click(await screen.findByRole('button', { name: 'Settings' }));
  await screen.findByRole('form', { name: 'Providers' });
  return user;
}

const card = (name: string) => within(screen.getByRole('region', { name }));

/** Anthropic enabled with one model, saved, and its key set: what several tests start from. */
function withAnthropic(host: FakeHost) {
  host.providerSettings = {
    revision: 2,
    settings: {
      ...host.providerSettings.settings,
      providers: [
        ...host.providerSettings.settings.providers,
        { name: 'anthropic', enabled: true, baseUrl: null, models: [{ id: 'claude-sonnet-5', contextLength: 200_000 }], defaultModel: 'claude-sonnet-5' },
        { name: 'mesh_api', enabled: true, baseUrl: null, models: [{ id: 'mesh-large', contextLength: 64_000 }], defaultModel: 'mesh-large' },
      ],
    },
  };
  host.secrets.push({ name: 'provider:anthropic', setAt: '2026-10-09T09:00:00Z', setBy: null });
  host.secrets.push({ name: 'provider:mesh_api', setAt: '2026-10-09T09:00:00Z', setBy: null });
  host.offerProviders();
}

async function openNewThread(host: FakeHost) {
  window.location.hash = '#/threads';
  const user = start(host);
  await user.click(await screen.findByRole('button', { name: 'New thread' }));
  return { user, dialog: within(screen.getByRole('dialog', { name: 'New thread' })) };
}

describe('the providers tab', () => {
  it('is where the settings open, with a card for every provider the factory knows', async () => {
    const host = new FakeHost();
    host.addProject();
    await onProviders(host);

    expect(window.location.hash).toBe('#/settings/providers');
    for (const name of ['Anthropic', 'OpenAI', 'Gemini', 'OpenRouter', 'MeshApi', 'Local server'])
      expect(screen.getByRole('region', { name })).toBeInTheDocument();

    const openRouter = card('OpenRouter');
    expect(openRouter.getByRole('checkbox', { name: 'Enabled' })).toBeChecked();
    expect(openRouter.getByText('deepseek/deepseek-v4.1-flash')).toBeInTheDocument();
    expect(openRouter.getByLabelText('Context length of deepseek/deepseek-v4.1-flash')).toHaveValue('1048576');
    expect(openRouter.getByText(/from the host’s environment/)).toBeInTheDocument();
    expect(card('Anthropic').getByRole('checkbox', { name: 'Enabled' })).not.toBeChecked();
    expect(card('MeshApi').getByText('Estimated')).toBeInTheDocument();
    expect(card('Local server').getByLabelText('Address')).toBeInTheDocument();
    expect(screen.getByLabelText('Default provider')).toHaveValue('openrouter');
    expect(screen.getByRole('checkbox', { name: /Offer members only providers whose budgets are strict/ })).toBeChecked();
    expect(screen.getByRole('button', { name: 'Save providers' })).toBeDisabled();
  });

  it('sets a key at once, never shows it again, and keeps unsaved edits', async () => {
    const host = new FakeHost();
    const user = await onProviders(host);
    const anthropic = card('Anthropic');

    await user.click(anthropic.getByRole('checkbox', { name: 'Enabled' }));
    await user.type(anthropic.getByLabelText('Key'), 'sk-ant-secret');
    await user.click(anthropic.getByRole('button', { name: 'Set' }));

    expect(await screen.findByRole('status')).toHaveTextContent('The Anthropic key is set.');
    expect(host.secretValues.get('provider:anthropic')).toBe('sk-ant-secret');
    expect(anthropic.getByLabelText('Key')).toHaveValue('');
    expect(anthropic.getByRole('button', { name: 'Replace' })).toBeInTheDocument();
    expect(document.body.textContent).not.toContain('sk-ant-secret');
    // Setting the key did not throw away the box ticked a moment before.
    expect(anthropic.getByRole('checkbox', { name: 'Enabled' })).toBeChecked();
  });

  it('allows a model, with its context length looked up, and saves the section whole', async () => {
    const host = new FakeHost();
    host.contextLengths.set('claude-sonnet-5', 200_000);
    const user = await onProviders(host);
    const anthropic = card('Anthropic');

    await user.click(anthropic.getByRole('checkbox', { name: 'Enabled' }));
    await user.type(anthropic.getByLabelText('Model id'), 'claude-sonnet-5');
    await user.click(anthropic.getByRole('button', { name: 'Look up' }));
    expect(await anthropic.findByDisplayValue('200000')).toHaveAccessibleName('Its context length');
    await user.click(anthropic.getByRole('button', { name: 'Allow model' }));
    expect(anthropic.getByRole('radio', { name: 'claude-sonnet-5 is the default' })).toBeChecked();
    await user.selectOptions(screen.getByLabelText('Default provider'), 'anthropic');
    await user.click(screen.getByRole('button', { name: 'Save providers' }));

    expect(await screen.findByRole('status')).toHaveTextContent('The providers are saved.');
    const sent = host.sent('PUT', '/api/admin/settings/providers')[0]!.body as { revision: number; settings: ProviderSettings };
    expect(sent.revision).toBe(1);
    expect(sent.settings.defaultProvider).toBe('anthropic');
    expect(sent.settings.providers.map((p) => p.name)).toEqual(['anthropic', 'openrouter']);
    expect(sent.settings.providers[0]).toEqual({
      name: 'anthropic',
      enabled: true,
      baseUrl: null,
      models: [{ id: 'claude-sonnet-5', contextLength: 200_000 }],
      defaultModel: 'claude-sonnet-5',
    });
  });

  it('adds a model with Enter, rather than saving the form', async () => {
    const host = new FakeHost();
    const user = await onProviders(host);
    const openRouter = card('OpenRouter');

    await user.type(openRouter.getByLabelText('Its context length'), '262144');
    await user.type(openRouter.getByLabelText('Model id'), 'qwen/qwen3-coder{Enter}');

    expect(openRouter.getByText('qwen/qwen3-coder')).toBeInTheDocument();
    expect(host.sent('PUT', '/api/admin/settings/providers')).toHaveLength(0);
  });

  it('asks for a context length before allowing a model, and refuses one allowed already', async () => {
    const host = new FakeHost();
    const user = await onProviders(host);
    const openRouter = card('OpenRouter');

    await user.type(openRouter.getByLabelText('Model id'), 'qwen/qwen3-coder');
    await user.click(openRouter.getByRole('button', { name: 'Allow model' }));
    expect(openRouter.getByRole('alert')).toHaveTextContent('Give its context length, or look it up.');

    await user.clear(openRouter.getByLabelText('Model id'));
    await user.type(openRouter.getByLabelText('Model id'), 'deepseek/deepseek-v4.1-flash');
    await user.type(openRouter.getByLabelText('Its context length'), '1000000');
    await user.click(openRouter.getByRole('button', { name: 'Allow model' }));
    expect(openRouter.getByRole('alert')).toHaveTextContent('deepseek/deepseek-v4.1-flash is already allowed.');
  });

  it('moves the default to another model when the default is removed', async () => {
    const host = new FakeHost();
    host.providerSettings.settings.providers[0]!.models.push({ id: 'qwen/qwen3-coder', contextLength: 262_144 });
    const user = await onProviders(host);
    const openRouter = card('OpenRouter');

    await user.click(openRouter.getByRole('button', { name: 'Remove deepseek/deepseek-v4.1-flash' }));

    expect(openRouter.getByRole('radio', { name: 'qwen/qwen3-coder is the default' })).toBeChecked();
  });

  it("shows the host's reasons for refusing a change", async () => {
    const host = new FakeHost();
    const user = await onProviders(host);

    await user.click(card('Gemini').getByRole('checkbox', { name: 'Enabled' }));
    await user.click(screen.getByRole('button', { name: 'Save providers' }));

    expect(await screen.findByText(/Gemini: an enabled provider needs at least one allowed model/)).toBeInTheDocument();
    expect(host.providerSettings.revision).toBe(1);
  });

  it('clears a key, after which nothing can be offered for a new thread', async () => {
    const host = new FakeHost();
    host.addProject();
    const user = await onProviders(host);

    await user.click(card('OpenRouter').getByRole('button', { name: 'Clear' }));

    expect(await screen.findByRole('status')).toHaveTextContent('The OpenRouter key is cleared.');
    expect(host.sent('DELETE', '/api/admin/secrets/provider%3Aopenrouter')).toHaveLength(1);
    await user.click(screen.getByRole('button', { name: 'Threads' }));
    await user.click(await screen.findByRole('button', { name: 'New thread' }));
    expect(within(screen.getByRole('dialog')).getByRole('note')).toHaveTextContent('No model provider is ready.');
  });

  it('sets the GitHub token', async () => {
    const host = new FakeHost();
    const user = await onProviders(host);
    const gitHub = card('GitHub');

    await user.type(gitHub.getByLabelText('Token'), 'ghp_token{Enter}');

    expect(await screen.findByRole('status')).toHaveTextContent('The GitHub token is set.');
    expect(host.secretValues.get('github')).toBe('ghp_token');
  });
});

describe('choosing a provider and model for a new thread', () => {
  it('offers the providers and models allowed, and sends the choice', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    withAnthropic(host);
    const { user, dialog } = await openNewThread(host);

    expect(dialog.getByLabelText('Provider')).toHaveValue('openrouter');
    expect(dialog.getByLabelText('Model')).toHaveValue('deepseek/deepseek-v4.1-flash');
    expect(dialog.getByText('Context 1,048,576 tokens.')).toBeInTheDocument();

    await user.selectOptions(dialog.getByLabelText('Provider'), 'anthropic');
    expect(dialog.getByLabelText('Model')).toHaveValue('claude-sonnet-5');
    await user.type(dialog.getByLabelText('What do you want changed?'), 'Add CSV export');
    await user.click(dialog.getByRole('button', { name: 'Create thread' }));

    await screen.findByRole('heading', { level: 1, name: 'Add CSV export' });
    expect(host.sent('POST', '/api/threads')[0]!.body).toMatchObject({ projectId: project.id, provider: 'anthropic', model: 'claude-sonnet-5' });
  });

  it('warns that an estimated provider can overrun its budget', async () => {
    const host = new FakeHost();
    host.addProject();
    withAnthropic(host);
    const { user, dialog } = await openNewThread(host);

    expect(within(dialog.getByLabelText('Provider')).getByRole('option', { name: 'MeshApi (estimated budget)' })).toBeInTheDocument();
    await user.selectOptions(dialog.getByLabelText('Provider'), 'mesh_api');

    expect(dialog.getByText(/MeshApi may not report what a call used/)).toBeInTheDocument();
  });

  it('does not offer a member an estimated provider while strict-only is on', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const member = host.addPerson();
    member.projectIds.push(project.id);
    host.signInAs(member);
    withAnthropic(host);
    const { dialog } = await openNewThread(host);

    const options = within(dialog.getByLabelText('Provider')).getAllByRole('option').map((o) => o.textContent);
    expect(options).toEqual(['OpenRouter', 'Anthropic']);
  });
});

describe('the providers form', () => {
  const known = new FakeHost().knownProviders;

  it('leaves out a provider that was never set up, and reads back what it was given', () => {
    const settings: ProviderSettings = {
      providers: [
        { name: 'local', enabled: true, baseUrl: 'http://localhost:1234/v1', models: [{ id: 'qwen3', contextLength: 32_768 }], defaultModel: 'qwen3' },
      ],
      defaultProvider: 'local',
      strictOnly: false,
    };

    expect(fromProvidersForm(toProvidersForm(settings, known), known)).toEqual({ settings });
  });

  it('refuses a context length that is not a whole number', () => {
    const form = toProvidersForm(new FakeHost().providerSettings.settings, known);
    form.entries.find((e) => e.name === 'openrouter')!.models[0]!.contextLength = 'a lot';

    expect(fromProvidersForm(form, known)).toEqual({ error: "OpenRouter: deepseek/deepseek-v4.1-flash's context length must be a whole number." });
  });

  it('drops an address from a provider that is not reached at one', () => {
    const form = toProvidersForm(new FakeHost().providerSettings.settings, known);
    form.entries.find((e) => e.name === 'openrouter')!.baseUrl = 'http://x';

    expect((fromProvidersForm(form, known) as { settings: ProviderSettings }).settings.providers[0]!.baseUrl).toBeNull();
  });
});
