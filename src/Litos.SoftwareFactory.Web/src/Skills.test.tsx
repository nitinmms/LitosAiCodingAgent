import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import type { FoundRepositorySkill, LibrarySkill, SkillSettings } from './api/types';
import { App } from './App';
import { approvalRows, fromSkillForm, isSkillName, toSkillForm } from './components/SkillsTab';
import { FakeHost } from './test/fakeHost';

function start(host: FakeHost) {
  host.signedIn = true;
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  return userEvent.setup();
}

async function onSkills(host: FakeHost) {
  window.location.hash = '#/settings/skills';
  const user = start(host);
  await screen.findByRole('form', { name: 'Skills' });
  return user;
}

const releaseNotes: LibrarySkill = {
  name: 'release-notes',
  description: 'Use when writing release notes for a change.',
  body: '# Release notes\n\nList what changed for users.',
  enabled: true,
};

const found = (patch: Partial<FoundRepositorySkill> = {}): FoundRepositorySkill => ({
  projectId: 'p1',
  project: 'filedb-sharp',
  name: 'run-benchmarks',
  hash: 'sha256:aaa',
  description: 'Use before claiming a change is faster.',
  folder: '.litos/skills/run-benchmarks',
  content: '---\nname: run-benchmarks\n---\n\nRun dotnet run -c Release.',
  otherFiles: [],
  firstFoundAt: '2026-10-01T09:00:00Z',
  lastFoundAt: '2026-10-01T09:00:00Z',
  approvedAt: null,
  latest: true,
  ...patch,
});

const card = (name: string) => within(screen.getByRole('region', { name }));
const approvals = () => within(screen.getByRole('region', { name: 'Found in repositories' }));

describe('the Skills tab', () => {
  it('starts with an empty library, and adds a skill that is saved with the policy', async () => {
    const host = new FakeHost();
    const user = await onSkills(host);
    expect(screen.getByRole('tab', { name: 'Skills' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByText('The library is empty.')).toBeInTheDocument();
    expect(screen.getByLabelText(/Runs use the skills a repository keeps/)).toHaveValue('ApproveEach');

    await user.click(screen.getByRole('button', { name: 'Add a skill' }));
    await user.type(card('New skill').getByLabelText('Name'), 'release-notes');
    const skill = card('release-notes');
    await user.type(skill.getByLabelText('Description'), releaseNotes.description);
    await user.type(skill.getByLabelText('Instructions'), '# Release notes{Enter}{Enter}List what changed for users.');
    await user.selectOptions(screen.getByLabelText(/Runs use the skills a repository keeps/), 'Never');
    expect(screen.getByText('Unsaved changes')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Save skills' }));

    expect(await screen.findByText('The skills are saved.')).toBeInTheDocument();
    const sent = host.sent('PUT', '/api/admin/settings/skills')[0]!.body as { revision: number; settings: SkillSettings };
    expect(sent).toEqual({ revision: 0, settings: { repositoryPolicy: 'Never', library: [releaseNotes] } });
  });

  it('turns a skill off without removing it, and removes another', async () => {
    const host = new FakeHost();
    host.skills = { revision: 3, settings: { repositoryPolicy: 'ApproveEach', library: [releaseNotes, { ...releaseNotes, name: 'old' }] } };
    const user = await onSkills(host);

    await user.click(card('release-notes').getByLabelText('Given to runs'));
    await user.click(card('old').getByRole('button', { name: 'Remove old' }));
    await user.click(screen.getByRole('button', { name: 'Save skills' }));

    await screen.findByText('The skills are saved.');
    const sent = host.sent('PUT', '/api/admin/settings/skills')[0]!.body as { revision: number; settings: SkillSettings };
    expect(sent.revision).toBe(3);
    expect(sent.settings.library).toEqual([{ ...releaseNotes, enabled: false }]);
  });

  it("shows the host's reasons for refusing a skill", async () => {
    const host = new FakeHost();
    const user = await onSkills(host);

    await user.click(screen.getByRole('button', { name: 'Add a skill' }));
    await user.type(card('New skill').getByLabelText('Name'), 'notes');
    await user.click(screen.getByRole('button', { name: 'Save skills' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('notes: describe when the agent should use it.');
    expect(host.skills.revision).toBe(0);
  });

  it('offers to load the change of an Admin who saved first', async () => {
    const host = new FakeHost();
    const user = await onSkills(host);
    host.skills = { revision: 1, settings: { repositoryPolicy: 'Always', library: [] } };

    await user.selectOptions(screen.getByLabelText(/Runs use the skills a repository keeps/), 'Never');
    await user.click(screen.getByRole('button', { name: 'Save skills' }));
    await user.click(await screen.findByRole('button', { name: 'Load their change' }));

    expect(await screen.findByLabelText(/Runs use the skills a repository keeps/)).toHaveValue('Always');
  });
});

describe('repository skills on the Skills tab', () => {
  it('says when no repository has had a skill', async () => {
    await onSkills(new FakeHost());
    expect(await approvals().findByText('No repository has had a skill yet.')).toBeInTheDocument();
  });

  it('shows a waiting skill with its SKILL.md and other files, and approves it at once', async () => {
    const host = new FakeHost();
    host.repositorySkills = [found({ otherFiles: ['bench.ps1'] })];
    const user = await onSkills(host);

    const list = approvals();
    expect(await list.findByText('Waiting for approval')).toBeInTheDocument();
    expect(list.getByText(/Run dotnet run -c Release\./)).toBeInTheDocument();
    expect(list.getByText(/Its folder also has bench\.ps1/)).toBeInTheDocument();

    await user.click(list.getByRole('button', { name: 'Approve run-benchmarks for filedb-sharp' }));

    expect(await screen.findByText('run-benchmarks is approved for filedb-sharp.')).toBeInTheDocument();
    expect(host.sent('PUT', '/api/admin/skills/repository/approval')[0]!.body).toEqual({
      projectId: 'p1',
      name: 'run-benchmarks',
      hash: 'sha256:aaa',
      approved: true,
    });
    expect(await list.findByText('Approved')).toBeInTheDocument();
    // An approval is not part of the form: nothing waits to be saved.
    expect(screen.queryByText('Unsaved changes')).not.toBeInTheDocument();
  });

  it('shows a changed skill as waiting for its own approval, and the earlier version’s approval can be taken back', async () => {
    const host = new FakeHost();
    host.repositorySkills = [
      found({ approvedAt: '2026-10-01T10:00:00Z', latest: false }),
      found({ hash: 'sha256:bbb', lastFoundAt: '2026-10-02T09:00:00Z' }),
    ];
    const user = await onSkills(host);

    const list = approvals();
    expect(await list.findByText('Changed since it was approved')).toBeInTheDocument();
    expect(list.getByText('An earlier version, approved')).toBeInTheDocument();

    await user.click(list.getByRole('button', { name: 'Take back approval of run-benchmarks for filedb-sharp' }));

    expect(await screen.findByText('run-benchmarks is no longer approved for filedb-sharp.')).toBeInTheDocument();
    expect((host.sent('PUT', '/api/admin/skills/repository/approval')[0]!.body as { hash: string }).hash).toBe('sha256:aaa');
  });

  it('says approvals are not needed under the Always policy', async () => {
    const host = new FakeHost();
    host.skills = { revision: 1, settings: { repositoryPolicy: 'Always', library: [] } };
    await onSkills(host);
    expect(approvals().getByText(/approvals are kept but not needed/)).toBeInTheDocument();
  });
});

describe('the Skills form', () => {
  it('reads back what it was given, trimming the name and description', () => {
    expect(fromSkillForm(toSkillForm(releaseNotes))).toEqual(releaseNotes);
    expect(fromSkillForm({ ...toSkillForm(releaseNotes), name: ' release-notes ', description: ' Use it. ' })).toEqual({
      ...releaseNotes,
      description: 'Use it.',
    });
  });

  it('knows a usable skill name', () => {
    expect(isSkillName('release-notes')).toBe(true);
    expect(isSkillName('v1.2_x')).toBe(true);
    expect(isSkillName('-x')).toBe(false);
    expect(isSkillName('has space')).toBe(false);
    expect(isSkillName('a'.repeat(65))).toBe(false);
  });

  it('lists each latest version and the earlier versions still approved, not the others', () => {
    const rows = approvalRows([
      found({ hash: 'old-unapproved', latest: false }),
      found({ hash: 'old-approved', latest: false, approvedAt: '2026-10-01T10:00:00Z' }),
      found({ hash: 'new' }),
      found({ name: 'another', projectId: 'p2', project: 'insta', hash: 'x', approvedAt: '2026-10-01T10:00:00Z' }),
    ]);
    expect(rows.map((r) => [r.skill.hash, r.status])).toEqual([
      ['new', 'Changed'],
      ['old-approved', 'Earlier'],
      ['x', 'Approved'],
    ]);
  });
});

describe('the skills a run started with', () => {
  it('lists the library and repository skills, saying which were skipped and why', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const thread = host.addThread(project, { state: 'Running', stage: 'Implement' });
    host.threads.get(thread.id)!.run = {
      id: 'r1',
      kind: 'Implement',
      status: 'Running',
      stopReason: null,
      baselineCommit: null,
      headCommit: null,
      promptRevision: 'm1.1',
      capabilities: {
        ptc: true,
        shellTimeoutSeconds: 300,
        webSearch: 'Off',
        mcpServers: [],
        mcpStatus: null,
        librarySkills: [
          { name: 'release-notes', description: 'Use when writing release notes.' },
          { name: 'style', description: 'House style.' },
        ],
        repositorySkillPolicy: 'ApproveEach',
        repositorySkills: [
          { name: 'style', hash: 'h1', status: 'Used', replacesLibrarySkill: true },
          { name: 'deploy', hash: 'h2', status: 'WaitingForApproval', replacesLibrarySkill: false },
        ],
      },
    };
    window.location.hash = `#/threads/${thread.id}`;
    start(host);

    const box = within(await screen.findByRole('region', { name: 'Run' }));

    expect(box.getByText('release-notes')).toHaveAttribute('title', 'Use when writing release notes.');
    expect(box.getByText('(repository)')).toBeInTheDocument();
    expect(box.getByText('(repository, skipped: waiting for approval)')).toBeInTheDocument();
    // The repository's "style" is used in place of the library's, so it is listed once.
    expect(box.getAllByText('style')).toHaveLength(1);
  });

  it('says None for a run with no skill, as runs from before skills do', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const thread = host.addThread(project, { state: 'Running', stage: 'Implement' });
    host.threads.get(thread.id)!.run = {
      id: 'r1',
      kind: 'Implement',
      status: 'Running',
      stopReason: null,
      baselineCommit: null,
      headCommit: null,
      promptRevision: 'm1.1',
      capabilities: { ptc: false, shellTimeoutSeconds: 300, webSearch: 'Off', mcpServers: [], mcpStatus: null },
    };
    window.location.hash = `#/threads/${thread.id}`;
    start(host);

    const box = within(await screen.findByRole('region', { name: 'Run' }));
    expect(box.getAllByText('None')).toHaveLength(2);
  });
});
