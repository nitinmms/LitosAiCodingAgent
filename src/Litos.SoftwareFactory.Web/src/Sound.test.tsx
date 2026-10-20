import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createApi } from './api/client';
import type { Thread } from './api/types';
import { App } from './App';
import { newlyAwaiting } from './domain/attention';
import { setSoundEnabled, soundEnabled } from './domain/sound';
import { FakeHost } from './test/fakeHost';

beforeEach(() => window.localStorage.clear());

/** Starts the app on the board with a spy for the sound; resolves once the board's stream is open. */
async function start(host: FakeHost) {
  host.signedIn = true;
  const play = vi.fn();
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} playAttentionSound={play} />);
  await screen.findByRole('heading', { name: 'Board' });
  await waitFor(() => expect(host.boardSources).toHaveLength(1));
  return { play, user: userEvent.setup() };
}

/** The thread as the host would send it after a change: the next revision, in the new state. */
const changed = (thread: Thread, state: Thread['state'], turn: Thread['turn']): Thread => ({ ...thread, state, turn, revision: thread.revision + 1 });

describe('the "meal ready" sound', () => {
  it('rings when one of your tasks starts waiting on you', async () => {
    const host = new FakeHost();
    const task = host.addThread(host.addProject(), { state: 'Running', stage: 'Implement' });
    const { play } = await start(host);

    act(() => host.announce(changed(task, 'AwaitingDecision', 'AwaitingYou')));

    await screen.findByRole('button', { name: '1 awaiting you' });
    expect(play).toHaveBeenCalledTimes(1);
  });

  it('does not ring for tasks already waiting when the board loads, or for the same wait again', async () => {
    const host = new FakeHost();
    const waiting = host.addThread(host.addProject(), { state: 'AwaitingHumanTesting', stage: 'Handoff' });
    const { play } = await start(host);
    await screen.findByRole('button', { name: '1 awaiting you' });

    // Still waiting, with a newer revision (a comment, a usage update): not a new wait.
    act(() => host.announce({ ...waiting, revision: waiting.revision + 1 }));

    expect(play).not.toHaveBeenCalled();
  });

  it("does not ring for someone else's task", async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const ben = host.addPerson({ userName: 'ben', displayName: 'Ben Okafor', projectIds: [project.id] });
    const bens = host.addThread(project, { state: 'Running', stage: 'Implement', ownerId: ben.id });
    const { play } = await start(host);

    act(() => host.announce(changed(bens, 'Blocked', 'AwaitingYou')));

    await waitFor(() => expect(screen.getByRole('button', { name: '0 awaiting you' })).toBeDisabled());
    expect(play).not.toHaveBeenCalled();
  });

  it('is turned off and on from the top bar, which remembers it, and plays once when turned on', async () => {
    const host = new FakeHost();
    const task = host.addThread(host.addProject(), { state: 'Running', stage: 'Implement' });
    const { play, user } = await start(host);

    await user.click(screen.getByRole('button', { name: 'Sound on' }));
    expect(screen.getByRole('button', { name: 'Sound off' })).toHaveAttribute('aria-pressed', 'false');
    expect(soundEnabled()).toBe(false);

    act(() => host.announce(changed(task, 'PausedBudget', 'AwaitingYou')));
    await screen.findByRole('button', { name: '1 awaiting you' });
    expect(play).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: 'Sound off' }));
    expect(play).toHaveBeenCalledTimes(1);
    expect(soundEnabled()).toBe(true);
  });
});

describe('newlyAwaiting', () => {
  const thread = (id: string, turn: Thread['turn'], ownerId = 'me') => ({ id, turn, ownerId }) as Thread;

  it('announces nothing on the first look, then only tasks that started waiting since the last', () => {
    const first = newlyAwaiting(null, [thread('a', 'AwaitingYou'), thread('b', 'AgentWorking')], 'me');
    expect(first.arrived).toEqual([]);

    const second = newlyAwaiting(first.awaiting, [thread('a', 'AwaitingYou'), thread('b', 'AwaitingYou'), thread('c', 'AwaitingYou', 'ben')], 'me');
    expect(second.arrived).toEqual(['b']);

    // Handled, then waiting again later: a new wait.
    const handled = newlyAwaiting(second.awaiting, [thread('a', 'AgentWorking'), thread('b', 'AwaitingYou')], 'me');
    expect(newlyAwaiting(handled.awaiting, [thread('a', 'AwaitingYou'), thread('b', 'AwaitingYou')], 'me').arrived).toEqual(['a']);
  });
});

describe('the sound setting', () => {
  it('is on by default, and survives storage that throws', () => {
    expect(soundEnabled()).toBe(true);
    setSoundEnabled(false);
    expect(soundEnabled()).toBe(false);

    const broken = { getItem: () => { throw new Error('blocked'); }, setItem: () => { throw new Error('blocked'); } };
    expect(soundEnabled(broken)).toBe(true);
    expect(() => setSoundEnabled(true, broken)).not.toThrow();
  });
});
