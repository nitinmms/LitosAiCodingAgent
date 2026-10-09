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
  defaultBudget: number | null;
  /** No task's budget may be above this, at creation or when raised; null for no limit. */
  maximumBudget: number | null;
  ptcEnabled: boolean;
  /** The fraction of a cached input token that counts against a task's budget, 0 to 1. */
  cachedInputWeight: number;
  presets: string[];
  taskTypes: string[];
  promptRevision: string;
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
}

export interface RegisterProject {
  gitHubUrl: string;
  name?: string;
  defaultBranch: string;
  preset: string;
  coverageThresholdPercent?: number;
  pullRequestEnabled: boolean;
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
  findings: Finding[];
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

export interface AdminSettings {
  budgets: SettingsSection<BudgetSettings>;
  providers: SettingsSection<ProviderSettings>;
  knownProviders: KnownProvider[];
  secrets: SecretStatus[];
}
