import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import { App } from './App';
import { FakeHost } from './test/fakeHost';

/** Starts the app on whatever the address says; with none, it opens on the board. */
function start(host: FakeHost) {
  host.signedIn = true;
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  return userEvent.setup();
}

const column = (stage: string) => within(screen.getByRole('listitem', { name: stage }));

/** Two projects, Ben's and the Admin's threads, in several stages and turns. */
function scene() {
  const host = new FakeHost();
  const sales = host.addProject({ name: 'SalesApp' });
  const filedb = host.addProject({ name: 'filedb-sharp', gitHub: 'harbor-tools/filedb-sharp' });
  const ben = host.addPerson({ userName: 'ben', displayName: 'Ben Okafor', projectIds: [sales.id] });
  const mineWaiting = host.addThread(sales, { title: 'Add CSV export', state: 'AwaitingHumanTesting', stage: 'Handoff', pullRequestNumber: 12, tokensUsed: 81_200 });
  const bensWaiting = host.addThread(sales, { title: 'Fix the footer', typeLabel: 'bug', state: 'AwaitingDecision', stage: 'Implement', ownerId: ben.id });
  const queued = host.addThread(filedb, {
    title: 'Compact the file',
    typeLabel: 'refactor',
    state: 'Queued',
    stage: 'Implement',
    stateReason: 'Waiting for a free slot (2 of 2 busy).',
  });
  const draft = host.addThread(filedb, { title: 'Speed up reads' });
  return { host, sales, filedb, ben, mineWaiting, bensWaiting, queued, draft };
}

describe('the board', () => {
  it('is where the app opens, with a column per stage and each task in its stage', async () => {
    const { host } = scene();
    start(host);

    expect(await screen.findByRole('heading', { name: 'Board' })).toBeInTheDocument();
    expect(screen.getAllByRole('listitem').map((c) => c.getAttribute('aria-label'))).toEqual([
      'Discuss',
      'Spec',
      'Implement',
      'Verify',
      'Review',
      'Handoff',
      'Done',
    ]);
    expect(column('Discuss').getByRole('button', { name: 'Speed up reads' })).toBeInTheDocument();
    expect(column('Implement').getByRole('button', { name: 'Fix the footer' })).toBeInTheDocument();
    expect(column('Implement').getByRole('button', { name: 'Compact the file' })).toBeInTheDocument();
    expect(column('Handoff').getByRole('button', { name: 'Add CSV export' })).toBeInTheDocument();
    expect(column('Done').queryAllByRole('button')).toHaveLength(0);
  });

  it('shows on each card whose move it is, its type, project, owner, budget, pull request and what it waits for', async () => {
    const { host } = scene();
    start(host);

    const handoff = within(await screen.findByRole('button', { name: 'Add CSV export' }));
    expect(handoff.getByText('Awaiting you')).toBeInTheDocument();
    expect(handoff.getByText('feature')).toBeInTheDocument();
    expect(handoff.getByText('SalesApp · You')).toBeInTheDocument();
    expect(handoff.getByText('81k of 300k')).toBeInTheDocument();
    expect(handoff.getByText('PR #12')).toBeInTheDocument();

    const bens = within(screen.getByRole('button', { name: 'Fix the footer' }));
    expect(bens.getByText('SalesApp · Ben Okafor')).toBeInTheDocument();

    const queued = within(screen.getByRole('button', { name: 'Compact the file' }));
    expect(queued.getByText('Awaiting agent')).toBeInTheDocument();
    expect(queued.getByText('Waiting for a free slot (2 of 2 busy).')).toBeInTheDocument();

    expect(within(screen.getByRole('button', { name: 'Speed up reads' })).getByText('Not started')).toBeInTheDocument();
  });

  /** Found on the M2 check: a task cancelled mid-way stays in its stage's column, and its green
   * "Done" read as finished work. */
  it('says Cancelled on a cancelled task, and Done only on accepted work', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    host.addThread(project, { title: 'Abandoned attempt', state: 'Cancelled', stage: 'Implement', turn: 'Done' });
    host.addThread(project, { title: 'Shipped', state: 'Accepted', stage: 'Done', turn: 'Done' });
    start(host);

    const cancelled = within(await screen.findByRole('button', { name: 'Abandoned attempt' }));
    expect(cancelled.getByText('Cancelled')).toHaveClass('pill', 'neutral');
    expect(cancelled.queryByText('Done')).not.toBeInTheDocument();
    expect(within(screen.getByRole('button', { name: 'Shipped' })).getByText('Done')).toHaveClass('pill', 'done');
  });

  /** §7.1: "Awaiting you" is personal. Ben's decision is not the Admin's to count. */
  it('counts as awaiting you only the waiting tasks you own', async () => {
    const { host } = scene();
    start(host);

    expect(await screen.findByRole('button', { name: '1 awaiting you' })).toBeEnabled();
  });

  it('narrows by project, type, owner and turn, and clears', async () => {
    const { host, filedb } = scene();
    const user = start(host);
    await screen.findByRole('heading', { name: 'Board' });
    const cards = () => screen.getAllByRole('button').filter((b) => b.classList.contains('card')).map((b) => b.getAttribute('aria-label'));

    await user.selectOptions(screen.getByLabelText('Project'), filedb.id);
    expect(cards().sort()).toEqual(['Compact the file', 'Speed up reads']);

    await user.selectOptions(screen.getByLabelText('Type'), 'refactor');
    expect(cards()).toEqual(['Compact the file']);

    await user.click(screen.getByRole('button', { name: 'Clear filters' }));
    await user.selectOptions(screen.getByLabelText('Owner'), 'Ben Okafor');
    expect(cards()).toEqual(['Fix the footer']);

    await user.selectOptions(screen.getByLabelText('Owner'), 'You');
    await user.selectOptions(screen.getByLabelText('Turn'), 'Awaiting you');
    expect(cards()).toEqual(['Add CSV export']);

    await user.click(screen.getByRole('button', { name: 'Clear filters' }));
    expect(cards()).toHaveLength(4);
  });

  it('opens a task from its card', async () => {
    const { host, queued } = scene();
    const user = start(host);

    await user.click(await screen.findByRole('button', { name: 'Compact the file' }));

    expect(await screen.findByRole('heading', { level: 1, name: 'Compact the file' })).toBeInTheDocument();
    expect(window.location.hash).toBe(`#/threads/${queued.id}`);
  });

  it('is in the top bar, and leads back from anywhere', async () => {
    const { host } = scene();
    window.location.hash = '#/projects';
    const user = start(host);

    await user.click(await screen.findByRole('button', { name: 'Board' }));

    expect(await screen.findByRole('heading', { name: 'Board' })).toBeInTheDocument();
    expect(window.location.hash).toBe('#/board');
  });

  it('says what to do when there is no task yet', async () => {
    const host = new FakeHost();
    host.addProject();
    start(host);

    expect(await screen.findByText('No task yet. Create a thread to delegate work to the factory.')).toBeInTheDocument();
  });

  /** Names come with the first load; with every owner named, the directory is not asked again. */
  it('asks the directory for names once, not on every render', async () => {
    const { host } = scene();
    start(host);
    await screen.findByText('SalesApp · Ben Okafor');
    await new Promise((resolve) => setTimeout(resolve, 100));

    expect(host.sent('GET', '/api/directory')).toHaveLength(1);
  });

  /** Someone whose name the directory does not give is still shown, never as a blank. */
  it('shows an owner it cannot name as someone', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    host.addThread(project, { title: 'From a removed account', ownerId: 'u-gone' });
    start(host);

    expect(await screen.findByText('SalesApp · Someone')).toBeInTheDocument();
  });
});

describe('renaming and re-filing a thread', () => {
  async function onThread(host: FakeHost, threadId: string) {
    window.location.hash = `#/threads/${threadId}`;
    const user = start(host);
    await screen.findByRole('heading', { level: 1 });
    return user;
  }

  it('its owner renames it and changes its type, and the board follows', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const thread = host.addThread(project, { title: 'Add CSV export' });
    const user = await onThread(host, thread.id);

    await user.click(screen.getByRole('button', { name: 'Rename or change type' }));
    await user.clear(screen.getByLabelText('Title'));
    await user.type(screen.getByLabelText('Title'), 'Export orders as CSV');
    await user.selectOptions(screen.getByLabelText('Type'), 'bug');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('heading', { level: 1, name: 'Export orders as CSV' })).toBeInTheDocument();
    expect(host.sent('PATCH', `/api/threads/${thread.id}`)[0]!.body).toEqual({ title: 'Export orders as CSV', typeLabel: 'bug' });

    await user.click(screen.getByRole('button', { name: 'Board' }));
    const card = within(await screen.findByRole('button', { name: 'Export orders as CSV' }));
    expect(card.getByText('bug')).toBeInTheDocument();
  });

  it('sends nothing when nothing changed', async () => {
    const host = new FakeHost();
    const thread = host.addThread(host.addProject());
    const user = await onThread(host, thread.id);

    await user.click(screen.getByRole('button', { name: 'Rename or change type' }));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(host.sent('PATCH', '/api/threads')).toHaveLength(0);
    expect(screen.getByRole('button', { name: 'Rename or change type' })).toBeInTheDocument();
  });

  it('refuses an empty title without asking the host', async () => {
    const host = new FakeHost();
    const thread = host.addThread(host.addProject());
    const user = await onThread(host, thread.id);

    await user.click(screen.getByRole('button', { name: 'Rename or change type' }));
    await user.clear(screen.getByLabelText('Title'));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('The title cannot be empty.');
    expect(host.sent('PATCH', '/api/threads')).toHaveLength(0);
  });

  /** Another member sees the thread but cannot change it, so they are not offered to. */
  it('is not offered to a member who does not own the thread', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const thread = host.addThread(project);
    host.signInAs(host.addPerson({ projectIds: [project.id] }));
    await onThread(host, thread.id);

    expect(screen.queryByRole('button', { name: 'Rename or change type' })).not.toBeInTheDocument();
  });

  it("shows the host's reason when it refuses", async () => {
    const host = new FakeHost();
    const thread = host.addThread(host.addProject());
    host.failNext('PATCH', `/api/threads/${thread.id}`, 403, "Only the thread's owner or an Admin can change it.");
    const user = await onThread(host, thread.id);

    await user.click(screen.getByRole('button', { name: 'Rename or change type' }));
    await user.selectOptions(screen.getByLabelText('Type'), 'chore');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('alert')).toHaveTextContent("Only the thread's owner or an Admin can change it.");
  });
});
