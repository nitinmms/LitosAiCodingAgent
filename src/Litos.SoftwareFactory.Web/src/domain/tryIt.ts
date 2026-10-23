import type { HandoffEvidence } from '../api/types';

/** Where a tester who has never seen the branch gets it: the links and the commands to copy. */
export interface TryItGuide {
  /** "owner/repo", as GitHub names it. */
  repository: string;
  repositoryUrl: string;
  branchUrl: string;
  /** The branch against the default branch; null when the handoff does not name one. */
  compareUrl: string | null;
  readmeUrl: string;
  /** From nothing: a new folder for this task. */
  freshCommands: string[];
  /** In a clone the tester already has. */
  existingCloneCommands: string[];
}

/**
 * The guide for a handoff, or null for one recorded before handoffs named their repository.
 * The folder is named for the repository and the task, so it sits beside the tester's other
 * copies without clashing with one of them.
 */
export function tryItGuide(e: HandoffEvidence, threadId: string): TryItGuide | null {
  const repositoryUrl = e.repositoryUrl?.replace(/\/+$/, '');
  if (!repositoryUrl || !e.cloneUrl || !/^https:\/\//i.test(repositoryUrl)) return null;

  const repository = repositoryUrl.replace(/^https:\/\/[^/]+\//i, '');
  const name = repository.split('/').pop() || 'repository';
  const folder = `${name}-${threadId.replace(/-/g, '').slice(0, 4)}`;
  const branchPath = e.branch.split('/').map(encodeURIComponent).join('/');
  const branchUrl = `${repositoryUrl}/tree/${branchPath}`;

  return {
    repository,
    repositoryUrl,
    branchUrl,
    compareUrl: e.defaultBranch ? `${repositoryUrl}/compare/${encodeURIComponent(e.defaultBranch)}...${branchPath}` : null,
    readmeUrl: `${branchUrl}#readme`,
    freshCommands: [`git clone -b ${e.branch} ${e.cloneUrl} ${folder}`, `cd ${folder}`],
    existingCloneCommands: ['git fetch origin', `git switch ${e.branch}`],
  };
}
