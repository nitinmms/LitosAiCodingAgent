import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import type { TestabilityView } from './api/types';
import { App } from './App';
import { testabilitySummary } from './components/TestabilityPanel';
import { FakeHost, notCheckedView } from './test/fakeHost';

function start(host: FakeHost) {
  host.signedIn = true;
  window.location.hash = '#/projects';
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  return userEvent.setup();
}

async function onProjects(host: FakeHost) {
  const user = start(host);
  await screen.findByRole('region', { name: 'Projects' });
  return user;
}

/** What a dry run of a Node app with no contract leaves (TestabilityChecks.CheckAsync). */
function gapsView(host: FakeHost, projectId: string, overrides: Partial<TestabilityView> = {}): TestabilityView {
  const project = host.projects.find((p) => p.id === projectId)!;
  return {
    ...notCheckedView(project),
    status: 'Gaps',
    checkedAt: '2026-11-18T09:00:00Z',
    commit: '4e1b9c2aa',
    stack: 'node-express-spa',
    gaps: ["No contract: these are the node-express-spa profile's defaults (npm start).", 'No reset declared: add one (strategy: none if the app keeps no data).'],
    run: {
      kind: 'dry-run',
      outcome: 'Passed',
      checks: [
        { id: 'build', kind: 'Build', status: 'Passed', classification: null, message: null, route: null, screenshots: [] },
        { id: 'visit:anonymous:/', kind: 'RouteVisit', status: 'Passed', classification: null, message: null, route: '/', screenshots: ['/api/run-artifacts/a1'] },
      ],
      lint: { pages: 1, interactive: 10, unnamed: 0 },
      wallSeconds: 41,
      emails: [],
    },
    ...overrides,
  };
}

const panel = () => within(screen.getByRole('region', { name: 'Testability of SalesApp' }));

describe('the testability summary', () => {
  it('says where a project stands in a few words', () => {
    const at = (status: TestabilityView['status']) => testabilitySummary({ status, checkedAt: null, stack: null, adopted: false }, 3);
    expect(at('Gaps')).toEqual({ text: 'Not testable yet', bad: true });
    expect(at('CouldNotCheck')).toEqual({ text: 'Could not check', bad: true });
    expect(at('Adopted').text).toBe('Contract adopted (rev 3)');
    expect(at('ContractFound').text).toBe('Contract found, not adopted');
    expect(at('NotAWebApp').bad).toBe(false);
    expect(testabilitySummary(null).text).toBe('');
  });
});

describe('the Projects page', () => {
  it('shows each project’s UI testing status in its column', async () => {
    const host = new FakeHost();
    host.addProject({ testability: { status: 'Gaps', checkedAt: '2026-11-18T09:00:00Z', stack: 'node-express-spa', adopted: false } });
    await onProjects(host);

    const table = within(screen.getByRole('table'));
    expect(table.getByRole('columnheader', { name: 'UI testing' })).toBeInTheDocument();
    expect(table.getByText('Not testable yet')).toBeInTheDocument();
  });
});

describe('the Testability panel', () => {
  it('shows the gaps and the dry run with its screenshots', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    host.testability.set(project.id, gapsView(host, project.id));
    const user = await onProjects(host);

    await user.click(screen.getByRole('button', { name: 'Testability of SalesApp' }));

    expect(await panel().findByText('Not testable yet')).toBeInTheDocument();
    expect(within(panel().getByRole('list', { name: 'Gaps' })).getByText(/No reset declared/)).toBeInTheDocument();
    const shot = panel().getByRole('img', { name: 'Screenshot of /' });
    expect(shot).toHaveAttribute('src', '/api/run-artifacts/a1');
    expect(panel().getByText(/No model calls; 41s/)).toBeInTheDocument();
  });

  it('checks again on request and shows the new result', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    host.testability.set(project.id, gapsView(host, project.id));
    host.testabilityAfterCheck = (current) => ({ ...current, status: 'ContractFound', gaps: ['litos.test.yaml on the default branch is ready to adopt.'], run: null,
      contract: { path: 'litos.test.yaml', commit: '9f3c2e1aa', valid: true, problems: [] } });
    const user = await onProjects(host);
    await user.click(screen.getByRole('button', { name: 'Testability of SalesApp' }));
    await panel().findByText('Not testable yet');

    await user.click(panel().getByRole('button', { name: 'Check again' }));

    expect(await panel().findByText('Contract found, not adopted')).toBeInTheDocument();
    expect(panel().getByText(/ready to adopt/, { selector: 'p' })).toBeInTheDocument();
    expect(host.testabilityChecks).toEqual([project.id]);
  });

  it('sets a missing role secret without ever showing it', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    host.testability.set(project.id, gapsView(host, project.id, { status: 'Adopted', adopted: true, missingSecrets: ['TEST_ADMIN_PASS'] }));
    const user = await onProjects(host);
    await user.click(screen.getByRole('button', { name: 'Testability of SalesApp' }));
    const secrets = within(await panel().findByRole('group', { name: 'Role secrets' }));
    expect(secrets.getByText('Missing')).toBeInTheDocument();

    await user.click(secrets.getByRole('button', { name: 'Set TEST_ADMIN_PASS' }));
    expect(secrets.getByLabelText('Name')).toHaveValue('TEST_ADMIN_PASS');
    await user.type(secrets.getByLabelText('Value'), 'hunter2!');
    await user.click(secrets.getByRole('button', { name: 'Set secret' }));

    expect(await secrets.findByText('Set')).toBeInTheDocument();
    expect(host.projectSecrets.get(project.id)!.get('TEST_ADMIN_PASS')).toBe('hunter2!');
    expect(host.sent('PUT', `/api/projects/${project.id}/secrets/TEST_ADMIN_PASS`)[0]!.body).toEqual({ value: 'hunter2!' });
    expect(screen.queryByText('hunter2!')).not.toBeInTheDocument();
    expect(secrets.getByLabelText('Value')).toHaveValue('');
  });

  it('refuses a secret name a contract could not use, before sending it', async () => {
    const host = new FakeHost();
    host.addProject();
    const user = await onProjects(host);
    await user.click(screen.getByRole('button', { name: 'Testability of SalesApp' }));
    const secrets = within(await panel().findByRole('group', { name: 'Role secrets' }));

    await user.type(secrets.getByLabelText('Name'), '9 lives');
    await user.type(secrets.getByLabelText('Value'), 'x');

    expect(secrets.getByRole('button', { name: 'Set secret' })).toBeDisabled();
    expect(secrets.getByText(/Use letters, digits and _/)).toBeInTheDocument();
  });

  it('clears a set secret', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    host.projectSecrets.set(project.id, new Map([['TEST_STAFF_PASS', 'x']]));
    const user = await onProjects(host);
    await user.click(screen.getByRole('button', { name: 'Testability of SalesApp' }));
    const secrets = within(await panel().findByRole('group', { name: 'Role secrets' }));

    await user.click(secrets.getByRole('button', { name: 'Clear TEST_STAFF_PASS' }));

    expect(await secrets.findByText(/No secret is set/)).toBeInTheDocument();
    expect(host.projectSecrets.get(project.id)!.size).toBe(0);
  });

  it('is open to a project Member, who cannot change the profile', async () => {
    const host = new FakeHost();
    host.user = { id: 'u-max', userName: 'max', displayName: 'Max Weber', roles: ['Member'] };
    const project = host.addProject();
    host.testability.set(project.id, gapsView(host, project.id));
    const user = await onProjects(host);

    expect(screen.queryByRole('button', { name: 'Change the profile of SalesApp' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Testability of SalesApp' }));
    expect(await panel().findByText('Not testable yet')).toBeInTheDocument();
  });
});

describe('adopting the contract on main', () => {
  const preview = (overrides: Partial<import('./api/types').AdoptionPreview> = {}): import('./api/types').AdoptionPreview => ({
    commit: '9f3c2e1aa0',
    found: true,
    problems: [],
    missingSecrets: [],
    changes: true,
    canAdopt: true,
    diff: [{ kind: 'Same', text: 'version: 1' }, { kind: 'Added', text: '  stack: node-express-spa' }],
    ...overrides,
  });

  it('shows the diff, then adopts the reviewed commit', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    host.testability.set(project.id, gapsView(host, project.id, { status: 'ContractFound', run: null,
      contract: { path: 'litos.test.yaml', commit: '9f3c2e1aa0', valid: true, problems: [] } }));
    host.adoptionPreviews.set(project.id, preview());
    const user = await onProjects(host);
    await user.click(screen.getByRole('button', { name: 'Testability of SalesApp' }));
    const adoption = within(await panel().findByRole('group', { name: 'Adopt the contract' }));

    await user.click(adoption.getByRole('button', { name: 'Review contract on main' }));
    expect(await adoption.findByText(/stack: node-express-spa/)).toHaveClass('diff-add');
    await user.click(adoption.getByRole('button', { name: 'Adopt this contract' }));

    expect(await panel().findByText(/Adopted as profile revision 2/)).toBeInTheDocument();
    expect(host.adopted).toEqual([{ projectId: project.id, commit: '9f3c2e1aa0' }]);
    expect(await panel().findByText('Test-ready (rev 2)')).toBeInTheDocument();
  });

  it('will not adopt a contract whose secrets are not set', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    host.testability.set(project.id, gapsView(host, project.id, { status: 'ContractFound', run: null,
      contract: { path: 'litos.test.yaml', commit: '9f3c2e1aa0', valid: true, problems: [] } }));
    host.adoptionPreviews.set(project.id, preview({ missingSecrets: ['TEST_ADMIN_PASS'], canAdopt: false }));
    const user = await onProjects(host);
    await user.click(screen.getByRole('button', { name: 'Testability of SalesApp' }));
    const adoption = within(await panel().findByRole('group', { name: 'Adopt the contract' }));

    await user.click(adoption.getByRole('button', { name: 'Review contract on main' }));

    expect(await adoption.findByText(/Set TEST_ADMIN_PASS below first/)).toBeInTheDocument();
    expect(adoption.getByRole('button', { name: 'Adopt this contract' })).toBeDisabled();
  });

  it('names the broken-on-main state as bad news', () => {
    expect(testabilitySummary({ status: 'BrokenOnMain', checkedAt: null, stack: null, adopted: true })).toEqual({ text: 'Broken on main', bad: true });
    expect(testabilitySummary({ status: 'TestReady', checkedAt: null, stack: null, adopted: true }, 4).text).toBe('Test-ready (rev 4)');
  });
});

describe('starting a Make-testable task', () => {
  it('creates a testability task for the project and delegates it with the gaps', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    host.testability.set(project.id, gapsView(host, project.id));
    const user = await onProjects(host);
    await user.click(screen.getByRole('button', { name: 'Testability of SalesApp' }));

    await user.click(await panel().findByRole('button', { name: 'Start a Make-testable task' }));

    await waitFor(() => expect(host.sent('POST', '/messages')).toHaveLength(1));
    expect(host.sent('POST', '/api/threads')[0]!.body).toMatchObject({ projectId: project.id, title: 'Make SalesApp testable', typeLabel: 'testability' });
    const text = String((host.sent('POST', '/messages')[0]!.body as { text: string }).text);
    expect(text.startsWith('@factory Make this app testable')).toBe(true);
    expect(text).toContain('- No reset declared');
    expect(window.location.hash).toMatch(/^#\/threads\//);
  });

  it('is not offered once the contract is adopted', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    host.testability.set(project.id, gapsView(host, project.id, { status: 'TestReady', adopted: true }));
    const user = await onProjects(host);
    await user.click(screen.getByRole('button', { name: 'Testability of SalesApp' }));

    await panel().findByText(/Test-ready/);
    expect(panel().queryByRole('button', { name: 'Start a Make-testable task' })).not.toBeInTheDocument();
  });
});
