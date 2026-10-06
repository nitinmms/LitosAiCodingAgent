import type {
  CreateThread,
  CurrentUser,
  DispatchResult,
  FindingVerdict,
  Project,
  PullRequestInfo,
  RegisterProject,
  Settings,
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

/** The host refuses state-changing requests without this header (Auth/FactoryAuth.cs). */
export const CSRF_HEADER = 'X-Factory-Request';

const FALLBACK: Record<number, string> = {
  401: 'You are signed out. Sign in again.',
  403: 'Your account is not allowed to do that.',
  404: 'That no longer exists.',
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
  createThread(request: CreateThread): Promise<Thread>;
  thread(id: string): Promise<ThreadDetails>;
  usage(id: string): Promise<UsageCall[]>;
  postMessage(id: string, messageId: string, text: string): Promise<DispatchResult>;
  setBudget(id: string, cap: number | null): Promise<Thread>;
  accept(id: string): Promise<Thread>;
  pause(id: string): Promise<void>;
  cancel(id: string): Promise<void>;
  resume(id: string): Promise<Thread>;
  /** Takes back a change request made after a handoff. */
  withdraw(id: string): Promise<Thread>;
  pullRequest(id: string): Promise<PullRequestInfo>;
  answerDecision(decisionId: string, answer: string): Promise<Thread>;
  /** Judges a review finding, or clears the judgement with null. */
  setFindingVerdict(findingId: string, verdict: FindingVerdict | null): Promise<Thread>;
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

    return json as T;
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
    createThread: (request) => send<Thread>('POST', '/api/threads', request),
    thread: (id) => send<ThreadDetails>('GET', thread(id)),
    usage: (id) => send<UsageCall[]>('GET', `${thread(id)}/usage`),
    postMessage: (id, messageId, text) => send<DispatchResult>('POST', `${thread(id)}/messages`, { messageId, text }),
    setBudget: (id, cap) => send<Thread>('POST', `${thread(id)}/budget`, { cap }),
    accept: (id) => send<Thread>('POST', `${thread(id)}/accept`),
    // Pausing or cancelling a running task answers 202 before the task has stopped; the new
    // state arrives on the event stream, so neither returns a thread to trust.
    pause: async (id) => void (await send<unknown>('POST', `${thread(id)}/pause`)),
    cancel: async (id) => void (await send<unknown>('POST', `${thread(id)}/cancel`)),
    resume: (id) => send<Thread>('POST', `${thread(id)}/resume`),
    withdraw: (id) => send<Thread>('POST', `${thread(id)}/withdraw`),
    pullRequest: (id) => send<PullRequestInfo>('GET', `${thread(id)}/pull-request`),
    answerDecision: (decisionId, answer) =>
      send<Thread>('POST', `/api/decisions/${encodeURIComponent(decisionId)}/answer`, { answer }),
    setFindingVerdict: (findingId, verdict) =>
      send<Thread>('POST', `/api/findings/${encodeURIComponent(findingId)}/verdict`, { verdict }),
  };
}
