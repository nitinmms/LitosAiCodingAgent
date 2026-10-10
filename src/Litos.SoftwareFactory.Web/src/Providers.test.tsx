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
        { name: 'anthropic', enabled: true, baseUrl: null, models: [{ id: 'claude-sonnet-5', contextLength: 200_000 }], defaultModel: 'claude-sonnet-5', allowEveryModel: false },
        { name: 'mesh_api', enabled: true, baseUrl: null, models: [{ id: 'mesh-large', contextLength: 64_000 }], defaultModel: 'mesh-large', allowEveryModel: false },
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
      allowEveryModel: false,
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

describe('the model catalog on the providers tab', () => {
  const sonnet = { id: 'anthropic/claude-sonnet-5', displayName: 'Anthropic: Claude Sonnet 5', contextLength: 200_000, supportsTools: true, inputPricePerMillion: 3, outputPricePerMillion: 15 };
  const haiku = { id: 'anthropic/claude-haiku-5', displayName: 'Anthropic: Claude Haiku 5', contextLength: 200_000, supportsTools: true, inputPricePerMillion: 1, outputPricePerMillion: 5 };
  const chatOnly = { id: 'some/chat-only', displayName: 'Chat only', contextLength: 32_000, supportsTools: false, inputPricePerMillion: 0, outputPricePerMillion: 0 };
  const qwen = { id: 'qwen/qwen3-coder', displayName: 'Qwen3 Coder', contextLength: 262_144, supportsTools: true, inputPricePerMillion: 0.2, outputPricePerMillion: 0.8 };
  const unknownWindow = { id: 'some/unknown-window', displayName: null, contextLength: null, supportsTools: true, inputPricePerMillion: null, outputPricePerMillion: null };

  it('says a catalog was never fetched, and fetches it on Refresh', async () => {
    const host = new FakeHost();
    host.catalogSource.set('openrouter', [sonnet, qwen]);
    const user = await onProviders(host);
    const openRouter = card('OpenRouter');

    expect(openRouter.getByText('Catalog: not fetched yet.')).toBeInTheDocument();
    await user.click(openRouter.getByRole('button', { name: 'Refresh' }));

    expect(await openRouter.findByText(/Catalog: 2 models, fetched/)).toBeInTheDocument();
    expect(host.sent('POST', '/api/admin/models/catalog/openrouter/refresh')).toHaveLength(1);
  });

  it('says why a fetch failed, and keeps the last list', async () => {
    const host = new FakeHost();
    host.catalogSource.set('openrouter', [sonnet]);
    const user = await onProviders(host);
    const openRouter = card('OpenRouter');
    await user.click(openRouter.getByRole('button', { name: 'Refresh' }));
    await openRouter.findByText(/Catalog: 1 models, fetched/);

    host.catalogSource.set('openrouter', { error: 'Response status code does not indicate success: 401 (Unauthorized).' });
    await user.click(openRouter.getByRole('button', { name: 'Refresh' }));

    expect(await openRouter.findByRole('alert')).toHaveTextContent(/The last fetch failed .*401 \(Unauthorized\)/);
    expect(openRouter.getByText(/Catalog: 1 models, fetched/)).toBeInTheDocument();
  });

  it('fetches a provider’s catalog when its key is set, and not when it is enabled without one', async () => {
    const host = new FakeHost();
    host.catalogSource.set('anthropic', [{ ...sonnet, id: 'claude-sonnet-5', supportsTools: null, inputPricePerMillion: null, outputPricePerMillion: null }]);
    const user = await onProviders(host);
    const anthropic = card('Anthropic');

    // Enabled with no key yet: nothing could be fetched, so nothing is asked.
    await user.click(anthropic.getByRole('checkbox', { name: 'Enabled' }));
    expect(host.sent('POST', '/api/admin/models/catalog/anthropic/refresh')).toHaveLength(0);

    await user.type(anthropic.getByLabelText('Key'), 'sk-ant{Enter}');
    expect(await anthropic.findByText(/Catalog: 1 models, fetched/)).toBeInTheDocument();
    expect(host.sent('POST', '/api/admin/models/catalog/anthropic/refresh')).toHaveLength(1);
  });

  it('fetches the catalog of a provider enabled for the first time with its key set', async () => {
    const host = new FakeHost();
    host.secrets.push({ name: 'provider:gemini', setAt: '2026-10-09T09:00:00Z', setBy: null });
    host.catalogSource.set('gemini', [{ id: 'gemini-3-pro', displayName: 'Gemini 3 Pro', contextLength: 1_048_576, supportsTools: null, inputPricePerMillion: null, outputPricePerMillion: null }]);
    const user = await onProviders(host);

    await user.click(card('Gemini').getByRole('checkbox', { name: 'Enabled' }));

    expect(await card('Gemini').findByText(/Catalog: 1 models, fetched/)).toBeInTheDocument();
  });

  it('picks models from the catalog, narrowed by its filters, and allows them with their context lengths', async () => {
    const host = new FakeHost();
    host.catalogSource.set('openrouter', [sonnet, haiku, chatOnly, qwen, unknownWindow, { ...qwen, id: 'deepseek/deepseek-v4.1-flash' }]);
    const user = await onProviders(host);
    const openRouter = card('OpenRouter');

    await user.click(openRouter.getByRole('button', { name: 'Choose from catalog' }));
    const picker = within(await openRouter.findByRole('group', { name: 'Choose OpenRouter models' }));
    // Takes tools and Hide allowed start on: the chat-only model and the allowed one are left out.
    expect(picker.getByText('4 of 6 models.')).toBeInTheDocument();
    expect(picker.queryByRole('checkbox', { name: 'some/chat-only' })).toBeNull();
    expect(picker.queryByRole('checkbox', { name: 'deepseek/deepseek-v4.1-flash' })).toBeNull();
    // Its context length is not reported: it cannot be picked, only added by id.
    expect(picker.getByRole('checkbox', { name: 'some/unknown-window' })).toBeDisabled();
    expect(picker.getByText('$3.00 / $15.00')).toBeInTheDocument();

    await user.selectOptions(picker.getByLabelText('Family'), 'anthropic');
    expect(picker.getByText('2 of 6 models.')).toBeInTheDocument();
    await user.type(picker.getByLabelText('Search'), 'sonnet');
    expect(picker.getByText('1 of 6 models.')).toBeInTheDocument();
    await user.click(picker.getByRole('checkbox', { name: 'anthropic/claude-sonnet-5' }));
    await user.clear(picker.getByLabelText('Search'));
    await user.selectOptions(picker.getByLabelText('Family'), '');
    await user.selectOptions(picker.getByLabelText('Context at least'), '200000');
    // A tick survives the filters changing.
    expect(picker.getByRole('checkbox', { name: 'anthropic/claude-sonnet-5' })).toBeChecked();
    await user.click(picker.getByRole('checkbox', { name: 'qwen/qwen3-coder' }));
    await user.click(picker.getByRole('button', { name: 'Allow 2 models' }));

    expect(openRouter.getByLabelText('Context length of anthropic/claude-sonnet-5')).toHaveValue('200000');
    expect(openRouter.getByLabelText('Context length of qwen/qwen3-coder')).toHaveValue('262144');
    // The default stays where it was.
    expect(openRouter.getByRole('radio', { name: 'deepseek/deepseek-v4.1-flash is the default' })).toBeChecked();
    // Now allowed, they are hidden from the picker.
    expect(picker.queryByRole('checkbox', { name: 'qwen/qwen3-coder' })).toBeNull();

    await user.click(screen.getByRole('button', { name: 'Save providers' }));
    await screen.findByText('The providers are saved.');
    const sent = host.sent('PUT', '/api/admin/settings/providers')[0]!.body as { settings: ProviderSettings };
    expect(sent.settings.providers[0]!.models.map((m) => m.id)).toEqual(['deepseek/deepseek-v4.1-flash', 'anthropic/claude-sonnet-5', 'qwen/qwen3-coder']);
  });

  it('fetches the catalog when the picker is opened with none, and the first model picked becomes the default', async () => {
    const host = new FakeHost();
    host.secrets.push({ name: 'provider:openai', setAt: '2026-10-09T09:00:00Z', setBy: null });
    host.catalogSource.set('openai', [
      { id: 'gpt-6-luna', displayName: null, contextLength: 1_050_000, supportsTools: null, inputPricePerMillion: null, outputPricePerMillion: null },
    ]);
    const user = await onProviders(host);
    const openai = card('OpenAI');

    await user.click(openai.getByRole('button', { name: 'Choose from catalog' }));
    const picker = within(await openai.findByRole('group', { name: 'Choose OpenAI models' }));
    expect(picker.getByText(/does not report which models take tools/)).toBeInTheDocument();
    await user.click(picker.getByRole('checkbox', { name: 'gpt-6-luna' }));
    await user.click(picker.getByRole('button', { name: 'Allow 1 model' }));

    expect(openai.getByRole('radio', { name: 'gpt-6-luna is the default' })).toBeChecked();
    expect(host.sent('POST', '/api/admin/models/catalog/openai/refresh')).toHaveLength(1);
  });

  it('marks an allowed model the provider no longer lists', async () => {
    const host = new FakeHost();
    host.catalogs.set('openrouter', { name: 'openrouter', attemptedAt: '2026-10-10T09:00:00Z', fetchedAt: '2026-10-10T09:00:00Z', error: null, models: [qwen] });
    await onProviders(host);

    const row = card('OpenRouter').getByText('deepseek/deepseek-v4.1-flash').closest('tr')!;
    expect(await within(row).findByText('not offered by provider')).toBeInTheDocument();
  });

  it('allows every model only with a warning, and saves the choice', async () => {
    const host = new FakeHost();
    const user = await onProviders(host);
    const openRouter = card('OpenRouter');

    await user.click(openRouter.getByRole('checkbox', { name: 'Allow every model in its catalog that takes tools' }));
    expect(openRouter.getByRole('note')).toHaveTextContent('Anyone offered OpenRouter can then start a task on any of its models');
    await user.click(screen.getByRole('button', { name: 'Save providers' }));

    await screen.findByText('The providers are saved.');
    const sent = host.sent('PUT', '/api/admin/settings/providers')[0]!.body as { settings: ProviderSettings };
    expect(sent.settings.providers[0]!.allowEveryModel).toBe(true);
  });
});

describe('unsaved changes on a settings tab', () => {
  it('are named in a bar kept in view, and saving them clears it', async () => {
    const host = new FakeHost();
    const user = await onProviders(host);
    expect(screen.getByText('All changes saved')).toBeInTheDocument();

    await user.type(card('OpenRouter').getByLabelText('Its context length'), '262144');
    await user.type(card('OpenRouter').getByLabelText('Model id'), 'qwen/qwen3-coder{Enter}');
    expect(screen.getByText('Unsaved changes')).toBeInTheDocument();
    expect(screen.getByText('Unsaved changes').closest('.save-bar')).toContainElement(screen.getByRole('button', { name: 'Save providers' }));

    await user.click(screen.getByRole('button', { name: 'Save providers' }));
    expect(await screen.findByText('All changes saved')).toBeInTheDocument();
  });

  it('make the browser ask before the page is left, and only while they are unsaved', async () => {
    const host = new FakeHost();
    const user = await onProviders(host);
    const leave = () => {
      const event = new Event('beforeunload', { cancelable: true });
      window.dispatchEvent(event);
      return event.defaultPrevented;
    };
    expect(leave()).toBe(false);

    await user.click(card('Anthropic').getByRole('checkbox', { name: 'Enabled' }));
    expect(leave()).toBe(true);

    await user.click(screen.getByRole('button', { name: 'Discard changes' }));
    expect(leave()).toBe(false);
  });

  it('are named on the budgets tab too', async () => {
    window.location.hash = '#/settings/budgets';
    const host = new FakeHost();
    const user = start(host);
    const slots = await screen.findByLabelText('Concurrent runs');

    await user.clear(slots);
    await user.type(slots, '3');

    expect(screen.getByText('Unsaved changes')).toBeInTheDocument();
  });
});

describe('choosing a model for a new thread', () => {
  const models = (dialog: ReturnType<typeof within>) => within(dialog.getByRole('listbox', { name: 'Models' }));
  const labels = (options: HTMLElement[]) => options.map((o) => o.getAttribute('aria-label'));

  it('lists the allowed models by provider, with the default chosen, and sends the choice', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    withAnthropic(host);
    const { user, dialog } = await openNewThread(host);
    const list = models(dialog);

    const openRouter = within(list.getByRole('group', { name: 'OpenRouter' }));
    expect(openRouter.getByRole('option', { name: 'deepseek/deepseek-v4.1-flash on OpenRouter' })).toHaveAttribute('aria-selected', 'true');
    expect(dialog.getByText('OpenRouter, deepseek/deepseek-v4.1-flash. Context 1,048,576 tokens.')).toBeInTheDocument();

    await user.click(within(list.getByRole('group', { name: 'Anthropic' })).getByRole('option', { name: 'claude-sonnet-5 on Anthropic' }));
    await user.type(dialog.getByLabelText('What do you want changed?'), 'Add CSV export');
    await user.click(dialog.getByRole('button', { name: 'Create thread' }));

    await screen.findByRole('heading', { level: 1, name: 'Add CSV export' });
    expect(host.sent('POST', '/api/threads')[0]!.body).toMatchObject({ projectId: project.id, provider: 'anthropic', model: 'claude-sonnet-5' });
  });

  it('pins the default and the person’s recent models, each once', async () => {
    const host = new FakeHost();
    host.addProject();
    withAnthropic(host);
    host.settings.recentModels = [
      { provider: 'anthropic', model: 'claude-sonnet-5' },
      { provider: 'openrouter', model: 'deepseek/deepseek-v4.1-flash' },
      { provider: 'openai', model: 'gpt-6-luna' }, // no longer offered: not pinned
    ];
    const { dialog } = await openNewThread(host);

    const pinned = within(models(dialog).getByRole('group', { name: 'Default and recent' })).getAllByRole('option');
    expect(labels(pinned)).toEqual(['deepseek/deepseek-v4.1-flash on OpenRouter', 'claude-sonnet-5 on Anthropic']);
  });

  it('narrows the list by a search, and Enter picks the only match without creating the thread', async () => {
    const host = new FakeHost();
    host.addProject();
    withAnthropic(host);
    const { user, dialog } = await openNewThread(host);

    await user.type(dialog.getByLabelText('Model'), 'sonnet{Enter}');

    expect(labels(models(dialog).getAllByRole('option'))).toEqual(['claude-sonnet-5 on Anthropic']);
    expect(models(dialog).getByRole('option', { name: 'claude-sonnet-5 on Anthropic' })).toHaveAttribute('aria-selected', 'true');
    expect(host.sent('POST', '/api/threads')).toHaveLength(0);

    await user.clear(dialog.getByLabelText('Model'));
    await user.type(dialog.getByLabelText('Model'), 'nothing-like-it');
    expect(models(dialog).getByText('No model matches.')).toBeInTheDocument();
  });

  it('warns that an estimated provider can overrun its budget', async () => {
    const host = new FakeHost();
    host.addProject();
    withAnthropic(host);
    const { user, dialog } = await openNewThread(host);

    expect(models(dialog).getByText('MeshApi (estimated budget)')).toBeInTheDocument();
    await user.click(models(dialog).getByRole('option', { name: 'mesh-large on MeshApi' }));

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

    const groups = models(dialog).getAllByRole('group').map((g) => g.getAttribute('aria-label'));
    expect(groups).toEqual(['Default and recent', 'OpenRouter', 'Anthropic']);
  });
});

describe('the providers form', () => {
  const known = new FakeHost().knownProviders;

  it('leaves out a provider that was never set up, and reads back what it was given', () => {
    const settings: ProviderSettings = {
      providers: [
        { name: 'local', enabled: true, baseUrl: 'http://localhost:1234/v1', models: [{ id: 'qwen3', contextLength: 32_768 }], defaultModel: 'qwen3', allowEveryModel: false },
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
