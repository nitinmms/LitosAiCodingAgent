import { act, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createApi } from '../api/client';
import type { LifecycleState, Stage } from '../api/types';
import { App } from '../App';
import { FakeHost, turnOf } from '../test/fakeHost';
import { Rail, Rich, SafeLink } from './bits';
import { phaseName, phaseTotals } from './Details';

describe('the stage rail', () => {
  const stations = () =>
    screen.getAllByRole('listitem').map((item) => `${item.className.replace(/^st ?/, '') || '-'}|${item.textContent}`);

  const rail = (stage: Stage, state: LifecycleState) => render(<Rail stage={stage} state={state} turn={turnOf(state)} />);

  it('a draft sits at the first station', () => {
    rail('Discuss', 'Draft');
    expect(stations()).toEqual(['now|1Discuss', '-|2Spec', '-|3Implement', '-|4Verify', '-|5Review', '-|6Handoff', '-|7Done']);
  });

  it('a running task moves, with the stations behind it done and the spec station skipped', () => {
    rail('Verify', 'Running');
    expect(stations()).toEqual([
      'done|✓Discuss',
      'skip|–Spec (skipped)',
      'done|✓Implement',
      'now moving|4Verify',
      '-|5Review',
      '-|6Handoff',
      '-|7Done',
    ]);
  });

  it('a task waiting on a person is marked as theirs, and does not move', () => {
    rail('Handoff', 'AwaitingHumanTesting');
    expect(stations()[5]).toBe('now you|6Handoff');
  });

  /** A cancelled task used to show the stage it stopped in as if it were still under way. */
  it('a cancelled task shows how far it got, and no station as current', () => {
    rail('Review', 'Cancelled');
    expect(stations()).toEqual([
      'done|\u2713Discuss',
      'skip|\u2013Spec (skipped)',
      'done|\u2713Implement',
      'done|\u2713Verify',
      '-|5Review',
      '-|6Handoff',
      '-|7Done',
    ]);
    expect(screen.getAllByRole('listitem').some((item) => item.getAttribute('aria-current'))).toBe(false);
  });

  it('an accepted task has every station behind it', () => {
    rail('Done', 'Accepted');
    expect(stations()[6]).toBe('done|✓Done');
    expect(screen.getAllByRole('listitem').some((item) => item.getAttribute('aria-current'))).toBe(false);
  });
});

describe('message text', () => {
  it('marks up code spans and mentions, and leaves everything else as text', () => {
    const { container } = render(
      <p>
        <Rich text={'Edit `CsvWriter.cs` then ask @factory <b>not html</b>'} />
      </p>,
    );

    expect(container.querySelector('code')).toHaveTextContent('CsvWriter.cs');
    expect(container.querySelector('.mention')).toHaveTextContent('@factory');
    expect(container.querySelector('b')).toBeNull();
    expect(container).toHaveTextContent('Edit CsvWriter.cs then ask @factory <b>not html</b>');
  });

  it('does not treat a longer word as a mention', () => {
    const { container } = render(<Rich text="@factoryfoo" />);
    expect(container.querySelector('.mention')).toBeNull();
  });
});

describe('links', () => {
  it.each(['javascript:alert(1)', 'http://example.com', 'data:text/html,x', '', null, undefined])('%j is shown as text, not as a link', (href) => {
    render(<SafeLink href={href}>PR</SafeLink>);
    expect(screen.getByText('PR')).toBeInTheDocument();
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
  });

  it('an https address opens in a new tab without handing over the opener', () => {
    render(<SafeLink href="https://github.com/a/b/pull/1">PR</SafeLink>);
    const link = screen.getByRole('link', { name: 'PR' });
    expect(link).toHaveAttribute('target', '_blank');
    expect(link.getAttribute('rel')).toContain('noopener');
  });
});

describe('the thread list', () => {
  afterEach(() => vi.useRealTimers());

  /** The board's stream (GET /api/events) keeps every visible thread live; nothing is polled. */
  it('picks up threads that change or appear elsewhere, without a reload', async () => {
    const host = new FakeHost();
    host.signedIn = true;
    const project = host.addProject();
    const open = host.addThread(project, { title: 'Open thread' });
    const other = host.addThread(project, { title: 'Other thread', state: 'Running', stage: 'Implement' });
    const createClient = (onSignedOut: () => void) => createApi(host.fetch, onSignedOut);
    if (!window.location.hash) window.location.hash = '#/threads';
    render(<App createClient={createClient} openEvents={host.openEvents} />);
    await screen.findByRole('heading', { level: 1, name: 'Open thread' });
    expect(screen.getByRole('button', { name: '0 awaiting you' })).toBeDisabled();
    await waitFor(() => expect(host.boardSources).toHaveLength(1));
    // The board listens from where the list it loaded left off.
    expect(host.boardSources[0]!.url).toBe(`/api/events?after=${host.lastSequence}`);

    // Neither change is announced on the open thread's stream, only on the board's.
    act(() => {
      host.change(other.id, { state: 'AwaitingHumanTesting', stage: 'Handoff' });
      host.addThread(project, { title: 'Made in another tab' });
    });

    expect(await screen.findByRole('button', { name: /Made in another tab/ })).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('button', { name: '1 awaiting you' })).toBeEnabled());
    expect(host.details(open.id).thread.state).toBe('Draft');
    expect(host.sent('GET', '/api/threads').filter((r) => r.path === '/api/threads')).toHaveLength(1);
  });

  it('stops listening to the board when signed out', async () => {
    const host = new FakeHost();
    host.signedIn = true;
    host.addProject();
    const createClient = (onSignedOut: () => void) => createApi(host.fetch, onSignedOut);
    render(<App createClient={createClient} openEvents={host.openEvents} />);
    await waitFor(() => expect(host.boardSources).toHaveLength(1));

    act(() => screen.getByRole('button', { name: 'Sign out' }).click());

    await waitFor(() => expect(host.boardSources[0]!.closed).toBe(true));
  });
});

describe('model call phases', () => {
  it('names the decision scan, and counts it apart from implementation and review', () => {
    expect(phaseName('Scan')).toBe('decision scan');
    expect(phaseName('LightReview')).toBe('light review');
    expect(phaseName(null)).toBe('other');

    const call = (phase: string, charged: number) => ({ phase, charged }) as Parameters<typeof phaseTotals>[0][number];
    expect(phaseTotals([call('Scan', 30), call('Implement', 100), call('Review', 40)])).toEqual({ implementation: 100, review: 40, other: 30 });
  });
});
