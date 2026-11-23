# Attachments Improvement 2.0 — token-aware text budget and image downscaling

Status: **design only, nothing built** (2026-11-21).
Scope: the Litos VS Code extension (`Litos.VsCodeHost`) and the Software Factory (Host + Worker).
Gui, Console, Api and Telegram can adopt the shared helper later; they are out of scope here.

Related: [docs/software-factory/attachments-architecture.md](docs/software-factory/attachments-architecture.md) (the factory's existing attachment design), `CLAUDE.md` (token accounting under prompt caching), `ReadMe_SilentContextOverflow500.md`.

---

## 1. Why — what happened on 2026-11-21

Three Litos (VS Code) sessions ran on this machine on 2026-11-21. None of them auto-compacted. One (`f6d7290e…`, the Maths Mastery paywall analysis) was compacted twice by hand with `/compact`. It is the session that shows the problem.

| Session | Provider | Peak context | Compaction |
|---|---|---|---|
| `eb4596da…` (LMS proposal) | Gemini direct | 121k | none (under the 250k trigger) |
| `2cbd4258…` (LMS explainer) | Gemini direct | 188k | none (under the 250k trigger) |
| `f6d7290e…` (Maths Mastery) | Gemini direct → OpenRouter (Relace) → OpenRouter (Ionstream) | **736k** | 2 × manual `/compact`, both "tokensBefore 572,092" |

The first message in `f6d7…` `@`-mentioned two Supabase CSV exports. Together they were about **787k characters, under 1 MB on disk**. Both were inlined into the user message, and the first request was **670,436 real tokens**. The rest of the session sent 682k → 693k → 712k → 736k, well past the 1M-window compaction trigger of 250k, and nothing intervened:

1. **The first request is never checked.** `CompactionPlanner.EstimatedTokensUsed` needs the last reported usage. Before the first call there is none, so `ShouldCompact` returns false and the request goes out unchecked.
2. **Compaction cannot cut message 0.** From the second call on, the trigger was exceeded, but almost all the tokens were in message 0. The planner keeps the last ~75k tokens (`KeepRecent` for a 1M window). Everything after message 0 was less than that, so the only cut point was index 0, and `FindCutPoint` treats that as "nothing old enough to cut" ([Compaction.cs](src/Litos.Agent/Session/Compaction.cs)).
3. **char/4 badly underestimates tabular data.** The planner's estimate for that message was ~197k tokens. The real count was 670k, about **1.2 characters per token**. Digits, commas, UUIDs and timestamps break into many tokens.
4. **The only attachment limit is 20 MB of raw bytes** (`AttachEndpoints.MaxAttachmentBytes`). It cannot catch a 1 MB file that is 670k tokens.

The model then read the same CSVs with kernel code anyway, so the inline copy added cost and context but no information.

The context window was **not** misread. `2cbd…` reached 188k without compacting, which is only possible with a window of at least ~290k. Gemini accepted 736k-token requests, so the model's real window is at least that large.

A separate small bug turned up in the same session. The two `/compact` summaries were written 3 s apart with the same `tokensBefore`, so two `/compact` requests ran concurrently on the same transcript. See §7.

## 2. What exists today

### 2.1 VS Code (`Litos.VsCodeHost`)

- All three attachment routes go through [AttachEndpoints.cs](src/Litos.VsCodeHost/Turns/AttachEndpoints.cs): `@`-mentions (`extension.ts` `resolveMentionsAsync` → `/attachments/from-path`), `/attach` (from-path) and clipboard paste (`/attachments/from-bytes`).
- **Text:** converted to Markdown, wrapped in `EXTERNAL_UNTRUSTED_CONTENT` and inlined in full. **No token check.**
- **Images:** raw bytes, base64, no resizing. Only the 20 MB check.
- **Lifetime:** attachments live in the append-only transcript and are re-sent on every later turn until compaction cuts them.

### 2.2 Software Factory

[RunAttachments.cs](src/Litos.SoftwareFactory.Host/Runs/RunAttachments.cs) and [BriefAttachments.cs](src/Litos.SoftwareFactory.Core/Briefs/BriefAttachments.cs) already do part of this:

- **Copies:** each file is copied to `.factory/attachments/` in the working copy, so the agent always has a path.
- **Caps:** text is capped at `MaxAttachmentChars = 50_000` per file and `MaxAttachmentsChars = 100_000` per brief. Past that the text is cut and the brief says "read the rest from `<path>`".
- **Earlier files:** files sent on earlier turns are listed by path only.
- **Upload limits:** 5 files per message, 50 MB per thread, 20 MB per file (`AttachmentSettings`).
- **Images:** shown only when the thread's model accepts images. The worker reads the raw bytes ([WorkerApp.cs `ReadImageAsync`](src/Litos.SoftwareFactory.Worker/WorkerApp.cs)) **without resizing**.

The factory has the same two gaps as VS Code:

1. **Character caps are not token caps, and ignore the window.**
   - 100k characters of prose is ~25k tokens.
   - 100k characters of CSV is ~85k tokens, already above the compaction trigger of a 128k-window model (83k).
   - Cutting a CSV at 50k characters also gives the model an arbitrary slice of rows instead of a useful sample.
2. **Unresized images.** A 6 MB screenshot passes the 20 MB check and then fails at Anthropic's 5 MB-per-image limit, or adds up towards Gemini's ~20 MB inline-request limit.

## 3. Principles

1. **Limit text by tokens, not bytes, and scale the limit with the active model's context window.**
2. **Text and images are separate problems.** Text uses up context. Images cost little context (providers downscale; ~250–1,600 tokens each) but can break provider *byte* limits, and in VS Code they are re-sent every turn.
3. **Over budget means a reference, not a failure.** The model gets the path, a size note and a useful preview, and reads the rest with its tools (`read_file`, kernel code). Nothing is silently dropped.
4. **Always tell the user** when a file went in as a reference rather than inline.
5. **Overestimate rather than underestimate.** An estimate that is too high costs a little inline content. One that is too low causes the failure in §1.
6. **Never replace a user's original file.** Downscale only what the model is shown.

## 4. Design

### 4.1 Shared helper — `Litos.Tools.Attachments`

Both faces use these. They are new types next to the existing `IAttachmentConverter`, `ImageMedia` and `PlainTextMedia`.

**`TokenEstimate`**: a density-aware estimate of tokens from text.

- Sample the text (the whole text when short; otherwise evenly spaced chunks totalling ~64k characters) and measure the share of digits, punctuation and separators, and of whitespace.
- Map that share to characters per token:
  - prose / Markdown / code: ~4.0
  - mixed (JSON, logs): ~2.5
  - dense tabular / numeric (CSV/TSV with IDs, timestamps, numbers): ~1.5
- Calibrate the bands with a unit test against real token counts. The `f6d7…` CSVs (≈1.2 chars/token) are a good fixture; anonymise them first or generate a synthetic equivalent.
- This is for budgeting only. `CompactionPlanner`'s char/4 estimate of trailing messages is left alone (see §8).

**`AttachmentBudget`**: how many tokens of text one turn may inline.

```
budget = floor(BudgetFractionOfTrigger × CompactionSettings.ForContextWindow(window).TriggerAtTokens)
BudgetFractionOfTrigger = 0.25   (proposed; see §9 Q1)
```

| Context window | Trigger | Text budget per turn |
|---|---|---|
| 32k (local fallback) | 24k | 6k |
| 128k | 83k | ~20k |
| 200k | 130k | ~32k |
| 1M | 250k | ~62k |

Keeping attached text well under the trigger means compaction always has older messages to cut once the conversation moves on. That fixes the "message 0 can never be cut" trap from §1.

**`AttachmentFitter`**: applies the budget across a turn's text attachments, in the order given.

- A file that fits in what's left of the budget goes inline in full.
- A file that doesn't fit becomes a **reference**:
  - **CSV / TSV:** header row, first 20 data rows, total row count, column count, file size and estimated tokens.
  - **Other text / converted documents:** the start of the text, up to `min(2_000 tokens, remaining budget)`, plus total size and estimated tokens.
  - Then one fixed line: *"Too large to include in full (~N tokens). The full file is at `<path>`; read or query it with your tools instead of asking for it to be pasted."*
- The output says, for each file: inline or reference, estimated tokens, and the preview text. Each face renders that in its own wording.

**`ImageDownscaler`**: makes an image provider-safe.

- If the longest side is above **1,568 px**, or the encoded size is above **3.75 MB**, resize so the longest side is at most 1,568 px and re-encode.
  - 1,568 px is Anthropic's recommended maximum, what Anthropic resizes to anyway, and well within Gemini/OpenAI tiling. 3.75 MB leaves headroom under Anthropic's 5 MB once base64 adds ~33%.
- **Output format:** PNG stays PNG when that keeps it under the size limit (screenshots and diagrams with sharp text); otherwise JPEG at quality 85. GIF and WebP become PNG or JPEG under the same rule.
- Images that are already small enough pass through byte-for-byte.
- **Library:** SkiaSharp (`SkiaSharp` + `SkiaSharp.NativeAssets.*` for win-x64, and linux-x64 for the factory installers). No image library is referenced in `src` today. Check the single-file publish of `Litos.VsCodeHost.exe` and the factory MSI include the native asset (see §8).
- **Per-turn count cap:** at most **10 images per turn** (VS Code). The factory already caps at 5 files per message.

### 4.2 VS Code (`Litos.VsCodeHost`)

**Text.** In `/attachments/from-path`:

1. Convert the file as today.
2. Estimate tokens with `TokenEstimate`.
3. Return the estimate with the `AttachedContent`, plus the file's absolute path.

The budget is applied **once per turn at send time**, not per file at attach time, because several mentions share one budget. That happens in `AttachEndpoints.ReadTurnRequestAsync` or the turn endpoint, using `worker.ContextLength`:

1. `AttachmentFitter` decides inline vs. reference for the turn's document attachments.
2. Inline files are sent as today. Referenced files become a `TextBlock` holding the preview and the path.
3. **Converted documents** (docx, pdf, xlsx…) have no readable text on disk, so the converted Markdown is saved to the session's `scratch/` folder (`~/.litos/sessions/local/<id>/scratch/attachments/<name>.md`) and that path is used.
4. **Clipboard paste of text** isn't a case today (from-bytes takes images only), so nothing is needed there.

**User notice.** The host returns which attachments became references. `extension.ts` posts a system message such as *"app_events_rows….csv is ~560k tokens, so it was attached as a reference with a 20-row preview."*

**Images.** Both `/attachments/from-path` and `/attachments/from-bytes` pass the bytes through `ImageDownscaler` before returning the `AttachedContent`. What gets written to the transcript is the downscaled copy; the user's file on disk is untouched. Over 10 images in one turn → reject with a clear message, as the 20 MB check does today.

**The 20 MB raw-bytes check stays** as a sanity limit on input size.

### 4.3 Software Factory

**Text.** In `RunAttachments.PrepareAsync` / `BriefComposer.FitAttachmentText`:

- Replace the character caps (`MaxAttachmentChars`, `MaxAttachmentsChars`) with `AttachmentFitter` and the token budget of **the thread's model window**. The Host already resolves the thread's provider/model; the window comes from the same `ModelContextResolver` path the worker uses.
- Replace "cut at N characters" with the reference preview. A referenced file still has its copy in `.factory/attachments/`, so the path in the preview is the working-copy path the brief already lists.
- The `<attachment …>` fence, the "attached earlier" listing and the untrusted-content handling stay as they are.
- **Thread note:** `RunAttachments.Describe` also says which files went in as references, e.g. *"…2 attached files: a.csv (reference, too large to include), b.md."*

**Images.** Downscale **only the copy sent to the model**, in the worker's `ReadImageAsync` (or in the Host just before `TurnImage` is built — see §9 Q3). The original stays in `.factory/attachments/` unchanged, because in the factory an image is often an asset the agent should use in the app (a logo, a mockup, an icon), and a downscaled JPEG of a logo would be wrong.

**Settings.** No new Admin setting in this version. The budget fraction is a constant, like the compaction constants. See §9 Q4.

## 5. What this does not change

- **Compaction logic** (`CompactionPlanner`, `CompactionSettings`) is unchanged, so nothing needs mirroring in `liveContextUsage.ts` (see `CLAUDE.md`).
- **Tool results** (e.g. a huge `read_file`) are not covered. That's a separate problem with its own limits.
- **No new behaviour for attachments already in a transcript.** Old sessions keep what they have.
- **Gui, Console, Api, Telegram** keep their current attachment code until they adopt the helper.

## 6. Build steps

Each step comes with its unit tests. Factory steps follow the "comprehensive unit tests for every factory change" rule.

1. **Shared helper** — `TokenEstimate`, `AttachmentBudget`, `AttachmentFitter` in `Litos.Tools.Attachments`. Tests:
   - density bands (prose, JSON, CSV, a synthetic Supabase-like CSV ≈ 1.2 chars/token);
   - budget at 32k / 128k / 200k / 1M windows;
   - fitter ordering, the inline/reference split, CSV preview (header + 20 rows, quoted fields with embedded newlines, empty file, header only), and the non-CSV preview cut at a line boundary.
2. **VsCodeHost text budget** — estimate in `/attachments/from-path`, apply the budget per turn at send time, save converted documents to `scratch/`, reference blocks, system-message notice in `extension.ts`. Tests:
   - endpoint tests for over/under budget and multiple mentions sharing one budget;
   - `extension.ts` notice;
   - a replay of the `f6d7…` shape: two large CSVs on a 1M window → both become references and the first request is ≪ 250k tokens.
3. **Factory text budget** — swap the character caps for the fitter in `RunAttachments`/`BriefComposer`; update `Describe`. Tests:
   - brief rendering for inline / reference / attached earlier / problem files;
   - the budget follows the thread's model window;
   - existing `BriefAttachments` tests updated, not deleted.
4. **Image downscaler** — `ImageDownscaler` with SkiaSharp, wired into both VsCodeHost endpoints and the factory worker. Tests:
   - large PNG screenshot → PNG ≤ 1,568 px; large photo → JPEG; a small image passes through byte-for-byte; output under 3.75 MB; EXIF orientation respected;
   - factory: the original in `.factory/attachments/` is unchanged and the model-bound copy is downscaled;
   - VsCodeHost: the per-turn image count cap.
   - The Anthropic image test runs on `claude-haiku-5-5`, as for other factory image tests.
5. **Packaging and live check** — confirm the SkiaSharp native asset ships in the `Litos.VsCodeHost.exe` single-file publish and in the factory MSI. Redeploy the host by hand (see the redeploy notes). Live check: re-run the `f6d7…` prompt with the two CSVs and confirm the first request size and the system notice. Rebuild the factory MSI only if asked.

## 7. Side fix — concurrent `/compact`

Two `/compact` requests on the same session ran concurrently in `f6d7…`. Both loaded the same transcript and both summarised ~570k tokens, so one full summarisation call was wasted. The later summary won. Add a per-session guard in the `/sessions/{id}/compact` endpoint ([AgentSettingsEndpoints.cs](src/Litos.VsCodeHost/Turns/AgentSettingsEndpoints.cs)): a second request while one is running returns "already compacting". Optionally the webview disables the command while a compaction is in flight. This is small and independent; it can ship with step 2 or alone.

## 8. Risks and notes

- **Estimator accuracy.** Density bands are a heuristic. They're tuned to overestimate, but an unusual file (base64 blobs, minified JS) can still land in the wrong band. A per-provider real tokenizer would be exact but is a much larger change; not planned.
- **`CompactionPlanner` keeps char/4.** Its estimate of trailing messages still underestimates tabular text. With attachments bounded that matters much less, but it's a known gap. Moving it to `TokenEstimate` would change the context meter and so needs the TypeScript mirror (`liveContextUsage.ts`) updated too; left for later.
- **First-request check.** This design prevents the §1 failure for *attachments*. A huge first message typed or pasted as text is still unchecked. A pre-flight check (estimate the first request; warn above the trigger) is a possible follow-up.
- **SkiaSharp in single-file publish.** Native libraries need `IncludeNativeLibrariesForSelfExtract` (or equivalent) in the single-file exe. Verify on a clean machine, not just the dev box.
- **Model does not read images** (factory). Unchanged: the brief already says so and the files are still copied.

## 9. Open questions for the user

1. **Budget fraction:** 25% of the compaction trigger (≈62k tokens on 1M, ≈32k on 200k, ≈20k on 128k)? Higher lets more files inline but leaves less room before compaction is needed.
2. **CSV preview size:** header + 20 rows enough, or more (e.g. 50) so the model sees more value variety before it queries the file?
3. **Factory image downscale location:** in the worker's `ReadImageAsync` (simplest, one place) or in the Host when building `TurnImage` (keeps the worker dumb)? Recommendation: the worker.
4. **Admin setting:** keep the budget fraction a constant (recommended for V1), or expose it on the factory's Attachments settings page?
5. **Image count cap in VS Code:** 10 per turn OK?
