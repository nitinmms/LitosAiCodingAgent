import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import { App } from './App';
import { FakeHost } from './test/fakeHost';

/** Run order and "Blocked by" (docs/software-factory/run-order-architecture.md). */

function start(host: FakeHost, hash = '') {
  host.signedIn = true;
  window.location.hash = hash;
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  return userEvent.setup();
}

afterEach(() => {
  window.location.hash = '';
});

/** One project with three queued tasks, Ben's in the middle, and a draft. */
function scene() {
  const host = new FakeHost();
  const sales = host.addProject({ name: 'SalesApp' });
  const ben = host.addPerson({ userName: 'ben', displayName: 'Ben Okafor', projectIds: [sales.id] });
  const queued = (title: string, queueRank: number, ownerId = host.user.id) =>
    host.addThread(sales, { title, state: 'Queued', stage: 'Implement', queueRank, ownerId });
  const a = queued('Add scheduled_at column', 1000);
  const b = queued('Record token usage', 2000, ben.id);
  const c = queued('Retry failed generations', 3000);
  const draft = host.addThread(sales, { title: 'Gemini provider' });
  return { host, sales, ben, a, b, c, draft };
}

const queueTitles = () =>
  within(screen.getByRole('list', { name: 'Queue' }))
    .getAllByRole('listitem')
    .map((li) => within(li).getAllByRole('button')[0]!.textContent);

async function openRunOrder(host: FakeHost, projectId: string) {
  const user = start(host);
  await user.selectOptions(await screen.findByLabelText('Project'), projectId);
  await screen.findByRole('list', { name: 'Queue' });
  return user;
}

describe('the run order on the board', () => {
  it('is shown only with one project chosen, in the order the coordinator takes it', async () => {
    const { host, sales } = scene();
    const user = start(host);

    await screen.findByRole('heading', { name: 'Board' });
    expect(screen.queryByRole('region', { name: 'Run order' })).not.toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText('Project'), sales.id);

    await screen.findByRole('list', { name: 'Queue' });
    expect(queueTitles()).toEqual(['Add scheduled_at column', 'Record token usage', 'Retry failed generations']);
    expect(screen.getByText('Starts next.')).toBeInTheDocument();
    // The cards say their place in line.
    const implement = within(screen.getByRole('listitem', { name: 'Implement' }));
    expect(within(implement.getByRole('button', { name: 'Record token usage' })).getByText('#2 in line')).toBeInTheDocument();
  });

  it("lets an Admin put a task first, and sends the revision they saw", async () => {
    const { host, sales, c } = scene();
    const user = await openRunOrder(host, sales.id);

    await user.click(screen.getAllByRole('button', { name: 'Run first' })[2]!);

    await waitFor(() => expect(queueTitles()[0]).toBe('Retry failed generations'));
    expect(host.sent('POST', `/api/threads/${c.id}/queue-position`)[0]!.body).toEqual({ placement: 'first', revision: c.revision });
  });

  it('moves a task one place with ↑ and ↓ for an Admin', async () => {
    const { host, sales, a } = scene();
    const user = await openRunOrder(host, sales.id);

    await user.click(screen.getByRole('button', { name: 'Move "Add scheduled_at column" down' }));

    await waitFor(() => expect(queueTitles()).toEqual(['Record token usage', 'Add scheduled_at column', 'Retry failed generations']));
    expect(host.sent('POST', `/api/threads/${a.id}/queue-position`)[0]!.body).toMatchObject({ placement: 'after' });
  });

  it('moves a dragged task to where it is dropped', async () => {
    const { host, sales } = scene();
    await openRunOrder(host, sales.id);
    const items = within(screen.getByRole('list', { name: 'Queue' })).getAllByRole('listitem');

    fireEvent.dragStart(items[2]!, { dataTransfer: { effectAllowed: '' } });
    fireEvent.dragOver(items[0]!);
    fireEvent.drop(items[0]!);

    await waitFor(() => expect(queueTitles()[0]).toBe('Retry failed generations'));
  });

  it("lets a Member swap only their own tasks, and offers them no Run first", async () => {
    const { host, sales, ben } = scene();
    const second = host.addThread(sales, { title: "Ben's other task", state: 'Queued', stage: 'Implement', queueRank: 4000, ownerId: ben.id });
    host.signInAs(host.people.find((p) => p.id === ben.id)!);
    const user = await openRunOrder(host, sales.id);

    expect(screen.queryByRole('button', { name: 'Run first' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Move "Add scheduled_at column" down' })).toBeDisabled();

    await user.click(screen.getByRole('button', { name: `Move "${second.title}" up` }));

    await waitFor(() =>
      expect(queueTitles()).toEqual(['Add scheduled_at column', "Ben's other task", 'Retry failed generations', 'Record token usage']),
    );
    expect(host.sent('POST', `/api/threads/${second.id}/queue-position`)[0]!.body).toMatchObject({ placement: 'swap' });
  });

  it('shows a blocked task as skipped, how long it has waited, and releases it now', async () => {
    const { host, sales, a, c } = scene();
    host.link(a.id, c.id, '2026-09-28T09:00:00Z');
    host.change(a.id, { stateReason: 'Waiting for "Retry failed generations" to be finished and merged.' });
    const user = await openRunOrder(host, sales.id);

    const first = within(screen.getByRole('listitem', { name: '1. Add scheduled_at column' }));
    expect(first.getByText('Waiting for "Retry failed generations" to be finished and merged.')).toBeInTheDocument();
    expect(first.getByText(/blocked \d+ days/)).toBeInTheDocument();
    // The coordinator takes the first task that waits for nothing.
    expect(within(screen.getByRole('listitem', { name: '2. Record token usage' })).getByText('Starts next.')).toBeInTheDocument();

    await user.click(first.getByRole('button', { name: 'Release now' }));

    await waitFor(() => expect(screen.getByRole('listitem', { name: '1. Add scheduled_at column' })).toHaveTextContent('Starts next.'));
    expect(host.sent('DELETE', `/api/threads/${a.id}/blockers/${c.id}`)).toHaveLength(1);
  });

  it('makes a task wait for another from its "+ Blocked by" list', async () => {
    const { host, sales, c, draft } = scene();
    const user = await openRunOrder(host, sales.id);

    await user.selectOptions(screen.getByLabelText('Make "Retry failed generations" wait for'), draft.id);

    await waitFor(() =>
      expect(within(screen.getByRole('listitem', { name: '3. Retry failed generations' })).getByText('Gemini provider')).toBeInTheDocument(),
    );
    expect(host.sent('POST', `/api/threads/${c.id}/blockers`)[0]!.body).toEqual({ blockerThreadId: draft.id });
  });

  it("says why a move was refused, in the host's words", async () => {
    const { host, sales } = scene();
    host.failNext('POST', 'queue-position', 409, 'The task has changed since you last saw it. Look again, then try once more.');
    const user = await openRunOrder(host, sales.id);

    await user.click(screen.getAllByRole('button', { name: 'Run first' })[1]!);

    expect(await screen.findByText('The task has changed since you last saw it. Look again, then try once more.')).toBeInTheDocument();
  });
});

describe('New thread', () => {
  it('sends Run after and Blocked by, chosen from the project', async () => {
    const { host, a, draft } = scene();
    const user = start(host, '#/threads');

    await user.click(await screen.findByRole('button', { name: 'New thread' }));
    const dialog = within(screen.getByRole('dialog', { name: 'New thread' }));
    await user.type(dialog.getByLabelText('What do you want changed?'), 'Schedule stories');
    expect(within(dialog.getByLabelText('Run after')).getAllByRole('option').map((o) => o.textContent)).toEqual([
      'End of the queue (first in, first out)',
      'Run first',
      'After #1 · Add scheduled_at column',
      'After #2 · Record token usage',
      'After #3 · Retry failed generations',
    ]);
    await user.selectOptions(dialog.getByLabelText('Run after'), a.id);
    await user.selectOptions(dialog.getByLabelText('Blocked by'), draft.id);
    await user.click(dialog.getByRole('button', { name: 'Create thread' }));

    await screen.findByRole('heading', { level: 1, name: 'Schedule stories' });
    expect(host.sent('POST', '/api/threads')[0]!.body).toMatchObject({ runAfterThreadId: a.id, blockedBy: [draft.id] });
  });

  it('offers a Member no Run first', async () => {
    const { host, ben } = scene();
    host.signInAs(host.people.find((p) => p.id === ben.id)!);
    const user = start(host, '#/threads');

    await user.click(await screen.findByRole('button', { name: 'New thread' }));
    const options = within(screen.getByLabelText('Run after')).getAllByRole('option').map((o) => o.textContent);

    expect(options).not.toContain('Run first');
  });

  it('sends Run first for an Admin, and nothing when left alone', async () => {
    const { host } = scene();
    const user = start(host, '#/threads');

    await user.click(await screen.findByRole('button', { name: 'New thread' }));
    let dialog = within(screen.getByRole('dialog', { name: 'New thread' }));
    await user.type(dialog.getByLabelText('What do you want changed?'), 'Urgent fix');
    await user.selectOptions(dialog.getByLabelText('Run after'), 'first');
    await user.click(dialog.getByRole('button', { name: 'Create thread' }));
    await screen.findByRole('heading', { level: 1, name: 'Urgent fix' });

    await user.click(screen.getByRole('button', { name: 'New thread' }));
    dialog = within(screen.getByRole('dialog', { name: 'New thread' }));
    await user.type(dialog.getByLabelText('What do you want changed?'), 'Plain task');
    await user.click(dialog.getByRole('button', { name: 'Create thread' }));
    await screen.findByRole('heading', { level: 1, name: 'Plain task' });

    const [urgent, plain] = host.sent('POST', '/api/threads').map((r) => r.body as Record<string, unknown>);
    expect(urgent).toMatchObject({ runFirst: true });
    expect(plain).not.toHaveProperty('runFirst');
    expect(plain).not.toHaveProperty('blockedBy');
  });
});

describe("a task's links on its page", () => {
  it('shows what it waits for, and releases it now', async () => {
    const { host, a, c } = scene();
    host.link(a.id, c.id);
    const user = start(host, `#/threads/${a.id}`);

    const box = within(await screen.findByRole('region', { name: 'Blocked by' }));
    expect(box.getByText('Retry failed generations')).toBeInTheDocument();

    await user.click(box.getByRole('button', { name: 'Release now' }));

    expect(await box.findByText('No longer waits for Retry failed generations: released by a person.')).toBeInTheDocument();
  });

  it('marks an accepted task merged, which releases the tasks that wait for it', async () => {
    const { host, sales, a } = scene();
    const accepted = host.addThread(sales, { title: 'Add the column', state: 'Accepted', stage: 'Done', pullRequestNumber: 44 });
    host.link(a.id, accepted.id);
    const user = start(host, `#/threads/${accepted.id}`);

    const box = within(await screen.findByRole('region', { name: 'Blocked by' }));
    expect(box.getByText('1 task waits for this one to be merged: Add scheduled_at column.')).toBeInTheDocument();

    await user.click(box.getByRole('button', { name: 'Mark merged' }));

    await waitFor(() => expect(host.blockerLinks[0]!.releaseReason).toBe('Merged'));
    await waitFor(() => expect(box.queryByRole('button', { name: 'Mark merged' })).not.toBeInTheDocument());
  });

  it('is not shown for a task with nothing to wait for or to merge', async () => {
    const host = new FakeHost();
    const sales = host.addProject();
    const running = host.addThread(sales, { title: 'Lone task', state: 'Running', stage: 'Implement', branch: 'factory/lone' });
    start(host, `#/threads/${running.id}`);

    await screen.findByRole('heading', { level: 1, name: 'Lone task' });
    await waitFor(() => expect(host.sent('GET', `/api/threads/${running.id}/links`)).toHaveLength(1));
    expect(screen.queryByRole('region', { name: 'Blocked by' })).not.toBeInTheDocument();
  });
});
