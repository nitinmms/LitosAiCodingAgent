import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import { App } from './App';
import { splitList, tabOf } from './components/LessonsPage';
import { parseRoute, routeHash } from './domain/route';
import { FakeHost } from './test/fakeHost';

/** The Lessons page (m4-architecture.md §3.4): Members write suggestions; an Admin approves and manages them. */

function start(host: FakeHost, hash = '#/lessons') {
  host.signedIn = true;
  window.location.hash = hash;
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  return userEvent.setup();
}

const tab = (name: RegExp) => screen.getByRole('tab', { name });
const card = (number: number) => within(screen.getByRole('region', { name: `L-${number}` }));

describe('lesson helpers', () => {
  it('splits a comma-separated list and files a lesson under its tab', () => {
    expect(splitList(' money, cents ,, invoices ')).toEqual(['money', 'cents', 'invoices']);
    const host = new FakeHost();
    const project = host.addProject();
    expect(tabOf(host.addLesson(project.id))).toBe('Suggested');
    expect(tabOf(host.addLesson(project.id, { status: 'Approved', enabled: false }))).toBe('Disabled');
    expect(tabOf(host.addLesson(project.id, { status: 'Dismissed' }))).toBe('Dismissed');
  });

  it('has a route of its own per project', () => {
    expect(parseRoute('#/lessons/p-1')).toEqual({ view: 'lessons', projectId: 'p-1' });
    expect(parseRoute('#/lessons')).toEqual({ view: 'lessons', projectId: null });
    expect(routeHash({ view: 'lessons', projectId: 'p-1' })).toBe('#/lessons/p-1');
  });
});

describe('the Lessons page', () => {
  it("opens from the top bar on the first project, with each status's count", async () => {
    const host = new FakeHost();
    const project = host.addProject();
    host.addLesson(project.id);
    host.addLesson(project.id, { status: 'Approved', approvedRevision: 1 }, { rule: 'Dates are stored in UTC.', approvedBy: 'admin' });
    const user = start(host, '#/board');

    await user.click(await screen.findByRole('button', { name: 'Lessons' }));

    expect(await screen.findByRole('heading', { name: 'Lessons' })).toBeInTheDocument();
    expect(tab(/Suggested \(1\)/)).toHaveAttribute('aria-selected', 'true');
    expect(tab(/Approved \(1\)/)).toBeInTheDocument();
    expect(screen.getByText('Money is stored as integer cents.')).toBeInTheDocument();
    await user.click(tab(/Dismissed/));
    expect(screen.getByText('No dismissed lessons.')).toBeInTheDocument();
  });

  it('lets a Member write a suggestion, which waits for an Admin', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    host.signInAs(host.addPerson({ projectIds: [project.id] }));
    const user = start(host);

    await user.click(await screen.findByRole('button', { name: 'New lesson' }));
    const form = within(screen.getByRole('form', { name: 'New lesson' }));
    expect(form.queryByRole('button', { name: 'Save and approve' })).toBeNull();
    await user.type(form.getByLabelText('Lesson'), 'Totals are rounded half to even.');
    await user.type(form.getByLabelText('Topics'), 'money, rounding');
    await user.click(form.getByRole('button', { name: 'Save' }));

    expect(host.sent('POST', `/api/projects/${project.id}/lessons`)[0]!.body).toEqual({
      rule: 'Totals are rounded half to even.',
      topics: ['money', 'rounding'],
      paths: [],
      reason: '',
      approve: false,
    });
    const lesson = card(1);
    expect(lesson.getByText('Totals are rounded half to even.')).toBeInTheDocument();
    expect(lesson.queryByRole('button', { name: 'Remember for project' })).toBeNull();
    expect(lesson.getByText('An Admin approves a lesson before any run is given it.')).toBeInTheDocument();
  });

  it('lets an Admin remember a suggestion for the project, which moves it to Approved', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const lesson = host.addLesson(project.id);
    const user = start(host);

    await user.click(within(await screen.findByRole('region', { name: 'L-1' })).getByRole('button', { name: 'Remember for project' }));

    expect(host.sent('POST', `/api/lessons/${lesson.id}/approve`)[0]!.body).toEqual({ expectedRevision: 1 });
    expect(await screen.findByText('L-1 is approved.')).toBeInTheDocument();
    expect(tab(/Suggested \(0\)/)).toBeInTheDocument();
    await user.click(tab(/Approved \(1\)/));
    expect(card(1).getByRole('button', { name: 'Disable' })).toBeInTheDocument();
  });

  it("shows an Admin's edit of an approved lesson as waiting, with the revision runs still get", async () => {
    const host = new FakeHost();
    const project = host.addProject();
    host.addLesson(project.id, { status: 'Approved', approvedRevision: 1 });
    const user = start(host);
    await user.click(await screen.findByRole('tab', { name: /Approved/ }));

    await user.click(card(1).getByRole('button', { name: 'Edit' }));
    const form = within(screen.getByRole('form', { name: 'Edit L-1' }));
    await user.clear(form.getByLabelText('Lesson'));
    await user.type(form.getByLabelText('Lesson'), 'Money is integer cents, in a long.');
    await user.click(form.getByRole('button', { name: 'Save as a suggestion' }));

    expect(host.sent('POST', `/api/lessons/${host.lessons[0]!.id}/revisions`)[0]!.body).toMatchObject({ expectedRevision: 1, approve: false });
    const edited = card(1);
    expect(await edited.findByText('Edit waiting for approval')).toBeInTheDocument();
    expect(edited.getByText(/Runs are given the approved revision 1 until the edit is approved/)).toBeInTheDocument();
    expect(edited.getByRole('button', { name: 'Approve the edit' })).toBeInTheDocument();
  });

  it('lets an Admin disable a lesson, or replace it with another approved one', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const old = host.addLesson(project.id, { status: 'Approved', approvedRevision: 1 });
    const replacement = host.addLesson(project.id, { status: 'Approved', approvedRevision: 1 }, { rule: 'Money is a decimal.' });
    const user = start(host);
    await user.click(await screen.findByRole('tab', { name: /Approved/ }));

    await user.click(card(1).getByRole('button', { name: 'Replace with another lesson' }));
    await user.selectOptions(card(1).getByLabelText('Replace with'), replacement.id);
    await user.click(card(1).getByRole('button', { name: 'Replace' }));

    expect(host.sent('POST', `/api/lessons/${old.id}/supersede`)[0]!.body).toEqual({ expectedRevision: 1, replacementId: replacement.id });
    expect(await screen.findByText('L-1 is replaced and disabled.')).toBeInTheDocument();
    await user.click(tab(/Disabled \(1\)/));
    expect(card(1).getByText('Replaced by L-2')).toBeInTheDocument();
    await user.click(card(1).getByRole('button', { name: 'Enable' }));
    await waitFor(() => expect(tab(/Disabled \(0\)/)).toBeInTheDocument());
  });

  it("shows the host's reason when a command is refused", async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const lesson = host.addLesson(project.id);
    const user = start(host);
    await screen.findByRole('region', { name: 'L-1' });
    host.lessons = host.lessons.map((l) => (l.id === lesson.id ? { ...l, currentRevision: 2 } : l)); // someone edited it meanwhile

    await user.click(card(1).getByRole('button', { name: 'Dismiss' }));

    expect(await card(1).findByText(/has changed since you read it/)).toBeInTheDocument();
  });

  it("lists a run's lessons on its thread, takes a verdict on each, and lets a person pick one for the next run", async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const thread = host.addThread(project, { state: 'AwaitingHumanTesting', stage: 'Handoff' });
    const given = host.addLesson(project.id, { status: 'Approved', approvedRevision: 1 }, { rule: 'Quote every CSV value.' });
    const spare = host.addLesson(project.id, { status: 'Approved', approvedRevision: 1 }, { rule: 'Dates are UTC.' });
    const details = host.details(thread.id);
    details.run = { id: 'r-1', kind: 'Implement', status: 'Finished', stopReason: null, baselineCommit: null, headCommit: null, promptRevision: 'm2.5' };
    details.runLessons = [
      { lessonId: given.id, number: given.number, revision: 1, rule: 'Quote every CSV value.', score: 2, inclusion: 'Retrieved', manual: false, includedAt: '2026-10-16T09:00:00Z', verdict: null, verdictAt: null },
      { lessonId: spare.id, number: spare.number, revision: 1, rule: 'Dates are UTC.', score: 2, inclusion: 'NotIncluded', manual: false, includedAt: '2026-10-16T09:00:00Z', verdict: null, verdictAt: null },
    ];
    const user = start(host, '#/threads');
    const box = within(await screen.findByRole('region', { name: 'Lessons' }));

    expect(box.getByText('Quote every CSV value.')).toBeInTheDocument();
    expect(box.getByText(`Left out, past the limit: L-${spare.number}.`)).toBeInTheDocument();
    const verdicts = within(box.getByRole('group', { name: `Did L-${given.number} help?` }));
    await user.click(verdicts.getByRole('button', { name: 'Helped' }));
    expect(host.sent('POST', `/api/runs/r-1/lessons/${given.id}/verdict`)[0]!.body).toEqual({ verdict: 'Helped' });
    await waitFor(() => expect(verdicts.getByRole('button', { name: 'Helped' })).toHaveAttribute('aria-pressed', 'true'));

    await user.click(box.getByRole('button', { name: 'Pick a lesson for the next run' }));
    await user.selectOptions(await box.findByLabelText('Lesson'), spare.id);
    await user.click(box.getByRole('button', { name: 'Pick' }));

    expect(host.sent('POST', `/api/threads/${thread.id}/lessons`)[0]!.body).toEqual({ lessonIds: [spare.id] });
    expect(await box.findByText(/Picked for the next run:/)).toBeInTheDocument();
    await user.click(box.getByRole('button', { name: `Unpick L-${spare.number}` }));
    expect(host.sent('POST', `/api/threads/${thread.id}/lessons`)[1]!.body).toEqual({ lessonIds: [] });
  });

  it('says so when a run was given no lessons', async () => {
    const host = new FakeHost();
    host.addThread(host.addProject());
    start(host, '#/threads');

    expect(await within(await screen.findByRole('region', { name: 'Lessons' })).findByText('This run was given no project lessons.')).toBeInTheDocument();
  });

  it("shows an accepted task's reflection with its proposals, which an Admin remembers or dismisses", async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const thread = host.addThread(project, { state: 'Accepted', stage: 'Done' });
    const proposal = host.addLesson(project.id, { createdBy: null, proposedByJobId: 'job-1' }, { rule: 'Export files are UTF-8 with a BOM.', author: null, reason: 'Excel needs it.' });
    const other = host.addLesson(project.id, { createdBy: null, proposedByJobId: 'job-1' }, { rule: 'Dates are UTC.', author: null });
    const details = host.details(thread.id);
    details.reflection = { status: 'Completed', attempts: 1, lastError: null, outcome: { lessons: [1, 2], refused: [] }, updatedAt: '2026-10-16T09:00:00Z' };
    details.proposedLessons = [proposal, other];
    const user = start(host, '#/threads');
    const panel = within(await screen.findByRole('generic', { name: 'Lesson reflection' }));

    expect(panel.getByText('Lesson reflection is done.')).toBeInTheDocument();
    const first = within(panel.getByRole('region', { name: `Proposed L-${proposal.number}` }));
    expect(first.getByText(/Why: Excel needs it\./)).toBeInTheDocument();
    await user.click(first.getByRole('button', { name: 'Remember for project' }));
    expect(host.sent('POST', `/api/lessons/${proposal.id}/approve`)[0]!.body).toEqual({ expectedRevision: 1 });
    expect(await first.findByText('remembered')).toBeInTheDocument();

    await user.click(within(panel.getByRole('region', { name: `Proposed L-${other.number}` })).getByRole('button', { name: 'Dismiss' }));
    expect(host.sent('POST', `/api/lessons/${other.id}/dismiss`)).toHaveLength(1);
  });

  it('offers Retry and Skip for a deferred reflection, and a Member sees no approval buttons', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const thread = host.addThread(project, { state: 'Accepted', stage: 'Done' });
    const proposal = host.addLesson(project.id, { proposedByJobId: 'job-1' });
    const details = host.details(thread.id);
    details.reflection = { status: 'DeferredBudget', attempts: 0, lastError: null, outcome: null, updatedAt: '2026-10-16T09:00:00Z' };
    details.proposedLessons = [proposal];
    host.signInAs(host.addPerson({ projectIds: [project.id] }));
    const user = start(host, '#/threads');
    const panel = within(await screen.findByRole('generic', { name: 'Lesson reflection' }));

    expect(panel.getByText(/deferred: the task has less than the reflection allowance left/)).toBeInTheDocument();
    expect(panel.queryByRole('button', { name: 'Remember for project' })).toBeNull();
    expect(panel.getByText('An Admin approves a lesson before any run is given it.')).toBeInTheDocument();
    await user.click(panel.getByRole('button', { name: 'Retry the reflection' }));

    expect(host.sent('POST', `/api/threads/${thread.id}/reflection/retry`)).toHaveLength(1);
    expect(await panel.findByText('Lesson reflection is queued.')).toBeInTheDocument();
  });

  it('links a lesson to the thread it rests on', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const thread = host.addThread(project);
    host.addLesson(project.id, {}, { source: 'Decision', sourceRef: 'd-1', sourceThreadId: thread.id });
    const user = start(host);

    await user.click(await within(await screen.findByRole('region', { name: 'L-1' })).findByRole('link', { name: 'its thread' }));

    expect(await screen.findByRole('heading', { level: 1, name: thread.title })).toBeInTheDocument();
  });
});
