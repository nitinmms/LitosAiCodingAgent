import { describe, expect, it } from 'vitest';
import type { BlockerLink, Thread } from '../api/types';
import {
  blockerCandidates,
  blockerStatus,
  canArrange,
  daysWaited,
  dropMove,
  notStarted,
  placesOf,
  queueOf,
  stepMove,
} from './runOrder';

const thread = (id: string, overrides: Partial<Thread> = {}): Thread =>
  ({
    id,
    projectId: 'p',
    ownerId: 'asha',
    title: id,
    state: 'Queued',
    stage: 'Implement',
    branch: null,
    queueRank: 1000,
    pullRequestMergedAt: null,
    pullRequestClosedAt: null,
    ...overrides,
  }) as Thread;

const link = (overrides: Partial<BlockerLink> = {}): BlockerLink => ({
  threadId: 'b',
  title: 'Add column',
  ownerId: 'asha',
  state: 'Running',
  turn: 'AgentWorking',
  pullRequestNumber: null,
  pullRequestUrl: null,
  pullRequestMergedAt: null,
  pullRequestClosedAt: null,
  since: '2026-11-01T09:00:00Z',
  releasedAt: null,
  releaseReason: null,
  ...overrides,
});

describe('moving a task with ↑ and ↓', () => {
  const queue = [thread('a'), thread('b', { ownerId: 'ben' }), thread('c'), thread('d', { ownerId: 'ben' })];

  it('moves it one place for an Admin', () => {
    expect(stepMove(queue, 'c', -1, 'asha', true)).toEqual({ placement: 'after', otherThreadId: 'a' });
    expect(stepMove(queue, 'b', -1, 'asha', true)).toEqual({ placement: 'first' });
    expect(stepMove(queue, 'b', 1, 'asha', true)).toEqual({ placement: 'after', otherThreadId: 'c' });
  });

  it('goes nowhere past either end', () => {
    expect(stepMove(queue, 'a', -1, 'asha', true)).toBeNull();
    expect(stepMove(queue, 'd', 1, 'asha', true)).toBeNull();
    expect(stepMove(queue, 'missing', 1, 'asha', true)).toBeNull();
  });

  it("swaps a Member's task with their own nearest one, so nobody else's moves", () => {
    expect(stepMove(queue, 'd', -1, 'ben', false)).toEqual({ placement: 'swap', otherThreadId: 'b' });
    expect(stepMove(queue, 'b', 1, 'ben', false)).toEqual({ placement: 'swap', otherThreadId: 'd' });
    expect(stepMove(queue, 'b', -1, 'ben', false)).toBeNull();
  });

  it("does not move someone else's task for a Member", () => {
    expect(stepMove(queue, 'c', -1, 'ben', false)).toBeNull();
  });
});

describe('dropping a dragged task', () => {
  const queue = [thread('a'), thread('b'), thread('c')];

  it('puts it in front of the task it is dropped on', () => {
    expect(dropMove(queue, 'c', 0)).toEqual({ placement: 'first' });
    expect(dropMove(queue, 'c', 1)).toEqual({ placement: 'after', otherThreadId: 'a' });
    expect(dropMove(queue, 'a', 2)).toEqual({ placement: 'after', otherThreadId: 'b' });
  });

  it('puts it last when dropped past the end', () => {
    expect(dropMove(queue, 'a', 3)).toEqual({ placement: 'end' });
  });

  it('does nothing when dropped where it already is', () => {
    expect(dropMove(queue, 'b', 1)).toBeNull();
    expect(dropMove(queue, 'b', 2)).toBeNull();
  });
});

describe('who arranges a task', () => {
  it('is its owner or an Admin', () => {
    expect(canArrange(thread('a'), 'asha', false)).toBe(true);
    expect(canArrange(thread('a'), 'ben', false)).toBe(false);
    expect(canArrange(thread('a'), 'ben', true)).toBe(true);
  });

  it('can make a task wait only before it starts', () => {
    expect(notStarted(thread('a', { state: 'Draft' }))).toBe(true);
    expect(notStarted(thread('a'))).toBe(true);
    // A rework is queued on a branch of its own already.
    expect(notStarted(thread('a', { branch: 'factory/a' }))).toBe(false);
    expect(notStarted(thread('a', { state: 'Running' }))).toBe(false);
  });
});

describe('what a task can wait for', () => {
  it('is another live, unmerged task of the same project whose pull request is not closed', () => {
    const threads = [
      thread('self'),
      thread('fine'),
      thread('other-project', { projectId: 'q' }),
      thread('cancelled', { state: 'Cancelled' }),
      thread('merged', { state: 'Accepted', pullRequestMergedAt: '2026-11-01T09:00:00Z' }),
      thread('closed', { state: 'Accepted', pullRequestClosedAt: '2026-11-01T09:00:00Z' }),
      thread('already'),
      thread('accepted', { state: 'Accepted' }),
    ];

    expect(blockerCandidates(threads, 'p', 'self', ['already']).map((t) => t.id)).toEqual(['fine', 'accepted']);
  });
});

describe("a project's queue, as New thread offers it", () => {
  it('is its queued work in rank order, without specs or other projects', () => {
    const threads = [
      thread('b', { queueRank: 2000 }),
      thread('a', { queueRank: 1000 }),
      thread('spec', { stage: 'Spec', queueRank: null }),
      thread('elsewhere', { projectId: 'q' }),
      thread('running', { state: 'Running' }),
      thread('legacy', { queueRank: null }),
    ];

    expect(queueOf(threads, 'p').map((t) => t.id)).toEqual(['legacy', 'a', 'b']);
  });
});

describe('where a blocking task stands', () => {
  it.each([
    [{ pullRequestMergedAt: '2026-11-02T09:00:00Z' }, 'merged'],
    [{ pullRequestClosedAt: '2026-11-02T09:00:00Z', pullRequestNumber: 44 }, 'PR #44 closed without merging'],
    [{ state: 'Accepted' as const, pullRequestNumber: 44 }, 'accepted, PR #44 not merged yet'],
    [{ state: 'Accepted' as const }, 'accepted, not marked merged yet'],
    [{ state: 'AwaitingHumanTesting' as const, pullRequestNumber: 44 }, 'PR #44 ready for testing'],
    [{ state: 'Running' as const }, 'agent working'],
    [{ state: 'Queued' as const }, 'queued'],
    [{ state: 'Draft' as const }, 'not delegated yet'],
    [{ state: 'AwaitingDecision' as const }, 'waiting on a person'],
  ])('%o says "%s"', (overrides, expected) => {
    expect(blockerStatus(link(overrides))).toBe(expected);
  });
});

describe('how long a task has waited', () => {
  it('is counted in whole days, never below none', () => {
    const since = '2026-11-01T09:00:00Z';
    expect(daysWaited(since, new Date('2026-11-01T20:00:00Z'))).toBe(0);
    expect(daysWaited(since, new Date('2026-11-03T09:00:00Z'))).toBe(2);
    expect(daysWaited(since, new Date('2026-10-30T09:00:00Z'))).toBe(0);
  });
});

describe('places in line', () => {
  it('number the queue from 1', () => {
    const entry = (id: string) => ({ thread: thread(id), waitsFor: [], waitedOnBy: 0, isRework: false });
    expect(placesOf([entry('a'), entry('b')])).toEqual({ a: 1, b: 2 });
  });
});
