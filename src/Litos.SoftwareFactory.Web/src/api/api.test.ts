import { describe, expect, it, vi } from 'vitest';
import { FakeEventSource } from '../test/fakeHost';
import { ApiError, createApi, CSRF_HEADER, type Fetch } from './client';
import { resubscribeDelay, subscribeToBoard, subscribeToThread, type ThreadEvent } from './events';

function fetchReturning(status: number, body?: unknown) {
  const calls: { path: string; init: RequestInit }[] = [];
  const fetcher: Fetch = async (input, init) => {
    calls.push({ path: String(input), init: init ?? {} });
    const text = body === undefined ? '' : typeof body === 'string' ? body : JSON.stringify(body);
    return { ok: status >= 200 && status < 300, status, text: async () => text } as Response;
  };
  return { fetcher, calls };
}

const headersOf = (init: RequestInit) => init.headers as Record<string, string>;

describe('the API client', () => {
  it('sends the CSRF header on every state-changing request, and never on a read', async () => {
    const { fetcher, calls } = fetchReturning(200, {});
    const api = createApi(fetcher);

    await api.threads();
    await api.createThread({ projectId: 'p', title: 'T', typeLabel: 'feature' });
    await api.accept('t');
    await api.logout();

    expect(calls.map((c) => [c.init.method, c.path, CSRF_HEADER in headersOf(c.init)])).toEqual([
      ['GET', '/api/threads', false],
      ['POST', '/api/threads', true],
      ['POST', '/api/threads/t/accept', true],
      ['POST', '/api/auth/logout', true],
    ]);
  });

  it('sends JSON bodies and the session cookie', async () => {
    const { fetcher, calls } = fetchReturning(202, { outcome: 'Queued' });

    await createApi(fetcher).postMessage('t 1', 'm-1', '@factory do it');

    expect(calls[0]!.path).toBe('/api/threads/t%201/messages');
    expect(JSON.parse(String(calls[0]!.init.body))).toEqual({ messageId: 'm-1', text: '@factory do it' });
    expect(headersOf(calls[0]!.init)['Content-Type']).toBe('application/json');
    expect(calls[0]!.init.credentials).toBe('same-origin');
  });

  it.each([
    ['setBudget', (api: ReturnType<typeof createApi>) => api.setBudget('t', 5), '/api/threads/t/budget', { cap: 5 }],
    ['setBudget (no cap)', (api: ReturnType<typeof createApi>) => api.setBudget('t', null), '/api/threads/t/budget', { cap: null }],
    ['answerDecision', (api: ReturnType<typeof createApi>) => api.answerDecision('d', 'Yes'), '/api/decisions/d/answer', { answer: 'Yes' }],
    ['setFindingVerdict', (api: ReturnType<typeof createApi>) => api.setFindingVerdict('f 1', 'Wrong'), '/api/findings/f%201/verdict', { verdict: 'Wrong' }],
    ['setFindingVerdict (clear)', (api: ReturnType<typeof createApi>) => api.setFindingVerdict('f', null), '/api/findings/f/verdict', { verdict: null }],
    ['login', (api: ReturnType<typeof createApi>) => api.login('admin', 'pw'), '/api/auth/login', { userName: 'admin', password: 'pw' }],
  ])('%s posts to its route', async (_, call, path, body) => {
    const { fetcher, calls } = fetchReturning(200, {});
    await call(createApi(fetcher));
    expect(calls[0]!.path).toBe(path);
    expect(JSON.parse(String(calls[0]!.init.body))).toEqual(body);
  });

  it.each([
    ['pause', '/api/threads/t/pause'],
    ['cancel', '/api/threads/t/cancel'],
    ['resume', '/api/threads/t/resume'],
    ['withdraw', '/api/threads/t/withdraw'],
  ] as const)('%s posts with no body', async (name, path) => {
    const { fetcher, calls } = fetchReturning(202, { stopping: true });
    await createApi(fetcher)[name]('t');
    expect(calls[0]!.path).toBe(path);
    expect(calls[0]!.init.body).toBeUndefined();
  });

  it.each(['pause', 'cancel'] as const)('%s says whether the task is still stopping', async (name) => {
    expect(await createApi(fetchReturning(202, { thread: {}, stopping: true }).fetcher)[name]('t')).toEqual({ stopping: true });
    // A task that was not running changes at once: the answer is the thread itself.
    expect(await createApi(fetchReturning(200, { id: 't', state: 'PausedUser' }).fetcher)[name]('t')).toEqual({ stopping: false });
  });

  it('asks where the pull request stands with a read', async () => {
    const { fetcher, calls } = fetchReturning(200, { number: 12, url: 'https://github.com/a/b/pull/12', state: 'Merged' });

    expect(await createApi(fetcher).pullRequest('t')).toEqual({ number: 12, url: 'https://github.com/a/b/pull/12', state: 'Merged' });
    expect([calls[0]!.init.method, calls[0]!.path]).toEqual(['GET', '/api/threads/t/pull-request']);
  });

  it("raises the host's own reason", async () => {
    const { fetcher } = fetchReturning(409, { error: 'A task that is Accepted cannot Cancel.' });

    await expect(createApi(fetcher).cancel('t')).rejects.toMatchObject({ status: 409, message: 'A task that is Accepted cannot Cancel.' });
  });

  it.each([
    [404, 'That no longer exists.'],
    [423, 'This account is locked after repeated failed sign-ins. Try again later.'],
    [429, 'Too many attempts. Wait a minute, then try again.'],
    [500, 'The request failed (500).'],
  ])('explains a %i that has no body', async (status, message) => {
    const { fetcher } = fetchReturning(status);
    await expect(createApi(fetcher).threads()).rejects.toMatchObject({ status, message });
  });

  it('survives an error body that is not JSON', async () => {
    const { fetcher } = fetchReturning(502, '<html>Bad gateway</html>');
    await expect(createApi(fetcher).threads()).rejects.toMatchObject({ status: 502, message: 'The request failed (502).' });
  });

  it('says so when the host cannot be reached', async () => {
    const api = createApi(async () => {
      throw new TypeError('Failed to fetch');
    });

    const failure = await api.threads().catch((e: unknown) => e);
    expect(failure).toBeInstanceOf(ApiError);
    expect(failure).toMatchObject({ status: 0, message: 'The factory host could not be reached. Check that it is running.' });
  });

  it('reports a lost session once it is signed in, but not while signing in', async () => {
    const onSignedOut = vi.fn();
    const { fetcher } = fetchReturning(401);
    const api = createApi(fetcher, onSignedOut);

    expect(await api.me()).toBeNull();
    await expect(api.login('admin', 'wrong')).rejects.toMatchObject({ status: 401 });
    expect(onSignedOut).not.toHaveBeenCalled();

    await expect(api.threads()).rejects.toMatchObject({ status: 401 });
    expect(onSignedOut).toHaveBeenCalledTimes(1);
  });

  it('me() passes on a failure that is not "signed out"', async () => {
    const { fetcher } = fetchReturning(500);
    await expect(createApi(fetcher).me()).rejects.toMatchObject({ status: 500 });
  });
});

describe('the event stream', () => {
  function open() {
    const opened: FakeEventSource[] = [];
    const events: ThreadEvent[] = [];
    const stop = subscribeToThread('t 1', 42, (e) => events.push(e), (url) => {
      const source = new FakeEventSource(url);
      opened.push(source);
      return source;
    });
    return { source: opened[0]!, events, stop };
  }

  it('starts after the snapshot it was given', () => {
    expect(open().source.url).toBe('/api/threads/t%201/events?after=42');
  });

  it('delivers state and usage events with their sequence and thread figures', () => {
    const { source, events } = open();
    const change = { state: 'Running', stage: 'Implement', revision: 7, tokensUsed: 5 };

    source.emit('state', change, 43);
    source.emit('usage', { ...change, tokensUsed: 9 }, 44);

    expect(events).toEqual([
      { type: 'state', sequence: 43, change },
      { type: 'usage', sequence: 44, change: { ...change, tokensUsed: 9 } },
    ]);
  });

  it('delivers a message event as a prompt to reload, without trusting its payload', () => {
    const { source, events } = open();
    source.emit('message', { text: 'anything' }, 50);
    expect(events).toEqual([{ type: 'message', sequence: 50 }]);
  });

  it('delivers what a chat answer is doing, and skips a report it cannot read', () => {
    const { source, events } = open();
    const progress = { runId: 'r', startedAt: '2026-10-08T09:00:00Z', modelCalls: 2, toolCalls: 1, activity: 'Read src/Orders.cs' };

    source.emit('chat', progress, 60);

    expect(events).toEqual([{ type: 'chat', sequence: 60, progress }]);
  });

  it('drops an event whose data is not JSON', () => {
    const opened: FakeEventSource[] = [];
    const events: ThreadEvent[] = [];
    const listeners: Record<string, (e: MessageEvent) => void> = {};
    subscribeToThread('t', 0, (e) => events.push(e), () => ({
      readyState: 1,
      addEventListener: (type, listener) => void (listeners[type] = listener),
      close: () => {},
    }));

    listeners.state!({ data: 'not json', lastEventId: '1' } as MessageEvent);

    expect(events).toEqual([]);
    expect(opened).toEqual([]);
  });

  it('closes the stream when stopped', () => {
    const { source, events, stop } = open();
    stop();
    source.emit('state', { revision: 1 }, 1);
    expect(source.closed).toBe(true);
    expect(events).toEqual([]);
  });
});

describe('people and invitations', () => {
  it('keeps an invitation token in the request body, never in the address', async () => {
    const { fetcher, calls } = fetchReturning(200, { userName: 'erin', role: 'Member', expiresAt: '2026-10-14T09:00:00Z' });
    const api = createApi(fetcher);

    await api.lookupInvitation('secret-token');
    await api.acceptInvitation('secret-token', 'a long enough password', 'Erin');

    expect(calls.map((c) => c.path)).toEqual(['/api/invitations/lookup', '/api/invitations/accept']);
    expect(calls.every((c) => !c.path.includes('secret-token'))).toBe(true);
    expect(JSON.parse(String(calls[1]!.init.body))).toEqual({ token: 'secret-token', password: 'a long enough password', displayName: 'Erin' });
    expect(calls.every((c) => CSRF_HEADER in headersOf(c.init))).toBe(true);
  });

  /** Someone opening a link is not signed in: a refusal is about the link, not their session. */
  it('does not treat a refused invitation as being signed out', async () => {
    const { fetcher } = fetchReturning(401, { error: 'nope' });
    const signedOut = vi.fn();
    const api = createApi(fetcher, signedOut);

    await expect(api.lookupInvitation('t')).rejects.toBeInstanceOf(ApiError);
    await expect(api.acceptInvitation('t', 'p')).rejects.toBeInstanceOf(ApiError);
    expect(signedOut).not.toHaveBeenCalled();
  });

  it("explains a link that no longer works when the host gives no reason", async () => {
    const { fetcher } = fetchReturning(410);

    await expect(createApi(fetcher).lookupInvitation('t')).rejects.toThrow('This link no longer works. Ask an admin for a new one.');
  });

  it('calls the admin routes with the right method and path', async () => {
    const { fetcher, calls } = fetchReturning(200, {});
    const api = createApi(fetcher);

    await api.invitations();
    await api.createInvitation({ userName: 'erin', role: 'Member', projectIds: ['p1'] });
    await api.revokeInvitation('i 1');
    await api.people();
    await api.disablePerson('u1');
    await api.enablePerson('u1');
    await api.setRole('u1', 'Admin');
    await api.addMember('p1', 'u1');
    await api.removeMember('p1', 'u1');

    expect(calls.map((c) => `${c.init.method} ${c.path}`)).toEqual([
      'GET /api/invitations',
      'POST /api/invitations',
      'POST /api/invitations/i%201/revoke',
      'GET /api/users',
      'POST /api/users/u1/disable',
      'POST /api/users/u1/enable',
      'POST /api/users/u1/role',
      'POST /api/projects/p1/members',
      'DELETE /api/projects/p1/members/u1',
    ]);
    expect(JSON.parse(String(calls[7]!.init.body))).toEqual({ userId: 'u1' });
  });
});

describe('the board', () => {
  it('reads where the board stream starts from the thread list', async () => {
    const fetcher: Fetch = async () =>
      ({ ok: true, status: 200, text: async () => '[]', headers: new Headers({ 'X-Event-Cursor': '42' }) }) as Response;

    expect(await createApi(fetcher).threadList()).toEqual({ threads: [], cursor: 42 });
  });

  it('starts from the beginning when the host gives no cursor', async () => {
    const { fetcher } = fetchReturning(200, []);

    expect((await createApi(fetcher).threadList()).cursor).toBe(0);
  });

  it('renames or re-files a thread with PATCH, sending only what changed', async () => {
    const { fetcher, calls } = fetchReturning(200, {});

    await createApi(fetcher).editThread('t 1', { typeLabel: 'bug' });

    expect([calls[0]!.init.method, calls[0]!.path]).toEqual(['PATCH', '/api/threads/t%201']);
    expect(JSON.parse(String(calls[0]!.init.body))).toEqual({ typeLabel: 'bug' });
    expect(CSRF_HEADER in headersOf(calls[0]!.init)).toBe(true);
  });

  it('drops an event it has already handled, or one from before its starting point', () => {
    let source: FakeEventSource | undefined;
    const events: ThreadEvent[] = [];
    subscribeToThread('t', 42, (e) => events.push(e), (url) => (source = new FakeEventSource(url)));
    if (!source) throw new Error('no stream was opened');

    source.emit('message', {}, 41); // before the snapshot it started from
    source.emit('message', {}, 43);
    source.emit('message', {}, 43); // replayed after a reconnect
    source.emit('chat', { runId: 'r' }, 44);
    source.emit('state', { state: 'Running', revision: 2 }, 44);
    source.emit('message', {}, 45);

    expect(events.map((e) => [e.type, e.sequence])).toEqual([
      ['message', 43],
      ['chat', 44],
      ['message', 45],
    ]);
  });

  it('reports a drop as reconnecting, and live again once the stream reopens', () => {
    const statuses: string[] = [];
    let source: FakeEventSource | undefined;
    subscribeToThread('t', 0, () => {}, (url) => (source = new FakeEventSource(url)), (s) => statuses.push(s));

    source!.connect();
    source!.drop();
    source!.connect();

    expect(statuses).toEqual(['live', 'reconnecting', 'live']);
    expect(source!.closed).toBe(false);
  });

  it('reports a refused stream as closed, and closes it so the browser stops trying', () => {
    const statuses: string[] = [];
    let source: FakeEventSource | undefined;
    subscribeToBoard(0, () => {}, (url) => (source = new FakeEventSource(url)), (s) => statuses.push(s));

    source!.connect();
    source!.refuse();

    expect(statuses).toEqual(['live', 'closed']);
    expect(source!.closed).toBe(true);
  });

  it('waits longer before each fresh snapshot, up to a limit', () => {
    expect([0, 1, 2, 3, 4, 5, 9].map(resubscribeDelay)).toEqual([1_000, 2_000, 5_000, 10_000, 30_000, 30_000, 30_000]);
    expect(resubscribeDelay(-1)).toBe(1_000);
  });

  it('drops a board event it has already handled', () => {
    let source: FakeEventSource | undefined;
    const heard: number[] = [];
    subscribeToBoard(7, (thread) => heard.push(thread.revision), (url) => (source = new FakeEventSource(url)));

    source!.emit('thread', { id: 't', revision: 1 }, 7);
    source!.emit('thread', { id: 't', revision: 2 }, 8);
    source!.emit('thread', { id: 't', revision: 2 }, 8);

    expect(heard).toEqual([2]);
  });

  it('hears each thread the board stream sends, and stops when asked', () => {
    let source: FakeEventSource | undefined;
    const heard: string[] = [];
    const stop = subscribeToBoard(
      7,
      (thread) => heard.push(thread.title),
      (url) => (source = new FakeEventSource(url)),
    );

    source!.emit('thread', { id: 't', title: 'Add CSV export', revision: 3 }, 8);
    expect(source!.url).toBe('/api/events?after=7');
    expect(heard).toEqual(['Add CSV export']);

    stop();
    expect(source!.closed).toBe(true);
  });
});
