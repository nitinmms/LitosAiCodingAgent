import type {
  AccountRole,
  AdminSettings,
  Attachment,
  AttachmentSettings,
  BudgetSettings,
  CreatedInvitation,
  CreateInvitation,
  CreateThread,
  CurrentUser,
  DirectoryEntry,
  DispatchResult,
  FindingVerdict,
  FlowMetrics,
  LessonReport,
  Counters,
  DispatchStatus,
  Invitation,
  InvitationPreview,
  Lesson,
  LessonCommand,
  LessonInput,
  LessonVerdict,
  McpServerSettings,
  McpSettings,
  McpTestResult,
  FoundRepositorySkill,
  SkillSettings,
  PresetSettings,
  UpdateProfile,
  Person,
  ProviderCatalog,
  ProviderSettings,
  SpecStatus,
  Project,
  PullRequestInfo,
  RegisterProject,
  Settings,
  SettingsSection,
  Thread,
  ThreadDetails,
  ToolSettings,
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
  /** Replaces a project's profile; its revision moves on and its baseline is measured again. */
  updateProjectProfile(projectId: string, request: UpdateProfile): Promise<Project>;
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
  /** Sends a message; attachmentIds are files already uploaded to the thread, sent with it. */
  postMessage(id: string, messageId: string, text: string, attachmentIds?: string[]): Promise<DispatchResult>;
  /** Changes the signed-in account's password; answers the account as it now stands. */
  changePassword(currentPassword: string, newPassword: string): Promise<CurrentUser>;
  /** Uploads one file to the thread, unsent until a message names it. */
  uploadAttachment(threadId: string, file: File): Promise<Attachment>;
  /** Approves the specification revision the person read; a newer one makes this a 409. */
  approveSpec(id: string, revision: number): Promise<SpecStatus>;
  setBudget(id: string, cap: number | null): Promise<Thread>;
  /** The task's chosen skills; a working task takes them once it is paused and resumed. */
  setThreadSkills(id: string, skills: string[]): Promise<Thread>;
  /** A project's lessons, newest first (m4-architecture.md §3.3). */
  lessons(projectId: string): Promise<Lesson[]>;
  createLesson(projectId: string, lesson: LessonInput): Promise<Lesson>;
  /** A new revision of a lesson; expectedRevision is the one the person read. */
  reviseLesson(id: string, expectedRevision: number, lesson: LessonInput): Promise<Lesson>;
  lessonCommand(id: string, command: LessonCommand, expectedRevision: number): Promise<Lesson>;
  supersedeLesson(id: string, expectedRevision: number, replacementId: string): Promise<Lesson>;
  /** Whether a lesson a run was given helped it; null clears the verdict. */
  setRunLessonVerdict(runId: string, lessonId: string, verdict: LessonVerdict | null): Promise<unknown>;
  /** The approved lessons picked for a task's next runs. */
  setThreadLessons(threadId: string, lessonIds: string[]): Promise<Thread>;
  /** A deferred or failed lesson reflection, run again or skipped. */
  retryReflection(threadId: string): Promise<unknown>;
  skipReflection(threadId: string): Promise<unknown>;
  /** The flow metrics over one project, or all the person's, for the last 7, 30 or 90 days. */
  metrics(projectId: string | null, days: number): Promise<FlowMetrics>;
  /** How the project lessons are doing, over the same scope and period as the metrics. */
  lessonMetrics(projectId: string | null, days: number): Promise<LessonReport>;
  /** The operational counters; Admins only. */
  counters(): Promise<Counters>;
  /** Whether dispatch is paused, for the banner everyone sees. */
  dispatch(): Promise<DispatchStatus>;
  /** Pauses or resumes dispatch; Admins only. */
  setDispatch(paused: boolean, reason: string | null): Promise<unknown>;
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
  saveProviders(revision: number, settings: ProviderSettings): Promise<SettingsSection<ProviderSettings>>;
  saveTools(revision: number, settings: ToolSettings): Promise<SettingsSection<ToolSettings>>;
  saveAttachments(revision: number, settings: AttachmentSettings): Promise<SettingsSection<AttachmentSettings>>;
  saveMcp(revision: number, settings: McpSettings): Promise<SettingsSection<McpSettings>>;
  /** Starts the server as described, with the secrets set for it, and lists its tools. */
  testMcp(server: McpServerSettings): Promise<McpTestResult>;
  saveSkills(revision: number, settings: SkillSettings): Promise<SettingsSection<SkillSettings>>;
  savePresets(revision: number, settings: PresetSettings): Promise<SettingsSection<PresetSettings>>;
  /** Every version of every skill found in a project's repository. */
  repositorySkills(): Promise<FoundRepositorySkill[]>;
  /** Approves one version of a repository skill, or takes the approval back; audited. */
  setRepositorySkillApproval(projectId: string, name: string, hash: string, approved: boolean): Promise<void>;
  /** Sets or replaces a secret; its value is never sent back. */
  setSecret(name: string, value: string): Promise<void>;
  clearSecret(name: string): Promise<void>;
  /** A model's context window, from OpenRouter's catalog or the engine's own table. */
  contextLength(model: string): Promise<number>;
  /** Every provider's model catalog, as last fetched. */
  modelCatalog(): Promise<ProviderCatalog[]>;
  /** Fetches one provider's model list now; a failure comes back in the catalog's error, not as a rejection. */
  refreshCatalog(provider: string): Promise<ProviderCatalog>;
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
    // A form sets its own multipart content type, with its boundary.
    const form = typeof FormData !== 'undefined' && body instanceof FormData;
    if (body !== undefined && !form) headers['Content-Type'] = 'application/json';

    let response: Response;
    try {
      response = await fetcher(path, {
        method,
        headers,
        credentials: 'same-origin',
        body: body === undefined ? undefined : form ? (body as FormData) : JSON.stringify(body),
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
    changePassword: (currentPassword, newPassword) =>
      send<CurrentUser>('POST', '/api/auth/password', { currentPassword, newPassword }),
    settings: () => send<Settings>('GET', '/api/settings'),
    projects: () => send<Project[]>('GET', '/api/projects'),
    registerProject: (request) => send<Project>('POST', '/api/projects', request),
    updateProjectProfile: (projectId, request) => send<Project>('PATCH', `/api/projects/${projectId}`, request),
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
    postMessage: (id, messageId, text, attachmentIds) =>
      send<DispatchResult>('POST', `${thread(id)}/messages`, attachmentIds?.length ? { messageId, text, attachmentIds } : { messageId, text }),
    uploadAttachment: (threadId, file) => {
      const form = new FormData();
      form.append('file', file, file.name);
      return send<Attachment>('POST', `${thread(threadId)}/attachments`, form);
    },
    approveSpec: (id, revision) => send<SpecStatus>('POST', `${thread(id)}/spec/${revision}/approve`),
    setBudget: (id, cap) => send<Thread>('POST', `${thread(id)}/budget`, { cap }),
    setThreadSkills: (id, skills) => send<Thread>('POST', `${thread(id)}/skills`, { skills }),
    lessons: (projectId) => send<Lesson[]>('GET', `/api/projects/${encodeURIComponent(projectId)}/lessons`),
    createLesson: (projectId, lesson) => send<Lesson>('POST', `/api/projects/${encodeURIComponent(projectId)}/lessons`, lesson),
    reviseLesson: (id, expectedRevision, lesson) =>
      send<Lesson>('POST', `/api/lessons/${encodeURIComponent(id)}/revisions`, { expectedRevision, ...lesson }),
    lessonCommand: (id, command, expectedRevision) =>
      send<Lesson>('POST', `/api/lessons/${encodeURIComponent(id)}/${command}`, { expectedRevision }),
    supersedeLesson: (id, expectedRevision, replacementId) =>
      send<Lesson>('POST', `/api/lessons/${encodeURIComponent(id)}/supersede`, { expectedRevision, replacementId }),
    setRunLessonVerdict: (runId, lessonId, verdict) =>
      send<unknown>('POST', `/api/runs/${encodeURIComponent(runId)}/lessons/${encodeURIComponent(lessonId)}/verdict`, { verdict }),
    setThreadLessons: (threadId, lessonIds) => send<Thread>('POST', `${thread(threadId)}/lessons`, { lessonIds }),
    retryReflection: (threadId) => send<unknown>('POST', `${thread(threadId)}/reflection/retry`),
    skipReflection: (threadId) => send<unknown>('POST', `${thread(threadId)}/reflection/skip`),
    counters: () => send<Counters>('GET', '/api/admin/counters'),
    dispatch: () => send<DispatchStatus>('GET', '/api/dispatch'),
    setDispatch: (paused, reason) => send<unknown>('POST', '/api/admin/dispatch', { paused, reason }),
    metrics: (projectId, days) =>
      send<FlowMetrics>('GET', `/api/metrics?days=${days}${projectId ? `&projectId=${encodeURIComponent(projectId)}` : ''}`),
    lessonMetrics: (projectId, days) =>
      send<LessonReport>('GET', `/api/metrics/lessons?days=${days}${projectId ? `&projectId=${encodeURIComponent(projectId)}` : ''}`),
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
    saveProviders: (revision, settings) =>
      send<SettingsSection<ProviderSettings>>('PUT', '/api/admin/settings/providers', { revision, settings }),
    saveAttachments: (revision, settings) =>
      send<SettingsSection<AttachmentSettings>>('PUT', '/api/admin/settings/attachments', { revision, settings }),
    saveTools: (revision, settings) =>
      send<SettingsSection<ToolSettings>>('PUT', '/api/admin/settings/tools', { revision, settings }),
    saveMcp: (revision, settings) =>
      send<SettingsSection<McpSettings>>('PUT', '/api/admin/settings/mcp', { revision, settings }),
    testMcp: (server) => send<McpTestResult>('POST', '/api/admin/mcp/test', server),
    saveSkills: (revision, settings) =>
      send<SettingsSection<SkillSettings>>('PUT', '/api/admin/settings/skills', { revision, settings }),
    savePresets: (revision, settings) =>
      send<SettingsSection<PresetSettings>>('PUT', '/api/admin/settings/presets', { revision, settings }),
    repositorySkills: async () => (await send<{ skills: FoundRepositorySkill[] }>('GET', '/api/admin/skills/repository')).skills,
    setRepositorySkillApproval: async (projectId, name, hash, approved) =>
      void (await send<unknown>('PUT', '/api/admin/skills/repository/approval', { projectId, name, hash, approved })),
    setSecret: async (name, value) => void (await send<unknown>('PUT', `/api/admin/secrets/${encodeURIComponent(name)}`, { value })),
    clearSecret: async (name) => void (await send<unknown>('DELETE', `/api/admin/secrets/${encodeURIComponent(name)}`)),
    contextLength: async (model) =>
      (await send<{ contextLength: number }>('GET', `/api/admin/models/context-length?model=${encodeURIComponent(model)}`)).contextLength,
    modelCatalog: async () => (await send<{ providers: ProviderCatalog[] }>('GET', '/api/admin/models/catalog')).providers,
    refreshCatalog: (provider) => send<ProviderCatalog>('POST', `/api/admin/models/catalog/${encodeURIComponent(provider)}/refresh`),
  };
}
