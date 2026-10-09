import type { LifecycleState, PullRequestState, Stage, Thread, ThreadChange, Turn } from '../api/types';

export const STAGES: readonly Stage[] = ['Discuss', 'Spec', 'Implement', 'Verify', 'Review', 'Handoff', 'Done'];

const STATE_NAMES: Record<LifecycleState, string> = {
  Draft: 'Not delegated',
  Queued: 'Queued',
  Running: 'Working',
  AwaitingDecision: 'Decision needed',
  PausedBudget: 'Budget paused',
  PausedUser: 'Paused',
  Blocked: 'Blocked',
  AwaitingHumanTesting: 'Ready for testing',
  Accepted: 'Accepted',
  Interrupted: 'Interrupted',
  Cancelled: 'Cancelled',
};

export const stateName = (state: LifecycleState): string => STATE_NAMES[state] ?? state;

export type TurnClass = 'you' | 'agent' | 'waiting' | 'done' | 'neutral';

export interface TurnLabel {
  text: string;
  cls: TurnClass;
}

const TURN_LABELS: Record<Turn, TurnLabel> = {
  NotStarted: { text: 'Not started', cls: 'neutral' },
  AwaitingYou: { text: 'Awaiting you', cls: 'you' },
  AwaitingAgent: { text: 'Awaiting agent', cls: 'waiting' },
  AgentWorking: { text: 'Agent working', cls: 'agent' },
  Paused: { text: 'Paused', cls: 'neutral' },
  Done: { text: 'Done', cls: 'done' },
};

/**
 * How the host's turn label (Core/Lifecycle/TurnLabels.cs) is shown. The host decides whose move
 * it is; amber ("you") means the task is stopped until a person does something.
 */
export const turnLabel = (turn: Turn): TurnLabel => TURN_LABELS[turn] ?? { text: turn, cls: 'neutral' };

/**
 * The pill on a task: its turn label, except that a cancelled task says so. Accepted and Cancelled
 * are both "Done" as turns (nobody's move), but a green Done on cancelled work reads as finished.
 */
export const taskLabel = (turn: Turn, state: LifecycleState): TurnLabel =>
  state === 'Cancelled' ? { text: 'Cancelled', cls: 'neutral' } : turnLabel(turn);

/** "Awaiting you" is personal (§7.1): the tasks waiting on a person that this user owns. */
export const awaitsYou = (thread: Thread, userId: string): boolean => thread.turn === 'AwaitingYou' && thread.ownerId === userId;

/** Accepted and Cancelled: nothing more can happen to the task. */
export const isClosed = (state: LifecycleState): boolean => state === 'Accepted' || state === 'Cancelled';

/** The states the one "Resume" action applies to (FactoryApi.cs, POST .../resume). */
export const canResume = (state: LifecycleState): boolean =>
  state === 'PausedUser' || state === 'PausedBudget' || state === 'Blocked' || state === 'Interrupted';

/** Only a running or queued task can be paused (Core/Lifecycle/TaskLifecycle.cs). */
export const canPause = (state: LifecycleState): boolean => state === 'Running' || state === 'Queued';

/**
 * The states in which an @factory message does something (the host's DispatchAsync): it
 * delegates a draft, asks for changes after a handoff, or steers a queued or running task.
 */
export const canMessage = (state: LifecycleState): boolean =>
  state === 'Draft' || state === 'AwaitingHumanTesting' || state === 'Queued' || state === 'Running';

/**
 * The states in which a plain message, without @factory, is answered as chat (the host's
 * ChatAsync): before delegating, after a handoff and after acceptance. While the task is queued
 * or running a plain message is a follow-up for its agent instead.
 */
export const canChat = (state: LifecycleState): boolean =>
  state === 'Draft' || state === 'AwaitingHumanTesting' || state === 'Accepted';

/** What a chat answer has done so far, in a few words: "2 lookups, 3 model calls". Empty before it has done anything. */
export function chatWork(progress: { modelCalls: number; toolCalls: number } | null): string {
  if (!progress) return '';
  const parts: string[] = [];
  if (progress.toolCalls > 0) parts.push(`${progress.toolCalls} ${progress.toolCalls === 1 ? 'lookup' : 'lookups'}`);
  if (progress.modelCalls > 0) parts.push(`${progress.modelCalls} model ${progress.modelCalls === 1 ? 'call' : 'calls'}`);
  return parts.join(', ');
}

/** Whether a message was sent without @factory: the host marks it, since it stores a delegation without its mention. */
export const isPlainMessage = (message: { payload: unknown }): boolean =>
  typeof message.payload === 'object' && message.payload !== null && (message.payload as { plain?: unknown }).plain === true;

/** Anything not closed, a draft included: one that will not be delegated can be closed (TaskLifecycle.cs). */
export const canCancel = (state: LifecycleState): boolean => !isClosed(state);

/**
 * The states from which a change request can be taken back (TaskLifecycle's WithdrawChanges):
 * every state in which its rework run is not executing. A running one is paused first.
 */
export const canWithdraw = (state: LifecycleState): boolean =>
  state === 'Queued' ||
  state === 'AwaitingDecision' ||
  state === 'PausedBudget' ||
  state === 'PausedUser' ||
  state === 'Blocked' ||
  state === 'Interrupted';

/** "Draft PR #12", "PR #12 merged": the factory opens the draft, and people take it from there on GitHub. */
export function pullRequestLabel(number: number, state: PullRequestState | undefined): string {
  switch (state) {
    case 'Merged':
      return `PR #${number} merged`;
    case 'Closed':
      return `PR #${number} closed`;
    case 'Draft':
      return `Draft PR #${number}`;
    default:
      // Open, or not known: say only what is certain.
      return `PR #${number}`;
  }
}

/**
 * Applies a `state` or `usage` event to a thread. Events can arrive out of order across a
 * reconnect, so an event older than what is already shown changes nothing.
 */
export function applyChange(thread: Thread, change: ThreadChange): Thread {
  if (change.revision <= thread.revision) return thread;
  return {
    ...thread,
    state: change.state,
    stage: change.stage,
    turn: change.turn ?? thread.turn,
    title: change.title ?? thread.title,
    typeLabel: change.typeLabel ?? thread.typeLabel,
    stateReason: change.reason,
    revision: change.revision,
    tokensUsed: change.tokensUsed,
    tokensReserved: change.tokensReserved,
    budgetCap: change.budgetCap,
    branch: change.branch,
    pullRequestUrl: change.pullRequestUrl,
  };
}

/** Whichever copy of a thread is newer; the server's revision decides. */
export const newer = (current: Thread | undefined, incoming: Thread): Thread =>
  current && current.revision > incoming.revision ? current : incoming;

const MENTION = '@factory';

/**
 * Mirrors the host's FactoryMention.TryParse: only a mention at the very start delegates, and
 * it needs a request after it. Returns the request, or null when the text does not delegate.
 */
export function parseMention(text: string): string | null {
  const trimmed = text.trimStart();
  if (!trimmed.toLowerCase().startsWith(MENTION)) return null;
  const rest = trimmed.slice(MENTION.length);
  if (rest.length > 0 && !/^\s/.test(rest)) return null;
  const request = rest.trim();
  return request.length > 0 ? request : null;
}

/**
 * Mirrors the host's SpecMention: given what follows @factory, whether it asks for a
 * specification ("spec" as a word of its own), and what for. Null when it asks for the work.
 */
export function parseSpec(request: string): string | null {
  if (!request.toLowerCase().startsWith('spec')) return null;
  const rest = request.slice(4);
  if (rest.length > 0 && !/^\s/.test(rest)) return null;
  return rest.trim();
}

/** "@factory" with nothing after it: neither a delegation nor a question. */
export const isBareMention = (text: string): boolean => text.trim().toLowerCase() === MENTION;

/** Puts the mention in front of a draft that does not have one yet. */
export const withMention = (draft: string): string =>
  draft.trimStart().toLowerCase().startsWith(MENTION) ? draft : `${MENTION} ${draft.trimStart()}`;
