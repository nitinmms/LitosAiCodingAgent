import { describe, expect, it } from 'vitest';
import type { LifecycleState, Thread, ThreadChange, Turn } from '../api/types';
import { elapsed, fmt, initials, pct, shortSha, toneOf, words } from './format';
import { parseRoute, routeHash } from './route';
import {
  applyChange,
  awaitsYou,
  canCancel,
  canChat,
  canMessage,
  canPause,
  canResume,
  canWithdraw,
  chatWork,
  isBareMention,
  isClosed,
  isPlainMessage,
  newer,
  parseMention,
  parseSpec,
  pullRequestLabel,
  stateName,
  taskLabel,
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
  ownerId: 'u1',
  turn: 'AgentWorking',
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
  ptcEnabled: true,
  revision: 5,
  createdAt: '',
  updatedAt: '',
  ...overrides,
});

const change = (overrides: Partial<ThreadChange> = {}): ThreadChange => ({
  state: 'PausedBudget',
  stage: 'Verify',
  turn: 'AwaitingYou',
  title: 'T',
  typeLabel: 'feature',
  reason: 'Out of budget.',
  revision: 6,
  tokensUsed: 900,
  tokensReserved: 50,
  budgetCap: 1000,
  branch: 'factory/x',
  pullRequestUrl: null,
  ...overrides,
});

describe('a chat answer in progress', () => {
  it.each([
    [0, '0:00'],
    [7.9, '0:07'],
    [65, '1:05'],
    [725, '12:05'],
    [-3, '0:00'],
  ])('%d seconds reads %s', (seconds, expected) => expect(elapsed(seconds)).toBe(expected));

  it('says what it has done, and nothing before it has done anything', () => {
    expect(chatWork(null)).toBe('');
    expect(chatWork({ modelCalls: 0, toolCalls: 0 })).toBe('');
    expect(chatWork({ modelCalls: 1, toolCalls: 0 })).toBe('1 model call');
    expect(chatWork({ modelCalls: 3, toolCalls: 1 })).toBe('1 lookup, 3 model calls');
    expect(chatWork({ modelCalls: 2, toolCalls: 4 })).toBe('4 lookups, 2 model calls');
  });
});

describe('chat mirrors the host (m2-architecture.md §5)', () => {
  it('a plain message is answered as chat before delegating, after a handoff and after acceptance only', () => {
    expect(ALL_STATES.filter(canChat).sort()).toEqual(['Accepted', 'AwaitingHumanTesting', 'Draft']);
  });

  it('a message the host marked plain is one; anything else is not', () => {
    expect(isPlainMessage({ payload: { plain: true } })).toBe(true);
    expect(isPlainMessage({ payload: { plain: 'yes' } })).toBe(false);
    expect(isPlainMessage({ payload: {} })).toBe(false);
    expect(isPlainMessage({ payload: null })).toBe(false);
    expect(isPlainMessage({ payload: 'plain' })).toBe(false);
  });

  it.each(['@factory', '  @Factory  ', '@FACTORY\n'])('%j is a bare mention', (text) => expect(isBareMention(text)).toBe(true));

  it.each(['@factory do it', '@factoryx', 'factory', ''])('%j is not a bare mention', (text) => expect(isBareMention(text)).toBe(false));
});

describe('parseSpec mirrors the host (SpecMention)', () => {
  it.each([
    ['spec Export orders', 'Export orders'],
    ['SPEC  Export orders  ', 'Export orders'],
    ['spec', ''],
  ])('%j asks for a specification of %j', (text, expected) => expect(parseSpec(text)).toBe(expected));

  it.each(['specify the export', 'special characters', 'Add a spec page'])('%j asks for the work', (text) =>
    expect(parseSpec(text)).toBeNull(),
  );
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
  // The host decides whose move it is (Core/Lifecycle/TurnLabels.cs); the app only shows it.
  it.each<[Turn, string, string]>([
    ['NotStarted', 'Not started', 'neutral'],
    ['AwaitingAgent', 'Awaiting agent', 'waiting'],
    ['AgentWorking', 'Agent working', 'agent'],
    ['AwaitingYou', 'Awaiting you', 'you'],
    ['Paused', 'Paused', 'neutral'],
    ['Done', 'Done', 'done'],
  ])('%s is "%s" (%s)', (turn, text, cls) => expect(turnLabel(turn)).toEqual({ text, cls }));

  it('a cancelled task says Cancelled; every other state shows its turn', () => {
    expect(taskLabel('Done', 'Cancelled')).toEqual({ text: 'Cancelled', cls: 'neutral' });
    for (const state of ALL_STATES.filter((s) => s !== 'Cancelled')) expect(taskLabel('Done', state)).toEqual(turnLabel('Done'));
    expect(taskLabel('AwaitingYou', 'Blocked')).toEqual(turnLabel('AwaitingYou'));
  });

  it('"Awaiting you" is only what this user owns', () => {
    expect(awaitsYou(thread({ turn: 'AwaitingYou', ownerId: 'u1' }), 'u1')).toBe(true);
    expect(awaitsYou(thread({ turn: 'AwaitingYou', ownerId: 'u2' }), 'u1')).toBe(false);
    expect(awaitsYou(thread({ turn: 'AgentWorking', ownerId: 'u1' }), 'u1')).toBe(false);
  });

  it('a state event carries the new turn label, title and type', () => {
    const next = applyChange(thread(), change({ turn: 'AwaitingYou', title: 'Renamed', typeLabel: 'bug' }));

    expect([next.turn, next.title, next.typeLabel]).toEqual(['AwaitingYou', 'Renamed', 'bug']);
  });

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
  it('cancel: anything not closed, a draft included', () =>
    expect(allowed(canCancel)).toEqual([
      'Draft',
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
  it('withdraw a change request: whenever its run is not executing', () =>
    expect(allowed(canWithdraw)).toEqual(['Queued', 'AwaitingDecision', 'PausedBudget', 'PausedUser', 'Blocked', 'Interrupted']));
});

describe('pullRequestLabel', () => {
  it.each([
    ['Draft', 'Draft PR #7'],
    ['Open', 'PR #7'],
    ['Merged', 'PR #7 merged'],
    ['Closed', 'PR #7 closed'],
    ['Unknown', 'PR #7'],
    [undefined, 'PR #7'],
  ] as const)('%s is "%s"', (state, label) => expect(pullRequestLabel(7, state)).toBe(label));
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
    expect(['Unavailable', 'NoTests', 'DidNotFinish'].map(toneOf)).toEqual(['you', 'you', 'you']);
    expect(['NotRun', 'NotMeasured', 'NotApplicable', 'anything'].map(toneOf)).toEqual(['neutral', 'neutral', 'neutral', 'neutral']);
  });
});

describe('routes', () => {
  it.each([
    // The app opens on the board.
    ['', { view: 'board' }],
    ['#/board', { view: 'board' }],
    ['#/threads', { view: 'threads', threadId: null }],
    ['#/threads/abc-123', { view: 'threads', threadId: 'abc-123' }],
    ['#/projects', { view: 'projects' }],
    ['#/people', { view: 'people' }],
    ['#/invite/AbC-12_x', { view: 'invite', token: 'AbC-12_x' }],
    // A link without its token is no invitation.
    ['#/invite', { view: 'threads', threadId: null }],
    ['#/nonsense', { view: 'threads', threadId: null }],
  ])('%j', (hash, expected) => expect(parseRoute(hash)).toEqual(expected));

  it('round-trips', () => {
    for (const route of [
      { view: 'board' },
      { view: 'threads', threadId: null },
      { view: 'threads', threadId: 'a b' },
      { view: 'projects' },
      { view: 'people' },
      { view: 'invite', token: 'AbC-12_x' },
    ] as const)
      expect(parseRoute(routeHash(route))).toEqual(route);
  });
});
