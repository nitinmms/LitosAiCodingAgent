import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import { App } from './App';
import { parseRoute, routeHash } from './domain/route';
import { FakeHost } from './test/fakeHost';

function start(host: FakeHost, hash = '#/tray') {
  window.location.hash = hash;
  host.signedIn = true;
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  return userEvent.setup();
}

describe('the tray app page', () => {
  it('has its own address', () => {
    expect(parseRoute('#/tray')).toEqual({ view: 'tray' });
    expect(routeHash({ view: 'tray' })).toBe('#/tray');
  });

  it('opens from the top bar, for anyone', async () => {
    const host = new FakeHost();
    host.signInAs(host.addPerson());
    const user = start(host, '#/board');

    await user.click(await screen.findByRole('button', { name: 'Tray app' }));

    expect(window.location.hash).toBe('#/tray');
    expect(await screen.findByRole('heading', { name: 'Tray app' })).toBeInTheDocument();
    expect(screen.getByLabelText('Factory address')).toHaveValue(`${window.location.origin}${window.location.pathname.replace(/\/$/, '')}`);
  });

  it('creates a token, shows it once, and lists it without it', async () => {
    const host = new FakeHost();
    const user = start(host);

    expect(await screen.findByText('You have no tokens yet.')).toBeInTheDocument();
    await user.type(screen.getByLabelText('Token name'), 'Work laptop');
    await user.click(screen.getByRole('button', { name: 'Create token' }));

    const shown = await screen.findByRole('status', { name: 'New token' });
    expect(within(shown).getByLabelText('Token')).toHaveValue('lsf_secret-tok-1');
    // An Admin's new token is in both lists: look at their own.
    const row = await within(screen.getByRole('region', { name: 'Your tokens' })).findByRole('row', { name: 'Work laptop' });
    expect(row).toHaveTextContent('Active');
    expect(row).toHaveTextContent('Never');
    expect(row).not.toHaveTextContent('lsf_');
    expect(host.sent('POST', '/api/access-tokens')).toHaveLength(1);
  });

  it('revokes a token', async () => {
    const host = new FakeHost();
    const user = start(host);
    await user.type(await screen.findByLabelText('Token name'), 'Old PC');
    await user.click(screen.getByRole('button', { name: 'Create token' }));

    const mine = screen.getByRole('region', { name: 'Your tokens' });
    await user.click(await within(mine).findByRole('button', { name: 'Revoke Old PC' }));

    expect(await screen.findByText(/“Old PC” is revoked/)).toBeInTheDocument();
    expect(within(mine).getByRole('row', { name: 'Old PC' })).toHaveTextContent('Revoked');
    expect(screen.queryByRole('button', { name: 'Revoke Old PC' })).not.toBeInTheDocument();
  });

  it("shows an Admin everyone's tokens, and a Member only their own", async () => {
    const host = new FakeHost();
    host.accessTokens.push({
      id: 'tok-ben',
      userId: 'u-ben',
      userName: 'ben',
      name: "Ben's laptop",
      status: 'Active',
      createdAt: '2026-10-20T09:00:00Z',
      lastUsedAt: '2026-10-21T08:00:00Z',
      revokedAt: null,
    });
    start(host);

    const everyone = await screen.findByRole('region', { name: "Everyone's tokens" });
    expect(await within(everyone).findByRole('row', { name: "Ben's laptop" })).toHaveTextContent('ben');
    expect(within(screen.getByRole('region', { name: 'Your tokens' })).getByText('You have no tokens yet.')).toBeInTheDocument();
  });

  it("does not show a Member anyone else's tokens", async () => {
    const host = new FakeHost();
    host.signInAs(host.addPerson());
    start(host);

    expect(await screen.findByRole('region', { name: 'Your tokens' })).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: "Everyone's tokens" })).not.toBeInTheDocument();
    expect(host.sent('GET', '/api/access-tokens?all=true')).toHaveLength(0);
  });

  it("says why a token can't be made", async () => {
    const host = new FakeHost();
    const user = start(host);
    await user.type(await screen.findByLabelText('Token name'), '   ');

    await user.click(screen.getByRole('button', { name: 'Create token' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Give the token a name');
  });
});
