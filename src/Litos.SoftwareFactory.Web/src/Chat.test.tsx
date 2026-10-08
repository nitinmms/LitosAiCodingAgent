import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createApi } from './api/client';
import { App } from './App';
import { FakeHost } from './test/fakeHost';

/** Chat before @factory (m2-architecture.md §5): a plain message is a question Litos answers without starting work. */

async function withThread(overrides: Parameters<FakeHost['addThread']>[1] = {}, setUp?: (host: FakeHost, threadId: string) => void) {
  const host = new FakeHost();
  const project = host.addProject();
  const thread = host.addThread(project, overrides);
  setUp?.(host, thread.id);
  host.signedIn = true;
  window.location.hash = '#/threads';
  render(<App createClient={(onSignedOut) => createApi(host.fetch, onSignedOut)} openEvents={host.openEvents} />);
  const user = userEvent.setup();
  await screen.findByRole('heading', { level: 1, name: thread.title });
  await waitFor(() => expect(host.sources).toHaveLength(1));
  return { host, thread, user };
}

const composer = () => screen.getByRole('textbox', { name: 'Message' });
const conversation = () => within(document.querySelector('.convo') as HTMLElement);

describe('asking a question', () => {
  it('sends it as typed, waits for the answer, then shows it, and the task stays a draft', async () => {
    const { host, thread, user } = await withThread();

    await user.type(composer(), 'Where are orders exported?');
    await user.click(screen.getByRole('button', { name: 'Ask' }));

    await waitFor(() => expect(composer()).toHaveValue(''));
    expect(host.sent('POST', `/api/threads/${thread.id}/messages`)[0]!.body).toMatchObject({ text: 'Where are orders exported?' });
    expect(await screen.findByText('Litos is reading the code to answer. Nothing is changed.')).toBeInTheDocument();
    expect(await conversation().findByText('Reading the code to answer...')).toBeInTheDocument();

    act(() => void host.answerChat(thread.id, 'In `src/Orders/Export.cs`.'));

    expect(await conversation().findByText('src/Orders/Export.cs')).toBeInTheDocument();
    await waitFor(() => expect(conversation().queryByText('Reading the code to answer...')).not.toBeInTheDocument());
    expect(host.details(thread.id).thread.state).toBe('Draft');
    expect(host.details(thread.id).run).toBeNull();
  });

  it('shows a question without the @factory mark, and a delegation with it', async () => {
    const { host, thread, user } = await withThread();

    await user.type(composer(), 'Is there an export already?{Enter}');
    await conversation().findByText('Is there an export already?');
    act(() => void host.answerChat(thread.id, 'No.'));
    await conversation().findByText('No.');
    await user.type(composer(), '@factory Add CSV export{Enter}');
    await conversation().findByText('Add CSV export');

    const bubbles = [...document.querySelectorAll('.m.user .bubble')];
    expect(bubbles.map((b) => b.textContent)).toEqual(['Is there an export already?', '@factory Add CSV export']);
  });

  it('holds a second question until the first is answered', async () => {
    const { host, thread, user } = await withThread();
    await user.type(composer(), 'First question?{Enter}');
    await conversation().findByText('Reading the code to answer...');

    await user.type(composer(), 'Second question?');

    expect(screen.getByRole('button', { name: 'Ask' })).toBeDisabled();
    expect(screen.getByText(/Litos is still answering your last question/)).toBeInTheDocument();
    await user.keyboard('{Enter}');
    expect(host.chatsSent).toEqual(['First question?']);

    act(() => void host.answerChat(thread.id, 'An answer.'));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Ask' })).toBeEnabled());
  });

  it('a thread that is already being answered says so when it opens', async () => {
    await withThread({}, (host, threadId) => {
      host.say(threadId, { author: 'User', kind: 'Text', text: 'Asked earlier?', payload: { plain: true } });
      host.details(threadId).chatPending = true;
    });

    expect(conversation().getByText('Reading the code to answer...')).toBeInTheDocument();
  });

  it('shows why there is no answer', async () => {
    const { host, thread, user } = await withThread();
    await user.type(composer(), 'What does this do?{Enter}');
    await conversation().findByText('Reading the code to answer...');

    act(() => void host.answerChat(thread.id, 'Litos could not answer that: The model is unavailable.', 'Status'));

    expect(await conversation().findByText('Litos could not answer that: The model is unavailable.')).toBeInTheDocument();
  });

  it('the empty thread says a question can be asked first', async () => {
    await withThread();

    expect(screen.getByText(/Not sure yet\? Ask a question without/)).toBeInTheDocument();
    expect(composer()).toHaveAttribute('placeholder', 'Ask a question, or start with @factory to say what you want done');
  });
});

describe('in other states', () => {
  it('to a running task, a plain message is guidance for its agent', async () => {
    const { host, thread, user } = await withThread({ state: 'Running', stage: 'Implement' });

    expect(composer()).toHaveAttribute('placeholder', 'Guidance for the agent, with or without @factory');
    await user.type(composer(), 'Use semicolons{Enter}');

    expect(await screen.findByText('Sent to the agent. It reads the message at its next safe point.')).toBeInTheDocument();
    expect(conversation().queryByText('Reading the code to answer...')).not.toBeInTheDocument();
    expect(host.details(thread.id).thread.state).toBe('Running');
  });

  it('an accepted task can be asked about, but takes no more work', async () => {
    const { host, thread, user } = await withThread({ state: 'Accepted', stage: 'Handoff', branch: 'factory/x' });

    expect(screen.getByRole('button', { name: '@factory' })).toBeDisabled();
    await user.type(composer(), '@factory one more thing');
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
    expect(screen.getByText(/This task is closed to new work; ask without @factory/)).toBeInTheDocument();

    await user.clear(composer());
    await user.type(composer(), 'Which tests cover this?{Enter}');

    await waitFor(() => expect(host.chatsSent).toEqual(['Which tests cover this?']));
    expect(host.details(thread.id).thread.state).toBe('Accepted');
  });

  it('a paused task takes nothing until it is resumed', async () => {
    await withThread({ state: 'PausedUser', stage: 'Implement' });

    expect(composer()).toBeDisabled();
  });

  it('a cancelled task has no composer', async () => {
    await withThread({ state: 'Cancelled', stage: 'Implement' });

    expect(screen.queryByRole('textbox', { name: 'Message' })).not.toBeInTheDocument();
  });
});
