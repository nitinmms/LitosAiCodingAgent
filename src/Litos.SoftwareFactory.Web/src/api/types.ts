// The host's API shapes (src/Litos.SoftwareFactory.Host/Api/FactoryApi.cs). The host writes
// camelCase JSON and sends every enum as its name.

export type Stage = 'Discuss' | 'Spec' | 'Implement' | 'Verify' | 'Review' | 'Handoff' | 'Done';

export type LifecycleState =
  | 'Draft'
  | 'Queued'
  | 'Running'
  | 'AwaitingDecision'
  | 'PausedBudget'
  | 'PausedUser'
  | 'Blocked'
  | 'AwaitingHumanTesting'
  | 'Accepted'
  | 'Interrupted'
  | 'Cancelled';

export interface CurrentUser {
  id: string;
  userName: string;
  displayName: string | null;
  roles: string[];
}

/** A provider a person may choose for a new thread, with the models allowed on it. */
export interface OfferedProvider {
  name: string;
  displayName: string;
  budgetPrecision: BudgetPrecision;
  models: AllowedModel[];
  defaultModel: string | null;
}

export type BudgetPrecision = 'strict' | 'estimated';

export interface Settings {
  /** What a new thread gets when its creator chooses nothing; null while no provider is ready. */
  provider: string | null;
  model: string | null;
  /** What this person may choose from: Members see only strict providers while strict-only is on. */
  providers: OfferedProvider[];
  /** The models of this person's newest threads that they can still choose, newest first: pinned in New thread. */
  recentModels: { provider: string; model: string }[];
  defaultBudget: number | null;
  /** No task's budget may be above this, at creation or when raised; null for no limit. */
  maximumBudget: number | null;
  /** Whether a new thread starts with Programmatic Tool Calling on. */
  ptcEnabled: boolean;
  /** Whether this person may create a thread with PTC other than that. */
  canChoosePtc: boolean;
  /** The fraction of a cached input token that counts against a task's budget, 0 to 1. */
  cachedInputWeight: number;
  /** The presets a project can be registered with, in the Admin's order. */
  presets: PresetName[];
  taskTypes: string[];
  /** The library skills a task can be given, with the task types that start with each chosen (m3-architecture.md §13). */
  skills?: TaskSkillOption[];
  /** The most instruction text a task's chosen skills may come to together. */
  maxTaskSkillChars?: number;
  promptRevision: string;
}

/** A library skill as New thread offers it: its size is the text it adds to every session's system prompt. */
export interface TaskSkillOption {
  name: string;
  description: string;
  chars: number;
  defaultFor: string[];
}

export interface Project {
  id: string;
  name: string;
  gitHub: string;
  defaultBranch: string;
  pullRequestEnabled: boolean;
  profileRevision: number;
  coverageThresholdPercent: number | null;
  createdAt: string;
  /** The verification profile as JSON: sent to Admins only, who may replace it. */
  profile?: string | null;
  /** Where its baseline stands: on the Projects page only. */
  baseline?: ProjectBaseline | null;
}

/** A project's baseline at its current profile revision (Api/FactoryApi.cs, m3-architecture.md §8). */
export interface ProjectBaseline {
  state: 'NotMeasured' | 'Measuring' | 'Measured' | 'CouldNotMeasure';
  /** Why it could not be measured at all. */
  problem: string | null;
  commit: string | null;
  measuredAt: string | null;
  build: 'Passed' | 'Failed' | 'NotApplicable' | 'Unavailable' | null;
  unitTests: 'Passed' | 'Failed' | 'NoTests' | 'NotRun' | 'Unavailable' | null;
  passedCount: number | null;
  failedCount: number | null;
  skippedCount: number | null;
  /** What went wrong in a baseline that ran: a failed restore, a missing report. */
  problems: string[] | null;
}

export interface RegisterProject {
  gitHubUrl: string;
  name?: string;
  defaultBranch: string;
  preset: string;
  coverageThresholdPercent?: number;
  pullRequestEnabled: boolean;
}

/** A project's new profile: a preset's, or one written out; the threshold alone changes only the threshold. */
export interface UpdateProfile {
  preset?: string;
  profileJson?: string;
  coverageThresholdPercent?: number;
}

/** Whose move a task is (§7.1), as the host computes it. */
export type Turn = 'NotStarted' | 'AwaitingYou' | 'AwaitingAgent' | 'AgentWorking' | 'Paused' | 'Done';

export interface Thread {
  id: string;
  projectId: string;
  ownerId: string;
  turn: Turn;
  title: string;
  typeLabel: string;
  stage: Stage;
  state: LifecycleState;
  stateReason: string | null;
  budgetCap: number | null;
  tokensUsed: number;
  tokensReserved: number;
  branch: string | null;
  pullRequestNumber: number | null;
  pullRequestUrl: string | null;
  provider: string;
  model: string;
  budgetPrecision: BudgetPrecision;
  /** Whether its runs use Programmatic Tool Calling; null for a thread created before M3. */
  ptcEnabled: boolean | null;
  /** The library skills chosen for the task; absent from a host before m3-architecture.md §13. */
  skills?: string[];
  /** Approved lessons a person picked for the task's runs. */
  manualLessons?: string[];
  revision: number;
  createdAt: string;
  updatedAt: string;
}

export interface CreateThread {
  projectId: string;
  title: string;
  typeLabel: string;
  budgetCap?: number;
  /** Left out for the factory's default provider, and its default model. */
  provider?: string;
  model?: string;
  /** Left out for the factory's default. */
  ptcEnabled?: boolean;
  /** Left out for the type's defaults; empty for none. */
  skills?: string[];
}

export type MessageAuthor = 'User' | 'Factory';
export type MessageKind = 'Text' | 'Status' | 'Decision' | 'DecisionAnswer' | 'Handoff' | 'Spec';

export interface Message {
  id: string;
  sequence: number;
  author: MessageAuthor;
  /** The person who wrote it; null for the factory's messages. */
  authorName: string | null;
  kind: MessageKind;
  text: string;
  decisionId: string | null;
  payload: unknown;
  createdAt: string;
}

export interface Decision {
  id: string;
  question: string;
  whyItBlocks: string;
  options: string[] | null;
  recommendation: string | null;
  impact: string | null;
  status: 'Open' | 'Answered';
  answer: string | null;
  createdAt: string;
  answeredAt: string | null;
}

export interface Run {
  id: string;
  kind: 'Implement' | 'Rework' | 'Spec';
  status: 'Queued' | 'Running' | 'Suspended' | 'Finished';
  stopReason: string | null;
  baselineCommit: string | null;
  headCommit: string | null;
  promptRevision: string | null;
  /** What the run started with; null for one that never started, or ran before M3. */
  capabilities?: RunCapabilities | null;
}

/** A run's capability snapshot (Core/Settings/RunCapabilities.cs). */
export interface RunCapabilities {
  ptc: boolean;
  shellTimeoutSeconds: number;
  webSearch: 'Off' | 'WorkTurns' | 'AllTurns';
  mcpServers: McpServerSettings[];
  /** How each server connected, once the worker said; null until then. */
  mcpStatus: McpServerReport[] | null;
  /** The library skills it was given; absent on a run from before step 6. */
  librarySkills?: { name: string; description: string }[];
  repositorySkillPolicy?: RepositorySkillPolicy;
  /** What became of each skill its working copy had, once its worker started; null until then. */
  repositorySkills?: RepositorySkillUse[] | null;
  /** The skills chosen for the task, whole in every session's system prompt; absent on a run from before §13. */
  taskSkills?: { name: string; text: string }[];
}

/** What became of one repository skill in a run (Core/Settings/SkillSettings.cs). */
export interface RepositorySkillUse {
  name: string;
  hash: string;
  status: 'Used' | 'WaitingForApproval' | 'ChangedSinceApproved' | 'NotUsedByPolicy';
  /** It is used, in place of a library skill of the same name. */
  replacesLibrarySkill: boolean;
}

export interface McpServerReport {
  name: string;
  connected: boolean;
  /** The tools the run may use. */
  tools: string[];
  error: string | null;
}

export interface Verification {
  build: string;
  unitTests: string;
  coverage: string;
  passedCount: number;
  failedCount: number;
  skippedCount: number;
  changedLineCoveragePercent: number | null;
}

/** A person's judgement of a review finding: what review yield is measured from. */
export type FindingVerdict = 'Real' | 'NotWorthFixing' | 'Wrong';

export interface Finding {
  /** Present on the thread's findings; a handoff's evidence carries none. */
  id?: string;
  severity: 'Blocking' | 'Minor';
  file: string;
  line: number | null;
  text: string;
  status?: 'Open' | 'Fixed' | 'Dismissed';
  verdict?: FindingVerdict | null;
}

export interface CriterionCoverage {
  criterion: string;
  tests: string[];
  manualOnly: boolean;
}

/** The handoff as structured evidence (Core/Orchestration/HandoffComposer.cs). */
export interface HandoffEvidence {
  summary: string;
  branch: string;
  commitSha: string | null;
  pullRequestNumber: number | null;
  pullRequestUrl: string | null;
  build: string;
  unitTests: string;
  testsPassed: number;
  testsFailed: number;
  testsSkipped: number;
  newTests: number;
  preExistingFailures: string[];
  coverage: string;
  changedLineCoveragePercent: number | null;
  coverageThresholdPercent: number | null;
  unmeasuredFiles: string[];
  review: string;
  findings: Finding[];
  criteria: CriterionCoverage[];
  testsAdded: string[];
  knownLimitations: string[];
  manualTestSteps: string[];
  commands: string[];
  changedFiles: string[];
  decisions: { question: string; answer: string }[];
  tokensUsed: number;
  budgetCap: number | null;
  /** The approved specification revision the work built, if it built one. */
  specificationRevision?: number | null;
}

/** A proposed specification, in full: a Spec message's payload (m2-architecture.md §5). */
export interface SpecPayload {
  revision: number;
  summary: string;
  acceptanceCriteria: string[];
  affectedAreas: string[];
  testPlan: string;
  openQuestions: string[];
}

/** The newest specification revision of a thread, and whether it is approved. */
export interface SpecStatus {
  revision: number;
  approved: boolean;
  approvedBy: string | null;
  approvedAt: string | null;
}

export interface Handoff {
  branch: string;
  commitSha: string | null;
  pullRequestNumber: number | null;
  pullRequestUrl: string | null;
  evidence: HandoffEvidence | null;
}

export interface ThreadDetails {
  /** Pass to the event stream to hear everything after this snapshot. */
  eventCursor: number;
  thread: Thread;
  project: Project;
  messages: Message[];
  decisions: Decision[];
  run: Run | null;
  verification: Verification | null;
  /** The latest run's review findings. */
  findings: Finding[];
  /** The earlier runs' findings, oldest first, still open to a verdict; absent from an older host. */
  earlierFindings?: Finding[];
  /** The latest run's lesson snapshot: what it was given, and left out. */
  runLessons?: RunLesson[];
  /** The lesson reflection after acceptance; null before. */
  reflection?: Reflection | null;
  /** The lessons the task's reflections proposed, whatever became of them since. */
  proposedLessons?: Lesson[];
  handoff: Handoff | null;
  /** A plain message is being answered; another waits until the reply arrives. */
  chatPending: boolean;
  /** What that answer is doing, as it last said; null until it says anything. */
  chatProgress: ChatProgress | null;
  /** The newest specification revision; null when none was proposed. */
  spec: SpecStatus | null;
}

/** What a chat answer in progress has done so far (the host's ChatProgress). */
export interface ChatProgress {
  runId: string;
  startedAt: string;
  modelCalls: number;
  toolCalls: number;
  /** What it is doing now, in a few words: "Thinking", "Read src/Orders.cs". */
  activity: string;
}

export interface UsageCall {
  id: string;
  runId: string | null;
  model: string;
  estimatedInput: number;
  reserved: number;
  actualInput: number;
  actualCachedInput: number;
  /** What the gateway expected the provider's cache to serve, and what it read from it. */
  expectedCachedInput: number;
  actualCacheRead: number;
  /** Tokens charged above what the call would have cost had the cache served what was expected; 0 when it was not a cache miss. */
  cacheMissTokens: number;
  /** A router's upstream, or the provider itself; null until settled. */
  servedBy: string | null;
  actualOutput: number;
  actualReasoning: number;
  charged: number;
  /** Estimated: the provider never reported usage, so the call was charged the host's input estimate. */
  status: 'Reserved' | 'Settled' | 'Unknown' | 'Estimated';
  /** What the call was for: Implement, Rework, Repair, Review, LightReview or Nudge; null outside a turn. */
  phase: string | null;
  createdAt: string;
  settledAt: string | null;
}

/** Where a task's pull request stands on GitHub now. Unknown: GitHub could not be asked. */
export type PullRequestState = 'Draft' | 'Open' | 'Merged' | 'Closed' | 'Unknown';

export interface PullRequestInfo {
  number: number;
  url: string | null;
  state: PullRequestState;
}

/** Chat: a plain message that Litos answers without starting work (m2-architecture.md §5). */
export type DispatchOutcome = 'Queued' | 'FollowUp' | 'Chat' | 'Duplicate' | 'Rejected';

export interface DispatchResult {
  outcome: DispatchOutcome;
  runId: string | null;
  thread: Thread;
}

/** The payload of a `state` or `usage` event: the thread fields a client redraws from. */
export interface ThreadChange {
  state: LifecycleState;
  stage: Stage;
  turn: Turn;
  title: string;
  typeLabel: string;
  reason: string | null;
  revision: number;
  tokensUsed: number;
  tokensReserved: number;
  budgetCap: number | null;
  branch: string | null;
  pullRequestUrl: string | null;
}

// ---- People and access (M2, docs/software-factory/m2-architecture.md §4) ----

export type AccountRole = 'Member' | 'Admin';

export type InvitationStatus = 'Pending' | 'Accepted' | 'Revoked' | 'Expired';

/** An invitation as the Admin sees it. Its link is shown only once, when it is created. */
export interface Invitation {
  id: string;
  userName: string;
  email: string | null;
  role: AccountRole;
  projectIds: string[];
  status: InvitationStatus;
  createdBy: string;
  createdAt: string;
  expiresAt: string;
  acceptedAt: string | null;
  acceptedUserId: string | null;
  revokedAt: string | null;
}

export interface CreateInvitation {
  userName: string;
  role: AccountRole;
  projectIds: string[];
}

export interface CreatedInvitation {
  invitation: Invitation;
  /** `#/invite/<token>`: the one time the link exists anywhere. */
  link: string;
}

/** What the invitation page shows before the invitee chooses a password. */
export interface InvitationPreview {
  userName: string;
  role: AccountRole;
  expiresAt: string;
}

/** A person on the Admin's people screen. */
export interface Person {
  id: string;
  userName: string;
  displayName: string | null;
  role: AccountRole;
  disabled: boolean;
  /** Admins see every project whatever this says. */
  projectIds: string[];
}

/** A name the board shows for an owner: only owners of threads the user can see. */
export interface DirectoryEntry {
  id: string;
  name: string;
}

// ---- Factory settings (Admin; Api/SettingsApi.cs, m3-architecture.md §3) ----

/** One settings section as stored: a change names the revision it was read at. */
export interface SettingsSection<T> {
  revision: number;
  settings: T;
}

/** The Budgets and limits tab (Core/Settings/BudgetSettings.cs). Null means "no cap", "no limit" or "no quota". */
export interface BudgetSettings {
  defaultTaskBudget: number | null;
  maximumTaskBudget: number | null;
  dailyUserQuota: number | null;
  monthlyUserQuota: number | null;
  repairCyclesPerRun: number;
  slotCap: number;
  /** A change request adds this share of the task's first cap: 0.5 is half. */
  reworkTopUpShare: number;
  outputAllowanceTokens: number;
  /** What one lesson reflection may spend from an accepted task's budget; 0 turns reflection off. */
  reflectionAllowanceTokens: number;
}

/** Whether a secret is set, and when: never its value. */
export interface SecretStatus {
  name: string;
  setAt: string;
  setBy: string | null;
}

/** A model members may choose, with the context window its worker compacts against. */
export interface AllowedModel {
  id: string;
  contextLength: number;
}

/** One provider on the Providers tab (Core/Settings/ProviderSettings.cs). Its key is a secret. */
export interface ProviderEntry {
  name: string;
  enabled: boolean;
  /** Where a provider that usesBaseUrl is reached. */
  baseUrl: string | null;
  models: AllowedModel[];
  defaultModel: string | null;
  /** Offer every tool-capable model in the provider's catalog, not only the allowed ones. */
  allowEveryModel: boolean;
}

export interface ProviderSettings {
  providers: ProviderEntry[];
  defaultProvider: string | null;
  strictOnly: boolean;
}

/** A provider the factory can offer, as the host describes it. */
export interface KnownProvider {
  name: string;
  displayName: string;
  precision: 'Strict' | 'Estimated';
  usesBaseUrl: boolean;
  /** The name its key is kept under. */
  keySecret: string;
}

/** The Tools tab (Core/Settings/ToolSettings.cs). The web search key is a secret. */
export interface ToolSettings {
  ptcByDefault: boolean;
  membersMayChoosePtc: boolean;
  shellTimeoutSeconds: number;
  webSearchEnabled: boolean;
  webSearchOnReadOnlyTurns: boolean;
}

export type McpAccess = 'Full' | 'Deny';

/** One MCP server (Core/Settings/McpSettings.cs). Its variables' values are secrets. */
export interface McpServerSettings {
  name: string;
  transport: 'Stdio' | 'Http';
  command: string | null;
  args: string[];
  url: string | null;
  enabled: boolean;
  permission: McpAccess;
  /** By the server's own tool name. */
  toolOverrides: Record<string, McpAccess>;
  secretVariables: string[];
}

export interface McpSettings {
  servers: McpServerSettings[];
}

/** A Test connection result. */
export interface McpTestResult {
  connected: boolean;
  tools: { name: string; description: string | null }[];
  error: string | null;
}

/** How runs treat the skills a repository brings in its .litos/skills. */
export type RepositorySkillPolicy = 'ApproveEach' | 'Always' | 'Never';

/** One skill of the factory library (Core/Settings/SkillSettings.cs): a SKILL.md written from these fields. */
export interface LibrarySkill {
  name: string;
  /** One line: all the agent sees until it loads the skill. */
  description: string;
  body: string;
  enabled: boolean;
  /** The task types whose new tasks start with this skill chosen. */
  defaultFor: string[];
}

/** The Skills tab (Core/Settings/SkillSettings.cs). */
export interface SkillSettings {
  repositoryPolicy: RepositorySkillPolicy;
  library: LibrarySkill[];
}

/** One version of a skill found in a project's repository (Api/SettingsApi.cs). */
export interface FoundRepositorySkill {
  projectId: string;
  project: string;
  name: string;
  hash: string;
  description: string;
  /** Its folder, relative to the repository's root. */
  folder: string;
  /** Its SKILL.md as found. */
  content: string;
  /** The other files in its folder, relative to it. */
  otherFiles: string[];
  firstFoundAt: string;
  lastFoundAt: string;
  approvedAt: string | null;
  /** The version found most recently: the one a run would meet now. */
  latest: boolean;
}

export interface PresetName {
  name: string;
  displayName: string;
}

/** One verification preset (Core/Settings/PresetSettings.cs): its profile is JSON text, edited as written. */
export interface VerificationPreset extends PresetName {
  profile: string;
}

/** The Presets tab. */
export interface PresetSettings {
  presets: VerificationPreset[];
}

export interface AdminSettings {
  budgets: SettingsSection<BudgetSettings>;
  providers: SettingsSection<ProviderSettings>;
  tools: SettingsSection<ToolSettings>;
  mcp: SettingsSection<McpSettings>;
  skills: SettingsSection<SkillSettings>;
  presets: SettingsSection<PresetSettings>;
  knownProviders: KnownProvider[];
  secrets: SecretStatus[];
}

/** A model a provider listed at its last catalog fetch. Null: the provider does not report it. */
export interface CatalogModel {
  id: string;
  displayName: string | null;
  contextLength: number | null;
  supportsTools: boolean | null;
  /** US dollars per million tokens. */
  inputPricePerMillion: number | null;
  outputPricePerMillion: number | null;
}

/** One provider's model catalog (Api/SettingsApi.cs, m3-architecture.md §4.3). */
export interface ProviderCatalog {
  name: string;
  /** Null: never fetched. */
  attemptedAt: string | null;
  /** When a fetch last worked; null if none has. */
  fetchedAt: string | null;
  /** Why the last fetch failed; null when it worked. */
  error: string | null;
  models: CatalogModel[];
  /** Allowed models (as saved) that the provider no longer lists. */
  retired: string[];
}


/** Where a lesson stands (Core/Lessons/Lessons.cs, m4-architecture.md §3). */
export type LessonStatus = 'Suggested' | 'Approved' | 'Dismissed';

/** What a lesson rests on. */
export type LessonSource = 'Manual' | 'HumanCorrection' | 'Decision' | 'ReviewFinding' | 'Evidence';

/** One immutable revision of a lesson. */
export interface LessonRevision {
  revision: number;
  rule: string;
  topics: string[];
  paths: string[];
  source: LessonSource;
  sourceRef: string | null;
  /** The thread the source belongs to, to link to; null for a manual lesson. */
  sourceThreadId: string | null;
  reason: string;
  /** Null for a reflection's proposal: an agent wrote it. */
  author: string | null;
  createdAt: string;
  approvedBy: string | null;
  approvedAt: string | null;
}

/** A project's lesson (Api/LessonsApi.cs). */
export interface Lesson {
  id: string;
  projectId: string;
  number: number;
  status: LessonStatus;
  enabled: boolean;
  currentRevision: number;
  approvedRevision: number | null;
  /** An edit waits for an Admin; runs keep using the approved revision meanwhile. */
  pendingEdit: boolean;
  supersededById: string | null;
  proposedByJobId: string | null;
  createdBy: string | null;
  createdAt: string;
  updatedAt: string;
  current: LessonRevision;
  /** The approved revision, when it is older than the current one. */
  approved: LessonRevision | null;
}

/** What a person writes as a lesson. */
export interface LessonInput {
  rule: string;
  topics: string[];
  paths: string[];
  reason: string;
  /** An Admin's Save and approve. */
  approve?: boolean;
}

export type LessonCommand = 'approve' | 'dismiss' | 'disable' | 'enable';

/** Where a task's lesson reflection stands (m4-architecture.md §5). */
export interface Reflection {
  status: 'Pending' | 'Running' | 'DeferredBudget' | 'Completed' | 'Failed' | 'Skipped';
  attempts: number;
  lastError: string | null;
  outcome: { lessons: number[]; refused: { rule: string; why: string }[] } | null;
  updatedAt: string;
}

/** What a person judged an included lesson to have done for a run. */
export type LessonVerdict = 'Helped' | 'DidNotMatter' | 'Misled';

/** One lesson in a run's snapshot (Api/LessonsApi.cs RunLessonView). */
export interface RunLesson {
  lessonId: string;
  number: number;
  revision: number;
  rule: string;
  score: number;
  inclusion: 'Retrieved' | 'Manual' | 'NotIncluded';
  manual: boolean;
  includedAt: string;
  verdict: LessonVerdict | null;
  verdictAt: string | null;
}

/** The count, median and 90th percentile of some values. */
export interface Spread {
  count: number;
  median: number;
  p90: number;
}

export interface TimeIn {
  name: string;
  count: number;
  totalHours: number;
  medianHours: number;
  p90Hours: number;
}

/** The flow metrics (Api/MetricsApi.cs, m4-architecture.md §6). */
export interface FlowMetrics {
  projectId: string | null;
  days: number;
  tasks: number;
  accepted: number;
  cancelled: number;
  open: number;
  /** Accepted tasks with no change request, over all accepted; null with none accepted. */
  firstHandoffAcceptance: number | null;
  reworkRounds: { rounds: number; tasks: number }[];
  timeInState: TimeIn[];
  timeInStage: TimeIn[];
  tokensPerTask: Spread;
  tokensTotal: number;
  tokensByPhase: { name: string; tokens: number; share: number }[];
  tokensByModel: { name: string; tokens: number; share: number }[];
  openDecisions: number;
  oldestOpenDecision: { threadId: string; title: string; question: string; ageHours: number } | null;
  /** Minutes queued waiting for the repository's lock, and for a free slot. */
  queueForRepository: Spread;
  queueForSlot: Spread;
  lessons: { included: number; leftOut: number; helped: number; didNotMatter: number; misled: number; unjudged: number };
}

/** Whether dispatch is paused, as everyone sees it (Api/DispatchApi.cs, m4-architecture.md §8.1). */
export interface DispatchStatus {
  paused: boolean;
  reason: string | null;
  since: string | null;
}

/** The operational counters, for Admins (Api/CountersApi.cs, m4-architecture.md §7). */
export interface Counters {
  /** Paused: nothing new starts, of any kind, until an Admin resumes it. */
  dispatch: { paused: boolean; reason: string | null; changedAt: string | null; changedBy: string | null };
  /** Only on a host started on data restored from a backup (m4-architecture.md §8.3). */
  restore: {
    backupTakenAt: string;
    backupHostVersion: string;
    filesChecked: number;
    /** Files the backup held that are missing or changed here. */
    problems: string[];
    /** Stored secrets the restored key ring cannot read. */
    unreadableSecrets: string[];
    checkedAt: string;
  } | null;
  tasks: { queued: number; running: number; stopped: { state: string; count: number }[] };
  openDecisions: number;
  oldestDecision: { threadId: string; title: string; question: string; ageHours: number } | null;
  runs: {
    runId: string;
    threadId: string;
    title: string;
    kind: string;
    minutesRunning: number | null;
    minutesSinceCheckpoint: number | null;
    executed: boolean;
    workerAlive: boolean;
    /** Marked running, but this host holds no executor for it. */
    needsAttention: boolean;
  }[];
  unsettled: { count: number; tokens: number; oldest: string | null };
  eventStreams: { open: number; slowestSecondsBehind: number };
  pushFailures: { days: number; count: number; latest: { threadId: string; title: string; at: string }[] };
  reflections: { failed: number; deferred: number; pending: number; completed: number };
  quotas: { daily: number | null; monthly: number | null };
  users: { userId: string; name: string; day: number; month: number; dayShare: number | null; monthShare: number | null }[];
}
