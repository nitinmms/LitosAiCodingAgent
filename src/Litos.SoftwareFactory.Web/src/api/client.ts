import type {
  AccountRole,
  AdminSettings,
  BudgetSettings,
  CreatedInvitation,
  CreateInvitation,
  CreateThread,
  CurrentUser,
  DirectoryEntry,
  DispatchResult,
  FindingVerdict,
  Invitation,
  InvitationPreview,
  Person,
  SpecStatus,
  Project,
  PullRequestInfo,
  RegisterProject,
  Settings,
  SettingsSection,
  Thread,
  ThreadDetails,
  UsageCall,
} from './types';

/** A request the host answered with an error status. `message` is the host's own reason. */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

/** What asking a task to pause or cancel did. */
export interface StopResult {
  /** True when the task was running: it has been asked to stop, and has not stopped yet. */
  stopping: boolean;
}

const stopResult = (body: unknown): StopResult => ({
  stopping: typeof body === 'object' && body !== null && (body as { stopping?: unknown }).stopping === true,
});

/** The host refuses state-changing requests without this header (Auth/FactoryAuth.cs). */
export const CSRF_HEADER = 'X-Factory-Request';

const FALLBACK: Record<number, string> = {
  401: 'You are signed out. Sign in again.',
  403: 'Your account is not allowed to do that.',
  404: 'That no longer exists.',
  410: 'This link no longer works. Ask an admin for a new one.',
  423: 'This account is locked after repeated failed sign-ins. Try again later.',
  429: 'Too many attempts. Wait a minute, then try again.',
};

export type Fetch = typeof fetch;

export interface FactoryApi {
  me(): Promise<CurrentUser | null>;
  login(userName: string, password: string): Promise<CurrentUser>;
  logout(): Promise<void>;
  settings(): Promise<Settings>;
  projects(): Promise<Project[]>;
  registerProject(request: RegisterProject): Promise<Project>;
  threads(): Promise<Thread[]>;
  /** The thread list with where the board's live stream starts, read before the list. */
  threadList(): Promise<{ threads: Thread[]; cursor: number }>;
  /** Renames a thread or files it under another type: its owner or an admin. */
  editThread(id: string, change: { title?: string; typeLabel?: string }): Promise<Thread>;
  /** The names of the owners of the threads this user can see. */
  directory(): Promise<DirectoryEntry[]>;
  createThread(request: CreateThread): Promise<Thread>;
  thread(id: string): Promise<ThreadDetails>;
  usage(id: string): Promise<UsageCall[]>;
  postMessage(id: string, messageId: string, text: string): Promise<DispatchResult>;
  /** Approves the specification revision the person read; a newer one makes this a 409. */
  approveSpec(id: string, revision: number): Promise<SpecStatus>;
  setBudget(id: string, cap: number | null): Promise<Thread>;
  accept(id: string): Promise<Thread>;
  /** `stopping`: the task is running, and stops at its next safe point; the new state arrives as an event. */
  pause(id: string): Promise<StopResult>;
  cancel(id: string): Promise<StopResult>;
  resume(id: string): Promise<Thread>;
  /** Takes back a change request made after a handoff. */
  withdraw(id: string): Promise<Thread>;
  pullRequest(id: string): Promise<PullRequestInfo>;
  answerDecision(decisionId: string, answer: string): Promise<Thread>;
  /** Judges a review finding, or clears the judgement with null. */
  setFindingVerdict(findingId: string, verdict: FindingVerdict | null): Promise<Thread>;

  // People and access (Admin), and accepting an invitation (anyone holding its link).
  invitations(): Promise<Invitation[]>;
  createInvitation(request: CreateInvitation): Promise<CreatedInvitation>;
  revokeInvitation(id: string): Promise<void>;
  /** The token travels in the body, never the URL, so the host never logs it. */
  lookupInvitation(token: string): Promise<InvitationPreview>;
  acceptInvitation(token: string, password: string, displayName?: string): Promise<CurrentUser>;
  people(): Promise<Person[]>;
  disablePerson(id: string): Promise<Person>;
  enablePerson(id: string): Promise<Person>;
  setRole(id: string, role: AccountRole): Promise<Person>;
  addMember(projectId: string, userId: string): Promise<void>;
  removeMember(projectId: string, userId: string): Promise<void>;

  // Factory settings (Admin).
  adminSettings(): Promise<AdminSettings>;
  /** Saves the section whole; a revision someone else has moved past makes this a 409. */
  saveBudgets(revision: number, settings: BudgetSettings): Promise<SettingsSection<BudgetSettings>>;
}

/**
 * The host's API over fetch. The session is an HTTP-only cookie the browser sends by itself,
 * so nothing here holds a token.
 *
 * @param onSignedOut Called when a request other than the sign-in ones is refused with 401:
 * the session ended, and the app should show the sign-in screen.
 */
export function createApi(fetcher: Fetch = (...args) => fetch(...args), onSignedOut: () => void = () => {}): FactoryApi {
  async function send<T>(method: string, path: string, body?: unknown, quiet401 = false): Promise<T> {
    return (await request<T>(method, path, body, quiet401)).json;
  }

  async function request<T>(method: string, path: string, body?: unknown, quiet401 = false): Promise<{ json: T; headers?: Headers }> {
    const headers: Record<string, string> = { Accept: 'application/json' };
    if (method !== 'GET') headers[CSRF_HEADER] = '1';
    if (body !== undefined) headers['Content-Type'] = 'application/json';

    let response: Response;
    try {
      response = await fetcher(path, {
        method,
        headers,
        credentials: 'same-origin',
        body: body === undefined ? undefined : JSON.stringify(body),
      });
    } catch {
      throw new ApiError(0, 'The factory host could not be reached. Check that it is running.');
    }

    const text = await response.text();
    let json: unknown;
    try {
      json = text ? JSON.parse(text) : undefined;
    } catch {
      json = undefined;
    }

    if (!response.ok) {
      if (response.status === 401 && !quiet401) onSignedOut();
      const reason = (json as { error?: unknown } | undefined)?.error;
      throw new ApiError(
        response.status,
        typeof reason === 'string' && reason ? reason : (FALLBACK[response.status] ?? `The request failed (${response.status}).`),
      );
    }

    return { json: json as T, headers: response.headers };
  }

  const thread = (id: string) => `/api/threads/${encodeURIComponent(id)}`;

  return {
    async me() {
      try {
        return await send<CurrentUser>('GET', '/api/auth/me', undefined, true);
      } catch (error) {
        if (error instanceof ApiError && error.status === 401) return null;
        throw error;
      }
    },
    login: (userName, password) => send<CurrentUser>('POST', '/api/auth/login', { userName, password }, true),
    logout: () => send<void>('POST', '/api/auth/logout', undefined, true),
    settings: () => send<Settings>('GET', '/api/settings'),
    projects: () => send<Project[]>('GET', '/api/projects'),
    registerProject: (request) => send<Project>('POST', '/api/projects', request),
    threads: () => send<Thread[]>('GET', '/api/threads'),
    async threadList() {
      const { json, headers } = await request<Thread[]>('GET', '/api/threads');
      return { threads: json, cursor: Number(headers?.get('X-Event-Cursor') ?? 0) || 0 };
    },
    editThread: (id, change) => send<Thread>('PATCH', thread(id), change),
    directory: () => send<DirectoryEntry[]>('GET', '/api/directory'),
    createThread: (request) => send<Thread>('POST', '/api/threads', request),
    thread: (id) => send<ThreadDetails>('GET', thread(id)),
    usage: (id) => send<UsageCall[]>('GET', `${thread(id)}/usage`),
    postMessage: (id, messageId, text) => send<DispatchResult>('POST', `${thread(id)}/messages`, { messageId, text }),
    approveSpec: (id, revision) => send<SpecStatus>('POST', `${thread(id)}/spec/${revision}/approve`),
    setBudget: (id, cap) => send<Thread>('POST', `${thread(id)}/budget`, { cap }),
    accept: (id) => send<Thread>('POST', `${thread(id)}/accept`),
    // Pausing or cancelling a running task answers 202 with { stopping: true } before the task
    // has stopped; the new state arrives on the event stream, so neither returns a thread to trust.
    pause: async (id) => stopResult(await send<unknown>('POST', `${thread(id)}/pause`)),
    cancel: async (id) => stopResult(await send<unknown>('POST', `${thread(id)}/cancel`)),
    resume: (id) => send<Thread>('POST', `${thread(id)}/resume`),
    withdraw: (id) => send<Thread>('POST', `${thread(id)}/withdraw`),
    pullRequest: (id) => send<PullRequestInfo>('GET', `${thread(id)}/pull-request`),
    answerDecision: (decisionId, answer) =>
      send<Thread>('POST', `/api/decisions/${encodeURIComponent(decisionId)}/answer`, { answer }),
    setFindingVerdict: (findingId, verdict) =>
      send<Thread>('POST', `/api/findings/${encodeURIComponent(findingId)}/verdict`, { verdict }),
    invitations: () => send<Invitation[]>('GET', '/api/invitations'),
    createInvitation: (request) => send<CreatedInvitation>('POST', '/api/invitations', request),
    revokeInvitation: async (id) => void (await send<unknown>('POST', `/api/invitations/${encodeURIComponent(id)}/revoke`)),
    lookupInvitation: (token) => send<InvitationPreview>('POST', '/api/invitations/lookup', { token }, true),
    acceptInvitation: (token, password, displayName) =>
      send<CurrentUser>('POST', '/api/invitations/accept', { token, password, displayName }, true),
    people: () => send<Person[]>('GET', '/api/users'),
    disablePerson: (id) => send<Person>('POST', `/api/users/${encodeURIComponent(id)}/disable`),
    enablePerson: (id) => send<Person>('POST', `/api/users/${encodeURIComponent(id)}/enable`),
    setRole: (id, role) => send<Person>('POST', `/api/users/${encodeURIComponent(id)}/role`, { role }),
    addMember: async (projectId, userId) =>
      void (await send<unknown>('POST', `/api/projects/${encodeURIComponent(projectId)}/members`, { userId })),
    removeMember: async (projectId, userId) =>
      void (await send<unknown>('DELETE', `/api/projects/${encodeURIComponent(projectId)}/members/${encodeURIComponent(userId)}`)),
    adminSettings: () => send<AdminSettings>('GET', '/api/admin/settings'),
    saveBudgets: (revision, settings) =>
      send<SettingsSection<BudgetSettings>>('PUT', '/api/admin/settings/budgets', { revision, settings }),
  };
}
