import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import { App } from './App';
import { invitationUrl } from './components/PeoplePage';
import { FakeHost } from './test/fakeHost';

const LONG = 'a long enough password';

function start(host: FakeHost, { signedIn = true } = {}) {
  host.signedIn = signedIn;
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  return userEvent.setup();
}

// ---- The invitation page ----

describe('opening an invitation link', () => {
  async function openLink(host: FakeHost, token: string) {
    window.location.hash = `#/invite/${token}`;
    const user = start(host, { signedIn: false });
    return user;
  }

  it('says who the link is for, creates the account and signs them in', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const token = host.invite('erin', 'Member', [project.id]);
    const user = await openLink(host, token);

    expect(await screen.findByText(/You will sign in as/)).toHaveTextContent('erin');
    await user.type(screen.getByLabelText('Your name'), 'Erin Mwangi');
    await user.type(screen.getByLabelText('Choose a password'), LONG);
    await user.type(screen.getByLabelText('Repeat the password'), LONG);
    await user.click(screen.getByRole('button', { name: 'Create account and sign in' }));

    expect(await screen.findByRole('button', { name: 'New thread' })).toBeInTheDocument();
    expect(screen.getByText('Erin Mwangi')).toBeInTheDocument();
    expect(window.location.hash).toBe('#/threads');
    expect(host.invitations[0]!.acceptedAt).not.toBeNull();
    // The token never leaves the request body.
    expect(host.requests.every((r) => !r.path.includes(token))).toBe(true);
  });

  it('says so, and asks the host nothing, when the passwords differ or are too short', async () => {
    const host = new FakeHost();
    const user = await openLink(host, host.invite());
    await screen.findByText(/You will sign in as/);

    await user.type(screen.getByLabelText('Choose a password'), 'short');
    await user.type(screen.getByLabelText('Repeat the password'), 'short');
    await user.click(screen.getByRole('button', { name: 'Create account and sign in' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Use at least 12 characters.');

    await user.clear(screen.getByLabelText('Choose a password'));
    await user.type(screen.getByLabelText('Choose a password'), LONG);
    await user.click(screen.getByRole('button', { name: 'Create account and sign in' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('The two passwords are different.');

    expect(host.sent('POST', '/api/invitations/accept')).toHaveLength(0);
  });

  it('explains a link that matches nothing', async () => {
    const host = new FakeHost();
    await openLink(host, 'not-a-token');

    expect(await screen.findByRole('heading', { name: 'This link does not work' })).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent('This invitation link is not valid.');
    expect(screen.queryByLabelText('Choose a password')).not.toBeInTheDocument();
  });

  it('explains a link that was already used', async () => {
    const host = new FakeHost();
    const token = host.invite();
    host.invitations[0]!.acceptedAt = '2026-10-02T09:00:00Z';
    await openLink(host, token);

    expect(await screen.findByRole('alert')).toHaveTextContent('This invitation has already been used.');
  });

  it('explains a link revoked while the page was open', async () => {
    const host = new FakeHost();
    const user = await openLink(host, host.invite());
    await screen.findByText(/You will sign in as/);
    host.invitations[0]!.revokedAt = '2026-10-01T10:00:00Z';

    await user.type(screen.getByLabelText('Choose a password'), LONG);
    await user.type(screen.getByLabelText('Repeat the password'), LONG);
    await user.click(screen.getByRole('button', { name: 'Create account and sign in' }));

    expect(await screen.findByRole('heading', { name: 'This link does not work' })).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent('revoked');
  });
});

// ---- The Admin's people screen ----

describe('the people screen', () => {
  async function onPeople(host: FakeHost) {
    window.location.hash = '#/people';
    const user = start(host);
    await screen.findByRole('region', { name: 'People' });
    return user;
  }

  const row = (name: string) => screen.getByRole('row', { name });

  it('is in the top bar for an admin only', async () => {
    const host = new FakeHost();
    host.signInAs(host.addPerson());
    window.location.hash = '#/people';
    start(host);

    expect(await screen.findByText('Only an admin can manage people.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'People' })).not.toBeInTheDocument();
    expect(host.sent('GET', '/api/users')).toHaveLength(0);
  });

  it('creates an invitation and shows its whole link once, ready to copy', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const user = await onPeople(host);

    await user.type(screen.getByLabelText('Username'), 'erin');
    await user.click(screen.getByRole('checkbox', { name: project.name }));
    await user.click(screen.getByRole('button', { name: 'Create invitation link' }));

    const link = (await screen.findByLabelText('Invitation link')) as HTMLInputElement;
    expect(link.value).toBe(invitationUrl(`#/invite/token-${host.invitations[0]!.id}`));
    expect(screen.getByRole('status', { name: 'New invitation' })).toHaveTextContent('It is shown only now');
    expect(host.invitations[0]!.projectIds).toEqual([project.id]);
    const invitations = within(screen.getByRole('region', { name: 'Invitations' }));
    expect(await invitations.findByText('Pending')).toBeInTheDocument();
    expect(screen.getByLabelText('Username')).toHaveValue('');
  });

  it("shows the host's reason when an invitation cannot be created", async () => {
    const host = new FakeHost();
    host.addPerson({ userName: 'ben' });
    const user = await onPeople(host);

    await user.type(screen.getByLabelText('Username'), 'ben');
    await user.click(screen.getByRole('button', { name: 'Create invitation link' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Someone already signs in as ben.');
    expect(screen.queryByLabelText('Invitation link')).not.toBeInTheDocument();
  });

  it('revokes an invitation still waiting', async () => {
    const host = new FakeHost();
    host.invite('erin');
    const user = await onPeople(host);
    const invitations = within(screen.getByRole('region', { name: 'Invitations' }));

    await user.click(await invitations.findByRole('button', { name: 'Revoke' }));

    expect(await invitations.findByText('Revoked')).toBeInTheDocument();
    expect(invitations.queryByRole('button', { name: 'Revoke' })).not.toBeInTheDocument();
  });

  it('disables someone and enables them again', async () => {
    const host = new FakeHost();
    const ben = host.addPerson();
    const user = await onPeople(host);

    await user.click(within(await screen.findByRole('row', { name: 'Ben Okafor' })).getByRole('button', { name: 'Disable' }));
    expect(await within(row('Ben Okafor')).findByText('Disabled')).toBeInTheDocument();
    expect(await screen.findByRole('status')).toHaveTextContent('Their open sessions end within a minute.');
    expect(ben.disabled).toBe(true);

    await user.click(within(row('Ben Okafor')).getByRole('button', { name: 'Enable' }));
    expect(await within(row('Ben Okafor')).findByText('Active')).toBeInTheDocument();
  });

  it('offers no way to disable yourself', async () => {
    const host = new FakeHost();
    await onPeople(host);

    const you = await screen.findByRole('row', { name: 'Priya Raman' });
    expect(within(you).getByText('(you)')).toBeInTheDocument();
    expect(within(you).queryByRole('button', { name: 'Disable' })).not.toBeInTheDocument();
  });

  it("changes someone's role, and explains why the last admin's cannot change", async () => {
    const host = new FakeHost();
    const ben = host.addPerson();
    const user = await onPeople(host);

    await user.selectOptions(await screen.findByLabelText('Role of Priya Raman'), 'Member');
    expect(await screen.findByRole('alert')).toHaveTextContent('admin is the last enabled Admin');
    expect(screen.getByLabelText('Role of Priya Raman')).toHaveValue('Admin');

    await user.selectOptions(screen.getByLabelText('Role of Ben Okafor'), 'Admin');
    await waitFor(() => expect(ben.role).toBe('Admin'));
    expect(screen.getByLabelText('Role of Ben Okafor')).toHaveValue('Admin');
  });

  it('adds someone to a project and takes them off it', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const ben = host.addPerson();
    const user = await onPeople(host);

    await user.selectOptions(await screen.findByLabelText('Add Ben Okafor to a project'), project.name);
    expect(await within(row('Ben Okafor')).findByText(project.name)).toBeInTheDocument();
    expect(ben.projectIds).toEqual([project.id]);

    await user.click(screen.getByRole('button', { name: `Remove Ben Okafor from ${project.name}` }));
    await waitFor(() => expect(ben.projectIds).toEqual([]));
    expect(screen.queryByRole('button', { name: `Remove Ben Okafor from ${project.name}` })).not.toBeInTheDocument();
  });
});
