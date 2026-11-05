import { render, screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { ArtifactLink } from '../api/types';
import { MessageArtifacts } from './Artifacts';

/** The cards under a chat answer for the pages it published (ReadMe_ArtifactsFeature.md §11.2). */
describe('published pages on an answer', () => {
  const page = (over: Partial<ArtifactLink> = {}): ArtifactLink => ({
    id: 'a1',
    title: 'Export Pipeline',
    version: 1,
    latestVersion: 1,
    url: '/api/artifacts/a1/v/1',
    latestUrl: '/api/artifacts/a1',
    ...over,
  });

  it('shows nothing for an answer that published no page, or from an older host', () => {
    const { container: none } = render(<MessageArtifacts artifacts={[]} />);
    const { container: older } = render(<MessageArtifacts artifacts={undefined} />);
    expect(none.innerHTML).toBe('');
    expect(older.innerHTML).toBe('');
  });

  it('shows a card per page, opening this version in a new tab that cannot reach back', () => {
    render(<MessageArtifacts artifacts={[page(), page({ id: 'b2', title: 'Coverage', url: '/api/artifacts/b2/v/1' })]} />);

    const cards = screen.getAllByRole('listitem');
    expect(cards.map((c) => within(c).getByText(/Export Pipeline|Coverage/).textContent)).toEqual(['Export Pipeline', 'Coverage']);
    const open = screen.getByRole('link', { name: 'Open Export Pipeline in a new tab' });
    expect(open.getAttribute('href')).toBe('/api/artifacts/a1/v/1');
    expect(open.getAttribute('target')).toBe('_blank');
    expect(open.getAttribute('rel')).toContain('noopener');
    expect(within(cards[0]!).getByText(/version 1/)).toBeTruthy();
    expect(screen.queryByText(/newer/)).toBeNull();
  });

  it('on an earlier answer, says a later one updated the page and links the newest version', () => {
    render(<MessageArtifacts artifacts={[page({ latestVersion: 3 })]} />);

    const newer = screen.getByRole('link', { name: 'newer: version 3' });
    expect(newer.getAttribute('href')).toBe('/api/artifacts/a1');
    expect(newer.getAttribute('target')).toBe('_blank');
    expect(newer.getAttribute('rel')).toContain('noopener');
    // The card itself still opens the version this answer published.
    expect(screen.getByRole('link', { name: 'Open Export Pipeline in a new tab' }).getAttribute('href')).toBe('/api/artifacts/a1/v/1');
  });
});
