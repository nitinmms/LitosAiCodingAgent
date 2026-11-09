# Software Factory — run order and "Blocked by"

Picked up 2026-11-09. A person sets the order a project's queued tasks run in, and can make a task wait until another task's work is merged. With nobody arranging anything, a project's queue stays first in, first out.

Interactive prototype of the agreed design: https://claude.ai/artifact/K5MbKSN1ECLtiT1pBdpvUa

## 1. Decisions (user, 2026-11-09)

- **Ordering is order only.** "Run after", "Run first" and dragging set where a task waits in line. They do not give it another task's changes.
- **One kind of link: "Blocked by X" waits until X's pull request is merged** into the project's default branch, so the waiting task starts with that work. An "until accepted" link was considered and dropped: in clone mode a handoff frees the project, so the next task would start without the work it needed.
- **Ways out of a link:** **Release now** (the waiting task's owner or an Admin; audited), and **Mark merged** for work the factory cannot see merged (no GitHub link, or merged another way). A cancelled blocker removes the link and tells the waiting task's owner. A blocker whose pull request is closed unmerged leaves the task waiting and puts it with its owner (turn **Awaiting you**) to release or cancel.
- **A link that would make a loop is refused**, naming the loop.
- **"Admin" means the factory Admin.** There is no project-admin role. An Admin arranges any task and can use Run first. A Member moves and links only their own tasks, and moves one only by swapping it with another of their own, so nobody else's task changes place.
- **A task blocked for 2 days or more** is marked on the Board, and the blocking task's owner is reminded with a status message on its thread. There is no new notification channel.
- **A rework after Request changes goes to the front of the queue.** Before this, it re-entered at the back with a fresh `QueuedAt`.
- **Spec runs are not ordered**, since they wait only for a slot, **but they wait for their blockers**: a specification written before the work it depends on is merged would describe code without it.
- **No model call anywhere.** An agent "order check" on every reorder was prototyped and rejected: it saw only titles and one-line requests, cost a call per drag, and warned about a problem reordering cannot fix.

## 2. Facts the design rests on

- **Clone mode releases the lease at handoff** (`EfFactoryStore.StopRunAsync`: Handoff is "finished"). "Ready for testing" does not hold the project; running, a decision, a pause, Blocked and Interrupted do.
- **A run fetches before it branches** (`RunExecutor`, `GitWorkspace.CreateTaskBranchAsync` from `origin/<default>`), so once a blocker is merged the waiting task's branch has its work.
- **Nothing in the factory merges.** Accept records approval; people merge on GitHub. `IGitHub.GetPullRequestStateAsync` already told Merged from Closed.

## 3. Data (migration `RunOrderAndBlockers`)

- **`task_threads`** gains `QueueRank` (double, null when not queued for work), `RunFirst` and `RunAfterThreadId` (a draft's asked-for place, used once on delegation), `PullRequestMergedAt`, `PullRequestClosedAt`, and `BlockerNeedsYou` (a blocker's pull request was closed unmerged).
- **`task_blockers`**: `ThreadId` waits for `BlockerThreadId`, both in `ProjectId`; `CreatedBy/At`; `ReleasedAt/By` and `ReleaseReason` (`Merged`, `Manual`, `BlockerCancelled`, `ThreadCancelled`); `RemindedAt`. Released rows are kept for the thread's history. A filtered unique index allows one open link per pair.
- Threads from before the migration have no rank and wait for nothing. The claim takes an unranked task first, so they keep their first-in, first-out place.

## 4. The rules (`Core/Queue/RunOrder.cs`, pure and unit-tested)

- **`Place`**: ranks are sparse. Delegating with no placement takes the last rank + 1000; Run first takes the first − 1000; a move goes halfway between its new neighbours, so it writes one row. When two neighbours have run out of room (a gap under 2·10⁻⁶), the whole queue is renumbered 1000, 2000, …. A swap trades two ranks.
- **`Arrange`**: a project keeps the places its task runs hold in the factory-wide arrival order, so its turn among projects is still when its work arrived. Its ranks decide which of its runs fills each place. Chats, reflections and spec runs keep their own places.
- **`FindLoop`**: the path a new link would close, from the open links.
- **`WaitingReason`**: what a waiting task says, for example *Waiting for "Add column" to be merged (PR #44).*

## 5. The store

- **Placement:** delegation applies the draft's Run first or Run after. A Run-after target that has left the queue has started ahead of it, so the task goes first. A target that is still a draft has not, so the task goes to the end. Request changes puts the rework first. A claim clears the rank, and so do cancelling and withdrawing.
- **The claim** (`ClaimNextRunAsync`) arranges the queued runs, then skips any task or spec run with an open link, writing the waiting reason. A blocker is the most specific reason, ahead of "held by" and "waiting for a slot". Of several blockers, a closed pull request is named first.
- **`MoveInQueueAsync`**, **`AddBlockerAsync`** (same project; the waiting task not started, so a draft or a queued task with no branch; the blocker live, unmerged and its PR not closed; no loop), **`ReleaseBlockerAsync`**, **`MarkMergedAsync`**: each is one write under the thread's row lock, audited (`thread.queue_move`, `thread.blocker_add`, `thread.blocker_release`, `thread.mark_merged`). A move takes the revision the person saw; a different one is a conflict. Permission failures are `StoreForbiddenException` (403).
- **Cancelling** a task, by a person or through its run, releases the links that wait for it, with a note to each waiting task, and closes its own links.
- **`RecordPullRequestOutcomeAsync`**: Merged releases the waiting tasks; Closed sets `BlockerNeedsYou` on them, with a reason and a note. Saying either again changes nothing. A closed pull request is still watched, since it may be reopened and merged.
- **`RemindOfLongWaitsAsync`**: one message per blocking thread for links open longer than the interval, at most once per interval.

## 6. The host

- **`MergeWatcher`** (a background service, every `MergeCheckInterval` = 2 minutes): asks GitHub about the accepted pull requests some task waits for, and only those. Only a merge into the project's default branch counts (`IGitHub.GetPullRequestDetailsAsync` reads `base.ref`; a squash merge reports merged like any other). One unreadable pull request does not stop the others. The claim never calls GitHub. The watcher also sends the reminders (`BlockerReminderAfter` = 2 days). Without a GitHub token it asks nothing, and Mark merged is the way through.
- **API** (`Api/RunOrderApi.cs`): `GET /api/projects/{id}/run-order`, `GET /api/threads/{id}/links`, `POST /api/threads/{id}/queue-position` (`first`, `after`, `end`, `swap`, with `otherThreadId` and `revision`), `POST /api/threads/{id}/blockers`, `DELETE /api/threads/{id}/blockers/{blockerId}` (Release now), `POST /api/threads/{id}/merged`. Each change wakes the coordinator. `POST /api/threads` takes `runFirst` (Admins only), `runAfterThreadId` and `blockedBy`, all checked against the project before anything is created. The thread view carries the new thread fields, and its turn is **Awaiting you** while `BlockerNeedsYou` holds.

## 7. The web app

- **Board:** with one project chosen, a **Run order** panel above the columns shows what holds the project and the queue in order. The next task to start is marked, and blocked tasks are shown skipped with what they wait for and for how long. Admins drag, or use ↑ ↓ and Run first; Members swap their own tasks with ↑ ↓. Each queued task has Release now on its open links and a "+ Blocked by…" list while it has not started. Cards show "#n in line". With all projects shown there is no panel: order only means something within a project.
- **New thread:** **Run after** (the project's queue; Run first for Admins) and **Blocked by**.
- **Thread page:** a **Blocked by** box with what the task waits for (Release now), what it waited for, the tasks waiting for it, adding a blocker, and **Mark merged** on an accepted, unmerged task.
- Pure helpers in `domain/runOrder.ts`; the fake host implements the routes with the store's rules.

## 8. Tests

- Core: `RunOrderTests`, plus `TurnLabelsTests` for `blockerNeedsYou`.
- Store, on SQLite and PostgreSQL: `FactoryStoreTests.RunOrder.cs` (ranks, placements, reworks, fairness between projects, blocked claims, spec runs, moves and permissions, links, loops, release, merges, closed PRs, cancellation, reminders, the views); `MigrationTests.RunOrderAndBlockers_LeavesEarlierThreadsUnrankedAndUnblocked`.
- GitHub client: the base branch of a merged pull request.
- Host: `RunOrderApiTests` (every route, permissions, New thread's fields, the merge watcher).
- Web: `RunOrder.test.tsx`, `domain/runOrder.test.ts`, the client's routes in `api.test.ts`.

## 9. Not done, and why

- **Stacked branches** (a blocked task branching from its blocker's branch, as GitHub's stacked PRs and Devin do) would remove the wait for a human merge, at the cost of rebasing when the blocker changes. Left for later.
- **Links between projects** are out of scope: one project's tasks only.
- **Throughput** is still one task at a time per project in clone mode. The worktree mode in the blueprint would let several run; `Arrange` then gives "the first N tasks that wait for nothing".
