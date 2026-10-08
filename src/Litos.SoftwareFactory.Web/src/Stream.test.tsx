import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import { App } from './App';
import { FakeHost, PASSWORD } from './test/fakeHost';

// Event-stream resilience (m2-architecture.md §7): a drop shows "reconnecting"; a stream the host
// closes for good is replaced from a fresh snapshot, which also finds an expired session.

/** The first fresh snapshot is taken a second after the host closed the stream. */
const RETRY = { timeout: 3_000 };

function start(host: FakeHost, hash: string) {
  host.signedIn = true;
  window.location.hash = hash;
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  return userEvent.setup();
}

/** A thread on screen with its stream open. */
async function onThread() {
  const host = new FakeHost();
  const project = host.addProject();
  const thread = host.addThread(project, { state: 'Running', stage: 'Implement' });
  const user = start(host, `#/threads/${thread.id}`);
  await screen.findByRole('heading', { level: 1, name: thread.title });
  await waitFor(() => expect(host.sources).toHaveLength(1));
  act(() => host.sources[0]!.connect());
  return { host, thread, user };
}

const reconnecting = () => screen.queryAllByRole('status').filter((s) => s.textContent === 'Reconnecting…');
const openThreadStreams = (host: FakeHost) => host.sources.filter((s) => !s.closed);
/** How many times the thread itself was read (not its usage or pull request). */
const threadReads = (host: FakeHost, id: string) => host.sent('GET', `/api/threads/${id}`).filter((r) => r.path === `/api/threads/${id}`).length;

describe("a thread's stream", () => {
  it('says it is reconnecting while dropped, and stops saying so once it is back', async () => {
    const { host } = await onThread();

    act(() => host.sources[0]!.drop());
    expect(reconnecting()).toHaveLength(1);

    act(() => host.sources[0]!.connect());
    expect(reconnecting()).toHaveLength(0);
    // The browser reconnected that same stream: nothing was reopened.
    expect(host.sources).toHaveLength(1);
  });

  it('ignores an event replayed after a reconnect', async () => {
    const { host, thread } = await onThread();
    host.say(thread.id, { text: 'Running unit tests.' });
    await screen.findByText('Running unit tests.');
    await new Promise((resolve) => setTimeout(resolve, 300));
    const reads = threadReads(host, thread.id);

    // The same event again, as a replay would send it: it is not fetched for a second time.
    act(() => host.sources[0]!.emit('message', {}, host.lastSequence));
    await new Promise((resolve) => setTimeout(resolve, 300));

    expect(threadReads(host, thread.id)).toBe(reads);
  });

  it('closed by the host, it is replaced from a fresh snapshot that shows what changed meanwhile', async () => {
    const { host, thread } = await onThread();

    act(() => host.sources[0]!.refuse());
    expect(reconnecting()).toHaveLength(1);
    // While it was down, the task moved on; no event reached the page.
    host.details(thread.id).thread = { ...host.details(thread.id).thread, state: 'PausedUser', turn: 'Paused', revision: 99 };
    host.details(thread.id).eventCursor = 77;

    await waitFor(() => expect(openThreadStreams(host)).toHaveLength(1), RETRY);
    const reopened = openThreadStreams(host)[0]!;
    expect(reopened.url).toBe(`/api/threads/${thread.id}/events?after=77`);
    expect(await screen.findByRole('button', { name: 'Resume' })).toBeInTheDocument();

    act(() => reopened.connect());
    expect(reconnecting()).toHaveLength(0);
  });

  it('closed because the session expired, it returns to sign-in, then picks up again after signing in', async () => {
    const { host, thread, user } = await onThread();

    host.signedIn = false;
    act(() => host.sources[0]!.refuse());

    expect(await screen.findByLabelText('Username', {}, RETRY)).toBeInTheDocument();
    expect(openThreadStreams(host)).toHaveLength(0);

    await user.type(screen.getByLabelText('Username'), 'admin');
    await user.type(screen.getByLabelText('Password'), PASSWORD);
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    // Back on the same thread, from a fresh snapshot, with a new stream from its cursor.
    expect(await screen.findByRole('heading', { level: 1, name: thread.title })).toBeInTheDocument();
    await waitFor(() => expect(openThreadStreams(host)).toHaveLength(1));
    expect(openThreadStreams(host)[0]!.url).toBe(`/api/threads/${thread.id}/events?after=${host.details(thread.id).eventCursor}`);
    expect(reconnecting()).toHaveLength(0);
  });

  it('closed because the thread is gone, it says so and stops trying', async () => {
    const { host, thread } = await onThread();

    host.threads.delete(thread.id);
    act(() => host.sources[0]!.refuse());

    expect(await screen.findByText('That no longer exists.', {}, RETRY)).toBeInTheDocument();
    await new Promise((resolve) => setTimeout(resolve, 2_500));
    // The first load and the one fresh snapshot that found it gone; no more after that.
    expect(threadReads(host, thread.id)).toBe(2);
    expect(openThreadStreams(host)).toHaveLength(0);
  });

  it('keeps trying while the host cannot be reached, then picks up', async () => {
    const { host, thread } = await onThread();

    host.failNext('GET', `/api/threads/${thread.id}`, 503);
    act(() => host.sources[0]!.refuse());

    // The first snapshot fails; the second, two seconds later, succeeds.
    await waitFor(() => expect(openThreadStreams(host)).toHaveLength(1), { timeout: 5_000 });
    expect(screen.getByRole('heading', { level: 1, name: thread.title })).toBeInTheDocument();
  }, 10_000);
});

describe("the board's stream", () => {
  async function onBoard() {
    const host = new FakeHost();
    const project = host.addProject();
    const kept = host.addThread(project, { title: 'Add CSV export' });
    const gone = host.addThread(project, { title: 'Fix the footer' });
    const user = start(host, '#/board');
    await screen.findByText('Fix the footer');
    await waitFor(() => expect(host.boardSources).toHaveLength(1));
    act(() => host.boardSources[0]!.connect());
    return { host, kept, gone, user };
  }

  it('says it is reconnecting while dropped', async () => {
    const { host } = await onBoard();

    act(() => host.boardSources[0]!.drop());
    expect(reconnecting()).toHaveLength(1);
    act(() => host.boardSources[0]!.connect());
    expect(reconnecting()).toHaveLength(0);
  });

  it('closed by the host, it lists the threads again and listens from the new cursor', async () => {
    const { host, kept, gone } = await onBoard();

    act(() => host.boardSources[0]!.refuse());
    // A thread the user can no longer see is gone from the fresh list.
    host.threads.delete(gone.id);
    host.details(kept.id).thread = { ...host.details(kept.id).thread, title: 'Export orders as CSV', revision: 50 };

    await waitFor(() => expect(host.boardSources.filter((s) => !s.closed)).toHaveLength(1), RETRY);
    expect(host.boardSources.filter((s) => !s.closed)[0]!.url).toBe(`/api/events?after=${host.lastSequence}`);
    expect(await screen.findByText('Export orders as CSV')).toBeInTheDocument();
    expect(screen.queryByText('Fix the footer')).not.toBeInTheDocument();
  });

  it('closed because the session expired, it returns to sign-in', async () => {
    const { host } = await onBoard();

    host.signedIn = false;
    act(() => host.boardSources[0]!.refuse());

    expect(await screen.findByLabelText('Username', {}, RETRY)).toBeInTheDocument();
    expect(host.boardSources.filter((s) => !s.closed)).toHaveLength(0);
  });
});
