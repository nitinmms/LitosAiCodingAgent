# Software Factory — a chat answer's allowance, and what chat spends

Picked up 2026-11-12. The question was how to raise the budget when a chat answer that writes an artifact runs out, as a task's Revise budget panel does. Most of the problem turned out to be the wording.

Mockup of the agreed Chat tab: https://claude.ai/artifact/19GW48GfDXAV5yyyv7tRgY

## 1. What was there

- **Artifacts are written by chat answers** (`ChatExecutor`), and **a chat answer is never charged to the task's budget.** Each answer is a chat run with its own allowance: `ChatBudgetCap = FACTORY_CHAT_TURN_CAP`, 100,000 tokens by default. The ledger (`EfFactoryStore.LedgerAsync`) charges a chat run to that allowance and to the person's quota, and never to the thread's cap.
- **The refusal said otherwise.** `BudgetLedger.Admit` names a chat run's allowance `TaskBudget` (the snapshot carries it in the task's place), so the thread read *"…but the task's budget has N left"* and *"Raise the task's budget, then resume."* Raising the task's budget changes nothing for chat.
- **A chat answer cannot resume.** Every answer is a fresh session on a reading worker that is shut down when the answer ends. Its run is `Finished`, and there is no paused state or checkpoint. The task's `PausedBudget` → Raise budget and resume path has nothing to resume here.
- **Flow counted chat as task tokens.** Every usage row on a thread went into tokens per task, tokens task by task and the phase and model breakdowns (chat folded into "Other"), although none of it was charged to the task.

## 2. Decisions (user, 2026-11-12)

- **Retry, not resume.** An answer its own allowance stopped offers **Try again with a larger allowance**: the same question, as a new chat run, from scratch. Making chat resumable (keeping or saving sessions, a paused state) was judged too large for what it buys.
- **Anyone on the project** may retry, as anyone may change a task's budget. **No ceiling** on the new allowance; it only has to be above the old one.
- **The message names the answer's allowance**, never the task's budget. The allowance itself stays an environment variable; it was not moved to Settings.
- **Flow no longer counts chat** in any task-token figure.
- **A Metrics tab, "Chat"**, for what chat spends as a whole, by project and over time, with answers that published an artifact told apart from plain ones. **Stopped attempts and retries are not tracked** in the metrics; an earlier mockup with an allowance-advice chart, a retry funnel and per-answer dots was set aside as more than needed.

## 3. The message

- `RefusalReason.ChatAllowance`, and `BudgetLedger.ForChat`, which renames a chat run's `TaskBudget` refusal or output limit to it. The store applies it in `ReserveAsync` when the run is a chat. The quota stays `UserQuota`.
- `ModelGateway.RefusalMessage` / `BudgetCutOffMessage`: *"…but this answer's allowance has N left. Try again with a larger allowance."* The gateway error code is still `BudgetExhausted`, so the worker behaves as before.
- `ActiveRun.RefuseForBudget(message, reason)` keeps whose limit it was (`BudgetRefusalReason`), so `ChatExecutor` can tell its allowance from the quota.

## 4. The retry (no migration)

- **Stopped by its allowance:** `FinishChatRunAsync(…, stoppedByAllowance: true)` with no reply sets the run's `StopReason = BudgetExhausted`, and the note's payload becomes a `ChatAnswerPayload`: the artifacts it published (as before) and `chatStop: { runId, allowance, used }`. A quota stop or a timeout carries no `chatStop` and offers nothing.
- **`POST /api/threads/{id}/chat/{runId}/retry { allowance }`** → `IFactoryStore.RetryChatAsync`:
  - the run must be a chat run on this thread (else 404), finished and stopped by its allowance, and the allowance must be above its own (else 409);
  - **once per stopped answer:** the retry's note has the dispatch key `retry:<runId>`, so a second press, by anyone, is `Duplicate`;
  - refused (`Rejected`, 409) while another answer is running, like any message;
  - the new chat run has the stopped run's `Request` and `ChatBudgetCap = allowance`; the person's note *"Tried again with an allowance of N tokens."* carries `{ retryOf, allowance }`.
- **The brief is the first attempt's:** `ReadingWorker.Conversation` cuts the thread at the question, so the retry is told what the first attempt was told. The artifacts the first attempt published are in the brief's artifact list, so the retry updates them by id rather than making new ones.
- **Web:** a Status note from Litos with `chatStop` and no later `retryOf` naming it shows `ChatRetryPanel`, which offers 1.5× the old allowance, rounded up to 10,000. Try again is disabled while another answer is running.

## 5. Flow without chat

`FlowUsage` carries its run's kind; `FlowMetrics.Compute` leaves `RunKind.Chat` out. A call no run made, and reflection runs (charged to the task), still count.

## 6. The Chat tab

- **Data** (`GetChatMetricsDataAsync`): every chat run **asked in the period** on the projects' threads (by when it ran, not when its task was created, unlike Flow), with its charged usage and whether any artifact version names it; and the task work charged in the period on the same projects (usage rows not made by a chat run).
- **Report** (`ChatMetrics.Compute`): chat tokens, the artifact/plain split, share of all tokens, answers, the spread per answer (over answers that spent anything) and the plain and artifact medians, by project (most first), the ten tasks with the most chat, and the same stretches as the flow trend (`FlowMetrics.Stretches`), split by project.
- **`GET /api/metrics/chat`**, scoped like the other metrics: one project or all the caller's, 7, 30 or 90 days.
- **Page** (`ChatReport.tsx`): summary tiles; the trend with a **By project / Plain or Artifact** toggle; chat by project on one scale; chat beside task work; tasks with the most chat. Each chart has its table under **Tables**.

## 7. Tests

- Core: `BudgetLedgerTests` (ForChat), `FlowMetricsTests` (chat left out), `ChatMetricsTests`.
- Store (SQLite and PostgreSQL): `FactoryStoreChatRetryTests`, `FactoryStoreChatMetricsTests`, the chat refusal reason in `FactoryStoreTests`, and `RunKind` on flow usage.
- Host: `GatewayTests` (messages), `ChatTests` (stop → note → retry answers; refused retries), `MetricsApiTests` (chat report; chat not in Flow; scoping).
- Web: `ChatRetry.test.tsx`, `ChatMetrics.test.tsx`, routes in `Metrics.test.tsx`.
