# Software Factory — files and images as task input

Picked up 2026-10-19, after M4. Builds the proposal recorded in m3-architecture.md §14; where this document differs, it wins.

## 1. Decisions (user, 2026-10-19)

- **The types are the Litos VS Code agent's**, no more and no fewer (`Litos.VsCodeHost/Turns/AttachEndpoints.cs`):
  - **Images**, sent to the model as vision input: `.png`, `.jpg`, `.jpeg`, `.gif`, `.webp` (`ImageMedia`).
  - **Documents**, converted to Markdown by MarkItDown: `.pdf`, `.doc`, `.docx`, `.ppt`, `.pptx`, `.xls`, `.xlsx`, `.epub`, `.msg`, `.eml` (`PlainTextMedia.IsKnownDocumentFormat`).
  - **Any text file**, read as it is: a file with no NUL byte in its first 8 KB (`PlainTextMedia.IsBinary`). Any other binary file is refused, with the VS Code agent's reason.
  - **20 MB per file.**
- **Limits beyond that**, in a new `attachments` settings section, which an Admin can change: **5 files per message, 50 MB per thread.**
- **Who may attach:** any member who may post to the thread. An Admin switch turns uploads off factory-wide; it is on by default. Files already attached stay readable when it is off.
- **Review turns get no attachments.** They judge the diff. Implement, rework, spec and chat runs get them.
- **Built as a plan, then five steps** (§8). Step 1 is a live check that an image survives the factory's own path.

Chosen without asking, and open to change:

- **No downscaling.** The VS Code agent sends images as they are, and Anthropic and OpenAI bill an image after their own resize, so scaling down would save upload bytes rather than tokens, at the cost of an image library. The 20 MB cap holds.
- **Whether a model accepts images** is a new `AcceptsImages` on each allowed model: from the catalog where the provider reports it (OpenRouter lists input modalities), otherwise on for Anthropic, OpenAI and Gemini and off for Local and MeshApi; an Admin can change it.

## 2. Today

A task takes text only: `PostMessageRequest(MessageId, Text)`, and every brief reaches the worker as one string (`IWorkerClient.RunTurnAsync(sessionId, kind, brief, …)`), which `Litos.Hosting`'s default reader turns into one `TextBlock`. A tool result is text only (`ToolResultBlock`), so an agent that reads a PNG from disk never sees it.

What exists to build on: `ImageBlock(MediaType, Data)` is a polymorphic content block (`"image"`, the bytes as base64 in JSON), so it should cross the worker-to-host gateway call unchanged, and compaction already estimates one. `IAttachmentConverter` (MarkItDown) and `UntrustedContent.Wrap` convert and fence documents. `Litos.Hosting`'s turns endpoint takes a `readTurnRequest`, as VsCodeHost uses. Every working copy excludes `/.factory/` from commits.

## 3. Storage, upload and download (build step 2)

- **`thread_attachments`**: Id, ThreadId, ProjectId, MessageId (null until sent), FileName, MediaType, Kind (`Image`, `Document`, `Text`), SizeBytes, Sha256, UploadedBy, UploadedAt. Migration `ThreadAttachments`.
- **Bytes** in `<data>/attachments/<threadId>/<id>`, never the database or `wwwroot`, so the M4 backup (which copies the data folder) takes them with it, and the restore check counts them.
- **`POST /api/threads/{id}/attachments`**: multipart, one file. Checks the switch, the type rule (§1), 20 MB, and the thread's 50 MB, and returns the record. An upload not sent with a message within a day is removed on the coordinator's periodic sweep.
- **`PostMessageRequest` gains `AttachmentIds`**: at most 5, each uploaded to this thread by this user and not yet sent. Refused with a reason on a follow-up to a running task, since steering a running turn takes text only (`SteerAsync`).
- **`GET /api/attachments/{id}`**: the file, under the thread membership rule (404 outside the user's projects), sent with `Content-Disposition: attachment` and `X-Content-Type-Options: nosniff`, so no upload is ever rendered as a page. Images are shown in the app through the same URL as `<img>`.
- The thread details list each message's attachments.

## 4. Into the run (build steps 1 and 3)

- **Files**: at preflight the host copies the thread's sent attachments into the working copy's `.factory/attachments/<id>-<file name>`, so the agent can open a CSV sample or copy an icon into the repository and commit it there.
- **The brief** (prompt revision m2.7) gains an **Attachments** section listing each file by path, the message it came with, and for a rework which ones came with the change request:
  - **Documents and text files** are converted and included, wrapped as untrusted, up to about 50,000 characters each; beyond that the brief says so and points to the file.
  - **Images** are named in the section and go into the turn as `ImageBlock`s after the brief's text, when the thread's model accepts images. Otherwise the brief says the model cannot see them and gives their paths.
- **The worker's turn reader**: `RunTurnAsync` gains `images: [{ path, mediaType }]`, paths relative to the working copy; a factory `readTurnRequest` reads the bytes on the worker's side, so no base64 crosses the host-to-worker call, and refuses a path outside the working copy (`WorkingCopyGuard`).
- **Which runs**: implement, rework, spec and chat runs get every attachment sent on the thread before the run started. Review, light review, the decision scan and reflection get none.
- **After compaction** the images are gone from the conversation (compaction summarises them) and the agent cannot look again: a tool result cannot carry an image. The brief tells the agent to describe what it needs from an image early. A tool to view an image again is out of V1.

## 5. Budget (build step 1)

An image is charged by the provider's own count. Before a call that carries one is reserved, the estimate adds a fixed allowance per image, the same figure compaction uses, so the reservation stays honest; step 1 measures what one screenshot really costs on the default models and sets that figure. A converted document counts as text.

## 6. Models that cannot see images (build step 2)

`AllowedModel` gains `AcceptsImages` (§1), shown and editable on the Providers tab; the catalog's `model_catalog` gains `SupportsImages`, filled where the provider reports it. A thread copies the flag when created, as it copies the context length. The composer says so when an image is attached to a thread whose model does not accept images; the file is still attached and copied to the working copy.

## 7. In the app (build step 4)

The composer gets an attach button, paste and drag-and-drop. Each file uploads when added and shows as a chip that can be removed, with its refusal reason if refused. A sent message shows image thumbnails and a chip for anything else, each downloadable. The run details list what the run was given. The Settings page gets the `attachments` section (switch and limits).

## 8. Build order

1. **The path, proven with a fixture image.** Worker turn reader, `RunTurnAsync` images, gateway pass-through, the estimate's image allowance; then one live call per strict provider (Anthropic, OpenAI, Gemini) and OpenRouter through the real worker and gateway, recording what each reports for the image. Tests with a fixture PNG.
2. **Store and API.** `thread_attachments` + migration, upload, download, `AttachmentIds`, the `attachments` settings section, the type rule and limits, `AcceptsImages`/`SupportsImages`, the sweep, backup coverage.
3. **Into the run.** Copy to `.factory/attachments/`, the brief's Attachments section (m2.7), images on the first turn of the runs in §4, none on review.
4. **The app.** Composer, message display, run details, Settings section, Providers tab flag.
5. **A check.** A UI bug fixed from a screenshot alone; a feature built from an attached CSV sample and a PDF note; a model without image input given an image; a Member refused a sixth file and an unknown binary.

Each step follows the factory rule: unit tests for every new piece and every change, as part of the step.

## 9. Not in V1

Viewing an image again after compaction; attachments on a follow-up to a running task; deleting an attachment (threads are never deleted); virus scanning; storage outside the data directory; downscaling.

## 10. Built

### Step 1 (2026-10-19): the path

`FactoryTurnRequest` and `TurnImage` (Contracts) carry a turn's images as working-copy paths; `IWorkerClient.RunTurnAsync` takes them; the worker's turn reader (`WorkerApp.ReadImageAsync`) reads each through `WorkingCopyGuard` and refuses the turn (400) for a path outside the working copy, a type no provider takes, a missing file or one over 20 MB, before anything is changed for the session. `AttachmentTypes` (Contracts) holds the VS Code agent's type rule for both sides. The gateway needed no change: `ImageBlock` crosses its HTTP call as base64 and reaches the provider byte for byte (`RunTests.AnImageInAModelCall_ReachesTheProvider_Unchanged`).

**Live check** (`LiveGatewayTests.AnImage_…`, opt-in): the same question with and without `Litos-Agent-Loop.png` (784×1,697, 115 KB), through the real gateway. Every model read the diagram's title correctly.

| Provider / model | Image cost (input tokens) |
| --- | --- |
| gemini / gemini-3.1-flash-lite | 1,078 |
| openrouter / deepseek/deepseek-v4.1-flash | 822 — it reads images, served by InferenceNet |
| openai / gpt-6-luna | 1,621 |
| anthropic / claude-haiku-5-5 | 1,711 |
| openrouter / google/gemini-2.5-flash | 2,322 |

Compaction's per-image figure (4,800 characters, about 1,200 tokens) is under three of five, so the factory's estimator now reserves `RequestEstimator.ImageTokens` = 2,500 per image; the engine's constant, which the other faces use, is unchanged.

### Step 2 (2026-10-19): store and API

Migration `ThreadAttachments`: the `thread_attachments` table and `model_catalog.SupportsImages`. `IFactoryStore` adds, gets, lists, sends, unsends and sweeps attachments (`EfFactoryStore.Attachments.cs`); a thread's total is checked under its row lock, unsent uploads counted. `ProjectScoped.Attachment` puts downloads under the one membership rule. `AttachmentsApi`: upload (multipart, one file, read without form binding since the CSRF header and the SameSite=Strict cookie already guard it), download (an image as its type, anything else as `application/octet-stream` to save, `nosniff`, a sandboxing CSP; an unsent file only to its uploader). `PostMessageRequest.AttachmentIds` marks the files sent by the message's client id before the message is posted, and takes them back if the thread refuses it; refused for a Running task and over the per-message limit. Thread details list each message's files. `AttachmentSweeper` removes unsent uploads a day old, hourly. The `attachments` settings section (Admin PUT, and the limits on `GET /api/settings` for the composer). `AllowedModel.AcceptsImages`, with `AllowedModel.Accepts` for the provider's usual; the OpenRouter catalog fills `SupportsImages` from `architecture.input_modalities`. The backup keeps `attachments/`.

Changed from §6: the flag is not copied to the thread. A run reads it from the settings when it starts, so an Admin's correction applies to the next run; a model no longer allowed falls back to its provider's usual.

### Step 3 (2026-10-19): into the run

`RunAttachments` (Host) copies a thread's sent files into the working copy's `.factory/attachments/<id8>-<name>`, reads text files and converts documents with the engine's MarkItDown converter (now registered in the host), and returns the brief's section and the images. `BriefComposer.Attachments` (prompt m2.7, template `attachments.md`) lists each file by path, fences each text in `<attachment>` with an untrusted-data line (a file cannot close its own fence), at most 50,000 characters a file and 100,000 a brief, and says when the model cannot see images. Given to: the run brief of an implementation and a rework brief (`RunExecutor.GiveAttachmentsAsync`), and every chat and spec turn (each a new session). A rework shows again only files sent since the last handoff; earlier ones are listed "attached earlier". Review, light review, scan, repair, reminder, resume and reflection turns get none. A work run notes in the thread what it was given ("Gave the agent 2 attached files: screen.png, orders.csv."), in place of a run-details row.

### Step 4 (2026-10-19): the app

The composer (`ThreadMain`, `Attachments.tsx`) has an Attach button, paste and drag-and-drop; each file uploads when added and shows as a chip with its size, or the reason it was refused (over the file limit, past the message's count, or the host's reason), and can be removed. Send waits for uploads and sends the ready files' ids. Attaching is off while answering a decision, while the task is Running, and when an Admin has turned it off (no button); a warning shows when an image is attached to a thread whose model does not read images. A sent message shows image thumbnails (from the download URL) and a chip for anything else, to save. Settings gets an Attachments tab (switch, files per message, total per task in MB). The Providers tab's allowed models get "Reads images" (Usual / Yes / No); a model allowed from the catalog takes the catalog's answer.

### Step 5 (2026-10-19): the check, on the live factory

Anthropic runs on `claude-haiku-5-5`, as the user asked for image testing. Files made for it: a 1280×180 PNG of a top bar ("Story Studio", a "Beta" pill, "Brief, photos, story: in three steps."), a CSV sample of form fields, and a one-page PDF of import rules; the request texts named none of what they held.

| Item | Result |
| --- | --- |
| A UI change from a screenshot alone (insta-story-generator) | **Pass.** Accepted at 62,966 tokens (one decision, no rework). The diff has the exact title, badge and tagline, and badge colours within a few points of the image's (#d9ebf1/#1d5468 against #dcebf0/#1d5c73). Checked by running the branch in Chrome: before "Insta Story Generator"; after the screenshot's text; 166 tests passed. |
| A feature from a CSV sample and a PDF note (forms-api) | **Pass.** Accepted at 198,591 tokens. The PDF reached the brief through MarkItDown (its line breaks ran together, every rule readable). An independent check of each rule of the note against the branch passed 11 of 11, including the note's own example error, `line 3: unknown type 'colour'`. |
| A model that does not read images (deepseek on OpenRouter, the usual "no") | **Pass.** Told by the brief, it said it could not see the image and had the file only, and answered from the code instead. |
| Limits | **Pass** (as Admin; the rule is the same for Members): an unknown binary refused with the VS Code agent's reason; a sixth file refused, "A message can carry at most 5 files."; five sent. |

Found by the check:

- **The decision scan asked what the files answer.** Given no attachments, it asked what the badge said; how the CSV was laid out, whether the import was an endpoint, and what happens to an existing id (all three in the note), and on the CSV task it assumed "Neither is in the repository". Fixed in `48b9778` (prompt m2.8): the scan's brief carries the files too. Not yet measured on a run.
- **The review judged the CSV work without the note** (review turns get no attachments, as decided in §1), raised "The attached note is not in the brief" as blocking, and cost a repair cycle (17,457). Open: whether review should get document and text attachments, not images.
- **Answers that said "read the attachments" did not settle the scan's remaining questions** ("none is settled yet" twice): the known over-asking re-check (m3 §13), unchanged.
- Two reading copies left owned by Administrators by an earlier elevated host made git refuse them ("dubious ownership"); moved aside to `reading-aside-2026-10-19`, re-cloned by the factory.
- Reflections proposed five lessons (insta L4–L5, forms-api L1–L3), left Suggested for an Admin.

### The spec-first task with attachments (2026-10-19)

forms-export, `claude-haiku-5-5`: "@factory spec Add a fixed-width export for the ledger, as the attached layout document describes", with a Word layout document (the rules, in a table) and the exact expected output for its example. The `.docx` reached the spec turn through MarkItDown, and the specification's 12 criteria matched the document point for point, including byte-for-byte equality with the sample (spec 20,128). Approved, then built: accepted at 165,955 (scan 29,250, implement 87,555, light review 18,015, reflection 11,007), draft PR #3. Independently checked: the document's example equals the attached sample byte for byte, and the answered decisions hold (ids cut to 10, half-away-from-zero rounding, too large or negative numbers are errors naming submission and field, repeating forms refused).

First run on m2.8: the scan, now given the files, asked only real gaps in the document (an id over 10 characters, more than two decimals or too large a number), none the files answered. The re-check still did not take "use your recommendations for the rest" as settling the remaining question (asked one more); the review raised two minor findings on choices already made as decisions, so it does not see a thread's decisions either.

### Decision fixes (2026-10-19, prompt m2.9)

User's decisions: review turns get every attachment, images too (changing §1); an answer that leaves the rest to the agent settles the remaining questions; the reviewer sees the task's decisions.

- **Delegating answers** (`DecisionPolicy.DelegatesTheRest`): an answer such as "use your recommendations for the rest", "your call on the remaining ones" or "do not ask again" settles every question not yet asked with its recommendation, cited as left to the agent, and goes straight to implementing: no re-check turn. The thread says what is assumed. A bare "use your recommendation" still answers only its own question.
- **The review brief lists the task's decisions** (answers and assumptions) as settled: raise a finding only where the change does not do what they say.
- **Review turns are given the attachments**, images included, in their new session; a run reviews once, and a rework's review, a new run, gets them again.
