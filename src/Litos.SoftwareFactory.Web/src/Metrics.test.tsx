import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import { App } from './App';
import { duration } from './components/MetricsPage';
import { parseRoute, routeHash } from './domain/route';
import { FakeHost } from './test/fakeHost';

/** The Metrics tab (m4-architecture.md §6). */

function start(host: FakeHost, hash = '#/board') {
  host.signedIn = true;
  window.location.hash = hash;
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  return userEvent.setup();
}

describe('metrics helpers', () => {
  it('reads durations as people do, and has a route', () => {
    expect(duration(0.25)).toBe('15 min');
    expect(duration(5)).toBe('5.0 h');
    expect(duration(72)).toBe('3.0 days');
    expect(parseRoute('#/metrics')).toEqual({ view: 'metrics' });
    expect(routeHash({ view: 'metrics' })).toBe('#/metrics');
  });
});

describe('the Metrics tab', () => {
  it('opens from the top bar on all my projects for 30 days, and shows the flow metrics', async () => {
    const host = new FakeHost();
    host.addProject();
    const user = start(host);

    await user.click(await screen.findByRole('button', { name: 'Metrics' }));

    const summary = within(await screen.findByRole('region', { name: 'Summary' }));
    expect(host.sent('GET', '/api/metrics?days=30')).toHaveLength(1);
    expect(summary.getByText('4 (3 accepted, 0 cancelled, 1 open)')).toBeInTheDocument();
    expect(summary.getByText('67%')).toBeInTheDocument();
    expect(summary.getByText(/median 0 \(2 with 0, 1 with 1\)/)).toBeInTheDocument();
    expect(summary.getByRole('link', { name: 'Export orders' })).toHaveAttribute('href', '#/threads/t-9');
    expect(summary.getByText('2 waits, median 12 min, 90th percentile 30 min')).toBeInTheDocument();
    const states = within(screen.getByRole('region', { name: 'Time in each state' }));
    expect(states.getByText('Awaiting Human Testing')).toBeInTheDocument();
    expect(states.getByText('8.0 h')).toBeInTheDocument();
    expect(screen.getByText(/Given to runs 5 times, left out past the limit 1 times/)).toBeInTheDocument();
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
});
