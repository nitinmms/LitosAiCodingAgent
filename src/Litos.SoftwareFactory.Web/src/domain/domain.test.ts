import { describe, expect, it } from 'vitest';
import type { LifecycleState, Thread, ThreadChange } from '../api/types';
import { fmt, initials, pct, shortSha, toneOf, words } from './format';
import { parseRoute, routeHash } from './route';
import {
  applyChange,
  canCancel,
  canMessage,
  canPause,
  canResume,
  isClosed,
  newer,
  parseMention,
  stateName,
  turnLabel,
  withMention,
} from './task';

const ALL_STATES: LifecycleState[] = [
  'Draft',
  'Queued',
  'Running',
  'AwaitingDecision',
  'PausedBudget',
  'PausedUser',
  'Blocked',
  'AwaitingHumanTesting',
  'Accepted',
  'Interrupted',
  'Cancelled',
];

const thread = (overrides: Partial<Thread> = {}): Thread => ({
  id: 't',
  projectId: 'p',
  title: 'T',
  typeLabel: 'feature',
  stage: 'Implement',
  state: 'Running',
  stateReason: null,
  budgetCap: 1000,
  tokensUsed: 10,
  tokensReserved: 0,
  branch: null,
  pullRequestNumber: null,
  pullRequestUrl: null,
  provider: 'openrouter',
  model: 'm',
  budgetPrecision: 'strict',
  revision: 5,
  createdAt: '',
  updatedAt: '',
  ...overrides,
});

const change = (overrides: Partial<ThreadChange> = {}): ThreadChange => ({
  state: 'PausedBudget',
  stage: 'Verify',
  reason: 'Out of budget.',
  revision: 6,
  tokensUsed: 900,
  tokensReserved: 50,
  budgetCap: 1000,
  branch: 'factory/x',
  pullRequestUrl: null,
  ...overrides,
});

describe('parseMention mirrors the host', () => {
  // The same cases as FactoryMentionTests in Litos.SoftwareFactory.Host.Tests.
  it.each([
    ['@factory Add CSV export', 'Add CSV export'],
    ['  @factory   fix the bug  ', 'fix the bug'],
    ['@FACTORY do it', 'do it'],
    ['@factory\nmultiple\nlines', 'multiple\nlines'],
  ])('%j delegates', (text, expected) => expect(parseMention(text)).toBe(expected));

  it.each(['', 'Add CSV export', 'please @factory do it', '@factoryfoo do it', '@factory', '@factory   ', '> @factory quoted', '`@factory` code'])(
    '%j does not',
    (text) => expect(parseMention(text)).toBeNull(),
  );
});

describe('withMention', () => {
  it('puts the mention in front of a plain draft', () => expect(withMention('fix it')).toBe('@factory fix it'));
  it('starts an empty draft', () => expect(withMention('')).toBe('@factory '));
  it('leaves a draft that already has one', () => expect(withMention('@factory fix it')).toBe('@factory fix it'));
});

describe('turnLabel', () => {
  it.each<[LifecycleState, string, string]>([
    ['Draft', 'Not delegated', 'neutral'],
    ['Queued', 'Awaiting agent', 'waiting'],
    ['Running', 'Agent working', 'agent'],
    ['AwaitingDecision', 'Awaiting you', 'you'],
    ['PausedBudget', 'Awaiting you', 'you'],
    ['PausedUser', 'Paused', 'neutral'],
    ['Blocked', 'Awaiting you', 'you'],
    ['AwaitingHumanTesting', 'Awaiting you', 'you'],
    ['Interrupted', 'Awaiting you', 'you'],
    ['Accepted', 'Done', 'done'],
    ['Cancelled', 'Cancelled', 'neutral'],
  ])('%s is "%s" (%s)', (state, text, cls) => expect(turnLabel(state)).toEqual({ text, cls }));

  it('names every state', () => {
    for (const state of ALL_STATES) expect(stateName(state)).not.toBe('');
    expect(stateName('AwaitingHumanTesting')).toBe('Ready for testing');
  });
});

describe('what each state allows matches the host', () => {
  const allowed = (rule: (s: LifecycleState) => boolean) => ALL_STATES.filter(rule);

  it('pause: running or queued', () => expect(allowed(canPause)).toEqual(['Queued', 'Running']));
  it('resume: the four stopped states', () =>
    expect(allowed(canResume)).toEqual(['PausedBudget', 'PausedUser', 'Blocked', 'Interrupted']));
  it('message: draft, queued, running, and after a handoff', () =>
    expect(allowed(canMessage)).toEqual(['Draft', 'Queued', 'Running', 'AwaitingHumanTesting']));
  it('cancel: anything delegated and not closed', () =>
    expect(allowed(canCancel)).toEqual([
      'Queued',
      'Running',
      'AwaitingDecision',
      'PausedBudget',
      'PausedUser',
      'Blocked',
      'AwaitingHumanTesting',
      'Interrupted',
    ]));
  it('closed: accepted or cancelled', () => expect(allowed(isClosed)).toEqual(['Accepted', 'Cancelled']));
});

describe('applyChange', () => {
  it('takes the state, stage, reason and budget figures from a newer event', () => {
    expect(applyChange(thread(), change())).toMatchObject({
      state: 'PausedBudget',
      stage: 'Verify',
      stateReason: 'Out of budget.',
      revision: 6,
      tokensUsed: 900,
      tokensReserved: 50,
      branch: 'factory/x',
      title: 'T',
    });
  });

  it('ignores an event that is not newer than what is shown', () => {
    const shown = thread();
    expect(applyChange(shown, change({ revision: 5 }))).toBe(shown);
    expect(applyChange(shown, change({ revision: 2 }))).toBe(shown);
  });
});

describe('newer', () => {
  it('keeps the higher revision', () => {
    const old = thread({ revision: 3 });
    const fresh = thread({ revision: 4 });
    expect(newer(old, fresh)).toBe(fresh);
    expect(newer(fresh, old)).toBe(fresh);
    expect(newer(undefined, old)).toBe(old);
  });

  it('prefers the incoming copy at the same revision', () => {
    const a = thread();
    const b = thread();
    expect(newer(a, b)).toBe(b);
  });
});

describe('format', () => {
  it('groups thousands', () => expect(fmt(1234567)).toBe('1,234,567'));
  it('rounds a percentage to one decimal', () => {
    expect(pct(86.25)).toBe('86.3%');
    expect(pct(80)).toBe('80%');
  });
  it.each([
    ['BelowThreshold', 'Below threshold'],
    ['NotRun', 'Not run'],
    ['Passed', 'Passed'],
    ['strict', 'Strict'],
    ['FindingsFixed', 'Findings fixed'],
  ])('words(%s) is %s', (name, expected) => expect(words(name)).toBe(expected));
  it.each([
    ['Priya Raman', 'PR'],
    ['admin', 'AD'],
    ['Ana Maria Lopez', 'AL'],
    ['', '?'],
  ])('initials(%j) is %s', (name, expected) => expect(initials(name)).toBe(expected));
  it('shortens a commit to seven characters', () => expect(shortSha('9f8e7d6c5b4a3928')).toBe('9f8e7d6'));

  it('only something that ran and passed is green', () => {
    expect(['Passed', 'Met', 'Clean', 'FindingsFixed'].map(toneOf)).toEqual(['done', 'done', 'done', 'done']);
    expect(['Failed', 'BelowThreshold', 'FindingsOpen'].map(toneOf)).toEqual(['bad', 'bad', 'bad']);
    expect(['Unavailable', 'NoTests'].map(toneOf)).toEqual(['you', 'you']);
    expect(['NotRun', 'NotMeasured', 'NotApplicable', 'anything'].map(toneOf)).toEqual(['neutral', 'neutral', 'neutral', 'neutral']);
  });
});

describe('routes', () => {
  it.each([
    ['', { view: 'threads', threadId: null }],
    ['#/threads', { view: 'threads', threadId: null }],
    ['#/threads/abc-123', { view: 'threads', threadId: 'abc-123' }],
    ['#/projects', { view: 'projects' }],
    ['#/nonsense', { view: 'threads', threadId: null }],
  ])('%j', (hash, expected) => expect(parseRoute(hash)).toEqual(expected));

  it('round-trips', () => {
    for (const route of [{ view: 'threads', threadId: null }, { view: 'threads', threadId: 'a b' }, { view: 'projects' }] as const)
      expect(parseRoute(routeHash(route))).toEqual(route);
  });
});
