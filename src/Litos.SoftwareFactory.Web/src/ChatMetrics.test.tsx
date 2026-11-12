import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import type { ChatMetrics } from './api/types';
import { App } from './App';
import { FakeHost } from './test/fakeHost';

/** The Metrics tab's Chat view: what chat answers spend, apart from task work. */

function start(host: FakeHost, hash = '#/metrics/chat') {
  host.signedIn = true;
  window.location.hash = hash;
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  return userEvent.setup();
}

afterEach(() => localStorage.clear());

function withChat(): { host: FakeHost; garmin: string; insta: string } {
  const host = new FakeHost();
  const garmin = host.addProject({ name: 'garmin-coach-ts' }).id;
  const insta = host.addProject({ name: 'insta-story-generator' }).id;
  const series: ChatMetrics['series'] = Array.from({ length: 10 }, (_, i) => ({
    start: new Date(Date.UTC(2026, 9, 14 + i * 3)).toISOString(),
    days: 3,
    tokens: i === 9 ? 420_000 : 0,
    artifactTokens: i === 9 ? 350_000 : 0,
    byProject: i === 9 ? [{ projectId: garmin, tokens: 300_000 }, { projectId: insta, tokens: 120_000 }] : [],
  }));
  host.chatReport = {
    projectId: null,
    days: 30,
    tokens: 3_200_000,
    artifactTokens: 2_660_000,
    plainTokens: 540_000,
    taskTokens: 8_600_000,
    share: 0.2712,
    answers: 184,
    artifactAnswers: 32,
    perAnswer: { count: 180, median: 14_000, p90: 58_000 },
    plainMedian: 3_600,
    artifactMedian: 62_000,
    byProject: [
      { projectId: garmin, tokens: 1_550_000, artifactTokens: 1_360_000, answers: 78, median: 15_000, share: 0.4844 },
      { projectId: insta, tokens: 1_080_000, artifactTokens: 920_000, answers: 64, median: 13_000, share: 0.3375 },
    ],
    topTasks: [{ threadId: 't-load', projectId: garmin, title: 'Weekly training load chart', answers: 14, artifactAnswers: 3, tokens: 412_000 }],
    series,
  };
  return { host, garmin, insta };
}

describe('the Chat tab', () => {
  it('opens from the Metrics tabs and shows what chat spends, apart from task work', async () => {
    const { host } = withChat();
    const user = start(host, '#/metrics');
    await screen.findByRole('heading', { name: 'Metrics' });

    await user.click(screen.getByRole('button', { name: 'Chat' }));

    const summary = within(await screen.findByRole('region', { name: 'Chat summary' }));
    expect(window.location.hash).toBe('#/metrics/chat');
    expect(summary.getByText('3.20M')).toBeInTheDocument();
    expect(summary.getByText('27% of the 11.8M tokens in the period')).toBeInTheDocument();
    expect(summary.getByText('184')).toBeInTheDocument();
    expect(summary.getByText('152 plain · 32 published an artifact')).toBeInTheDocument();
    expect(summary.getByText('90th percentile 58k')).toBeInTheDocument();
    expect(summary.getByText('2.66M')).toBeInTheDocument();
    expect(host.sent('GET', '/api/metrics/chat')[0]!.path).toContain('days=30');
  });

  it('shows each project on one scale, chat beside task work, and the tasks with the most chat', async () => {
    const { host } = withChat();
    start(host);

    const projects = within(await screen.findByRole('region', { name: 'Chat by project' }));
    expect(projects.getAllByText('garmin-coach-ts').length).toBeGreaterThan(0);
    expect(projects.getByText(/78 answers · 15k median/)).toBeInTheDocument();
    const beside = within(screen.getByRole('region', { name: 'Chat beside task work' }));
    expect(beside.getByText('Task work')).toBeInTheDocument();
    expect(beside.getByText('Chat answers with an artifact')).toBeInTheDocument();
    const top = within(screen.getByRole('region', { name: 'Tasks with the most chat' }));
    expect(top.getByRole('link', { name: 'Weekly training load chart' })).toHaveAttribute('href', '#/threads/t-load');
    expect(top.getByText('412,000')).toBeInTheDocument();
  });

  it('splits the trend by project, or into plain and artifact answers', async () => {
    const { host } = withChat();
    const user = start(host);

    const trend = within(await screen.findByRole('region', { name: 'Chat tokens over the period' }));
    expect(trend.getByRole('button', { name: 'By project' })).toHaveAttribute('aria-pressed', 'true');
    expect(trend.getByText('420k')).toBeInTheDocument(); // the column's total, as tokens
    expect(trend.getAllByText('insta-story-generator').length).toBeGreaterThan(0);

    await user.click(trend.getByRole('button', { name: 'Plain or Artifact' }));

    expect(trend.getByRole('button', { name: 'Plain or Artifact' })).toHaveAttribute('aria-pressed', 'true');
    const key = within(document.querySelector('[aria-label="Chat tokens over the period"] .chart-key') as HTMLElement);
    expect(key.getByText('Artifact')).toBeInTheDocument();
    expect(key.getByText('Plain')).toBeInTheDocument();
    expect(key.queryByText('garmin-coach-ts')).not.toBeInTheDocument();
  });

  it('asks again for one project and another period', async () => {
    const { host, insta } = withChat();
    const user = start(host);
    await screen.findByRole('region', { name: 'Chat summary' });

    await user.selectOptions(screen.getByRole('combobox', { name: 'Project' }), insta);
    await user.click(screen.getByRole('tab', { name: 'Last 7 days' }));

    await waitFor(() => expect(host.sent('GET', '/api/metrics/chat').some((r) => r.path.includes(`projectId=${insta}`) && r.path.includes('days=7'))).toBe(true));
  });

  it('says so when there was no chat', async () => {
    start(new FakeHost());

    expect(await screen.findByText('No chat answers in the period.')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Chat by project' })).not.toBeInTheDocument();
  });
});
