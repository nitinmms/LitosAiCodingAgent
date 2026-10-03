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

export interface Settings {
  provider: string;
  model: string;
  defaultBudget: number | null;
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

export interface Thread {
  id: string;
  projectId: string;
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
  budgetPrecision: string;
  revision: number;
  createdAt: string;
  updatedAt: string;
}

export interface CreateThread {
  projectId: string;
  title: string;
  typeLabel: string;
  budgetCap?: number;
}

export type MessageAuthor = 'User' | 'Factory';
export type MessageKind = 'Text' | 'Status' | 'Decision' | 'DecisionAnswer' | 'Handoff';

export interface Message {
  id: string;
  sequence: number;
  author: MessageAuthor;
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
  kind: 'Implement' | 'Rework';
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

export interface Finding {
  severity: 'Blocking' | 'Minor';
  file: string;
  line: number | null;
  text: string;
  status?: 'Open' | 'Fixed' | 'Dismissed';
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

export type DispatchOutcome = 'Queued' | 'FollowUp' | 'Duplicate' | 'Rejected';

export interface DispatchResult {
  outcome: DispatchOutcome;
  runId: string | null;
  thread: Thread;
}

/** The payload of a `state` or `usage` event: the thread fields a client redraws from. */
export interface ThreadChange {
  state: LifecycleState;
  stage: Stage;
  reason: string | null;
  revision: number;
  tokensUsed: number;
  tokensReserved: number;
  budgetCap: number | null;
  branch: string | null;
  pullRequestUrl: string | null;
}
