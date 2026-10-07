import { CSRF_HEADER, type Fetch } from '../api/client';
import type { EventSourceFactory, EventSourceLike } from '../api/events';
import type {
  AccountRole,
  CurrentUser,
  Decision,
  Finding,
  HandoffEvidence,
  Invitation,
  LifecycleState,
  Message,
  Person,
  Project,
  PullRequestState,
  Settings,
  Thread,
  ThreadDetails,
  Turn,
  UsageCall,
} from '../api/types';

export interface RecordedRequest {
  method: string;
  path: string;
  body: unknown;
  headers: Record<string, string>;
}

/** One open event stream, driven by the test. */
export class FakeEventSource implements EventSourceLike {
  closed = false;
  private readonly listeners = new Map<string, ((event: MessageEvent) => void)[]>();

  constructor(public readonly url: string) {}

  addEventListener(type: string, listener: (event: MessageEvent) => void): void {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]);
  }

  close(): void {
    this.closed = true;
  }

  emit(type: string, data: unknown, id: number): void {
    if (this.closed) return;
    const event = { data: JSON.stringify(data), lastEventId: String(id) } as MessageEvent;
    for (const listener of this.listeners.get(type) ?? []) listener(event);
  }
}

const NOW = '2026-10-01T09:00:00Z';

/** The host's turn-label table (Core/Lifecycle/TurnLabels.cs): the fake answers as the host does. */
export function turnOf(state: LifecycleState): Turn {
  switch (state) {
    case 'Draft':
      return 'NotStarted';
    case 'Queued':
      return 'AwaitingAgent';
    case 'Running':
      return 'AgentWorking';
    case 'PausedUser':
      return 'Paused';
    case 'Accepted':
    case 'Cancelled':
      return 'Done';
    default:
      return 'AwaitingYou';
  }
}

export const ADMIN: CurrentUser = { id: 'u-admin', userName: 'admin', displayName: 'Priya Raman', roles: ['Admin'] };
export const PASSWORD = 'correct horse battery';

export function evidence(overrides: Partial<HandoffEvidence> = {}): HandoffEvidence {
  return {
    summary: 'Added CSV export for Orders.',
    branch: 'factory/add-csv-export-1a2b',
    commitSha: '9f8e7d6c5b4a39281706f5e4d3c2b1a098765432',
    pullRequestNumber: 12,
    pullRequestUrl: 'https://github.com/harbor-tools/salesapp/pull/12',
    build: 'Passed',
    unitTests: 'Passed',
    testsPassed: 142,
    testsFailed: 0,
    testsSkipped: 0,
    newTests: 6,
    preExistingFailures: [],
    coverage: 'Met',
    changedLineCoveragePercent: 91.4,
    coverageThresholdPercent: 80,
    unmeasuredFiles: [],
    review: 'Clean',
    findings: [],
    criteria: [{ criterion: 'Orders can be exported as CSV', tests: ['CsvWriterTests.Quotes'], manualOnly: false }],
    testsAdded: ['CsvWriterTests'],
    knownLimitations: [],
    manualTestSteps: ['Export 10 orders and open the file in Excel'],
    commands: ['dotnet build — ok in 12.1s'],
    changedFiles: ['src/SalesApp.Api/Export/CsvWriter.cs', 'tests/SalesApp.UnitTests/Export/CsvWriterTests.cs'],
    decisions: [],
    tokensUsed: 81_200,
    budgetCap: 300_000,
    ...overrides,
  };
}

/**
 * The factory host, in memory: the same routes, status codes and state rules as the real one
 * (Host/Api/FactoryApi.cs), so the app is tested against the contract rather than against mocks
 * of its own client. Tests drive the "agent" side with the helper methods.
 */
export class FakeHost {
  user: CurrentUser = ADMIN;
  signedIn = false;
  settings: Settings = {
    provider: 'openrouter',
    model: 'deepseek/deepseek-v4.1-flash',
    defaultBudget: 300_000,
    ptcEnabled: true,
    cachedInputWeight: 0.1,
    presets: ['dotnet', 'node-react'],
    taskTypes: ['bug', 'feature', 'refactor', 'chore'],
    promptRevision: 'm1.1',
  };
  projects: Project[] = [];
  readonly threads = new Map<string, ThreadDetails>();
  readonly usage = new Map<string, UsageCall[]>();
  /** Where each thread's pull request stands "on GitHub"; a draft unless a test says otherwise. */
  readonly pullRequestStates = new Map<string, PullRequestState>();
  readonly requests: RecordedRequest[] = [];
  /** The open threads' streams (GET /api/threads/{id}/events). */
  readonly sources: FakeEventSource[] = [];
  /** The board's streams (GET /api/events). */
  readonly boardSources: FakeEventSource[] = [];
  /** Answers the next matching request with this instead of handling it. */
  private readonly failures: { match: (r: RecordedRequest) => boolean; status: number; error?: string }[] = [];
  private sequence = 0;
  private ids = 0;

  /** The newest event's sequence number, as the host's X-Event-Cursor reports it. */
  get lastSequence(): number {
    return this.sequence;
  }
  /** Everyone with an account; the signed-in user is one of them. */
  readonly people: Person[] = [
    { id: ADMIN.id, userName: ADMIN.userName, displayName: ADMIN.displayName, role: 'Admin', disabled: false, projectIds: [] },
  ];
  readonly invitations: Invitation[] = [];
  /** Each invitation's link token, which the real host never keeps; tests open links with it. */
  readonly invitationTokens = new Map<string, string>();
  /** What "now" is for invitation expiry. */
  now = new Date(NOW);

  // ---- what the app is given ----

  readonly fetch: Fetch = async (input, init) => {
    const request: RecordedRequest = {
      method: init?.method ?? 'GET',
      path: String(input),
      body: init?.body ? JSON.parse(String(init.body)) : undefined,
      headers: (init?.headers ?? {}) as Record<string, string>,
    };
    this.requests.push(request);

    const failure = this.failures.findIndex((f) => f.match(request));
    const [status, body] =
      failure >= 0
        ? [this.failures[failure]!.status, this.failures[failure]!.error ? { error: this.failures[failure]!.error } : undefined]
        : this.handle(request);
    if (failure >= 0) this.failures.splice(failure, 1);

    const text = body === undefined ? '' : JSON.stringify(body);
    const headers = new Headers();
    if (request.method === 'GET' && request.path === '/api/threads') headers.set('X-Event-Cursor', String(this.sequence));
    return { ok: status >= 200 && status < 300, status, text: async () => text, headers } as Response;
  };

  readonly openEvents: EventSourceFactory = (url) => {
    const source = new FakeEventSource(url);
    (url.startsWith('/api/events') ? this.boardSources : this.sources).push(source);
    return source;
  };

  // ---- setting the scene ----

  nextId(prefix: string): string {
    return `${prefix}-${++this.ids}`;
  }

  failNext(method: string, pathPart: string, status: number, error?: string): void {
    this.failures.push({ match: (r) => r.method === method && r.path.includes(pathPart), status, error });
  }

  addProject(overrides: Partial<Project> = {}): Project {
    const project: Project = {
      id: this.nextId('p'),
      name: 'SalesApp',
      gitHub: 'harbor-tools/salesapp',
      defaultBranch: 'main',
      pullRequestEnabled: true,
      profileRevision: 1,
      coverageThresholdPercent: 80,
      createdAt: NOW,
      ...overrides,
    };
    this.projects.push(project);
    return project;
  }

  addThread(project: Project, overrides: Partial<Thread> = {}): Thread {
    const state = overrides.state ?? 'Draft';
    const thread: Thread = {
      id: this.nextId('t'),
      projectId: project.id,
      ownerId: this.user.id,
      turn: turnOf(state),
      title: 'Add CSV export',
      typeLabel: 'feature',
      stage: 'Discuss',
      state: 'Draft',
      stateReason: null,
      budgetCap: 300_000,
      tokensUsed: 0,
      tokensReserved: 0,
      branch: null,
      pullRequestNumber: null,
      pullRequestUrl: null,
      provider: 'openrouter',
      model: 'deepseek/deepseek-v4.1-flash',
      budgetPrecision: 'strict',
      revision: 1,
      createdAt: NOW,
      updatedAt: NOW,
      ...overrides,
    };
    if (!overrides.turn) thread.turn = turnOf(thread.state);
    this.announce(thread);
    this.threads.set(thread.id, {
      eventCursor: 0,
      thread,
      project,
      messages: [],
      decisions: [],
      run: null,
      verification: null,
      findings: [],
      handoff: null,
    });
    this.usage.set(thread.id, []);
    return thread;
  }

  details(threadId: string): ThreadDetails {
    const details = this.threads.get(threadId);
    if (!details) throw new Error(`No thread ${threadId}`);
    return details;
  }

  /** Adds someone with an account. */
  addPerson(overrides: Partial<Person> = {}): Person {
    const person: Person = {
      id: this.nextId('u'),
      userName: 'ben',
      displayName: 'Ben Okafor',
      role: 'Member',
      disabled: false,
      projectIds: [],
      ...overrides,
    };
    this.people.push(person);
    return person;
  }

  /** Signs in as that person instead of the Admin. */
  signInAs(person: Person): void {
    this.user = { id: person.id, userName: person.userName, displayName: person.displayName, roles: [person.role] };
    this.signedIn = true;
  }

  /** Creates an invitation as the host would, and returns the token its link carries. */
  invite(userName = 'erin', role: AccountRole = 'Member', projectIds: string[] = []): string {
    const invitation: Invitation = {
      id: this.nextId('i'),
      userName,
      email: null,
      role,
      projectIds,
      status: 'Pending',
      createdBy: ADMIN.id,
      createdAt: this.now.toISOString(),
      expiresAt: new Date(this.now.getTime() + 7 * 86_400_000).toISOString(),
      acceptedAt: null,
      acceptedUserId: null,
      revokedAt: null,
    };
    const token = `token-${invitation.id}`;
    this.invitations.unshift(invitation);
    this.invitationTokens.set(token, invitation.id);
    return token;
  }

  // ---- the factory's side of the story ----

  /** Changes the thread and announces it, as the host does on every transition. */
  change(threadId: string, patch: Partial<Thread>, eventType: 'state' | 'usage' = 'state'): Thread {
    const details = this.details(threadId);
    details.thread = { ...details.thread, ...patch, revision: details.thread.revision + 1 };
    details.thread.turn = turnOf(details.thread.state);
    const t = details.thread;
    // The run follows the task: queued, running, stopped where it can continue, or over.
    if (details.run && patch.state) {
      const status =
        patch.state === 'Queued' ? 'Queued'
        : patch.state === 'Running' ? 'Running'
        : patch.state === 'AwaitingHumanTesting' || patch.state === 'Accepted' || patch.state === 'Cancelled' ? 'Finished'
        : 'Suspended';
      details.run = { ...details.run, status };
    }
    this.emit(threadId, eventType, {
      state: t.state,
      stage: t.stage,
      turn: t.turn,
      title: t.title,
      typeLabel: t.typeLabel,
      reason: t.stateReason,
      revision: t.revision,
      tokensUsed: t.tokensUsed,
      tokensReserved: t.tokensReserved,
      budgetCap: t.budgetCap,
      branch: t.branch,
      pullRequestUrl: t.pullRequestUrl,
    });
    this.announce(t);
    return t;
  }

  /** Sends the thread's whole view to every open board stream (GET /api/events). */
  announce(thread: Thread): void {
    const id = ++this.sequence;
    for (const source of this.boardSources) source.emit('thread', thread, id);
  }

  say(threadId: string, message: Partial<Message> & { text: string }): Message {
    const details = this.details(threadId);
    const full: Message = {
      id: this.nextId('m'),
      sequence: details.messages.length + 1,
      author: 'Factory',
      kind: 'Status',
      decisionId: null,
      payload: null,
      createdAt: NOW,
      ...message,
    };
    details.messages = [...details.messages, full];
    this.emit(threadId, 'message', { id: full.id, sequence: full.sequence, kind: full.kind, text: full.text });
    return full;
  }

  askDecision(threadId: string, overrides: Partial<Decision> = {}): Decision {
    const details = this.details(threadId);
    const decision: Decision = {
      id: this.nextId('d'),
      question: 'Should the export include cancelled orders?',
      whyItBlocks: 'The request does not say, and it changes what the file contains.',
      options: ['Include them', 'Leave them out'],
      recommendation: 'Leave them out',
      impact: 'the export query and its tests',
      status: 'Open',
      answer: null,
      createdAt: NOW,
      answeredAt: null,
      ...overrides,
    };
    details.decisions = [...details.decisions, decision];
    this.say(threadId, { kind: 'Decision', text: decision.question, decisionId: decision.id, payload: { ...decision } });
    this.change(threadId, { state: 'AwaitingDecision' });
    return decision;
  }

  handOff(threadId: string, overrides: Partial<HandoffEvidence> = {}): HandoffEvidence {
    const details = this.details(threadId);
    const e = evidence(overrides);
    details.handoff = {
      branch: e.branch,
      commitSha: e.commitSha,
      pullRequestNumber: e.pullRequestNumber,
      pullRequestUrl: e.pullRequestUrl,
      evidence: e,
    };
    details.verification = {
      build: e.build,
      unitTests: e.unitTests,
      coverage: e.coverage,
      passedCount: e.testsPassed,
      failedCount: e.testsFailed,
      skippedCount: e.testsSkipped,
      changedLineCoveragePercent: e.changedLineCoveragePercent,
    };
    this.say(threadId, { kind: 'Handoff', text: 'Ready for human testing.', payload: e });
    this.change(threadId, {
      state: 'AwaitingHumanTesting',
      stage: 'Handoff',
      branch: e.branch,
      pullRequestNumber: e.pullRequestNumber,
      pullRequestUrl: e.pullRequestUrl,
      tokensUsed: e.tokensUsed,
    });
    return e;
  }

  addCall(threadId: string, overrides: Partial<UsageCall> = {}): UsageCall {
    const call: UsageCall = {
      id: this.nextId('c'),
      runId: null,
      model: this.settings.model,
      estimatedInput: 9_000,
      reserved: 14_300,
      actualInput: 9_400,
      actualCachedInput: 0,
      actualOutput: 1_200,
      actualReasoning: 300,
      charged: 10_600,
      status: 'Settled',
      phase: 'Implement',
      createdAt: NOW,
      settledAt: NOW,
      ...overrides,
    };
    this.usage.set(threadId, [...(this.usage.get(threadId) ?? []), call]);
    return call;
  }

  emit(threadId: string, type: string, data: unknown): void {
    const id = ++this.sequence;
    this.details(threadId).eventCursor = id;
    for (const source of this.sources) {
      if (source.url.startsWith(`/api/threads/${threadId}/events`)) source.emit(type, data, id);
    }
  }

  /** The requests the app made, newest last, filtered by method and path. */
  sent(method: string, pathPart: string): RecordedRequest[] {
    return this.requests.filter((r) => r.method === method && r.path.includes(pathPart));
  }

  // ---- the routes ----

  private handle(request: RecordedRequest): [number, unknown?] {
    const { method, path, body } = request;
    const data = (body ?? {}) as Record<string, unknown>;

    if (path === '/api/auth/login' && method === 'POST') {
      if (data.userName !== this.user.userName || data.password !== PASSWORD)
        return [401, { error: 'The user name or password is incorrect.' }];
      this.signedIn = true;
      return [200, this.user];
    }

    // Opening an invitation's link: anonymous, as on the host (Auth/InvitationsApi.cs).
    if (path === '/api/invitations/lookup' && method === 'POST') return this.lookup(String(data.token ?? ''));
    if (path === '/api/invitations/accept' && method === 'POST') {
      if (!request.headers[CSRF_HEADER]) return [400, { error: `State-changing requests must send the ${CSRF_HEADER} header.` }];
      return this.accept(String(data.token ?? ''), String(data.password ?? ''), data.displayName as string | undefined);
    }

    if (!this.signedIn) return [401];
    if (method !== 'GET' && !request.headers[CSRF_HEADER])
      return [400, { error: `State-changing requests must send the ${CSRF_HEADER} header.` }];

    if (path === '/api/auth/me') return [200, this.user];
    if (path === '/api/auth/logout') {
      this.signedIn = false;
      return [200];
    }
    if (path === '/api/settings') return [200, this.settings];

    if (path === '/api/projects' && method === 'GET') return [200, this.projects];
    if (path === '/api/projects' && method === 'POST') {
      const match = /^https:\/\/github\.com\/([^/]+)\/([^/]+?)(\.git)?\/?$/.exec(String(data.gitHubUrl ?? ''));
      if (!match) return [400, { error: 'gitHubUrl must be a GitHub repository URL, such as https://github.com/owner/repo.' }];
      const gitHub = `${match[1]}/${match[2]}`;
      if (this.projects.some((p) => p.gitHub === gitHub)) return [409, { error: `${gitHub} is already registered.` }];
      return [
        201,
        this.addProject({
          name: (data.name as string | undefined) ?? match[2]!,
          gitHub,
          defaultBranch: String(data.defaultBranch),
          pullRequestEnabled: data.pullRequestEnabled !== false,
          coverageThresholdPercent: (data.coverageThresholdPercent as number | undefined) ?? 80,
        }),
      ];
    }

    if (path === '/api/threads' && method === 'GET') return [200, [...this.threads.values()].map((d) => d.thread)];
    if (path === '/api/directory' && method === 'GET') {
      // The owners of visible threads, and the user: as the host answers (FactoryApi.cs).
      const owners = new Set([...this.threads.values()].map((d) => d.thread.ownerId).concat(this.user.id));
      return [200, this.people.filter((p) => owners.has(p.id)).map((p) => ({ id: p.id, name: p.displayName || p.userName }))];
    }
    if (path === '/api/threads' && method === 'POST') {
      const project = this.projects.find((p) => p.id === data.projectId);
      if (!project) return [404, { error: 'The project does not exist.' }];
      if (!String(data.title ?? '').trim()) return [400, { error: 'title is required.' }];
      return [
        201,
        this.addThread(project, {
          title: String(data.title),
          typeLabel: String(data.typeLabel),
          budgetCap: (data.budgetCap as number | undefined) ?? this.settings.defaultBudget,
        }),
      ];
    }

    const people = this.handlePeople(method, path, data);
    if (people) return people;

    const decision = /^\/api\/decisions\/([^/]+)\/answer$/.exec(path);
    if (decision && method === 'POST') return this.answer(decision[1]!, String(data.answer ?? ''));

    const verdict = /^\/api\/findings\/([^/]+)\/verdict$/.exec(path);
    if (verdict && method === 'POST') return this.judge(verdict[1]!, (data.verdict as string | null | undefined) ?? null);

    const route = /^\/api\/threads\/([^/?]+)(?:\/([\w-]+))?$/.exec(path);
    if (!route) return [404];
    const details = this.threads.get(route[1]!);
    if (!details) return [404];
    const id = details.thread.id;
    const state = details.thread.state;
    const conflict = (action: string): [number, unknown] => [409, { error: `A task that is ${state} cannot ${action}.` }];

    switch (`${method} ${route[2] ?? ''}`) {
      case 'GET ':
        return [200, details];
      case 'PATCH ': {
        if (details.thread.ownerId !== this.user.id && !this.user.roles.includes('Admin'))
          return [403, { error: "Only the thread's owner or an Admin can change it." }];
        const title = typeof data.title === 'string' ? data.title.trim() : undefined;
        if (title !== undefined && !title) return [400, { error: 'title must be 1 to 300 characters.' }];
        const typeLabel = data.typeLabel as string | undefined;
        if (typeLabel !== undefined && !this.settings.taskTypes.includes(typeLabel))
          return [400, { error: `typeLabel must be one of: ${this.settings.taskTypes.join(', ')}.` }];
        return [200, this.change(id, { ...(title ? { title } : {}), ...(typeLabel ? { typeLabel } : {}) })];
      }
      case 'GET usage':
        return [200, this.usage.get(id) ?? []];
      case 'POST messages':
        return this.dispatch(id, String(data.messageId ?? ''), String(data.text ?? ''));
      case 'POST budget':
        if (typeof data.cap === 'number' && data.cap <= 0) return [400, { error: 'cap must be positive, or null for no cap.' }];
        return [200, this.change(id, { budgetCap: data.cap as number | null }, 'usage')];
      case 'POST accept':
        if (state !== 'AwaitingHumanTesting') return conflict('Accept');
        this.say(id, { author: 'User', text: 'Accepted.' });
        return [200, this.change(id, { state: 'Accepted', stage: 'Done' })];
      case 'POST pause':
        // A running task is stopped through its worker: accepted now, paused a moment later.
        if (state === 'Running') return [202, { thread: details.thread, stopping: true }];
        if (state !== 'Queued') return conflict('Pause');
        return [200, this.change(id, { state: 'PausedUser' })];
      case 'POST cancel':
        if (state === 'Running') return [202, { thread: details.thread, stopping: true }];
        if (state === 'Accepted' || state === 'Cancelled' || state === 'Draft') return conflict('Cancel');
        return [200, this.change(id, { state: 'Cancelled' })];
      case 'POST withdraw': {
        if (state === 'Running') return [409, { error: 'The change request is being worked on. Pause the task, then withdraw it.' }];
        const stopped: LifecycleState[] = ['Queued', 'AwaitingDecision', 'PausedBudget', 'PausedUser', 'Blocked', 'Interrupted'];
        if (!stopped.includes(state)) return [409, { error: `A task that is ${state} has no change request to withdraw.` }];
        if (details.run?.kind !== 'Rework' || details.run.status === 'Finished' || !details.handoff)
          return [409, { error: 'This task has no change request to withdraw: its run is the original work. Cancel the task to stop it.' }];
        details.run = { ...details.run, stopReason: 'Withdrawn' };
        details.decisions = details.decisions.map((d) =>
          d.status === 'Open' ? { ...d, status: 'Answered', answer: 'Not answered: the change request was withdrawn.', answeredAt: NOW } : d,
        );
        this.say(id, { author: 'User', text: 'Change request withdrawn. The task is back at its last handoff.' });
        return [200, this.change(id, { state: 'AwaitingHumanTesting', stage: 'Handoff', stateReason: null })];
      }
      case 'GET pull-request':
        if (details.thread.pullRequestNumber === null) return [404, { error: 'This task has no pull request.' }];
        return [
          200,
          { number: details.thread.pullRequestNumber, url: details.thread.pullRequestUrl, state: this.pullRequestStates.get(id) ?? 'Draft' },
        ];
      case 'POST resume': {
        const resumable: LifecycleState[] = ['PausedUser', 'PausedBudget', 'Blocked', 'Interrupted'];
        if (!resumable.includes(state)) return [409, { error: `A task that is ${state} has nothing to resume.` }];
        return [200, this.change(id, { state: 'Queued', stateReason: null })];
      }
      default:
        return [404];
    }
  }

  // ---- people and invitations (Auth/InvitationsApi.cs, Auth/UsersApi.cs) ----

  private statusOf(invitation: Invitation): Invitation['status'] {
    if (invitation.acceptedAt) return 'Accepted';
    if (invitation.revokedAt) return 'Revoked';
    return this.now.getTime() >= new Date(invitation.expiresAt).getTime() ? 'Expired' : 'Pending';
  }

  /** The invitation a token belongs to, or the host's refusal of it. */
  private usable(token: string): Invitation | [number, unknown] {
    const invitation = this.invitations.find((i) => i.id === this.invitationTokens.get(token));
    if (!invitation)
      return [404, { error: 'This invitation link is not valid. Check that it was copied whole, or ask an Admin for a new one.' }];
    switch (this.statusOf(invitation)) {
      case 'Accepted':
        return [410, { error: 'This invitation has already been used.' }];
      case 'Revoked':
        return [410, { error: 'This invitation was revoked. Ask an Admin for a new one.' }];
      case 'Expired':
        return [410, { error: 'This invitation has expired. Ask an Admin for a new one.' }];
      default:
        return invitation;
    }
  }

  private lookup(token: string): [number, unknown] {
    const found = this.usable(token);
    return Array.isArray(found) ? found : [200, { userName: found.userName, role: found.role, expiresAt: found.expiresAt }];
  }

  private accept(token: string, password: string, displayName?: string): [number, unknown] {
    const found = this.usable(token);
    if (Array.isArray(found)) return found;
    if (password.length < 12) return [400, { error: 'Passwords must be at least 12 characters.' }];
    const person = this.addPerson({
      userName: found.userName,
      displayName: displayName ?? found.userName,
      role: found.role,
      projectIds: [...found.projectIds],
    });
    found.acceptedAt = this.now.toISOString();
    found.acceptedUserId = person.id;
    this.signInAs(person);
    return [200, this.user];
  }

  private handlePeople(method: string, path: string, data: Record<string, unknown>): [number, unknown?] | null {
    const admin = this.user.roles.includes('Admin');
    const enabledAdmins = () => this.people.filter((p) => p.role === 'Admin' && !p.disabled).length;
    const lastAdmin = (name: string): [number, unknown] => [409, { error: `${name} is the last enabled Admin. Make someone else an Admin first.` }];

    if (path === '/api/invitations' && method === 'GET') {
      if (!admin) return [403];
      return [200, this.invitations.map((i) => ({ ...i, status: this.statusOf(i) }))];
    }
    if (path === '/api/invitations' && method === 'POST') {
      if (!admin) return [403];
      const userName = String(data.userName ?? '').trim();
      if (!/^[A-Za-z0-9._@+-]{1,100}$/.test(userName))
        return [400, { error: 'userName is required: up to 100 letters, digits and - . _ @ +.' }];
      const same = (name: string) => name.toLowerCase() === userName.toLowerCase();
      if (this.people.some((p) => same(p.userName))) return [409, { error: `Someone already signs in as ${userName}.` }];
      if (this.invitations.some((i) => same(i.userName) && this.statusOf(i) === 'Pending'))
        return [409, { error: `An invitation for ${userName} is already waiting. Revoke it to send a new one.` }];
      const token = this.invite(userName, (data.role as AccountRole | undefined) ?? 'Member', (data.projectIds as string[] | undefined) ?? []);
      return [201, { invitation: this.invitations[0], link: `#/invite/${token}` }];
    }
    const revoke = /^\/api\/invitations\/([^/]+)\/revoke$/.exec(path);
    if (revoke && method === 'POST') {
      if (!admin) return [403];
      const invitation = this.invitations.find((i) => i.id === revoke[1]);
      if (!invitation) return [404];
      const status = this.statusOf(invitation);
      if (status === 'Accepted') return [409, { error: 'This invitation has already been used.' }];
      if (status === 'Revoked') return [409, { error: 'This invitation has already been revoked.' }];
      invitation.revokedAt = this.now.toISOString();
      return [204];
    }

    if (path === '/api/users' && method === 'GET') return admin ? [200, this.people] : [403];
    const user = /^\/api\/users\/([^/]+)\/(disable|enable|role)$/.exec(path);
    if (user && method === 'POST') {
      if (!admin) return [403];
      const person = this.people.find((p) => p.id === user[1]);
      if (!person) return [404];
      if (user[2] === 'disable') {
        if (person.disabled) return [409, { error: `${person.userName} is already disabled.` }];
        if (person.id === this.user.id) return [409, { error: 'You cannot disable your own account. Ask another Admin.' }];
        if (person.role === 'Admin' && enabledAdmins() <= 1) return lastAdmin(person.userName);
        person.disabled = true;
        return [200, person];
      }
      if (user[2] === 'enable') {
        if (!person.disabled) return [409, { error: `${person.userName} is not disabled.` }];
        person.disabled = false;
        return [200, person];
      }
      const role = data.role as AccountRole;
      if (role !== 'Admin' && role !== 'Member') return [400, { error: 'role must be Member or Admin.' }];
      if (person.role === 'Admin' && role === 'Member' && !person.disabled && enabledAdmins() <= 1) return lastAdmin(person.userName);
      person.role = role;
      return [200, person];
    }

    const members = /^\/api\/projects\/([^/]+)\/members(?:\/([^/]+))?$/.exec(path);
    if (members) {
      if (!admin) return [403];
      const projectId = members[1]!;
      if (!this.projects.some((p) => p.id === projectId)) return [404, { error: 'The project does not exist.' }];
      const person = this.people.find((p) => p.id === (members[2] ?? data.userId));
      if (!person) return [404, { error: 'The user does not exist.' }];
      if (method === 'POST') {
        if (person.projectIds.includes(projectId)) return [204];
        person.projectIds = [...person.projectIds, projectId];
        return [201];
      }
      if (method === 'DELETE') {
        if (!person.projectIds.includes(projectId)) return [404];
        person.projectIds = person.projectIds.filter((id) => id !== projectId);
        return [204];
      }
    }

    return null;
  }

  private readonly dispatched = new Set<string>();

  private dispatch(threadId: string, messageId: string, text: string): [number, unknown] {
    if (!messageId) return [400, { error: 'messageId is required: it makes a retried request safe.' }];
    const match = /^\s*@factory\s+(\S[\s\S]*)$/i.exec(text);
    if (!match) return [400, { error: 'Start the message with @factory followed by what you want done.' }];

    const details = this.details(threadId);
    if (this.dispatched.has(messageId)) return [202, { outcome: 'Duplicate', runId: null, thread: details.thread }];

    const state = details.thread.state;
    const queues = state === 'Draft' || state === 'AwaitingHumanTesting';
    if (!queues && state !== 'Queued' && state !== 'Running')
      return [409, { error: `A task that is ${state} cannot take new work.`, thread: details.thread }];

    this.dispatched.add(messageId);
    // A draft starts the original work; after a handoff the same message is a change request.
    if (queues) {
      details.run = {
        id: this.nextId('r'),
        kind: state === 'Draft' ? 'Implement' : 'Rework',
        status: 'Queued',
        stopReason: null,
        baselineCommit: null,
        headCommit: null,
        promptRevision: this.settings.promptRevision,
      };
    }
    this.say(threadId, { author: 'User', kind: 'Text', text: match[1]!.trim() });
    const thread = queues ? this.change(threadId, { state: 'Queued', stage: 'Implement', stateReason: null }) : details.thread;
    return [202, { outcome: queues ? 'Queued' : 'FollowUp', runId: 'r-1', thread }];
  }

  private judge(findingId: string, verdict: string | null): [number, unknown?] {
    if (verdict !== null && !['Real', 'NotWorthFixing', 'Wrong'].includes(verdict)) return [400, { error: 'verdict must be Real, NotWorthFixing or Wrong.' }];
    for (const details of this.threads.values()) {
      if (!details.findings.some((f) => f.id === findingId)) continue;
      details.findings = details.findings.map((f) => (f.id === findingId ? { ...f, verdict: verdict as Finding['verdict'] } : f));
      return [200, this.change(details.thread.id, {})];
    }
    return [404];
  }

  private answer(decisionId: string, answer: string): [number, unknown?] {
    if (!answer.trim()) return [400, { error: 'answer is required.' }];
    for (const details of this.threads.values()) {
      const decision = details.decisions.find((d) => d.id === decisionId);
      if (!decision) continue;
      if (decision.status !== 'Open') return [409, { error: 'That decision has already been answered.' }];
      details.decisions = details.decisions.map((d) =>
        d.id === decisionId ? { ...d, status: 'Answered', answer: answer.trim(), answeredAt: NOW } : d,
      );
      this.say(details.thread.id, { author: 'User', kind: 'DecisionAnswer', text: answer.trim(), decisionId });
      return [200, this.change(details.thread.id, { state: 'Queued' })];
    }
    return [404];
  }
}
