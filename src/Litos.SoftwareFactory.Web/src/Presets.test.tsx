import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import type { PresetSettings } from './api/types';
import { App } from './App';
import { jsonProblem, isPresetName } from './components/PresetsTab';
import { baselineSummary } from './components/ProjectsPage';
import { ADMIN, DOTNET_PROFILE, FakeHost, NODE_PROFILE, baselineOf } from './test/fakeHost';

function start(host: FakeHost, hash: string) {
  host.signedIn = true;
  window.location.hash = hash;
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  return userEvent.setup();
}

async function onPresets(host: FakeHost) {
  const user = start(host, '#/settings/presets');
  await screen.findByRole('form', { name: 'Presets' });
  return user;
}

async function onProjects(host: FakeHost) {
  const user = start(host, '#/projects');
  await screen.findByRole('region', { name: 'Projects' });
  return user;
}

const card = (name: string) => within(screen.getByRole('region', { name }));

describe('the Presets tab', () => {
  it('shows the shipped presets, and saves a new one made from the first', async () => {
    const host = new FakeHost();
    const user = await onPresets(host);
    expect(screen.getByRole('tab', { name: 'Presets' })).toHaveAttribute('aria-selected', 'true');
    expect(card('.NET').getByLabelText('Profile')).toHaveValue(DOTNET_PROFILE);
    expect(card('.NET').getByText('Offered first')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Add a preset' }));
    await user.type(card('New preset').getByLabelText('Name'), 'dotnet-fast');
    await user.type(card('dotnet-fast').getByLabelText('Shown as'), '.NET, quick');
    await user.click(screen.getByRole('button', { name: 'Save presets' }));

    expect(await screen.findByText('The presets are saved.')).toBeInTheDocument();
    const sent = host.sent('PUT', '/api/admin/settings/presets')[0]!.body as { revision: number; settings: PresetSettings };
    expect(sent.revision).toBe(1);
    expect(sent.settings.presets.map((p) => p.name)).toEqual(['dotnet', 'node-react', 'dotnet-fast']);
    expect(sent.settings.presets[2]!.profile).toBe(DOTNET_PROFILE);
  });

  it('says at once when a profile is not JSON, and shows the host’s reasons for refusing a preset', async () => {
    const host = new FakeHost();
    const user = await onPresets(host);
    const profile = card('Node / React').getByLabelText('Profile');

    await user.clear(profile);
    await user.type(profile, 'not json');
    expect(card('Node / React').getByText(/^Not valid JSON/)).toBeInTheDocument();

    await user.clear(card('Node / React').getByLabelText('Name'));
    await user.type(card('Node / React').getByLabelText('Name'), 'Node');
    await user.click(screen.getByRole('button', { name: 'Save presets' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('"Node" is not a usable preset name.');
  });

  it('removes a preset, and shows nothing unsaved until something changes', async () => {
    const host = new FakeHost();
    const user = await onPresets(host);
    expect(screen.queryByText('Unsaved changes')).not.toBeInTheDocument();

    await user.click(card('Node / React').getByRole('button', { name: 'Remove Node / React' }));
    expect(screen.getByText('Unsaved changes')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Save presets' }));

    await screen.findByText('The presets are saved.');
    expect(host.presetSettings.settings.presets.map((p) => p.name)).toEqual(['dotnet']);
    expect(host.settings.presets).toEqual([{ name: 'dotnet', displayName: '.NET' }]);
  });

  it('knows a usable name and a profile that is JSON', () => {
    expect(isPresetName('python-3')).toBe(true);
    expect(isPresetName('Python')).toBe(false);
    expect(isPresetName('-x')).toBe(false);
    expect(jsonProblem('{"a":1}')).toBeNull();
    expect(jsonProblem('  ')).toBe('The profile is empty.');
    expect(jsonProblem('{')).toMatch(/^Not valid JSON/);
  });
});

describe('a project’s baseline', () => {
  it('says how each project’s default branch stands', async () => {
    const host = new FakeHost();
    host.addProject({ name: 'Green', baseline: baselineOf('Measured', { passedCount: 1_204 }) });
    host.addProject({ name: 'Red', gitHub: 'a/red', baseline: baselineOf('Measured', { unitTests: 'Failed', passedCount: 10, failedCount: 2 }) });
    host.addProject({
      name: 'Gone',
      gitHub: 'a/gone',
      baseline: baselineOf('CouldNotMeasure', { problem: 'Repository not found.' }),
    });
    host.addProject({
      name: 'Broken',
      gitHub: 'a/broken',
      baseline: baselineOf('Measured', { build: 'Failed', problems: ["Step 'dotnet': build failed with exit code 1."] }),
    });
    await onProjects(host);

    const rows = within(screen.getByRole('table'));
    expect(rows.getByText('1,204 tests pass')).toBeInTheDocument();
    expect(rows.getByText('2 of 12 tests fail')).toHaveClass('error');
    expect(rows.getByText('Could not be measured')).toBeInTheDocument();
    expect(rows.getByText('Repository not found.')).toBeInTheDocument();
    expect(rows.getByText('Build fails')).toBeInTheDocument();
    expect(rows.getByText("Step 'dotnet': build failed with exit code 1.")).toBeInTheDocument();
  });

  it('is shown as measuring when a project is registered, and looked at again until it is measured', async () => {
    const host = new FakeHost();
    const user = await onProjects(host);

    await user.type(screen.getByLabelText('GitHub URL'), 'https://github.com/nitinmms/filedb-sharp');
    await user.click(screen.getByRole('button', { name: 'Register project' }));
    expect(await within(screen.getByRole('table')).findByText('Measuring…')).toBeInTheDocument();
    expect(await screen.findByRole('status')).toHaveTextContent('its baseline is being measured');

    host.setBaseline(host.projects[0]!.id, baselineOf('Measured', { passedCount: 40 }));
    await waitFor(() => expect(within(screen.getByRole('table')).getByText('40 tests pass')).toBeInTheDocument(), { timeout: 5_000 });
  });

  it('summarises every state', () => {
    expect(baselineSummary(baselineOf('NotMeasured')).text).toBe('Not measured yet');
    expect(baselineSummary(baselineOf('Measured', { unitTests: 'NoTests' }))).toEqual({ text: 'Builds; no tests found', bad: true });
    expect(baselineSummary(baselineOf('Measured', { build: 'Unavailable' })).text).toBe('A command could not start');
    expect(baselineSummary(null).text).toBe('');
  });
});

describe('changing a project’s profile', () => {
  it('replaces it with a preset, and the host measures its baseline again', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const user = await onProjects(host);

    await user.click(screen.getByRole('button', { name: 'Change the profile of SalesApp' }));
    const editor = within(screen.getByRole('region', { name: 'Profile of SalesApp' }));
    expect(editor.getByText(/^Revision 1\./)).toBeInTheDocument();
    await user.selectOptions(editor.getByLabelText('Replace it with'), 'preset');
    await user.selectOptions(editor.getByLabelText('Preset'), 'node-react');
    await user.click(editor.getByRole('button', { name: 'Save profile' }));

    expect(await screen.findByRole('status')).toHaveTextContent('SalesApp has a new profile, revision 2. Its baseline is being measured again.');
    expect(host.sent('PATCH', `/api/projects/${project.id}`)[0]!.body).toEqual({ preset: 'node-react', coverageThresholdPercent: 80 });
    expect(host.projects[0]!.profile).toBe(NODE_PROFILE);
    expect(within(screen.getByRole('table')).getByText('Measuring…')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Profile of SalesApp' })).not.toBeInTheDocument();
  });

  it('edits the profile as written, refusing text that is not JSON before sending it', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const user = await onProjects(host);
    await user.click(screen.getByRole('button', { name: 'Change the profile of SalesApp' }));
    const editor = within(screen.getByRole('region', { name: 'Profile of SalesApp' }));
    const profile = editor.getByLabelText('Profile');
    // Laid out for editing.
    expect((profile as HTMLTextAreaElement).value).toBe(JSON.stringify(JSON.parse(DOTNET_PROFILE), null, 2));

    await user.type(profile, 'x');
    expect(editor.getByRole('button', { name: 'Save profile' })).toBeDisabled();

    await user.clear(profile);
    await user.click(profile);
    await user.paste(NODE_PROFILE);
    await user.click(editor.getByRole('button', { name: 'Save profile' }));

    await screen.findByText(/has a new profile/);
    expect((host.sent('PATCH', `/api/projects/${project.id}`)[0]!.body as { profileJson: string }).profileJson).toBe(NODE_PROFILE);
  });

  it('shows the host’s reason, such as a program it cannot find', async () => {
    const host = new FakeHost();
    host.addProject();
    host.missingPrograms.add('npm');
    const user = await onProjects(host);
    await user.click(screen.getByRole('button', { name: 'Change the profile of SalesApp' }));
    const editor = within(screen.getByRole('region', { name: 'Profile of SalesApp' }));

    await user.selectOptions(editor.getByLabelText('Replace it with'), 'preset');
    await user.selectOptions(editor.getByLabelText('Preset'), 'node-react');
    await user.click(editor.getByRole('button', { name: 'Save profile' }));

    expect(await editor.findByRole('alert')).toHaveTextContent('The host cannot find npm on its PATH');
    expect(host.projects[0]!.profileRevision).toBe(1);
  });

  it('changes only the threshold when asked to', async () => {
    const host = new FakeHost();
    const project = host.addProject();
    const user = await onProjects(host);
    await user.click(screen.getByRole('button', { name: 'Change the profile of SalesApp' }));
    const editor = within(screen.getByRole('region', { name: 'Profile of SalesApp' }));

    await user.selectOptions(editor.getByLabelText('Replace it with'), 'threshold');
    expect(editor.queryByLabelText('Profile')).not.toBeInTheDocument();
    await user.clear(editor.getByLabelText('Changed-line coverage threshold (%)'));
    await user.type(editor.getByLabelText('Changed-line coverage threshold (%)'), '90');
    await user.click(editor.getByRole('button', { name: 'Save profile' }));

    await screen.findByText(/has a new profile/);
    expect(host.sent('PATCH', `/api/projects/${project.id}`)[0]!.body).toEqual({ coverageThresholdPercent: 90 });
    expect(within(screen.getByRole('table')).getByText('90%')).toBeInTheDocument();
  });

  it('is not offered to a Member', async () => {
    const host = new FakeHost();
    host.user = { ...ADMIN, roles: ['Member'] };
    host.addProject();
    await onProjects(host);

    expect(screen.queryByRole('button', { name: 'Change the profile of SalesApp' })).not.toBeInTheDocument();
  });
});
