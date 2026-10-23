import { describe, expect, it } from 'vitest';
import { evidence } from '../test/fakeHost';
import { tryItGuide } from './tryIt';

const THREAD = '3f9a1c2e-0000-4000-8000-000000000000';

const named = evidence({
  branch: 'factory/3f9a-caption-count',
  repositoryUrl: 'https://github.com/acme/stories',
  cloneUrl: 'https://github.com/acme/stories.git',
  defaultBranch: 'main',
});

describe('the guide to trying a handoff', () => {
  it('links the branch, its comparison and its README on GitHub', () => {
    const guide = tryItGuide(named, THREAD)!;

    expect(guide.repository).toBe('acme/stories');
    expect(guide.branchUrl).toBe('https://github.com/acme/stories/tree/factory/3f9a-caption-count');
    expect(guide.compareUrl).toBe('https://github.com/acme/stories/compare/main...factory/3f9a-caption-count');
    expect(guide.readmeUrl).toBe('https://github.com/acme/stories/tree/factory/3f9a-caption-count#readme');
  });

  it('clones into a folder named for the repository and the task', () => {
    expect(tryItGuide(named, THREAD)!.freshCommands).toEqual([
      'git clone -b factory/3f9a-caption-count https://github.com/acme/stories.git stories-3f9a',
      'cd stories-3f9a',
    ]);
  });

  it('switches an existing clone to the branch', () => {
    expect(tryItGuide(named, THREAD)!.existingCloneCommands).toEqual(['git fetch origin', 'git switch factory/3f9a-caption-count']);
  });

  it('has no comparison without a default branch', () => {
    expect(tryItGuide({ ...named, defaultBranch: null }, THREAD)!.compareUrl).toBeNull();
  });

  it('is absent from a handoff recorded before handoffs named their repository', () => {
    expect(tryItGuide(evidence(), THREAD)).toBeNull();
    expect(tryItGuide({ ...named, cloneUrl: null }, THREAD)).toBeNull();
  });

  it('never links anywhere but https', () => {
    expect(tryItGuide({ ...named, repositoryUrl: 'javascript:alert(1)' }, THREAD)).toBeNull();
  });
});
