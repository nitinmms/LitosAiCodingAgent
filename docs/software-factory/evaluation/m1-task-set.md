# Software Factory M1 evaluation task set

The M1 exit gate in [ReadMe_LitosSoftwareFactory_V1.md](../../../ReadMe_LitosSoftwareFactory_V1.md) §18 needs about 12 real tasks with acceptance criteria written **before** any run. This file is that set. It stays fixed so results from different prompt revisions and milestones can be compared; add tasks at the end rather than editing existing ones.

## Repositories and baselines

Every task starts from the same baseline commit on a fresh `factory/*` branch, independently of the other tasks.

| Repo | Stack | Baseline | Verification profile |
| --- | --- | --- | --- |
| [nitinmms/filedb-sharp](https://github.com/nitinmms/filedb-sharp) | .NET 10 library, xUnit | `07fb8b8` | .NET preset: `dotnet build`, `dotnet test --logger trx --collect "XPlat Code Coverage"` |
| [nitinmms/insta-story-generator](https://github.com/nitinmms/insta-story-generator) | React 19 + Vite + Express, Vitest | `324ec0e` | Node/React preset: `npm ci`, `npm run typecheck`, `npm run test:ci` (JUnit + Cobertura) |

## How a task is run and judged

1. **Start.** Create a thread in the task's project. Set the budget from the task's size, then send the **Request** text exactly as written with `@factory`. Don't discuss the task first, so the request is the whole specification.
2. **Decisions.** When the factory asks a question, answer with the task's **scripted answer**. If it asks something not listed, answer briefly and record the question. If an expected decision never comes, record that; the task can still pass.
3. **Judging.** A reviewer checks every acceptance criterion against the handoff branch: read the code and tests, run the build and tests, and do the manual check where listed.
   - **Accepted:** every criterion is met.
   - **One rework round is allowed:** send one `@factory` message listing the unmet criteria, then judge again.
   - **Failed:** criteria are still unmet after one rework, or the run ended `Blocked` or budget-paused.
4. **Always true, for every task:**
   - existing tests keep passing, and none are deleted or weakened to make the run pass;
   - the build has no new warnings (filedb-sharp treats warnings as errors);
   - public APIs are documented in the repo's existing style, and the README is updated when behaviour visible to users changes;
   - changed-line coverage meets the project threshold (80%);
   - nothing is committed to `main`, and the handoff is a `factory/*` branch.

**Budgets:** small 300,000 tokens · medium 600,000 · large 1,200,000. From prompt revision `m1.7`, each change request adds half the original cap (the factory does it, and the thread says so); a budget pause against the raised cap still counts as failed.

These were recalibrated on 2026-10-02 after F1 and F2, as the original note allowed ("initial caps, to be calibrated after the first full run"). The initial caps were 150,000 / 300,000 / 600,000, written before any run; F1 needed about 203,000 and F2 575,884 including its rework. F1 and F2 stay recorded against the initial caps. Tokens are counted as §9.1 of the blueprint defines them, with cached input at 10%.

**Recorded per run:**

- accepted (yes/no);
- rework rounds and repair cycles;
- decisions asked, and whether each was warranted;
- tokens used;
- estimator error (§9.5);
- wall-clock time;
- evidence mismatches between the handoff and the host's records;
- prompt revision.

**M1 gate:**

- at least 7 of 12 accepted with at most one rework;
- zero evidence mismatches;
- no budget overrun;
- estimator 95th-percentile under-estimate within the margin;
- no lock violations.

## Summary

| ID | Repo | Task | Size | Expected decision |
| --- | --- | --- | --- | --- |
| F1 | filedb-sharp | Enforce size limits on keys, names and documents | S | — |
| F2 | filedb-sharp | JSON Lines export and import | M | — |
| F3 | filedb-sharp | Automatic compaction | M | — |
| F4 | filedb-sharp | Read-only open mode | M | — |
| F5 | filedb-sharp | Async API with cancellation | M | — |
| F6 | filedb-sharp | Document expiry (TTL) | L | Yes: file format upgrade |
| F7 | filedb-sharp | Optimistic concurrency with document versions | L | Possibly: format shared with F6 |
| F8 | filedb-sharp | Secondary indexes | L | — |
| R1 | insta-story-generator | Per-slide text alignment and size | S | — |
| R2 | insta-story-generator | Focal point for photo cropping | M | — |
| R3 | insta-story-generator | Cancel a running generation | M | — |
| R4 | insta-story-generator | AI image generation for slides without a photo | L | Yes: when images are generated |

The set mixes sizes, includes two tasks where a good factory must stop and ask, and covers four kinds of change:

- **on-disk format changes:** F6, F7;
- **concurrency:** F4, F5, R3;
- **work across client and server:** R3, R4;
- **pure UI:** R1, R2.

---

## filedb-sharp

### F1 · Enforce size limits on keys, names and documents (S)

**Request**
> @factory Keys, collection names and documents currently have no size limits, so one oversized value can create a huge log frame. Add limits: collection names at most 128 UTF-8 bytes, keys at most 1,024 UTF-8 bytes, serialized documents at most 16 MiB. Exceeding a limit must throw a clear exception before anything is written.

**Acceptance criteria**
1. A write whose collection name, key or serialized document exceeds its limit throws `ArgumentException` (or a documented subclass). The message names the limit and the actual size.
2. Limits are measured in UTF-8 bytes, not characters. A test uses multi-byte characters at exactly the limit (accepted) and one byte over (rejected).
3. A transaction containing one oversized operation is rejected when that operation is staged. Committing the transaction writes nothing.
4. The file on disk is unchanged after a rejected write (same length).
5. The limits are public constants and are listed in the README's "Current limits" section.

### F2 · JSON Lines export and import (M)

**Request**
> @factory Add export and import in JSON Lines so a database can be backed up or moved. Export writes one line per document with its collection, key and document JSON. Import reads that format back. Import must be all-or-nothing: if any line is invalid, nothing is imported and the error says which line was wrong.

**Acceptance criteria**
1. `ExportJsonLines(Stream)` writes one line per document: a JSON object with `collection`, `key` and `document`. Collections appear in ordinal order, with keys in ordinal order inside each collection.
2. `ImportJsonLines(Stream)` imports every line as one atomic commit. A reopened database shows either all documents or none.
3. An invalid line (malformed JSON, missing field, or empty key) causes an exception naming the 1-based line number. The database is unchanged.
4. Importing a key that already exists overwrites it (upsert semantics). This is documented.
5. Round trip: export, then import into an empty database, gives identical collections, keys and document JSON. Tested with Unicode keys and nested documents.
6. Blank lines are ignored.
7. README documents the format with an example line.

### F3 · Automatic compaction (M)

**Request**
> @factory Add optional automatic compaction. When reclaimable space passes a configurable share of the file, the database should compact itself after a commit. It must be off by default, and it must never compact small files.

**Acceptance criteria**
1. There is a new option on `FileDatabaseOptions`: either a nullable threshold ratio (such as `AutoCompactRatio`, reclaimable bytes ÷ file bytes) or an equivalent documented pair of settings. It's off by default.
2. There is also a minimum file size (default 1 MiB, configurable). Below it, automatic compaction never runs.
3. When both conditions hold after a commit, the database compacts once. A test shows the file shrinking after enough overwrites, and data intact after reopening.
4. A failure during automatic compaction doesn't fail the commit that triggered it. The committed data remains, and the failure is observable: a documented event or property, or a later explicit `Compact()` that surfaces the error.
5. With the option off, behaviour and file size match the baseline for the same writes. Test included.

### F4 · Read-only open mode (M)

**Request**
> @factory Add a read-only open mode so several processes or instances can read a database at the same time. Read-only instances must never change the file. A writer and readers must not have the file open at the same time.

**Acceptance criteria**
1. A new option (for example `FileDatabaseOptions.ReadOnly`) opens the database read-only.
2. Two read-only instances of the same file can be open together. A test proves this in one process.
3. Opening read-write while a read-only instance is open, or read-only while a writer is open, throws `DatabaseLockedException`.
4. Every write on a read-only instance throws `InvalidOperationException` with a clear message: collection writes, transactions, `DropCollection` and `Compact`.
5. A read-only open never modifies the file. A torn tail is ignored in memory, not truncated, and the file length and bytes are unchanged afterwards. `RecoveredTailBytes` still reports what was skipped.
6. A read-only open of a missing file throws a clear exception instead of creating the file.
7. README explains the mode and its locking rules.

### F5 · Async API with cancellation (M)

**Request**
> @factory Add async versions of the collection and transaction operations that do I/O, with cancellation support, for use in ASP.NET Core apps. Keep the existing synchronous API unchanged.

**Acceptance criteria**
1. `Collection<T>` gains async counterparts: at least `UpsertAsync`, `InsertAsync`, `UpdateAsync`, `DeleteAsync`, `GetAsync` and `TryGetAsync`, or an equivalent documented pattern. `Transaction` gains `CommitAsync`. Each takes a `CancellationToken`.
2. Async reads and writes use asynchronous file I/O. They don't wrap the synchronous methods in `Task.Run`.
3. A token that is already cancelled throws `OperationCanceledException` and writes nothing. A test shows the file unchanged.
4. The async methods have the same semantics and exceptions as the synchronous ones, shown by tests for duplicate keys, missing documents and commit-time conditions.
5. Mixing sync and async calls concurrently on one instance stays correct: a concurrency test with both kinds of writers checks the final count.
6. The synchronous API is unchanged: all existing tests pass unmodified.

### F6 · Document expiry (TTL) (L) — expects a decision

**Request**
> @factory Add document expiry. A document can be written with a time-to-live; after it expires it must behave as if it had been deleted: invisible to reads, counts, keys and queries. Expiry must survive reopening the database, and compaction should drop expired documents.

**Expected decision:** persisting expiry times requires changing the on-disk format. The factory should ask how to handle existing version-1 files.

**Scripted answer:**
> Keep reading version-1 files. Write the new format (version 2) only to files created after this change or when a file is compacted; an existing v1 file stays v1 until compacted. Document this in the README's format section.

**Acceptance criteria**
1. There is an overload such as `Upsert(key, document, TimeSpan timeToLive)` (plus `Insert` if natural). A non-positive TTL throws `ArgumentOutOfRangeException`.
2. Once expired, a document is invisible to `Get`/`TryGet`/`Exists`/`Count`/`Keys`/`All`/`Find`. `Insert` of the same key succeeds.
3. Time is injectable. Tests use a fake clock, never real sleeps.
4. Expiry survives reopening. `Compact()` drops expired documents, and `GetStats` doesn't count them as live.
5. Existing version-1 files still open and read correctly, with a test using a v1 file written by the baseline code or an equivalent fixture. File-format behaviour follows the scripted answer.
6. The README format section documents version 2 and the compatibility rule.

### F7 · Optimistic concurrency with document versions (L)

**Request**
> @factory Add optimistic concurrency. Every document should carry a version number that increases on each write. Reads can return the version, and updates can require an expected version, failing with a specific exception if the document changed in the meantime. Versions must survive reopening and compaction.

**Expected decision:** possibly how versions are stored, since the format changes. If asked, the scripted answer is:
> Use the same versioned format approach as any other format change: keep reading v1 files, where every existing document starts at version 1.

**Acceptance criteria**
1. Reads can return the version. Use the most natural fitting API, for example `TryGet(key, out doc, out long version)` or a `Versioned<T>` result.
2. The version is 1 on first write and increases by exactly 1 on every successful write of that key. A delete followed by a re-insert starts again at 1, and this is documented.
3. `Update(key, doc, expectedVersion)` (or an equivalent) throws a new `ConcurrencyConflictException` when the version differs. In a transaction, the check happens at commit time and nothing is written.
4. Versions survive reopening and `Compact()` unchanged.
5. A concurrency test with two writers racing on the same key and expected version shows exactly one succeeding.
6. v1 files open with every document at version 1.

### F8 · Secondary indexes (L)

**Request**
> @factory Find currently scans and deserializes every document in a collection. Add secondary indexes: let a collection declare an index on a document property, and add a lookup that uses the index instead of scanning. Indexes can be kept in memory and rebuilt when the database opens.

**Acceptance criteria**
1. There is an API to declare an index on a property: for example `EnsureIndex(p => p.City)` on `Collection<T>`, or an equivalent documented form.
2. A lookup by indexed value (for example `FindBy(p => p.City, "Oslo")`) returns the same documents as the equivalent `Find` predicate, in key order.
3. The index is maintained by upserts, updates, deletes, transactions (only on commit), `DropCollection` and `Compact`. There is a test for each.
4. A test proves the lookup doesn't deserialize non-matching documents. For example, count deserializations with an injected serializer option or converter, or use another observable signal.
5. Indexes are rebuilt when the database opens; nothing new is written to the file format.
6. Declaring an index on an existing collection with data indexes the current documents.

---

## insta-story-generator

### R1 · Per-slide text alignment and size (S)

**Request**
> @factory Let users choose, per slide, whether the text is left-aligned, centred or right-aligned, and choose a text size of small, medium or large. The preview and the exported PNGs must match, and text must still stay inside Instagram's safe area.

**Acceptance criteria**
1. The slide model gains alignment (`left`/`center`/`right`) and size (`small`/`medium`/`large`), with defaults of left and medium. Drafts saved before the change still load, using the defaults.
2. The slide editor has labelled controls for both. Changes update the preview immediately.
3. The renderer uses the choices: `textAlign` and x-position for alignment, and scaled headline and body sizes. Tests use the recording context.
4. At every size and layout, text stays inside `SAFE_TOP`..`SAFE_BOTTOM`, including the longest allowed text. Test included.
5. AI-generated slides get the defaults; the JSON schema sent to providers doesn't change.

### R2 · Focal point for photo cropping (M)

**Request**
> @factory Photos are always cropped around their centre, which cuts off subjects near the edges. Let users set a focal point per slide by clicking on the preview, and use it when cropping the photo, both in the preview and in exported slides.

**Acceptance criteria**
1. Each slide stores a focal point `{x, y}` in 0–1, defaulting to the centre. Old drafts load with the default.
2. Clicking the preview sets the focal point for slides with a photo. It's also operable by keyboard: arrow keys move it in small steps when the preview is focused, and the control has an accessible name.
3. The focal point is passed to `coverRect` in both preview and export, with a test that checks the source rectangle.
4. A visible marker shows the focal point while the preview is focused or hovered, and never appears in exported PNGs.
5. Slides without a photo ignore the focal point, and the control is disabled or hidden for them.

### R3 · Cancel a running generation (M)

**Request**
> @factory Writing a story or rewriting a slide can take a while and cannot be stopped. Add a Cancel button while the AI is working. Cancelling must stop the request to the AI provider on the server, not just ignore the result in the browser.

**Acceptance criteria**
1. While a story is being written or a slide rewritten, a Cancel button is shown. It isn't shown otherwise.
2. Cancelling aborts the browser request (`AbortController`). The status reads "Cancelled", and the story and brief are unchanged.
3. The server notices the client disconnecting and aborts the provider call. Both SDK adapters pass an abort signal, with a test using a fake client that observes the signal.
4. A cancelled request never replaces slides, even if a response arrives late (the race is tested).
5. After cancelling, a new generation works normally.

### R4 · AI image generation for slides without a photo (L) — expects a decision

**Request**
> @factory Slides without a photo only get a gradient background. Use AI image generation to create a background image for those slides from the slide's imagePrompt. Use OpenAI's image API when OPENAI_API_KEY is set; if it isn't, the feature should not appear.

**Expected decision:** whether images are generated automatically for every slide without a photo, which could be expensive, or only on request.

**Scripted answer:**
> Only on request: a "Generate background" button per slide. Never generate automatically.

**Acceptance criteria**
1. There is a new server endpoint that generates a vertical image from a prompt using the `openai` SDK images API (with the model name configurable). It returns base64 data. The prompt asks for no text in the image.
2. `/api/config` reports whether image generation is available. The button appears only when it is.
3. A per-slide "Generate background" button (per the scripted answer) shows progress. On success it stores the image with the slide and uses it as the background. It's available only on slides without a photo, or it explicitly replaces the gradient.
4. Generated images are drawn with the same crop, scrim and white text as photos, and are included in exported PNGs.
5. Errors (refusal, rate limit, missing key) show a clear message, and the slide is left unchanged. The server adapter is tested with a fake client for success, refusal and error.
6. Drafts keep generated images when storage allows. When storage is full, the existing "saved without photos" behaviour also covers generated images.
7. README documents the feature and the key it needs.
