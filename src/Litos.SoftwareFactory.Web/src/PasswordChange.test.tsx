import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import { App } from './App';
import { FakeHost, PASSWORD } from './test/fakeHost';

/** Changing a password, and the one-time password an installer shows once (SoftwareFactoryInstallation.md §4.4). */

function start(host: FakeHost) {
  host.signedIn = true;
  window.location.hash = '#/threads';
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  return userEvent.setup();
}

async function fill(user: ReturnType<typeof userEvent.setup>, current: string, next: string, again = next) {
  await user.type(screen.getByLabelText(/password you signed in with|Current password/), current);
  await user.type(screen.getByLabelText('New password'), next);
  await user.type(screen.getByLabelText('New password again'), again);
  await user.click(screen.getByRole('button', { name: 'Save new password' }));
}

describe('a one-time password', () => {
  it('shows only the change-password screen, and the factory once a new password is chosen', async () => {
    const host = new FakeHost();
    host.addProject();
    host.user = { ...host.user, mustChangePassword: true };
    const user = start(host);

    expect(await screen.findByRole('heading', { name: 'Choose your own password' })).toBeInTheDocument();
    expect(screen.queryByRole('navigation', { name: 'Main' })).not.toBeInTheDocument();
    expect(host.sent('GET', '/api/threads')).toHaveLength(0);

    await fill(user, PASSWORD, 'my-own-long-password');

    expect(await screen.findByRole('navigation', { name: 'Main' })).toBeInTheDocument();
    expect(host.password).toBe('my-own-long-password');
  });

  it('says why when the password signed in with is wrong', async () => {
    const host = new FakeHost();
    host.user = { ...host.user, mustChangePassword: true };
    const user = start(host);
    await screen.findByRole('heading', { name: 'Choose your own password' });

    await fill(user, 'not it at all', 'my-own-long-password');

    expect(await screen.findByText('The current password is incorrect.')).toBeInTheDocument();
  });
});

describe('changing a password from the top bar', () => {
  it('changes it, and says so', async () => {
    const host = new FakeHost();
    host.addProject();
    const user = start(host);
    await user.click(await screen.findByRole('button', { name: 'Change password' }));

    await fill(user, PASSWORD, 'another-long-password');

    expect(await screen.findByText(/Your password is changed/)).toBeInTheDocument();
    expect(screen.queryByRole('form', { name: 'Change password' })).not.toBeInTheDocument();
    expect(host.password).toBe('another-long-password');
  });

  it('checks length and the repeat before asking the host', async () => {
    const host = new FakeHost();
    const user = start(host);
    await user.click(await screen.findByRole('button', { name: 'Change password' }));

    await fill(user, PASSWORD, 'short');
    expect(await screen.findByText('The new password must be at least 12 characters.')).toBeInTheDocument();

    await user.clear(screen.getByLabelText('New password'));
    await user.clear(screen.getByLabelText('New password again'));
    await user.type(screen.getByLabelText('New password'), 'a-long-password-1');
    await user.type(screen.getByLabelText('New password again'), 'a-long-password-2');
    await user.click(screen.getByRole('button', { name: 'Save new password' }));

    expect(await screen.findByText('The two new passwords are not the same.')).toBeInTheDocument();
    await waitFor(() => expect(host.sent('POST', '/api/auth/password')).toHaveLength(0));
  });
});
