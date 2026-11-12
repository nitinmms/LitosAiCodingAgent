import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import { App } from './App';
import { chatStopOf, isRetried, suggestedAllowance } from './domain/task';
import { FakeHost } from './test/fakeHost';

/**
 * A chat answer its own allowance stopped (not the task's budget): the note offers to ask the same
 * question again with a larger allowance. Anyone on the project may; there is no ceiling.
 */

async function withThread(setUp?: (host: FakeHost, threadId: string) => void) {
  const host = new FakeHost();
  const thread = host.addThread(host.addProject());
  setUp?.(host, thread.id);
  host.signedIn = true;
  window.location.hash = '#/threads';
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  const user = userEvent.setup();
  await screen.findByRole('heading', { level: 1, name: thread.title });
  await waitFor(() => expect(host.sources).toHaveLength(1));
  return { host, thread, user };
}

const retryForm = () => screen.findByRole('form', { name: 'Try again with a larger allowance' });

describe('a chat answer stopped by its allowance', () => {
  it('offers half as much again, says what it used, and retries with the allowance given', async () => {
    const { host, user } = await withThread((h, id) => {
      h.say(id, { author: 'User', kind: 'Text', text: 'Draw the pipeline.', payload: { plain: true } });
      h.stopChat(id, 'r-stopped', 100_000, 96_000);
    });

    const form = within(await retryForm());
    const input = form.getByRole('spinbutton', { name: 'New allowance for this answer' });
    expect(input).toHaveValue(150_000);
    expect(form.getByText(/It used 96,000 of 100,000/)).toBeInTheDocument();
    // The note names the answer's allowance, never the task's budget.
    expect(screen.getByText(/this answer's allowance has 4,000 left/)).toBeInTheDocument();

    await user.clear(input);
    await user.type(input, '400000');
    await user.click(form.getByRole('button', { name: 'Try again' }));

    await waitFor(() => expect(host.chatRetries).toEqual([['r-stopped', 400_000]]));
    // Asked again: the note says so, and the offer is gone.
    expect(await screen.findByText('Tried again with an allowance of 400,000 tokens.')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('form', { name: 'Try again with a larger allowance' })).not.toBeInTheDocument());
  });

  it('refuses an allowance that is not above the one that stopped it, before asking the host', async () => {
    const { host, user } = await withThread((h, id) => h.stopChat(id, 'r-stopped', 100_000, 96_000));

    const form = within(await retryForm());
    const input = form.getByRole('spinbutton', { name: 'New allowance for this answer' });
    await user.clear(input);
    await user.type(input, '100000');

    expect(form.getByRole('button', { name: 'Try again' })).toBeDisabled();
    expect(host.chatRetries).toEqual([]);
  });

  it('has no ceiling: a very large allowance is sent as given', async () => {
    const { host, user } = await withThread((h, id) => h.stopChat(id, 'r-stopped'));

    const form = within(await retryForm());
    const input = form.getByRole('spinbutton', { name: 'New allowance for this answer' });
    await user.clear(input);
    await user.type(input, '50000000');
    await user.click(form.getByRole('button', { name: 'Try again' }));

    await waitFor(() => expect(host.chatRetries).toEqual([['r-stopped', 50_000_000]]));
  });

  it('cannot be retried while another answer is being written', async () => {
    const { host, thread } = await withThread((h, id) => h.stopChat(id, 'r-stopped'));
    await retryForm();

    act(() => {
      host.details(thread.id).chatPending = true;
      host.say(thread.id, { author: 'User', kind: 'Text', text: 'Something else?', payload: { plain: true } });
    });

    await waitFor(() => expect(within(screen.getByRole('form', { name: 'Try again with a larger allowance' })).getByRole('button', { name: 'Try again' })).toBeDisabled());
  });

  it('shows a refusal from the host and keeps the offer', async () => {
    const { host, user } = await withThread((h, id) => h.stopChat(id, 'r-stopped'));
    host.failNext('POST', '/retry', 409, 'Only an answer its allowance stopped can be tried again with a larger one.');

    const form = within(await retryForm());
    await user.click(form.getByRole('button', { name: 'Try again' }));

    expect(await screen.findByText('Only an answer its allowance stopped can be tried again with a larger one.')).toBeInTheDocument();
    expect(await retryForm()).toBeInTheDocument();
  });

  it('a stop already retried, and any other failure, offer nothing', async () => {
    await withThread((h, id) => {
      h.stopChat(id, 'r-stopped');
      h.say(id, { author: 'User', kind: 'Status', text: 'Tried again with an allowance of 150,000 tokens.', payload: { retryOf: 'r-stopped', allowance: 150_000 } });
      h.say(id, { author: 'Factory', kind: 'Status', text: 'Litos took longer than 10 minutes to answer, so it was stopped.' });
    });

    await screen.findByText('Litos took longer than 10 minutes to answer, so it was stopped.');
    expect(screen.queryByRole('form', { name: 'Try again with a larger allowance' })).not.toBeInTheDocument();
  });
});

describe('the retry helpers', () => {
  it('read the stop from a note, and only from a note that carries one', () => {
    expect(chatStopOf({ payload: { artifacts: [], chatStop: { runId: 'r1', allowance: 100_000, used: 9 } } })).toEqual({ runId: 'r1', allowance: 100_000, used: 9 });
    expect(chatStopOf({ payload: { artifacts: [] } })).toBeNull();
    expect(chatStopOf({ payload: { chatStop: { runId: 'r1' } } })).toBeNull();
    expect(chatStopOf({ payload: null })).toBeNull();
  });

  it('tell a retried stop by the note that names it', () => {
    expect(isRetried('r1', [{ payload: { retryOf: 'r1' } }])).toBe(true);
    expect(isRetried('r1', [{ payload: { retryOf: 'r2' } }, { payload: null }])).toBe(false);
  });

  it('suggest half as much again, rounded up to ten thousand', () => {
    expect(suggestedAllowance(100_000)).toBe(150_000);
    expect(suggestedAllowance(10)).toBe(10_000);
    expect(suggestedAllowance(123_456)).toBe(190_000);
  });
});
