import type { Thread } from '../api/types';
import { awaitsYou } from './task';

/**
 * Which of this user's tasks have just started waiting on them: the moment for the "meal ready"
 * sound. The host decides when a task awaits a person (its turn label: a decision, a handoff to
 * test, a block, a paused budget); this only notices the change. With no `previous` set, the first
 * look after loading, nothing has "just" arrived: tasks already waiting are not announced again.
 */
export function newlyAwaiting(
  previous: ReadonlySet<string> | null,
  threads: readonly Thread[],
  userId: string,
): { awaiting: Set<string>; arrived: string[] } {
  const awaiting = new Set(threads.filter((t) => awaitsYou(t, userId)).map((t) => t.id));
  const arrived = previous === null ? [] : [...awaiting].filter((id) => !previous.has(id));
  return { awaiting, arrived };
}
