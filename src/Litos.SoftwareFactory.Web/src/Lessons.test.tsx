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
