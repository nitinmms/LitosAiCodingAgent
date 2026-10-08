import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import { App } from './App';
import { FakeHost } from './test/fakeHost';

/** The spec stage (m2-architecture.md §5): ask for a specification, approve it, then build it. */

async function withThread(overrides: Parameters<FakeHost['addThread']>[1] = {}) {
  const host = new FakeHost();
  const project = host.addProject();
  const thread = host.addThread(project, overrides);
  host.signedIn = true;
  window.location.hash = '#/threads';
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  const user = userEvent.setup();
  await screen.findByRole('heading', { level: 1, name: thread.title });
  await waitFor(() => expect(host.sources).toHaveLength(1));
  return { host, thread, user };
}

const composer = () => screen.getByRole('textbox', { name: 'Message' });

/** A thread whose spec has been asked for and proposed, revision 1, not yet approved. */
async function proposed() {
  const context = await withThread();
  await context.user.type(composer(), '@factory spec Export the orders list.{Enter}');
  await waitFor(() => expect(context.host.details(context.thread.id).thread.state).toBe('Queued'));
  act(() => void context.host.proposeSpec(context.thread.id));
  const card = within(await screen.findByRole('group', { name: 'Specification revision 1' }));
  return { ...context, card };
}

describe('asking for a specification', () => {
  it('says so on the button, and sends it as typed', async () => {
    const { host, thread, user } = await withThread();

    await user.type(composer(), '@factory spec Export the orders list.');
    expect(screen.getByRole('button', { name: 'Ask for a spec' })).toBeEnabled();
    await user.keyboard('{Enter}');

    await waitFor(() => expect(host.details(thread.id).thread.stage).toBe('Spec'));
    expect(host.sent('POST', `/api/threads/${thread.id}/messages`)[0]!.body).toMatchObject({ text: '@factory spec Export the orders list.' });
    expect(await screen.findByText('spec Export the orders list.')).toBeInTheDocument();
  });

  it('will not send @factory spec with nothing after it', async () => {
    const { host, user } = await withThread();

    await user.type(composer(), '@factory spec ');

    expect(screen.getByRole('button', { name: 'Ask for a spec' })).toBeDisabled();
    expect(screen.getByText(/Say what the specification is for after @factory spec/)).toBeInTheDocument();
    await user.keyboard('{Enter}');
    expect(host.sent('POST', '/messages')).toHaveLength(0);
  });

  it('is offered only before the work starts', async () => {
    const { user } = await withThread({ state: 'AwaitingHumanTesting', stage: 'Handoff', branch: 'factory/x' });

    await user.type(composer(), '@factory spec Something else.');

    expect(screen.getByRole('button', { name: 'Ask for a spec' })).toBeDisabled();
    expect(screen.getByText(/A specification is written before the work starts/)).toBeInTheDocument();
  });

  it('the empty thread mentions it', async () => {
    await withThread();

    expect(screen.getByText(/to have Litos write a/)).toBeInTheDocument();
  });
});

describe('a proposed specification', () => {
  it('shows the revision in full, waiting for you', async () => {
    const { card } = await proposed();

    expect(card.getByText('Awaiting your approval')).toBeInTheDocument();
    expect(card.getByText('Administrators can export the orders list as CSV.')).toBeInTheDocument();
    expect(card.getByText('Administrators see an Export button.')).toBeInTheDocument();
    expect(card.getByText('Others get a 403.')).toBeInTheDocument();
    expect(card.getByText('src/Orders/OrdersController.cs')).toBeInTheDocument();
    expect(card.getByText('Unit tests cover the 403; the button is checked by hand.')).toBeInTheDocument();
    expect(screen.getAllByText('Awaiting you').length).toBeGreaterThan(0);
  });

  it('shows open questions when it has them', async () => {
    const { host, thread, user } = await withThread();
    await user.type(composer(), '@factory spec Export.{Enter}');
    await waitFor(() => expect(host.details(thread.id).thread.state).toBe('Queued'));

    act(() => void host.proposeSpec(thread.id, { openQuestions: ['Include cancelled orders?'] }));

    const card = within(await screen.findByRole('group', { name: 'Specification revision 1' }));
    expect(card.getByText('Include cancelled orders?')).toBeInTheDocument();
  });

  it('holds @factory back until it is approved, and says why', async () => {
    const { host, user } = await proposed();
    const before = host.sent('POST', '/messages').length;

    await user.type(composer(), '@factory Build it.');

    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
    expect(screen.getByText(/Approve specification revision 1, or ask for changes with @factory spec/)).toBeInTheDocument();
    await user.keyboard('{Enter}');
    expect(host.sent('POST', '/messages')).toHaveLength(before);
  });

  it('Ask for changes starts a revision request in the composer', async () => {
    const { card, user } = await proposed();

    await user.click(card.getByRole('button', { name: 'Ask for changes' }));

    expect(composer()).toHaveValue('@factory spec ');
    expect(composer()).toHaveFocus();
    await user.type(composer(), 'Leave cancelled orders out.');
    expect(screen.getByRole('button', { name: 'Ask for a spec' })).toBeEnabled();
  });

  it('a revision supersedes the one before it', async () => {
    const { host, thread, user } = await proposed();
    await user.type(composer(), '@factory spec Leave cancelled orders out.{Enter}');
    await waitFor(() => expect(host.details(thread.id).thread.state).toBe('Queued'));

    act(() => void host.proposeSpec(thread.id, { summary: 'Export without cancelled orders.' }));

    const second = within(await screen.findByRole('group', { name: 'Specification revision 2' }));
    expect(second.getByRole('button', { name: 'Approve' })).toBeEnabled();
    const first = within(screen.getByRole('group', { name: 'Specification revision 1' }));
    expect(first.getByText('Superseded')).toBeInTheDocument();
    expect(first.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
  });
});

describe('approving, then building', () => {
  it('Approve approves the revision read, then Build it puts the request in the composer', async () => {
    const { host, thread, card, user } = await proposed();

    await user.click(card.getByRole('button', { name: 'Approve' }));

    expect(host.sent('POST', `/api/threads/${thread.id}/spec/1/approve`)).toHaveLength(1);
    expect(await card.findByText('Approved')).toBeInTheDocument();
    expect(await screen.findByText('Specification revision 1 approved. Send @factory to build it.')).toBeInTheDocument();
    expect(screen.getByText(/is approved:/)).toBeInTheDocument();

    await user.click(card.getByRole('button', { name: 'Build it' }));
    expect(composer()).toHaveValue('@factory Build the approved specification.');
    await user.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => expect(host.details(thread.id).thread.state).toBe('Queued'));
    expect(host.details(thread.id).thread.stage).toBe('Implement');
    // Once the work is under way, the card is a record, not a choice.
    await waitFor(() => expect(card.queryByRole('button', { name: 'Build it' })).not.toBeInTheDocument());
    expect(card.getByText('Approved')).toBeInTheDocument();
  });

  it('a refused approval says why', async () => {
    const { host, thread, card, user } = await proposed();
    host.failNext('POST', `/api/threads/${thread.id}/spec/1/approve`, 409, 'Revision 1 has been replaced by revision 2. Read that one before approving.');

    await user.click(card.getByRole('button', { name: 'Approve' }));

    expect(await screen.findByText('Revision 1 has been replaced by revision 2. Read that one before approving.')).toBeInTheDocument();
  });

  it('the handoff names the revision it built', async () => {
    const { host, thread, card, user } = await proposed();
    await user.click(card.getByRole('button', { name: 'Approve' }));
    await card.findByText('Approved');
    await user.type(composer(), '@factory Build it.{Enter}');
    await waitFor(() => expect(host.details(thread.id).thread.state).toBe('Queued'));

    act(() => void host.handOff(thread.id, { specificationRevision: 1 }));

    const handoff = within(await screen.findByRole('group', { name: 'Handoff' }));
    expect(handoff.getByText('Built against approved revision 1')).toBeInTheDocument();
  });
});
