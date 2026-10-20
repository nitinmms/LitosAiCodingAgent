import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import { App } from './App';
import { niceScale, tickValues } from './components/charts';
import { pointsText } from './components/Guidance';
import { duration } from './components/MetricsPage';
import { label, percent, tokens } from './domain/metrics';
import { parseRoute, routeHash } from './domain/route';
import { FakeHost } from './test/fakeHost';

/** The Metrics tab (m4-architecture.md §6), and how the lessons are doing (§12). */

function start(host: FakeHost, hash = '#/board') {
  host.signedIn = true;
  window.location.hash = hash;
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  return userEvent.setup();
}

afterEach(() => localStorage.clear());

describe('metrics helpers', () => {
  it('write figures as people read them', () => {
    expect(duration(0.25)).toBe('15 min');
    expect(duration(5)).toBe('5.0 h');
    expect(duration(72)).toBe('3.0 days');
    expect(tokens(950)).toBe('950');
    expect(tokens(412_300)).toBe('412k');
    expect(tokens(1_084_000)).toBe('1.08M');
    expect(tokens(14_620_000)).toBe('14.6M');
    expect(percent(2 / 3)).toBe('67%');
    expect(percent(null)).toBe('—');
    expect(label('AwaitingHumanTesting')).toBe('Awaiting Human Testing');
    expect(pointsText({ with: 0.8, withTasks: 5, without: 0.55, withoutTasks: 6 })).toBe('+25 pts');
    expect(pointsText({ with: 0.2, withTasks: 5, without: 0.6, withoutTasks: 5 })).toBe('−40 pts');
    expect(pointsText(null)).toBe('too few tasks');
  });

  it('give an axis round steps that reach the largest value', () => {
    expect(niceScale(14.2)).toEqual({ max: 15, step: 5 });
    expect(niceScale(1_084_000)).toEqual({ max: 1_500_000, step: 500_000 });
    expect(niceScale(0)).toEqual({ max: 1, step: 1 });
    expect(tickValues({ max: 15, step: 5 })).toEqual([0, 5, 10, 15]);
  });

  it('have routes for both tabs, and for one lesson on the Lessons page', () => {
    expect(parseRoute('#/metrics')).toEqual({ view: 'metrics' });
    expect(parseRoute('#/metrics/guidance')).toEqual({ view: 'metrics', tab: 'guidance' });
    expect(routeHash({ view: 'metrics' })).toBe('#/metrics');
    expect(routeHash({ view: 'metrics', tab: 'guidance' })).toBe('#/metrics/guidance');
    expect(parseRoute('#/lessons/p-1/L-12')).toEqual({ view: 'lessons', projectId: 'p-1', lesson: 12 });
    expect(parseRoute('#/lessons/p-1')).toEqual({ view: 'lessons', projectId: 'p-1' });
    expect(routeHash({ view: 'lessons', projectId: 'p-1', lesson: 12 })).toBe('#/lessons/p-1/L-12');
  });
});

describe('the Flow tab', () => {
  it('opens from the top bar on all my projects for 30 days, with the summary as figures and charts', async () => {
    const host = new FakeHost();
    host.addProject();
    const user = start(host);

    await user.click(await screen.findByRole('button', { name: 'Metrics' }));

    const summary = within(await screen.findByRole('region', { name: 'Summary' }));
    expect(host.sent('GET', '/api/metrics?days=30')).toHaveLength(1);
    expect(summary.getByRole('img', { name: 'Tasks: Accepted 3, Open 1, Cancelled 0' })).toBeInTheDocument();
    expect(summary.getByText('67%')).toBeInTheDocument();
    expect(summary.getByText('above the M1 gate')).toBeInTheDocument();
    expect(summary.getByRole('img', { name: 'Tasks accepted over the period' })).toBeInTheDocument();
    expect(summary.getByText('120k')).toBeInTheDocument();
    expect(summary.getByRole('link', { name: 'Export orders' })).toHaveAttribute('href', '#/threads/t-9');
    expect(summary.getByText('oldest 26.0 h')).toHaveClass('bad');
  });

  it('says where the time goes, by whose turn it was and stage by stage', async () => {
    const host = new FakeHost();
    host.addProject();
    host.metrics.timeInState = [
      { name: 'AwaitingHumanTesting', count: 3, totalHours: 30, medianHours: 8, p90Hours: 14 },
      { name: 'Running', count: 4, totalHours: 10, medianHours: 2, p90Hours: 4 },
    ];
    start(host, '#/metrics');

    const time = within(await screen.findByRole('region', { name: 'Where the time goes' }));
    expect(time.getByText(/Tasks spent/).textContent).toBe(
      'Tasks spent 75% of their time waiting on people, mostly in human testing, and 25% with the factory working.',
    );
    expect(time.getByRole('img', { name: /^Time by whose turn it was: Factory working 10, Waiting on people 30/ })).toBeInTheDocument();
    const stages = within(time.getByRole('list', { name: 'Stages in order' })).getAllByRole('listitem');
    expect(stages.map((s) => s.querySelector('.name')!.textContent)).toEqual(['Discuss', 'Spec', 'Implement', 'Verify', 'Review', 'Handoff']);
    expect(stages[2]).toHaveClass('factory', 'busiest');
    expect(within(stages[2]!).getByText('30 min')).toBeInTheDocument();
  });

  it('draws time in each state, rework, tokens and queues, and shows each mark’s figures on hover', async () => {
    const host = new FakeHost();
    host.addProject();
    start(host, '#/metrics');

    const states = within(await screen.findByRole('region', { name: 'Time in each state' }));
    expect(states.getByRole('img', { name: 'Median and 90th percentile time per task in each state' })).toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Rework rounds' })).getByRole('img', { name: /change requests they needed/ })).toBeInTheDocument();
    const perTask = within(screen.getByRole('region', { name: 'Tokens, task by task' }));
    expect(perTask.getByRole('img', { name: 'Tokens charged to each task, by project' }).querySelectorAll('circle')).toHaveLength(3); // the uncharged draft is left out
    // The chart and its table are both in the page; the stylesheet shows one.
    expect(within(screen.getByRole('region', { name: 'Tokens by phase and model' })).getAllByText('deepseek/deepseek-v4.1-flash')).toHaveLength(2);
    expect(within(screen.getByRole('region', { name: 'Queue waits' })).getByText('2 waits, median 12 min, 90th percentile 30 min')).toBeInTheDocument();
    expect(screen.getByText(/Given to runs 5 times, left out past the limit 1 times/)).toBeInTheDocument();

    const mark = perTask.getByRole('img', { name: /Tokens charged/ }).querySelector('circle')!;
    fireEvent.pointerMove(mark, { clientX: 10, clientY: 10 });
    expect(screen.getByRole('tooltip').textContent).toBe('Export orders\nSalesApp · Awaiting Decision\n400,000 tokens');
  });

  it('shows the same figures as tables on request, and remembers the choice', async () => {
    const host = new FakeHost();
    host.addProject();
    const user = start(host, '#/metrics');
    await screen.findByRole('region', { name: 'Summary' });

    await user.click(screen.getByRole('button', { name: 'Tables' }));

    const states = within(screen.getByRole('table', { name: 'Time in each state' }));
    expect(states.getByText('Awaiting Human Testing')).toBeInTheDocument();
    expect(states.getByText('8.0 h')).toBeInTheDocument();
    expect(within(screen.getByRole('table', { name: 'Tokens, task by task' })).getByRole('link', { name: 'Fix the totals' })).toHaveAttribute('href', '#/threads/t-3');
    expect(screen.getByRole('button', { name: 'Tables' })).toHaveAttribute('aria-pressed', 'true');
    expect(localStorage.getItem('litos-metrics-view')).toBe('tables');
  });

  it('asks again for another period or one project', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const user = start(host, '#/metrics');
    await screen.findByRole('region', { name: 'Summary' });

    await user.click(screen.getByRole('tab', { name: 'Last 7 days' }));
    await waitFor(() => expect(host.sent('GET', '/api/metrics?days=7')).toHaveLength(1));
    await user.selectOptions(screen.getByLabelText('Project'), project.id);

    await waitFor(() => expect(host.sent('GET', `/api/metrics?days=7&projectId=${project.id}`)).toHaveLength(1));
  });

  it('puts what needs attention first, from the decisions and, for an Admin, the counters', async () => {
    const host = new FakeHost();
    host.addProject();
    start(host, '#/metrics');

    const attention = within(await screen.findByRole('region', { name: 'Needs attention' }));
    expect(attention.getByText(/The oldest has waited 26.0 h/)).toBeInTheDocument();
    expect(await attention.findByRole('link', { name: 'Orphaned task' })).toHaveAttribute('href', '#/threads/t-2');
    expect(attention.getByText(/1 push or pull-request failure/)).toBeInTheDocument();
  });

  it('shows an Admin the operations counters, with what needs attention marked', async () => {
    const host = new FakeHost();
    host.addProject();
    start(host, '#/metrics');

    const ops = within(await screen.findByRole('region', { name: 'Operations' }));
    expect(await ops.findByText('1 queued, 1 running; stopped: 2 paused budget')).toBeInTheDocument();
    expect(ops.getByText('1: marked running, but this host is not executing them')).toHaveClass('error');
    expect(ops.getByText('2 calls holding 40,000 tokens after their run stopped')).toBeInTheDocument();
    expect(ops.getByText('3 open; the slowest caught up 14 s ago')).toBeInTheDocument();
    expect(ops.getByRole('link', { name: 'Push refused' })).toHaveAttribute('href', '#/threads/t-5');
    const runs = within(ops.getByRole('table', { name: 'Running runs' }));
    expect(runs.getByRole('link', { name: 'Orphaned task' }).closest('tr')).toHaveClass('error');
    expect(within(ops.getByRole('table', { name: 'Use against the quotas' })).getByText('250,000 (25%)')).toBeInTheDocument();
  });

  it('shows a Member no operations counters', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    host.signInAs(host.addPerson({ projectIds: [project.id] }));
    start(host, '#/metrics');

    await screen.findByRole('region', { name: 'Summary' });
    expect(screen.queryByRole('region', { name: 'Operations' })).toBeNull();
    expect(host.sent('GET', '/api/admin/counters')).toHaveLength(0);
  });

  it('lets an Admin pause dispatch with a reason, shows everyone the banner, and resumes it', async () => {
    const host = new FakeHost();
    host.addProject();
    const user = start(host, '#/metrics');

    const ops = within(await screen.findByRole('region', { name: 'Operations' }));
    await user.type(await ops.findByLabelText('Why dispatch is paused'), 'Taking a backup.');
    await user.click(ops.getByRole('button', { name: 'Pause dispatch' }));

    expect(host.sent('POST', '/api/admin/dispatch')[0]!.body).toEqual({ paused: true, reason: 'Taking a backup.' });
    expect(await screen.findByText(/Dispatch is paused: Taking a backup\. Nothing new starts/)).toHaveClass('banner');
    expect(await ops.findByText(/By Priya Raman/)).toBeInTheDocument();

    await user.click(ops.getByRole('button', { name: 'Resume dispatch' }));
    expect(host.sent('POST', '/api/admin/dispatch')[1]!.body).toEqual({ paused: false, reason: null });
    await waitFor(() => expect(screen.queryByText(/Nothing new starts/)).toBeNull());
  });

  it('shows a Member the paused banner, without the control', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    host.dispatchState = { paused: true, reason: null, changedAt: '2026-10-17T09:00:00Z', changedBy: 'Priya Raman' };
    host.signInAs(host.addPerson({ projectIds: [project.id] }));
    start(host, '#/board');

    expect(await screen.findByText(/^Dispatch is paused\. Nothing new starts/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Resume dispatch' })).toBeNull();
  });

  it('shows what a host on restored data found', async () => {
    const host = new FakeHost();
    host.addProject();
    host.counters.restore = {
      backupTakenAt: '2026-10-17T08:00:00Z',
      backupHostVersion: '1.0.0+abc',
      filesChecked: 40,
      problems: ['runs/r1/worker.log is missing'],
      unreadableSecrets: ['github'],
      checkedAt: '2026-10-17T09:00:00Z',
    };
    start(host, '#/metrics');

    const restored = within(await screen.findByRole('region', { name: 'Operations' })).getByLabelText('Restored from backup');
    expect(within(restored).getByText(/1 of its 40 files missing or changed, 1 secrets unreadable/)).toHaveClass('error');
    expect(within(restored).getByText('runs/r1/worker.log is missing')).toBeInTheDocument();
    expect(within(restored).getByText(/The secret github cannot be read/)).toBeInTheDocument();
  });
});

describe('the Skills & lessons tab', () => {
  it('opens from the Flow tab, and asks for the lesson report over the same scope', async () => {
    const host = new FakeHost();
    host.addProject();
    const user = start(host, '#/metrics');
    await screen.findByRole('region', { name: 'Summary' });

    await user.click(within(screen.getByRole('navigation', { name: 'Metrics' })).getByRole('button', { name: 'Skills & lessons' }));

    expect(window.location.hash).toBe('#/metrics/guidance');
    const summary = within(await screen.findByRole('region', { name: 'Lesson summary' }));
    expect(host.sent('GET', '/api/metrics/lessons?days=30')).toHaveLength(1);
    expect(host.sent('GET', '/api/metrics/skills?days=30')).toHaveLength(1);
    expect(summary.getByText('75%')).toBeInTheDocument(); // 9 of 12 uses judged
    expect(summary.getByText('enough to go on')).toBeInTheDocument();
    expect(summary.getByText('3 misled')).toHaveClass('bad');
    expect(summary.getByText('misleading', { exact: false })).toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'Last 90 days' }));
    await waitFor(() => expect(host.sent('GET', '/api/metrics/lessons?days=90')).toHaveLength(1));
    expect(host.sent('GET', '/api/metrics/skills?days=90')).toHaveLength(1);
  });

  it('lists what to improve for lessons and skills together, most urgent first, each opening the page to change it on', async () => {
    const host = new FakeHost();
    host.addProject();
    start(host, '#/metrics/guidance');

    const fixes = within(await screen.findByRole('region', { name: 'What to improve' }));
    const items = fixes.getAllByRole('listitem');
    expect(items.map((i) => i.querySelector('.mono')!.textContent)).toEqual(['L-12', 'L-15', 'security-review', 'csv-and-encoding']);
    expect(within(items[0]!).getByText('Fix now')).toHaveClass('pill', 'bad');
    expect(within(items[0]!).getByText('Misled 3 of the 5 runs that judged it.')).toBeInTheDocument();
    expect(within(items[0]!).getByRole('link', { name: 'Edit the rule, or disable it. →' })).toHaveAttribute('href', '#/lessons/p-1/L-12');
    expect(within(items[1]!).getByText('Improve')).toBeInTheDocument();
    expect(within(items[2]!).getByRole('link', { name: 'Let it load on demand instead of choosing it for every task. →' })).toHaveAttribute(
      'href',
      '#/settings/skills',
    );
  });

  it('shows each skill by its cost and how tasks with it fared, and names the ones it cannot place', async () => {
    const host = new FakeHost();
    host.addProject();
    start(host, '#/metrics/guidance');

    const skills = within(await screen.findByRole('region', { name: 'Skills' }));
    expect(skills.getByText('36 of 40 runs had skills; the agent loaded one 14 times.')).toBeInTheDocument();
    const chart = skills.getByRole('img', { name: 'Skills by what they cost a run and the change in first-time acceptance' }) as unknown as HTMLElement;
    expect(within(chart).getByText('page-format')).toBeInTheDocument();
    expect(within(chart).getByText('accepted first time: with − without (points)')).toBeInTheDocument();
    expect(skills.getByText('never loaded', { selector: '.chip' })).toBeInTheDocument();

    const table = within(skills.getByRole('table', { name: 'Skills' }));
    const row = (name: string) => within(table.getByRole('link', { name }).closest('tr')!);
    expect(row('page-format').getByText('repository skill')).toBeInTheDocument();
    expect(row('page-format').getByText('+22 pts')).toHaveClass('lift', 'up');
    expect(row('security-review').getByText('chosen for the task')).toBeInTheDocument();
    expect(row('security-review').getByText('Costly, no clear gain')).toHaveClass('pill');
    expect(row('csv-and-encoding').getByText('too few tasks')).toBeInTheDocument();
    expect(table.getByRole('link', { name: 'csv-and-encoding' })).toHaveAttribute('href', '#/settings/skills');
  });

  it('says so when no run had a skill, and still shows the lessons', async () => {
    const host = new FakeHost();
    host.addProject();
    host.skillReport = { ...host.skillReport, runsWithSkills: 0, loads: 0, skills: [], improvements: [] };
    start(host, '#/metrics/guidance');

    expect(await screen.findByText('No run in the period had a skill.')).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Lesson map' })).toBeInTheDocument();
  });

  it('maps each judged lesson, and names the ones it cannot place', async () => {
    const host = new FakeHost();
    host.addProject();
    start(host, '#/metrics/guidance');

    const map = within(await screen.findByRole('region', { name: 'Lesson map' }));
    const svg = map.getByRole('img', { name: 'Lessons by how often they were given and how people judged them' });
    expect(within(svg as unknown as HTMLElement).getByText('L-12')).toBeInTheDocument();
    expect(within(svg as unknown as HTMLElement).getByText('Misleading · fix now')).toBeInTheDocument();
    expect(map.getByText('never matched')).toBeInTheDocument();
  });

  it('scores every lesson in a table that sorts by any figure', async () => {
    const host = new FakeHost();
    host.addProject();
    const user = start(host, '#/metrics/guidance');

    const table = await screen.findByRole('table', { name: 'Lesson scorecard' });
    const lessonsInOrder = () => within(table).getAllByRole('row').slice(1).map((r) => within(r).getAllByRole('link')[0]!.textContent);
    expect(lessonsInOrder()).toEqual(['L-3', 'L-12', 'L-15']); // most given first
    const misleading = within(table).getByRole('link', { name: 'L-12' }).closest('tr')!;
    expect(within(misleading).getByText('Misleading')).toBeInTheDocument();
    expect(within(misleading).getByText('−40 pts')).toHaveClass('lift', 'down');
    expect(within(misleading).getByRole('img', { name: 'Verdicts on L-12: Helped 1, Did not matter 1, Misled 3, Not judged 0' })).toBeInTheDocument();
    expect(within(table).getByRole('link', { name: 'L-3' })).toHaveAttribute('href', '#/lessons/p-1/L-3');

    await user.click(within(table).getByRole('button', { name: /^Given/ }));
    expect(lessonsInOrder()).toEqual(['L-15', 'L-12', 'L-3']);
    await user.click(within(table).getByRole('button', { name: /^Helped/ }));
    expect(lessonsInOrder()).toEqual(['L-3', 'L-12', 'L-15']);
  });

  it('shows whether ranking and the lessons’ sources work', async () => {
    const host = new FakeHost();
    host.addProject();
    start(host, '#/metrics/guidance');

    const ranking = within(await screen.findByRole('region', { name: 'Ranking' }));
    expect(ranking.getByRole('img', { name: 'Verdicts by the rank retrieval gave the lesson' })).toBeInTheDocument();
    const sources = within(screen.getByRole('region', { name: 'Where good lessons come from' }));
    expect(sources.getByText('7 proposed by reflection · 1 written by people')).toBeInTheDocument();
    expect(sources.getByText('A change request')).toBeInTheDocument();
    expect(sources.getByText('80%')).toBeInTheDocument();
  });

  it('says so when a project has no lessons yet', async () => {
    const host = new FakeHost();
    host.addProject();
    host.lessonReport = { ...host.lessonReport, lessons: [], improvements: [] };
    start(host, '#/metrics/guidance');

    expect(await screen.findByText(/No approved lessons yet/)).toBeInTheDocument();
  });
});

describe('a lesson opened from the Metrics tab', () => {
  it('opens on its tab on the Lessons page, marked', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    host.addLesson(project.id);
    host.addLesson(project.id, { status: 'Approved', approvedRevision: 1 }, { rule: 'Dates are stored in UTC.', approvedBy: 'admin' });
    start(host, `#/lessons/${project.id}/L-2`);

    const lesson = await screen.findByRole('region', { name: 'L-2' });
    expect(lesson).toHaveAttribute('aria-current', 'true');
    expect(lesson).toHaveClass('focused');
    expect(screen.getByRole('tab', { name: /^Approved/ })).toHaveAttribute('aria-selected', 'true');
  });
});
