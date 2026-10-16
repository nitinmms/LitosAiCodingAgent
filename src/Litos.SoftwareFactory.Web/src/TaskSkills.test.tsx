import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import type { SkillSettings, TaskSkillOption } from './api/types';
import { App } from './App';
import { chosenSize, defaultsFor } from './components/TaskSkillsPicker';
import { FakeHost } from './test/fakeHost';

/** Skills chosen for a task (m3-architecture.md §13): picked in New thread, changed on the thread, defaults on the Skills tab. */

const tdd: TaskSkillOption = { name: 'tdd', description: 'Write the failing test first.', chars: 4_000, defaultFor: ['bug', 'feature'] };
const prove: TaskSkillOption = { name: 'prove-it-works', description: 'Show it working before handing off.', chars: 5_000, defaultFor: ['bug'] };
const notes: TaskSkillOption = { name: 'release-notes', description: 'Write release notes.', chars: 6_000, defaultFor: [] };

function hostWithSkills() {
  const host = new FakeHost();
  host.settings.skills = [tdd, prove, notes];
  host.settings.maxTaskSkillChars = 12_000;
  return host;
}

function start(host: FakeHost) {
  host.signedIn = true;
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  return userEvent.setup();
}

async function openNewThread(host: FakeHost) {
  window.location.hash = '#/threads';
  const user = start(host);
  await user.click(await screen.findByRole('button', { name: 'New thread' }));
  return { user, dialog: within(screen.getByRole('dialog', { name: 'New thread' })) };
}

const box = (dialog: ReturnType<typeof within>, name: string) => dialog.getByRole('checkbox', { name: new RegExp(`^${name} `) });

describe('the picker', () => {
  it('adds up the chosen skills and gives each type its defaults in the library order', () => {
    expect(chosenSize([tdd, prove, notes], ['tdd', 'release-notes'])).toBe(10_000);
    expect(defaultsFor([tdd, prove, notes], 'bug')).toEqual(['tdd', 'prove-it-works']);
    expect(defaultsFor([tdd, prove, notes], 'chore')).toEqual([]);
  });
});

describe('skills in New thread', () => {
  it("starts with the type's defaults, follows the type until the person picks, and sends the choice", async () => {
    const host = hostWithSkills();
    host.addProject();
    const { user, dialog } = await openNewThread(host);

    expect(box(dialog, 'tdd')).toBeChecked();
    expect(box(dialog, 'prove-it-works')).not.toBeChecked();
    expect(dialog.getByText(/4,000 of 12,000 characters/)).toBeInTheDocument();

    await user.selectOptions(dialog.getByLabelText('Type'), 'bug');
    expect(box(dialog, 'prove-it-works')).toBeChecked();

    // Once the person picks, a change of type leaves the choice alone.
    await user.click(box(dialog, 'tdd'));
    await user.selectOptions(dialog.getByLabelText('Type'), 'feature');
    expect(box(dialog, 'tdd')).not.toBeChecked();
    expect(box(dialog, 'prove-it-works')).toBeChecked();

    await user.type(dialog.getByLabelText('What do you want changed?'), 'Add CSV export');
    await user.click(dialog.getByRole('button', { name: 'Create thread' }));

    await screen.findByRole('heading', { level: 1, name: 'Add CSV export' });
    expect(host.sent('POST', '/api/threads')[0]!.body).toMatchObject({ typeLabel: 'feature', skills: ['prove-it-works'] });
    expect(screen.getByText('prove-it-works', { selector: '.th-sub .mono' })).toBeInTheDocument();
  });

  it('refuses skills past the limit before sending anything', async () => {
    const host = hostWithSkills();
    host.addProject();
    const { user, dialog } = await openNewThread(host);

    await user.click(box(dialog, 'prove-it-works'));
    await user.click(box(dialog, 'release-notes'));
    expect(dialog.getByText(/15,000 of 12,000 characters/)).toHaveClass('error');
    await user.type(dialog.getByLabelText('What do you want changed?'), 'Add CSV export');
    await user.click(dialog.getByRole('button', { name: 'Create thread' }));

    expect(await dialog.findByText("A task's skills can come to at most 12,000 characters. Untick one.")).toBeInTheDocument();
    expect(host.sent('POST', '/api/threads')).toHaveLength(0);
  });

  it('is not shown when the library has no skill', async () => {
    const host = new FakeHost();
    host.addProject();
    const { dialog } = await openNewThread(host);

    expect(dialog.queryByText(/Skills for this task/)).toBeNull();
    expect(host.sent('POST', '/api/threads')).toHaveLength(0);
  });
});

describe("changing a task's skills", () => {
  async function withThread(state: 'Running' | 'Draft') {
    const host = hostWithSkills();
    const project = host.addProject();
    const thread = host.addThread(project, { state, stage: state === 'Running' ? 'Implement' : 'Discuss', skills: ['tdd'] });
    window.location.hash = '#/threads';
    const user = start(host);
    await screen.findByRole('heading', { level: 1, name: thread.title });
    await waitFor(() => expect(host.sources).toHaveLength(1));
    return { host, thread, user };
  }

  it('while the task works, says the change waits for a pause and resume, and the thread is told', async () => {
    const { host, thread, user } = await withThread('Running');

    await user.click(screen.getByRole('button', { name: 'Change' }));
    const editor = within(screen.getByRole('generic', { name: "Change the task's skills" }));
    expect(editor.getByText('The task is working: the change is applied after it is paused and resumed.')).toBeInTheDocument();
    await user.click(editor.getByRole('checkbox', { name: /^prove-it-works / }));
    await user.click(editor.getByRole('button', { name: 'Save skills' }));

    expect(host.sent('POST', `/api/threads/${thread.id}/skills`)[0]!.body).toEqual({ skills: ['tdd', 'prove-it-works'] });
    expect(
      await screen.findByText('The skills for this task changed to: tdd, prove-it-works. They will be applied after the task is paused and resumed.'),
    ).toBeInTheDocument();
    expect(screen.getByText('tdd, prove-it-works', { selector: '.th-sub .mono' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save skills' })).toBeNull();
  });

  it('on a task that is not working, does not mention pausing', async () => {
    const { user } = await withThread('Draft');

    await user.click(screen.getByRole('button', { name: 'Change' }));

    expect(screen.queryByText(/applied after it is paused and resumed/)).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('button', { name: 'Save skills' })).toBeNull();
  });
});

describe('defaults on the Skills tab', () => {
  it('saves the task types a skill is chosen for by default', async () => {
    const host = new FakeHost();
    host.skills = {
      revision: 1,
      settings: { repositoryPolicy: 'ApproveEach', library: [{ name: 'tdd', description: 'Test first.', body: 'Write the test.', enabled: true, defaultFor: [] }] },
    };
    window.location.hash = '#/settings/skills';
    const user = start(host);
    const card = within(await screen.findByRole('region', { name: 'tdd' }));
    expect(screen.queryByText('Unsaved changes')).not.toBeInTheDocument();

    await user.click(card.getByRole('checkbox', { name: 'Feature' }));
    await user.click(card.getByRole('checkbox', { name: 'Bug' }));
    await user.click(screen.getByRole('button', { name: 'Save skills' }));

    expect(await screen.findByText('The skills are saved.')).toBeInTheDocument();
    const sent = host.sent('PUT', '/api/admin/settings/skills')[0]!.body as { settings: SkillSettings };
    // In the task types' own order, whatever order they were ticked in.
    expect(sent.settings.library[0]!.defaultFor).toEqual(['bug', 'feature']);
  });
});
