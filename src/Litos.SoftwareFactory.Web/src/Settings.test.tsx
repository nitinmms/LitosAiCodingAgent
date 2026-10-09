import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import { App } from './App';
import { fromForm, toForm } from './components/SettingsPage';
import { parseRoute, routeHash } from './domain/route';
import { FakeHost } from './test/fakeHost';

function start(host: FakeHost) {
  host.signedIn = true;
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  return userEvent.setup();
}

async function onBudgets(host: FakeHost) {
  const user = start(host);
  await user.click(await screen.findByRole('button', { name: 'Settings' }));
  await user.click(await screen.findByRole('tab', { name: 'Budgets and limits' }));
  await screen.findByRole('form', { name: 'Budgets and limits' });
  return user;
}

/** Replaces what a field holds with what an Admin types. */
async function retype(user: ReturnType<typeof userEvent.setup>, label: string, value: string) {
  const input = screen.getByLabelText(label);
  await user.clear(input);
  if (value) await user.type(input, value);
}

describe('the settings area', () => {
  it('is in the top bar for an admin only', async () => {
    const host = new FakeHost();
    host.signInAs(host.addPerson());
    window.location.hash = '#/settings/budgets';
    start(host);

    expect(await screen.findByText("Only an admin can change the factory's settings.")).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Settings' })).not.toBeInTheDocument();
    expect(host.sent('GET', '/api/admin/settings')).toHaveLength(0);
  });

  it('opens on the budgets tab, filled in from the host', async () => {
    const host = new FakeHost();
    host.budgets.settings.dailyUserQuota = 1_000_000;
    await onBudgets(host);

    expect(window.location.hash).toBe('#/settings/budgets');
    expect(screen.getByRole('tab', { name: 'Budgets and limits' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByLabelText('Default task budget')).toHaveValue('300000');
    expect(screen.getByLabelText('Maximum task budget')).toHaveValue('');
    expect(screen.getByLabelText('Daily quota')).toHaveValue('1000000');
    expect(screen.getByLabelText('Change-request top-up')).toHaveValue('50');
    expect(screen.getByLabelText('Output allowance per call')).toHaveValue('32768');
    expect(screen.getByText(/past 1,000,000 today is refused before it is sent/)).toBeInTheDocument();
    // Nothing changed yet: nothing to save.
    expect(screen.getByRole('button', { name: 'Save budgets' })).toBeDisabled();
  });

  it('saves the whole section with the revision it read, and members see the new limits', async () => {
    const host = new FakeHost();
    host.addProject();
    const user = await onBudgets(host);

    await retype(user, 'Maximum task budget', '600,000');
    await retype(user, 'Monthly quota', '5000000');
    await retype(user, 'Change-request top-up', '25');
    await retype(user, 'Concurrent runs', '3');
    await user.click(screen.getByRole('button', { name: 'Save budgets' }));

    expect(await screen.findByRole('status')).toHaveTextContent('The budgets are saved.');
    expect(host.sent('PUT', '/api/admin/settings/budgets')[0]!.body).toEqual({
      revision: 1,
      settings: {
        defaultTaskBudget: 300_000,
        maximumTaskBudget: 600_000,
        dailyUserQuota: null,
        monthlyUserQuota: 5_000_000,
        repairCyclesPerRun: 2,
        slotCap: 3,
        reworkTopUpShare: 0.25,
        outputAllowanceTokens: 32_768,
      },
    });
    // The form now stands at the saved revision, with nothing left to save.
    expect(screen.getByLabelText('Maximum task budget')).toHaveValue('600000');
    expect(screen.getByRole('button', { name: 'Save budgets' })).toBeDisabled();

    // The New thread dialog knows the new maximum without a reload.
    await user.click(screen.getByRole('button', { name: 'Threads' }));
    await user.click(await screen.findByRole('button', { name: 'New thread' }));
    expect(within(screen.getByRole('dialog')).getByText(/At most 600,000\./)).toBeInTheDocument();
  });

  it('clears a quota when its field is emptied', async () => {
    const host = new FakeHost();
    host.budgets.settings.dailyUserQuota = 100_000;
    const user = await onBudgets(host);

    await retype(user, 'Daily quota', '');
    await user.click(screen.getByRole('button', { name: 'Save budgets' }));

    await screen.findByRole('status');
    expect((host.sent('PUT', '/api/admin/settings/budgets')[0]!.body as { settings: { dailyUserQuota: unknown } }).settings.dailyUserQuota).toBeNull();
    expect(screen.getByText(/Without a quota/)).toBeInTheDocument();
  });

  it('discards changes back to what was read', async () => {
    const host = new FakeHost();
    const user = await onBudgets(host);

    await retype(user, 'Concurrent runs', '9');
    await user.click(screen.getByRole('button', { name: 'Discard changes' }));

    expect(screen.getByLabelText('Concurrent runs')).toHaveValue('2');
    expect(screen.getByRole('button', { name: 'Save budgets' })).toBeDisabled();
  });

  it('says what cannot be read as a number, and asks the host nothing', async () => {
    const host = new FakeHost();
    const user = await onBudgets(host);

    await retype(user, 'Default task budget', 'lots');
    await retype(user, 'Repair cycles per run', '');
    await user.click(screen.getByRole('button', { name: 'Save budgets' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('The default task budget must be a whole number.');
    expect(alert).toHaveTextContent('Repair cycles per run is required.');
    expect(host.sent('PUT', '/api/admin/settings/budgets')).toHaveLength(0);
  });

  it("shows the host's reasons for refusing a change", async () => {
    const host = new FakeHost();
    const user = await onBudgets(host);

    await retype(user, 'Concurrent runs', '0');
    await retype(user, 'Maximum task budget', '100000');
    await user.click(screen.getByRole('button', { name: 'Save budgets' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Concurrent runs must be between 1 and 16.');
    expect(alert).toHaveTextContent('The default task budget cannot be above the maximum.');
    expect(host.budgets.revision).toBe(1);
  });

  it("does not overwrite another admin's change, and loads it on request", async () => {
    const host = new FakeHost();
    const user = await onBudgets(host);
    host.budgetsChangedElsewhere({ slotCap: 4 });

    await retype(user, 'Repair cycles per run', '5');
    await user.click(screen.getByRole('button', { name: 'Save budgets' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('changed by someone else');
    expect(host.budgets.settings).toMatchObject({ slotCap: 4, repairCyclesPerRun: 2 });

    await user.click(screen.getByRole('button', { name: 'Load their change' }));

    expect(await screen.findByDisplayValue('4')).toHaveAccessibleName('Concurrent runs');
    expect(screen.getByLabelText('Repair cycles per run')).toHaveValue('2');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});

describe('the budgets form', () => {
  const read = toForm({
    defaultTaskBudget: 300_000,
    maximumTaskBudget: null,
    dailyUserQuota: null,
    monthlyUserQuota: 9_000_000,
    repairCyclesPerRun: 2,
    slotCap: 2,
    reworkTopUpShare: 0.125,
    outputAllowanceTokens: 32_768,
  });

  it('reads back what it was given', () => {
    expect(fromForm(read)).toEqual({
      settings: {
        defaultTaskBudget: 300_000,
        maximumTaskBudget: null,
        dailyUserQuota: null,
        monthlyUserQuota: 9_000_000,
        repairCyclesPerRun: 2,
        slotCap: 2,
        reworkTopUpShare: 0.125,
        outputAllowanceTokens: 32_768,
      },
    });
  });

  it('takes token counts written with separators', () => {
    const parsed = fromForm({ ...read, maximumTaskBudget: '1,200,000', dailyUserQuota: '50 000', monthlyUserQuota: '2_000_000' });

    expect(parsed).toMatchObject({ settings: { maximumTaskBudget: 1_200_000, dailyUserQuota: 50_000, monthlyUserQuota: 2_000_000 } });
  });

  it('refuses fractions, negatives and an empty top-up', () => {
    expect(fromForm({ ...read, slotCap: '1.5' })).toEqual({ error: 'Concurrent runs must be a whole number.' });
    expect(fromForm({ ...read, defaultTaskBudget: '-5' })).toEqual({ error: 'The default task budget must be a whole number.' });
    expect(fromForm({ ...read, reworkTopUpPercent: '' })).toEqual({ error: 'The change-request top-up must be a percentage, 0 or more.' });
  });
});

describe('the settings route', () => {
  it('names its tab, and falls back to the first for one it does not know', () => {
    expect(parseRoute('#/settings/budgets')).toEqual({ view: 'settings', tab: 'budgets' });
    expect(parseRoute('#/settings/providers')).toEqual({ view: 'settings', tab: 'providers' });
    expect(parseRoute('#/settings')).toEqual({ view: 'settings', tab: 'providers' });
    expect(parseRoute('#/settings/nonsense')).toEqual({ view: 'settings', tab: 'providers' });
    expect(routeHash({ view: 'settings', tab: 'budgets' })).toBe('#/settings/budgets');
  });
});

describe('a new thread under a maximum budget', () => {
  it('refuses a budget above the maximum before asking the host', async () => {
    const host = new FakeHost();
    host.addProject();
    host.settings = { ...host.settings, maximumBudget: 500_000 };
    window.location.hash = '#/threads';
    const user = start(host);

    await user.click(await screen.findByRole('button', { name: 'New thread' }));
    const dialog = within(screen.getByRole('dialog'));
    await user.type(dialog.getByLabelText('What do you want changed?'), 'Add CSV export');
    await user.clear(dialog.getByLabelText('Token budget'));
    await user.type(dialog.getByLabelText('Token budget'), '700000');
    await user.click(dialog.getByRole('button', { name: 'Create thread' }));

    expect(await dialog.findByRole('alert')).toHaveTextContent("A task's budget can be at most 500,000 tokens.");
    expect(host.sent('POST', '/api/threads')).toHaveLength(0);
  });
});
