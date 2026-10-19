import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import { App } from './App';
import { fileSize, isImageName } from './components/Attachments';
import { fromAttachmentsForm, toAttachmentsForm } from './components/AttachmentsTab';
import { FakeHost } from './test/fakeHost';

/** Files and images as task input (attachments-architecture.md §7). */

function start(host: FakeHost, hash = '#/threads') {
  host.signedIn = true;
  window.location.hash = hash;
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  return userEvent.setup();
}

async function withThread(overrides: Parameters<FakeHost['addThread']>[1] = {}, prepare?: (host: FakeHost) => void) {
  const host = new FakeHost();
  prepare?.(host);
  const project = host.addProject();
  const thread = host.addThread(project, overrides);
  const user = start(host, `#/threads/${thread.id}`);
  await screen.findByRole('heading', { level: 1, name: thread.title });
  return { host, thread, user };
}

const png = (name = 'screen.png', size = 2_048) => new File([new Uint8Array(size)], name, { type: 'image/png' });
const composer = () => screen.getByRole('textbox', { name: 'Message' });
const chips = () => screen.queryByRole('list', { name: 'Files to send' });

describe('attaching files to a message', () => {
  it('uploads a picked file at once, sends it with the message, and shows it on the message', async () => {
    const { host, user } = await withThread();

    await user.upload(screen.getByLabelText('Attach files'), png());
    expect(await within(chips()!).findByText('2 KB', { exact: false })).toBeInTheDocument();
    await user.type(composer(), '@factory Fix the button in the screenshot.');
    await user.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => expect(host.sent('POST', '/messages')).toHaveLength(1));
    const body = host.sent('POST', '/messages')[0]!.body as { attachmentIds: string[] };
    expect(body.attachmentIds).toHaveLength(1);
    expect(host.uploads.get(body.attachmentIds[0]!)?.fileName).toBe('screen.png');
    await waitFor(() => expect(chips()).not.toBeInTheDocument());
    const sent = await screen.findByRole('img', { name: 'screen.png' });
    expect(sent).toHaveAttribute('src', `/api/attachments/${body.attachmentIds[0]}`);
  });

  it('takes a pasted screenshot as a file, and leaves pasted text alone', async () => {
    const { host } = await withThread();

    fireEvent.paste(composer(), { clipboardData: { files: [png('pasted.png')], types: ['Files'] } });

    expect(await within(chips()!).findByText('pasted.png')).toBeInTheDocument();
    await waitFor(() => expect(host.sent('POST', '/attachments')).toHaveLength(1));
  });

  it('takes a dropped file', async () => {
    const { host } = await withThread();
    const box = composer().closest('.compose-box')!;

    fireEvent.dragOver(box, { dataTransfer: { types: ['Files'], files: [] } });
    fireEvent.drop(box, { dataTransfer: { types: ['Files'], files: [png('dropped.png')] } });

    expect(await within(chips()!).findByText('dropped.png')).toBeInTheDocument();
    await waitFor(() => expect(host.sent('POST', '/attachments')).toHaveLength(1));
  });

  it("shows the host's reason for a refused file, and sends the message without it", async () => {
    const { host, user } = await withThread();

    await user.upload(screen.getByLabelText('Attach files'), new File([new Uint8Array([77, 90, 0])], 'tool.exe'));
    expect(await within(chips()!).findByText(/binary file with no known document format/)).toBeInTheDocument();
    await user.type(composer(), '@factory Do it.');
    await user.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => expect(host.sent('POST', '/messages')).toHaveLength(1));
    expect(host.sent('POST', '/messages')[0]!.body).not.toHaveProperty('attachmentIds');
  });

  it('refuses more files than a message may carry, and a file over the size limit, before uploading them', async () => {
    const { host, user } = await withThread({}, (h) => {
      h.settings = { ...h.settings, attachments: { ...h.settings.attachments!, maxFilesPerMessage: 1, maxFileBytes: 10_000 } };
    });

    await user.upload(screen.getByLabelText('Attach files'), [png('a.png'), png('b.png'), png('big.png', 20_000)]);

    const list = await waitFor(() => chips()!);
    expect(within(list).getByText(/A message can carry at most 1 files/)).toBeInTheDocument();
    expect(within(list).getByText(/Over the 10 KB limit/)).toBeInTheDocument();
    await waitFor(() => expect(host.sent('POST', '/attachments')).toHaveLength(1));
  });

  it('takes a file off the message', async () => {
    const { user } = await withThread();
    await user.upload(screen.getByLabelText('Attach files'), png());
    await within(chips()!).findByText('2 KB', { exact: false });

    await user.click(screen.getByRole('button', { name: 'Remove screen.png' }));

    expect(chips()).not.toBeInTheDocument();
  });

  it('warns when the thread\'s model does not read images', async () => {
    const { user } = await withThread({}, (h) => {
      h.settings = {
        ...h.settings,
        providers: h.settings.providers.map((p) => ({ ...p, models: p.models.map((m) => ({ ...m, seesImages: false })) })),
      };
    });

    await user.upload(screen.getByLabelText('Attach files'), png());

    expect(await screen.findByText(/does not read images: Litos gets the file, not what it shows/)).toBeInTheDocument();
  });

  it('cannot attach to a task while it works', async () => {
    await withThread({ state: 'Running', stage: 'Implement' });

    expect(screen.getByRole('button', { name: 'Attach' })).toBeDisabled();
  });

  it('offers no attaching when an Admin has turned it off', async () => {
    await withThread({}, (h) => {
      h.settings = { ...h.settings, attachments: { ...h.settings.attachments!, enabled: false } };
    });

    expect(screen.queryByRole('button', { name: 'Attach' })).not.toBeInTheDocument();
  });
});

describe('the Attachments settings tab', () => {
  it('saves the switch and the limits, the total in megabytes', async () => {
    const host = new FakeHost();
    const user = start(host, '#/settings/attachments');

    const files = await screen.findByLabelText('Files per message');
    await user.clear(files);
    await user.type(files, '3');
    await user.clear(screen.getByLabelText('Total per task'));
    await user.type(screen.getByLabelText('Total per task'), '40');
    await user.click(screen.getByRole('button', { name: 'Save attachments' }));

    await waitFor(() => expect(host.sent('PUT', '/settings/attachments')).toHaveLength(1));
    expect(host.sent('PUT', '/settings/attachments')[0]!.body).toEqual({
      revision: 0,
      settings: { enabled: true, maxFilesPerMessage: 3, maxBytesPerThread: 40 * 1024 * 1024 },
    });
    expect(host.settings.attachments!.maxFilesPerMessage).toBe(3);
  });

  it("shows the host's reason for a limit out of range", async () => {
    const host = new FakeHost();
    const user = start(host, '#/settings/attachments');

    const files = await screen.findByLabelText('Files per message');
    await user.clear(files);
    await user.type(files, '0');
    await user.click(screen.getByRole('button', { name: 'Save attachments' }));

    expect(await screen.findByText('Files per message must be between 1 and 20.')).toBeInTheDocument();
  });
});

describe('the Providers tab', () => {
  it('saves whether an allowed model reads images, and leaves the provider\'s usual unsaid', async () => {
    const host = new FakeHost();
    const user = start(host, '#/settings/providers');

    const choice = (await screen.findAllByRole('combobox', { name: /reads images/ }))[0]!;
    expect(choice).toHaveValue('usual');
    await user.selectOptions(choice, 'yes');
    await user.click(screen.getByRole('button', { name: 'Save providers' }));

    await waitFor(() => expect(host.sent('PUT', '/settings/providers')).toHaveLength(1));
    const saved = host.sent('PUT', '/settings/providers')[0]!.body as { settings: { providers: { models: { acceptsImages?: boolean }[] }[] } };
    const models = saved.settings.providers.flatMap((p) => p.models);
    expect(models.filter((m) => m.acceptsImages === true)).toHaveLength(1);
    expect(models.filter((m) => !('acceptsImages' in m))).toHaveLength(models.length - 1);
  });
});

describe('helpers', () => {
  it('names sizes as people read them', () => {
    expect([fileSize(900), fileSize(2_048), fileSize(3 * 1024 * 1024), fileSize(3.45 * 1024 * 1024)]).toEqual(['900 B', '2 KB', '3 MB', '3.5 MB']);
  });

  it('knows the image types every Litos face sends as images', () => {
    expect(['a.PNG', 'b.jpeg', 'c.gif', 'd.webp', 'e.svg', 'f.pdf'].map(isImageName)).toEqual([true, true, true, true, false, false]);
  });

  it('reads the tab form, refusing what is not a whole number', () => {
    const form = toAttachmentsForm({ enabled: true, maxFilesPerMessage: 5, maxBytesPerThread: 50 * 1024 * 1024 });
    expect(form.maxMegabytesPerThread).toBe('50');
    expect(fromAttachmentsForm({ ...form, maxFilesPerMessage: 'x' })).toEqual({ error: 'Files per message must be a whole number.' });
    expect(fromAttachmentsForm({ ...form, maxMegabytesPerThread: '1,024' })).toEqual({
      settings: { enabled: true, maxFilesPerMessage: 5, maxBytesPerThread: 1024 * 1024 * 1024 },
    });
  });
});
