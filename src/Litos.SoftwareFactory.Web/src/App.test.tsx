import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import { App } from './App';
import { ADMIN, FakeHost, PASSWORD } from './test/fakeHost';

/** Starts the app against a fake host. Signed in unless told otherwise. */
function start(host: FakeHost, { signedIn = true } = {}) {
  host.signedIn = signedIn;
  const createClient = (onSignedOut: () => void) => createApi(host.fetch, onSignedOut);
  // These tests are about the threads screen; the app itself opens on the board (Board.test.tsx).
  if (!window.location.hash) window.location.hash = '#/threads';
  render(<App createClient={createClient} openEvents={host.openEvents} />);
  return userEvent.setup();
}

/** A host with one project and one thread in the given state, already on screen. */
async function withThread(overrides: Parameters<FakeHost['addThread']>[1] = {}) {
  const host = new FakeHost();
  const project = host.addProject();
  const thread = host.addThread(project, overrides);
  const user = start(host);
  await screen.findByRole('heading', { level: 1, name: thread.title });
  // The stream opens once the thread has loaded.
  await waitFor(() => expect(host.sources).toHaveLength(1));
  return { host, project, thread, user };
}

const composer = () => screen.getByRole('textbox', { name: 'Message' });
const stageNow = () => screen.getByRole('list', { name: 'Stage' }).querySelector('[aria-current="step"]')?.textContent;

describe('signing in', () => {
  it('shows the sign-in screen to a visitor, and the factory after a correct password', async () => {
    const host = new FakeHost();
    host.addProject();
    const user = start(host, { signedIn: false });

    await user.type(await screen.findByLabelText('Username'), 'admin');
    await user.type(screen.getByLabelText('Password'), PASSWORD);
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('button', { name: 'New thread' })).toBeInTheDocument();
    expect(screen.getByText('Priya Raman')).toBeInTheDocument();
    expect(screen.queryByLabelText('Password')).not.toBeInTheDocument();
  });

  it("shows the host's reason for a wrong password, clears it, and stays on the sign-in screen", async () => {
    const host = new FakeHost();
    const user = start(host, { signedIn: false });

    await user.type(await screen.findByLabelText('Username'), 'admin');
    await user.type(screen.getByLabelText('Password'), 'wrong password');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('The user name or password is incorrect.');
    expect(screen.getByLabelText('Password')).toHaveValue('');
    expect(screen.queryByRole('button', { name: 'New thread' })).not.toBeInTheDocument();
  });

  it('explains a locked account', async () => {
    const host = new FakeHost();
    host.failNext('POST', '/api/auth/login', 423, 'This account is locked after repeated failed sign-ins. Try again later.');
    const user = start(host, { signedIn: false });

    await user.type(await screen.findByLabelText('Username'), 'admin');
    await user.type(screen.getByLabelText('Password'), PASSWORD);
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('locked after repeated failed sign-ins');
  });

  it('signs out, and shows nothing of the factory afterwards', async () => {
    const { host, user } = await withThread();

    await user.click(screen.getByRole('button', { name: 'Sign out' }));

    expect(await screen.findByLabelText('Password')).toBeInTheDocument();
    expect(screen.queryByText('Add CSV export')).not.toBeInTheDocument();
    expect(host.signedIn).toBe(false);
    // Leaving the thread stops listening to it.
    expect(host.sources[0]!.closed).toBe(true);
  });

  it('returns to the sign-in screen when the session ends under it', async () => {
    const { host, user } = await withThread({ state: 'AwaitingHumanTesting', stage: 'Handoff' });
    host.signedIn = false;

    await user.click(screen.getByRole('button', { name: 'Cancel task' }));
    await user.click(screen.getByRole('button', { name: 'Yes, cancel this task' }));

    expect(await screen.findByLabelText('Password')).toBeInTheDocument();
  });

  it('says so when the host cannot be reached', async () => {
    const host = new FakeHost();
    host.failNext('GET', '/api/auth/me', 500);
    start(host, { signedIn: false });

    expect(await screen.findByRole('alert')).toHaveTextContent('The request failed (500).');
    expect(screen.getByLabelText('Username')).toBeInTheDocument();
  });
});

describe('projects', () => {
  it('sends a newcomer to register a project before anything else', async () => {
    const host = new FakeHost();
    const user = start(host);

    expect(await screen.findByRole('heading', { name: 'Register a project first' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'New thread' })).toBeDisabled();

    await user.click(screen.getByRole('button', { name: 'Go to Projects' }));
    expect(await screen.findByRole('heading', { name: 'Register a GitHub project' })).toBeInTheDocument();
  });

  it('registers a GitHub project and lists it', async () => {
    const host = new FakeHost();
    const user = start(host);
    await user.click(await screen.findByRole('button', { name: 'Projects' }));

    await user.type(await screen.findByLabelText('GitHub URL'), 'https://github.com/nitinmms/filedb-sharp');
    await user.selectOptions(screen.getByLabelText('Verification preset'), 'dotnet');
    await user.type(screen.getByLabelText('Changed-line coverage threshold (%)'), '75');
    await user.click(screen.getByRole('button', { name: 'Register project' }));

    const table = within(await screen.findByRole('table'));
    expect(table.getByText('nitinmms/filedb-sharp')).toBeInTheDocument();
    expect(table.getByText('75%')).toBeInTheDocument();
    expect(host.sent('POST', '/api/projects')[0]!.body).toEqual({
      gitHubUrl: 'https://github.com/nitinmms/filedb-sharp',
      defaultBranch: 'main',
      preset: 'dotnet',
      coverageThresholdPercent: 75,
      pullRequestEnabled: true,
    });
    expect(screen.getByLabelText('GitHub URL')).toHaveValue('');
    expect(await screen.findByRole('status')).toHaveTextContent('filedb-sharp is registered');
  });

  it("shows the host's reason when the URL is not a GitHub repository, and keeps what was typed", async () => {
    const host = new FakeHost();
    const user = start(host);
    await user.click(await screen.findByRole('button', { name: 'Projects' }));

    await user.type(await screen.findByLabelText('GitHub URL'), 'https://gitlab.com/a/b');
    await user.click(screen.getByRole('button', { name: 'Register project' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('gitHubUrl must be a GitHub repository URL');
    expect(screen.getByLabelText('GitHub URL')).toHaveValue('https://gitlab.com/a/b');
    expect(screen.getByText('No project is registered yet.')).toBeInTheDocument();
  });

  it('refuses a coverage threshold outside 0 to 100 without calling the host', async () => {
    const host = new FakeHost();
    const user = start(host);
    await user.click(await screen.findByRole('button', { name: 'Projects' }));

    await user.type(await screen.findByLabelText('GitHub URL'), 'https://github.com/a/b');
    await user.type(screen.getByLabelText('Changed-line coverage threshold (%)'), '140');
    await user.click(screen.getByRole('button', { name: 'Register project' }));

    // The browser's own validation stops the form; nothing reaches the host.
    expect(screen.getByLabelText('Changed-line coverage threshold (%)')).toBeInvalid();
    expect(host.sent('POST', '/api/projects')).toHaveLength(0);
    expect(screen.getByLabelText('GitHub URL')).toHaveValue('https://github.com/a/b');
  });

  it('does not offer registration to someone who is not an admin', async () => {
    const host = new FakeHost();
    host.user = { ...ADMIN, roles: ['Member'] };
    const user = start(host);
    await user.click(await screen.findByRole('button', { name: 'Projects' }));

    expect(await screen.findByText('Only an admin can register a project.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Register project' })).not.toBeInTheDocument();
  });
});

describe('threads', () => {
  it('creates a thread with the default budget and opens it as a draft', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const user = start(host);

    await user.click(await screen.findByRole('button', { name: 'New thread' }));
    const dialog = within(screen.getByRole('dialog', { name: 'New thread' }));
    expect(dialog.getByLabelText('Token budget')).toHaveValue(300_000);
    await user.type(dialog.getByLabelText('What do you want changed?'), 'Add CSV export to Orders');
    await user.selectOptions(dialog.getByLabelText('Type'), 'bug');
    await user.click(dialog.getByRole('button', { name: 'Create thread' }));

    expect(await screen.findByRole('heading', { level: 1, name: 'Add CSV export to Orders' })).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(host.sent('POST', '/api/threads')[0]!.body).toEqual({
      projectId: project.id,
      title: 'Add CSV export to Orders',
      typeLabel: 'bug',
      budgetCap: 300_000,
    });
    expect(screen.getByRole('heading', { name: 'Delegate this task' })).toBeInTheDocument();
    expect(stageNow()).toContain('Discuss');
  });

  // Clicking into the budget closed the dialog: a press inside a field that ends on the backdrop,
  // or an autofill entry picked over it, reaches the backdrop as a click.
  it('stays open when a press inside a field ends on the backdrop', async () => {
    const host = new FakeHost();
    host.addProject();
    const user = start(host);

    await user.click(await screen.findByRole('button', { name: 'New thread' }));
    const budget = within(screen.getByRole('dialog')).getByLabelText('Token budget');
    const backdrop = screen.getByRole('dialog').parentElement!;
    await user.click(budget);
    await user.pointer([{ keys: '[MouseLeft>]', target: budget }, { target: backdrop }, { keys: '[/MouseLeft]', target: backdrop }]);
    fireEvent.click(backdrop);

    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(budget).toHaveFocus();
  });

  it('closes on a click that starts and ends on the backdrop', async () => {
    const host = new FakeHost();
    host.addProject();
    const user = start(host);

    await user.click(await screen.findByRole('button', { name: 'New thread' }));
    await user.click(screen.getByRole('dialog').parentElement!);

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('turns off browser autofill in the dialog', async () => {
    const host = new FakeHost();
    host.addProject();
    const user = start(host);

    await user.click(await screen.findByRole('button', { name: 'New thread' }));

    expect(screen.getByRole('dialog')).toHaveAttribute('autocomplete', 'off');
  });

  it('refuses a budget that is not a positive whole number', async () => {
    const host = new FakeHost();
    host.addProject();
    const user = start(host);

    await user.click(await screen.findByRole('button', { name: 'New thread' }));
    const dialog = within(screen.getByRole('dialog'));
    await user.type(dialog.getByLabelText('What do you want changed?'), 'X');
    await user.clear(dialog.getByLabelText('Token budget'));
    await user.type(dialog.getByLabelText('Token budget'), '-5');
    await user.click(dialog.getByRole('button', { name: 'Create thread' }));

    expect(dialog.getByLabelText('Token budget')).toBeInvalid();
    expect(host.sent('POST', '/api/threads')).toHaveLength(0);
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('lists threads under their project and switches between them', async () => {
    const host = new FakeHost();
    const sales = host.addProject();
    const db = host.addProject({ name: 'filedb-sharp', gitHub: 'nitinmms/filedb-sharp' });
    host.addThread(sales, { title: 'Add CSV export' });
    const second = host.addThread(db, { title: 'Fix page split', state: 'Running', stage: 'Implement' });
    const user = start(host);

    const list = within(await screen.findByRole('complementary', { name: 'Threads' }));
    expect(list.getByRole('heading', { name: 'SalesApp' })).toBeInTheDocument();
    expect(list.getByRole('heading', { name: 'filedb-sharp' })).toBeInTheDocument();
    expect(list.getByRole('button', { name: /Add CSV export/ })).toHaveAttribute('aria-current', 'true');

    await user.click(list.getByRole('button', { name: /Fix page split/ }));

    expect(await screen.findByRole('heading', { level: 1, name: 'Fix page split' })).toBeInTheDocument();
    expect(window.location.hash).toBe(`#/threads/${second.id}`);
    expect(list.getByRole('button', { name: /Fix page split/ })).toHaveAttribute('aria-current', 'true');
    // The first thread's stream was closed; only the open thread is listened to.
    await waitFor(() => expect(host.sources.filter((s) => !s.closed)).toHaveLength(1));
    expect(host.sources.find((s) => !s.closed)!.url).toContain(second.id);
  });

  it('opens the thread named in the address', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    host.addThread(project, { title: 'First' });
    const second = host.addThread(project, { title: 'Second' });
    window.location.hash = `#/threads/${second.id}`;
    start(host);

    expect(await screen.findByRole('heading', { level: 1, name: 'Second' })).toBeInTheDocument();
  });

  it('counts the tasks waiting on a person, and takes the user to the first', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    host.addThread(project, { title: 'Working', state: 'Running', stage: 'Implement' });
    host.addThread(project, { title: 'Needs testing', state: 'AwaitingHumanTesting', stage: 'Handoff' });
    host.addThread(project, { title: 'Out of budget', state: 'PausedBudget', stage: 'Implement' });
    const user = start(host);

    await user.click(await screen.findByRole('button', { name: '2 awaiting you' }));

    expect(await screen.findByRole('heading', { level: 1, name: 'Needs testing' })).toBeInTheDocument();
  });
});

describe('delegating', () => {
  it('sends an @factory message, shows it in the conversation and queues the task', async () => {
    const { host, thread, user } = await withThread();

    await user.type(composer(), '@factory Add CSV export for Orders');
    await user.click(screen.getByRole('button', { name: 'Send' }));

    expect(await screen.findByText('Add CSV export for Orders')).toBeInTheDocument();
    const sent = host.sent('POST', `/api/threads/${thread.id}/messages`);
    expect(sent).toHaveLength(1);
    expect(sent[0]!.body).toMatchObject({ text: '@factory Add CSV export for Orders' });
    expect((sent[0]!.body as { messageId: string }).messageId).not.toBe('');
    expect(composer()).toHaveValue('');
    expect(screen.getAllByText('Awaiting agent').length).toBeGreaterThan(0);
    expect(stageNow()).toContain('Implement');
    expect(screen.queryByRole('heading', { name: 'Delegate this task' })).not.toBeInTheDocument();
  });

  it('will not send a message that does not start with @factory, and says why', async () => {
    const { host, user } = await withThread();

    await user.type(composer(), 'Add CSV export');

    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
    expect(screen.getByText(/Start the message with @factory/)).toBeInTheDocument();
    await user.keyboard('{Enter}');
    expect(host.sent('POST', '/messages')).toHaveLength(0);
  });

  it('the @factory button puts the mention in front of the draft', async () => {
    const { user } = await withThread();

    await user.type(composer(), 'fix the export');
    await user.click(screen.getByRole('button', { name: '@factory' }));

    expect(composer()).toHaveValue('@factory fix the export');
    expect(screen.getByRole('button', { name: 'Send' })).toBeEnabled();
  });

  it('sends on Enter, and Shift+Enter makes a new line', async () => {
    const { host, user } = await withThread();

    await user.type(composer(), '@factory line one{Shift>}{Enter}{/Shift}line two{Enter}');

    await waitFor(() => expect(host.sent('POST', '/messages')).toHaveLength(1));
    expect(host.sent('POST', '/messages')[0]!.body).toMatchObject({ text: '@factory line one\nline two' });
  });

  it('retries a failed send with the same message id, so the host cannot run it twice', async () => {
    const { host, user } = await withThread();
    host.failNext('POST', '/messages', 500);

    await user.type(composer(), '@factory do it');
    await user.click(screen.getByRole('button', { name: 'Send' }));
    expect(await screen.findByRole('status')).toHaveTextContent('The request failed (500).');
    expect(composer()).toHaveValue('@factory do it');

    await user.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(composer()).toHaveValue(''));

    const ids = host.sent('POST', '/messages').map((r) => (r.body as { messageId: string }).messageId);
    expect(ids).toHaveLength(2);
    expect(ids[0]).toBe(ids[1]);
  });

  it('a message to a running task is a follow-up for the agent', async () => {
    const { host, thread, user } = await withThread({ state: 'Running', stage: 'Implement' });

    await user.type(composer(), '@factory also cover empty orders{Enter}');

    expect(await screen.findByRole('status')).toHaveTextContent('It reads the message at its next safe point.');
    expect(await screen.findByText('also cover empty orders')).toBeInTheDocument();
    expect(host.details(thread.id).thread.state).toBe('Running');
  });
});

describe('live updates', () => {
  it('listens from the snapshot it loaded', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const thread = host.addThread(project, { state: 'Running', stage: 'Implement' });
    host.say(thread.id, { text: 'Created the branch.' });
    host.say(thread.id, { text: 'Reading the code.' });
    start(host);

    await screen.findByText('Reading the code.');
    await waitFor(() => expect(host.sources).toHaveLength(1));
    expect(host.sources[0]!.url).toBe(`/api/threads/${thread.id}/events?after=${host.details(thread.id).eventCursor}`);
  });

  it('shows progress, stage changes and budget use as they happen', async () => {
    const { host, thread } = await withThread({ state: 'Running', stage: 'Implement' });

    act(() => {
      host.say(thread.id, { text: 'Running the build and the unit tests.' });
      host.change(thread.id, { stage: 'Verify', tokensUsed: 42_000, tokensReserved: 8_000 }, 'usage');
    });

    expect(await screen.findByText('Running the build and the unit tests.')).toBeInTheDocument();
    expect(stageNow()).toContain('Verify');
    const budget = within(screen.getByRole('region', { name: 'Budget' }));
    expect(budget.getByText('42,000 used')).toBeInTheDocument();
    expect(budget.getByText('250,000 left of 300,000')).toBeInTheDocument();
    expect(budget.getByText('8,000 reserved for a call in flight.')).toBeInTheDocument();
  });

  it('keeps the thread list in step with the open thread', async () => {
    const { host, thread } = await withThread({ state: 'Running', stage: 'Implement' });
    const list = within(screen.getByRole('complementary', { name: 'Threads' }));
    expect(list.getByText('Agent working')).toBeInTheDocument();

    act(() => void host.change(thread.id, { state: 'Blocked', stateReason: 'The build could not be started.' }));

    expect(await list.findByText('Awaiting you')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '1 awaiting you' })).toBeEnabled();
  });

  it('ignores an event older than what is on screen', async () => {
    const { host, thread } = await withThread({ state: 'Running', stage: 'Verify', revision: 9, tokensUsed: 500 });

    act(() =>
      host.sources[0]!.emit('usage', { state: 'Running', stage: 'Implement', reason: null, revision: 4, tokensUsed: 100, tokensReserved: 0, budgetCap: 300_000, branch: null, pullRequestUrl: null }, 99),
    );

    expect(stageNow()).toContain('Verify');
    expect(screen.getByText('500 used')).toBeInTheDocument();
    expect(host.details(thread.id).thread.revision).toBe(9);
  });

  it('lists the recent model calls with what each reserved and used', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const thread = host.addThread(project, { state: 'Running', stage: 'Implement' });
    host.addCall(thread.id, { reserved: 14_300, charged: 10_600 });
    host.addCall(thread.id, { reserved: 20_000, charged: 0, status: 'Reserved' });
    host.addCall(thread.id, { reserved: 9_000, charged: 9_000, status: 'Unknown' });
    host.addCall(thread.id, { reserved: 58_689, charged: 23_900, status: 'Estimated' });
    start(host);

    const budget = within(await screen.findByRole('region', { name: 'Budget' }));
    expect(await budget.findByText('up to 14,300, used 10,600')).toBeInTheDocument();
    expect(budget.getByText(/Input the provider serves from its cache counts at 10%./)).toBeInTheDocument();
    expect(budget.getByText('up to 20,000, in flight')).toBeInTheDocument();
    expect(budget.getByText('up to 9,000, usage not reported')).toBeInTheDocument();
    expect(budget.getByText('up to 58,689, charged 23,900 (estimate)')).toBeInTheDocument();
    expect(budget.getByText(/the most it could cost is set aside/)).toBeInTheDocument();
    expect(budget.getByText('Strict')).toBeInTheDocument();
  });

  /** Where the tokens went: the work against its review. */
  it('labels each call with what it was for, and totals the work against the review', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const thread = host.addThread(project, { state: 'Running', stage: 'Review' });
    host.addCall(thread.id, { charged: 100_000, phase: 'Implement' });
    host.addCall(thread.id, { charged: 20_000, phase: 'Repair' });
    host.addCall(thread.id, { charged: 30_000, phase: 'Review' });
    host.addCall(thread.id, { charged: 6_000, phase: 'LightReview' });
    host.addCall(thread.id, { charged: 1_000, phase: null });
    start(host);

    const budget = within(await screen.findByRole('region', { name: 'Budget' }));
    const phases = within(await budget.findByLabelText('Tokens by phase'));
    expect(phases.getByText('120,000')).toBeInTheDocument();
    expect(phases.getByText('Review (30% of implementation)')).toBeInTheDocument();
    expect(phases.getByText('36,000')).toBeInTheDocument();
    expect(phases.getByText('Other')).toBeInTheDocument();
    expect(budget.getByText('Call 1 · implement')).toBeInTheDocument();
    expect(budget.getByText('Call 2 · repair')).toBeInTheDocument();
    expect(budget.getByText('Call 4 · light review')).toBeInTheDocument();
    expect(budget.getByText('Call 5 · other')).toBeInTheDocument();
  });

  it('shows no phase totals before any work has been charged', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const thread = host.addThread(project, { state: 'Running', stage: 'Implement' });
    host.addCall(thread.id, { charged: 0, status: 'Reserved', phase: 'Implement' });
    start(host);

    const budget = within(await screen.findByRole('region', { name: 'Budget' }));
    await budget.findByText('Call 1 · implement');
    expect(budget.queryByLabelText('Tokens by phase')).not.toBeInTheDocument();
  });
});

describe('a decision', () => {
  async function awaitingDecision() {
    const context = await withThread({ state: 'Running', stage: 'Implement' });
    let decision!: ReturnType<FakeHost['askDecision']>;
    act(() => void (decision = context.host.askDecision(context.thread.id)));
    const card = within(await screen.findByRole('group', { name: 'Decision needed' }));
    return { ...context, decision, card };
  }

  it('shows the question, why it blocks, the options and the recommendation', async () => {
    const { card } = await awaitingDecision();

    expect(card.getByText('Should the export include cancelled orders?')).toBeInTheDocument();
    expect(card.getByText(/The request does not say/)).toBeInTheDocument();
    expect(card.getByRole('button', { name: 'Include them' })).toBeEnabled();
    expect(card.getByRole('button', { name: /Leave them out.*Recommended/ })).toBeEnabled();
    expect(card.getByText(/Affects the export query and its tests\./)).toBeInTheDocument();
    expect(card.getByText('Awaiting you')).toBeInTheDocument();
  });

  it('answers with an option, and the task goes back to the agent', async () => {
    const { host, decision, card, user } = await awaitingDecision();

    await user.click(card.getByRole('button', { name: 'Include them' }));

    expect(await card.findByText('Answered')).toBeInTheDocument();
    expect(host.sent('POST', `/api/decisions/${decision.id}/answer`)[0]!.body).toEqual({ answer: 'Include them' });
    expect(card.getByRole('button', { name: 'Include them' })).toBeDisabled();
    expect(card.getByRole('button', { name: 'Include them' })).toHaveClass('chosen');
    expect(screen.getByText('answered the decision')).toBeInTheDocument();
  });

  it('offers Other, which opens an answer box in the card and sends the user’s own words', async () => {
    const { host, decision, card, user } = await awaitingDecision();
    const other = card.getByRole('button', { name: /Other…/ });
    expect(other).toHaveAttribute('aria-expanded', 'false');
    expect(card.getByText(/choose Other to answer in your own words/)).toBeInTheDocument();

    await user.click(other);
    expect(other).toHaveAttribute('aria-expanded', 'true');
    const box = card.getByLabelText('Your answer');
    expect(box).toHaveFocus();
    expect(card.getByRole('button', { name: 'Send answer' })).toBeDisabled();

    await user.type(box, 'Return base64 from the server and store it with the slide');
    await user.click(card.getByRole('button', { name: 'Send answer' }));

    expect(await card.findByText('Answered')).toBeInTheDocument();
    expect(host.sent('POST', `/api/decisions/${decision.id}/answer`)[0]!.body).toEqual({
      answer: 'Return base64 from the server and store it with the slide',
    });
    expect(card.queryByRole('button', { name: /Other…/ })).not.toBeInTheDocument();
    expect(card.getByText('Return base64 from the server and store it with the slide')).toBeInTheDocument();
  });

  it('sends an Other answer with Ctrl+Enter, and Cancel closes the box without sending', async () => {
    const { host, decision, card, user } = await awaitingDecision();

    await user.click(card.getByRole('button', { name: /Other…/ }));
    await user.click(card.getByRole('button', { name: 'Cancel' }));
    expect(card.queryByLabelText('Your answer')).not.toBeInTheDocument();
    expect(host.sent('POST', `/api/decisions/${decision.id}/answer`)).toHaveLength(0);

    await user.click(card.getByRole('button', { name: /Other…/ }));
    await user.type(card.getByLabelText('Your answer'), 'Keep reading them{Control>}{Enter}{/Control}');

    expect(await card.findByText('Answered')).toBeInTheDocument();
    expect(host.sent('POST', `/api/decisions/${decision.id}/answer`)[0]!.body).toEqual({ answer: 'Keep reading them' });
  });

  it('answers in the user’s own words from the composer, with no @factory needed', async () => {
    const { host, decision, card, user } = await awaitingDecision();
    expect(composer()).toHaveAttribute('placeholder', expect.stringContaining('Answer the decision'));

    await user.type(composer(), 'Only the ones cancelled this year');
    await user.click(screen.getByRole('button', { name: 'Answer' }));

    expect(await card.findByText('Only the ones cancelled this year')).toBeInTheDocument();
    expect(host.sent('POST', `/api/decisions/${decision.id}/answer`)[0]!.body).toEqual({ answer: 'Only the ones cancelled this year' });
    expect(host.sent('POST', '/messages')).toHaveLength(0);
  });
});

describe('a handoff', () => {
  async function handedOff(overrides: Parameters<FakeHost['handOff']>[1] = {}) {
    const context = await withThread({ state: 'Running', stage: 'Review', branch: 'factory/add-csv-export-1a2b' });
    act(() => void context.host.handOff(context.thread.id, overrides));
    const card = within(await screen.findByRole('group', { name: 'Handoff' }));
    return { ...context, card };
  }

  it('shows the evidence the host measured', async () => {
    const { card } = await handedOff();

    expect(card.getByRole('heading', { name: 'Ready for human testing' })).toBeInTheDocument();
    expect(card.getByText('Added CSV export for Orders.')).toBeInTheDocument();
    expect(card.getByText('factory/add-csv-export-1a2b')).toBeInTheDocument();
    expect(card.getByText('9f8e7d6')).toBeInTheDocument();
    expect(await card.findByRole('link', { name: 'Draft PR #12' })).toHaveAttribute('href', 'https://github.com/harbor-tools/salesapp/pull/12');
    expect(card.getByText('Passed (142 passed, 6 new)')).toBeInTheDocument();
    expect(card.getByText('91.4% (threshold 80%)')).toBeInTheDocument();
    expect(card.getByText('Clean')).toBeInTheDocument();
    expect(card.getByText('81,200 of 300,000 used')).toBeInTheDocument();
    expect(card.getByText('Orders can be exported as CSV')).toBeInTheDocument();
    expect(card.getByText('CsvWriterTests.Quotes')).toBeInTheDocument();
    expect(card.getByLabelText('Export 10 orders and open the file in Excel')).not.toBeChecked();
    expect(card.getByText(/Unit tests are not application testing/)).toBeInTheDocument();
  });

  it('never dresses up what did not pass', async () => {
    const { card } = await handedOff({
      build: 'Passed',
      unitTests: 'Failed',
      testsPassed: 140,
      testsFailed: 2,
      newTests: 0,
      preExistingFailures: ['LegacyTests.Flaky'],
      coverage: 'Unavailable',
      changedLineCoveragePercent: null,
      unmeasuredFiles: ['web/src/Orders.tsx'],
      review: 'FindingsFixed',
      findings: [
        { severity: 'Blocking', file: 'src/Csv.cs', line: 14, text: 'Quotes are not escaped.' },
        { severity: 'Minor', file: 'src/Csv.cs', line: null, text: 'Name is unclear.' },
      ],
      knownLimitations: ['The draft pull request could not be opened'],
      criteria: [{ criterion: 'Looks right in Excel', tests: [], manualOnly: true }],
      pullRequestNumber: null,
      pullRequestUrl: null,
      budgetCap: null,
    });

    expect(card.getByText('Failed (140 passed, 2 failed)')).toBeInTheDocument();
    expect(card.getByText('Unavailable')).toBeInTheDocument();
    expect(card.getByText('1 finding fixed, 1 minor finding open')).toBeInTheDocument();
    expect(card.getByText('Quotes are not escaped.')).toBeInTheDocument();
    expect(card.getByText('src/Csv.cs:14')).toBeInTheDocument();
    expect(card.getByText('LegacyTests.Flaky')).toBeInTheDocument();
    expect(card.getByText('web/src/Orders.tsx')).toBeInTheDocument();
    expect(card.getByText(/The draft pull request could not be opened\./)).toBeInTheDocument();
    expect(card.getByText('Manual testing only')).toBeInTheDocument();
    expect(card.getByText('81,200 used (no cap)')).toBeInTheDocument();
    expect(card.queryByRole('link')).not.toBeInTheDocument();
  });

  it('lets a person judge each review finding, and pressing the verdict again clears it', async () => {
    const context = await withThread({ state: 'Running', stage: 'Review', branch: 'factory/add-csv-export-1a2b' });
    context.host.details(context.thread.id).findings = [
      { id: 'f-1', severity: 'Minor', file: 'src/Csv.cs', line: 14, text: 'Rewriting resets the choice.', status: 'Open', verdict: null },
      { id: 'f-2', severity: 'Blocking', file: 'src/Csv.cs', line: 3, text: 'Quotes are not escaped.', status: 'Fixed', verdict: null },
    ];
    act(() => void context.host.handOff(context.thread.id));
    const list = within(await screen.findByRole('list', { name: 'Review findings' }));
    expect(list.getByText('fixed')).toBeInTheDocument();

    const first = within(list.getAllByRole('group', { name: 'Was this finding right?' })[0]!);
    await userEvent.click(first.getByRole('button', { name: 'Real defect' }));

    expect(context.host.sent('POST', '/api/findings/f-1/verdict')[0]!.body).toEqual({ verdict: 'Real' });
    await waitFor(() => expect(first.getByRole('button', { name: 'Real defect' })).toHaveAttribute('aria-pressed', 'true'));

    await userEvent.click(first.getByRole('button', { name: 'Real defect' }));

    expect(context.host.sent('POST', '/api/findings/f-1/verdict')[1]!.body).toEqual({ verdict: null });
    await waitFor(() => expect(first.getByRole('button', { name: 'Real defect' })).toHaveAttribute('aria-pressed', 'false'));
  });

  it('says a review that was started but cut off did not finish, rather than that it never ran', async () => {
    const { card } = await handedOff({ review: 'DidNotFinish', findings: [] });

    expect(card.getByText("Didn't finish (its model reached the output limit without replying)")).toBeInTheDocument();
  });

  it('says plainly when a change was not reviewed because it did not need one', async () => {
    const { card } = await handedOff({ review: 'NotNeeded', findings: [] });

    expect(card.getByText('Not needed (a small change with no risk signals)')).toBeInTheDocument();
  });

  it('fills the verification panel and the changed files', async () => {
    await handedOff();

    const verification = within(screen.getByRole('region', { name: 'Verification' }));
    expect(verification.getByText('142 passed')).toBeInTheDocument();
    expect(verification.getByText('91.4%')).toBeInTheDocument();
    expect(verification.getByText('Awaiting you')).toBeInTheDocument();
    const files = within(screen.getByRole('region', { name: 'Changed files' }));
    expect(files.getByText('src/SalesApp.Api/Export/CsvWriter.cs')).toBeInTheDocument();
    expect(stageNow()).toContain('Handoff');
  });

  it('accepting closes the task', async () => {
    const { host, thread, card, user } = await handedOff();

    await user.click(card.getByRole('button', { name: 'Accept' }));

    expect(await card.findByText('Accepted')).toBeInTheDocument();
    expect(host.details(thread.id).thread.state).toBe('Accepted');
    expect(card.queryByRole('button', { name: 'Accept' })).not.toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: 'Message' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Cancel task' })).not.toBeInTheDocument();
    expect(screen.getByText(/This task is closed; the branch is yours to merge/)).toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Verification' })).getByText('Accepted')).toBeInTheDocument();
    expect(screen.getByRole('list', { name: 'Stage' }).querySelector('[aria-current="step"]')).toBeNull();
  });

  it('asking for changes starts a rework run on the same thread, and the old handoff is superseded', async () => {
    const { host, thread, card, user } = await handedOff();

    await user.click(card.getByRole('button', { name: 'Request changes' }));
    expect(composer()).toHaveValue('@factory ');
    expect(composer()).toHaveFocus();
    await user.type(composer(), 'Quote fields that contain commas{Enter}');

    expect(await screen.findByText('Quote fields that contain commas')).toBeInTheDocument();
    expect(host.details(thread.id).thread.state).toBe('Queued');
    expect(card.queryByRole('button', { name: 'Accept' })).not.toBeInTheDocument();

    act(() => void host.handOff(thread.id, { summary: 'Quoted fields that contain commas.', commitSha: 'aaaaaaa1111111' }));

    await screen.findByText('Quoted fields that contain commas.');
    const cards = screen.getAllByRole('group', { name: 'Handoff' });
    expect(cards).toHaveLength(2);
    expect(within(cards[0]!).getByText('Superseded')).toBeInTheDocument();
    expect(within(cards[0]!).queryByRole('button', { name: 'Accept' })).not.toBeInTheDocument();
    expect(within(cards[1]!).getByRole('button', { name: 'Accept' })).toBeEnabled();
  });

  it('does not link to an address that is not https', async () => {
    const { card } = await handedOff({ pullRequestUrl: 'javascript:alert(1)' });

    expect(await card.findByText('Draft PR #12')).toBeInTheDocument();
    expect(card.queryByRole('link')).not.toBeInTheDocument();
  });
});

describe('after a handoff, @factory is a change request', () => {
  async function handedOff() {
    const context = await withThread();
    await context.user.type(composer(), '@factory Add CSV export for Orders{Enter}');
    await waitFor(() => expect(context.host.details(context.thread.id).thread.state).toBe('Queued'));
    act(() => void context.host.change(context.thread.id, { state: 'Running', branch: 'factory/add-csv-export-1a2b' }));
    act(() => void context.host.handOff(context.thread.id));
    await screen.findByRole('group', { name: 'Handoff' });
    return context;
  }

  /** The first real task's mistake: a question, sent with @factory, became a rework run. */
  it('says so before anything is sent', async () => {
    await handedOff();

    expect(screen.getByText(/asks for changes: it starts a rework run on this branch/)).toBeInTheDocument();
    expect(screen.getByText(/It cannot answer questions yet/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Send change request' })).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Send' })).not.toBeInTheDocument();
  });

  it('a draft does not talk about change requests', async () => {
    await withThread();

    expect(screen.getByRole('button', { name: 'Send' })).toBeInTheDocument();
    expect(screen.queryByText(/asks for changes/)).not.toBeInTheDocument();
  });

  it('a change request can be withdrawn, which brings back the handoff and its Accept button', async () => {
    const { host, thread, user } = await handedOff();
    expect(screen.queryByRole('button', { name: 'Withdraw change request' })).not.toBeInTheDocument();

    await user.type(composer(), '@factory How do I set up manual tests?');
    await user.click(screen.getByRole('button', { name: 'Send change request' }));
    expect(await screen.findByRole('status')).toHaveTextContent('You can withdraw it if that is not what you meant.');
    expect(screen.queryByRole('button', { name: 'Accept' })).not.toBeInTheDocument();

    await user.click(await screen.findByRole('button', { name: 'Withdraw change request' }));

    expect(await screen.findByRole('button', { name: 'Accept' })).toBeEnabled();
    expect(host.sent('POST', `/api/threads/${thread.id}/withdraw`)).toHaveLength(1);
    expect(host.details(thread.id).thread.state).toBe('AwaitingHumanTesting');
    expect(screen.getByText('Change request withdrawn. The task is back at its last handoff.', { selector: '.ev span' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Withdraw change request' })).not.toBeInTheDocument();
    expect(stageNow()).toContain('Handoff');
  });

  it.each(['Blocked', 'PausedBudget', 'PausedUser', 'Interrupted'] as const)('can be withdrawn while the rework is %s', async (state) => {
    const { host, thread, user } = await handedOff();
    await user.type(composer(), '@factory How do I set up manual tests?{Enter}');
    await screen.findByRole('button', { name: 'Withdraw change request' });
    act(() => void host.change(thread.id, { state: 'Running' }));
    act(() => void host.change(thread.id, { state, stateReason: 'Stopped.' }));

    await user.click(await screen.findByRole('button', { name: 'Withdraw change request' }));

    await waitFor(() => expect(host.details(thread.id).thread.state).toBe('AwaitingHumanTesting'));
    expect(await screen.findByRole('button', { name: 'Accept' })).toBeEnabled();
  });

  it('a change request that is being worked on must be paused first, and the screen says so', async () => {
    const { host, thread, user } = await handedOff();
    await user.type(composer(), '@factory How do I set up manual tests?{Enter}');
    await screen.findByRole('button', { name: 'Withdraw change request' });

    act(() => void host.change(thread.id, { state: 'Running' }));

    expect(await screen.findByText(/Pause it if you want to withdraw it/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Withdraw change request' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Pause' })).toBeInTheDocument();
  });

  it('the original work cannot be withdrawn: only cancelled', async () => {
    const { host, thread, user } = await withThread();
    await user.type(composer(), '@factory Add CSV export for Orders{Enter}');
    await waitFor(() => expect(host.details(thread.id).thread.state).toBe('Queued'));
    act(() => void host.change(thread.id, { state: 'Running' }));
    act(() => void host.change(thread.id, { state: 'Blocked', stateReason: 'The build could not be started.' }));

    await screen.findByRole('group', { name: 'Blocked' });
    expect(screen.queryByRole('button', { name: 'Withdraw change request' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Cancel task' })).toBeInTheDocument();
  });

  it("shows the host's reason when the withdrawal is refused", async () => {
    const { host, thread, user } = await handedOff();
    await user.type(composer(), '@factory How do I set up manual tests?{Enter}');
    const button = await screen.findByRole('button', { name: 'Withdraw change request' });
    // The agent picked it up a moment ago; this tab has not heard yet.
    host.details(thread.id).thread = { ...host.details(thread.id).thread, state: 'Running', revision: 99 };

    await user.click(button);

    expect(await screen.findByRole('status')).toHaveTextContent('Pause the task, then withdraw it.');
  });
});

describe('the pull request label follows GitHub', () => {
  async function withPullRequest(state: 'Draft' | 'Open' | 'Merged' | 'Closed' | 'Unknown') {
    const host = new FakeHost();
    const project = host.addProject();
    const thread = host.addThread(project, { state: 'Running', stage: 'Review', branch: 'factory/add-csv-export-1a2b' });
    host.handOff(thread.id);
    host.pullRequestStates.set(thread.id, state);
    start(host);
    await screen.findByRole('group', { name: 'Handoff' });
    return { host, thread };
  }

  it.each([
    ['Draft', 'Draft PR #12'],
    ['Open', 'PR #12'],
    ['Merged', 'PR #12 merged'],
    ['Closed', 'PR #12 closed'],
    ['Unknown', 'PR #12'],
  ] as const)('%s on GitHub is shown as "%s", in the header and on the handoff', async (state, label) => {
    await withPullRequest(state);

    await waitFor(() => expect(screen.getAllByRole('link', { name: label })).toHaveLength(2));
    for (const link of screen.getAllByRole('link', { name: label }))
      expect(link).toHaveAttribute('href', 'https://github.com/harbor-tools/salesapp/pull/12');
  });

  /** The first real task was merged on GitHub and the factory went on calling it a draft. */
  it('a merged pull request is never called a draft', async () => {
    await withPullRequest('Merged');

    await screen.findAllByRole('link', { name: 'PR #12 merged' });
    expect(screen.queryByText(/Draft PR/)).not.toBeInTheDocument();
  });

  it('when GitHub cannot be asked, the label says only what is certain', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const thread = host.addThread(project, { state: 'Running', stage: 'Review' });
    host.handOff(thread.id);
    host.failNext('GET', '/pull-request', 500);
    start(host);

    await screen.findByRole('group', { name: 'Handoff' });
    await waitFor(() => expect(host.sent('GET', '/pull-request')).toHaveLength(1));
    expect(screen.getAllByRole('link', { name: 'PR #12' })).toHaveLength(2);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('a task with no pull request asks GitHub nothing', async () => {
    const { host } = await withThread({ state: 'Running', stage: 'Implement' });

    expect(host.sent('GET', '/pull-request')).toHaveLength(0);
  });
});

describe('stopping and restarting', () => {
  it('pauses a queued task, then resumes it', async () => {
    const { host, thread, user } = await withThread({ state: 'Queued', stage: 'Implement' });

    await user.click(screen.getByRole('button', { name: 'Pause' }));
    await user.click(await screen.findByRole('button', { name: 'Resume' }));

    await waitFor(() => expect(host.details(thread.id).thread.state).toBe('Queued'));
    expect(await screen.findByRole('button', { name: 'Pause' })).toBeInTheDocument();
    expect(host.sent('POST', '/pause')).toHaveLength(1);
    expect(host.sent('POST', '/resume')).toHaveLength(1);
  });

  it('pausing a running task waits for the worker to stop, then shows it paused', async () => {
    const { host, thread, user } = await withThread({ state: 'Running', stage: 'Implement' });

    await user.click(screen.getByRole('button', { name: 'Pause' }));
    await waitFor(() => expect(host.sent('POST', '/pause')).toHaveLength(1));
    // Accepted, but still running until the worker reaches a safe point.
    expect(screen.getByRole('button', { name: 'Pause' })).toBeInTheDocument();

    act(() => void host.change(thread.id, { state: 'PausedUser' }));

    expect(await screen.findByRole('button', { name: 'Resume' })).toBeInTheDocument();
    expect(composer()).toBeDisabled();
    expect(composer()).toHaveAttribute('placeholder', 'Resume the task before sending it anything');
  });

  it('cancelling asks first, and can be backed out of', async () => {
    const { host, thread, user } = await withThread({ state: 'Queued', stage: 'Implement' });

    await user.click(screen.getByRole('button', { name: 'Cancel task' }));
    expect(screen.getByText(/A cancelled task cannot be restarted/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Keep it' }));
    expect(host.sent('POST', '/cancel')).toHaveLength(0);

    await user.click(screen.getByRole('button', { name: 'Cancel task' }));
    await user.click(screen.getByRole('button', { name: 'Yes, cancel this task' }));

    expect(await screen.findByText('Cancelled. This task is closed.')).toBeInTheDocument();
    expect(host.details(thread.id).thread.state).toBe('Cancelled');
    expect(screen.queryByRole('textbox', { name: 'Message' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Cancel task' })).not.toBeInTheDocument();
  });

  it('a draft has nothing to pause or cancel', async () => {
    await withThread();

    expect(screen.queryByRole('button', { name: 'Pause' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Cancel task' })).not.toBeInTheDocument();
  });

  it('a budget pause says why, and raising the budget resumes the task', async () => {
    const reason = 'The next request needs about 42,000 tokens. Only 8,000 of the 300,000-token budget remain, so nothing was sent.';
    const { host, thread, user } = await withThread({
      state: 'PausedBudget',
      stage: 'Implement',
      stateReason: reason,
      tokensUsed: 292_000,
    });
    const panel = within(screen.getByRole('group', { name: 'Budget paused' }));

    expect(panel.getByText(reason)).toBeInTheDocument();
    expect(panel.getByLabelText('New token budget')).toHaveValue(400_000);

    await user.clear(panel.getByLabelText('New token budget'));
    await user.type(panel.getByLabelText('New token budget'), '350000');
    await user.click(panel.getByRole('button', { name: 'Raise budget and resume' }));

    await waitFor(() => expect(host.details(thread.id).thread.state).toBe('Queued'));
    expect(host.sent('POST', '/budget')[0]!.body).toEqual({ cap: 350_000 });
    expect(host.sent('POST', '/resume')).toHaveLength(1);
    await waitFor(() => expect(screen.queryByRole('group', { name: 'Budget paused' })).not.toBeInTheDocument());
    expect(screen.getByText('58,000 left of 350,000')).toBeInTheDocument();
  });

  it('will not raise the budget to less than is already committed', async () => {
    const { host, user } = await withThread({ state: 'PausedBudget', stage: 'Implement', tokensUsed: 292_000 });
    const panel = within(screen.getByRole('group', { name: 'Budget paused' }));

    await user.clear(panel.getByLabelText('New token budget'));
    await user.type(panel.getByLabelText('New token budget'), '200000');

    expect(panel.getByRole('button', { name: 'Raise budget and resume' })).toBeDisabled();
    expect(host.sent('POST', '/budget')).toHaveLength(0);
  });

  it.each([
    ['Blocked', 'Resume', 'The review found a blocking problem and no repair cycles are left.'],
    ['Interrupted', 'Recover and resume', 'The host restarted while this run was in progress.'],
  ] as const)('a task that is %s says why and offers "%s"', async (state, button, reason) => {
    const { host, thread, user } = await withThread({ state, stage: 'Review', stateReason: reason });
    const panel = within(screen.getByRole('group', { name: state }));

    expect(panel.getByText(reason)).toBeInTheDocument();
    await user.click(panel.getByRole('button', { name: button }));

    await waitFor(() => expect(host.details(thread.id).thread.state).toBe('Queued'));
    await waitFor(() => expect(screen.queryByRole('group', { name: state })).not.toBeInTheDocument());
  });

  it("shows the host's refusal when the task has moved on, and catches up", async () => {
    const { host, thread, user } = await withThread({ state: 'Queued', stage: 'Implement' });
    // Another tab cancelled it; this one has not heard yet.
    host.details(thread.id).thread = { ...host.details(thread.id).thread, state: 'Cancelled', revision: 2 };

    await user.click(screen.getByRole('button', { name: 'Pause' }));

    expect(await screen.findByRole('status')).toHaveTextContent('A task that is Cancelled cannot Pause.');
    expect(await screen.findByText('Cancelled. This task is closed.')).toBeInTheDocument();
  });
});

describe('loading problems', () => {
  it('says why a thread could not be loaded', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const thread = host.addThread(project);
    host.failNext('GET', `/api/threads/${thread.id}`, 500);
    start(host);

    expect(await screen.findByRole('alert')).toHaveTextContent('The request failed (500).');
    expect(host.sources).toHaveLength(0);
  });

  it('says why the factory could not be loaded', async () => {
    const host = new FakeHost();
    host.failNext('GET', '/api/projects', 500, 'The database is not reachable.');
    start(host);

    expect(await screen.findByRole('alert')).toHaveTextContent('The database is not reachable.');
  });
});
