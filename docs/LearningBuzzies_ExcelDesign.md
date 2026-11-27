# Learning Buzzies (Excel MVP): implementation design

> Design for ReadMeLearningBuzziesSimpleMVP.md, stages 1–4 (Excel). Status: proposed, 2026-11-27, awaiting approval.
> Decisions: the learning call uses the taught Buzzy's model; candidate rules are inferred locally and the model only chooses among them; one plain table per sheet; scheduled runs use no model; routine input = watched folder + pattern + only-unprocessed (ask when ambiguous); example files are not retained.
>
> **Licence pin:** ClosedXML 0.105.1 accepts SixLabors.Fonts [1.0, 3.0). 1.x is Apache-2.0; 2.x is the Six Labors Split License (commercial terms). Pin `SixLabors.Fonts` 1.0.1 explicitly in Litos.Office.

I read every file you listed except the parts noted under "Not verified". Estimate: about 11 dev days plus 1.5 days of buffer and live testing.

## 0. Findings that shape the design

- **WorkflowStore versions.**
  - `WorkflowStore.LatestVersion` counts only *files* matching `versions/v*.md` (`EnumerateFiles`). A per-version sidecar *directory* `versions/v3/` is invisible to it. So learned artefacts can sit next to `vN.md` and routine pinning (`Routine.SkillName/SkillVersion`, `WorkflowStore.Read`) keeps working unchanged.
  - `Save` computes `Latest+1` itself and writes `vN.md` and `SKILL.md`. The sidecar must therefore be written first, under the same N, and `Save` last.
- **`BuzzyRunExecutor` setup.** It awaits `environment(ct)` first. It then fails on an empty model *before* it reads the workflow. The learned branch must go after `env` is available (it needs `env.Tools.Store`) but before the model check. A learned run needs no model.
- **Routine plumbing.** `RoutineScheduler.ExecuteAsync` only calls `IBuzzyRunExecutor.RunAsync` and records `RunOutcome`. `Finish` accepts any `RunStatus`, so a deterministic run needs no scheduler changes.
- **`BuzzyDb` migration.** The migration is `if (version < N)` blocks in one transaction, with `SaveRoutine` and `QueryRoutines` both listing the columns. v3 follows the same pattern.
- **Globalization.** `Litos.Gui` publishes with `InvariantGlobalization=true`. All parsing and formatting must use `CultureInfo.InvariantCulture` or explicit formats. Never rely on the ambient culture.
- **Wizard layout.** Per CLAUDE.md, put `Margin` on the `ScrollViewer`'s direct child. Never use `ScrollViewer.Padding`.
- **Fixed decisions.** These are changes versus the spec's §8 diagram or additions to it:
  - v1 supports one source workbook, one sheet and one example workbook.
  - The executable artefacts live per version (`versions/vN/`), not at the skill root.

## 1. Project layout

### New `src/Litos.Office/Litos.Office.csproj`
- net10.0, ImplicitUsings, Nullable.
- Dependencies: `ClosedXML 0.105.1` and an explicit `DocumentFormat.OpenXml 3.4.1`. The explicit reference avoids an NU1605 downgrade when Gui unifies versions.
- **No** reference to Litos.Agent, Litos.Tools or Avalonia. It is pure and fully testable.
- `InternalsVisibleTo Litos.Office.Tests`.
- Add it to `LitosAiAgent.slnx` under `/src/`. Add `Litos.Gui.csproj` `<ProjectReference ..\Litos.Office>` alongside the Mail one.

### New `tests/Litos.Office.Tests/Litos.Office.Tests.csproj`
- Copy the Mail.Tests csproj: xunit 2.9.3, Test.Sdk 17.14.1, runner 3.1.4, coverlet 6.0.4, `Using Include="Xunit"`.
- Reference `src/Litos.Office`. Add it to the slnx under `/tests/`.

### Step-0 checks, before any code
- Run `dotnet list package --include-transitive`. Confirm ClosedXML's transitive font and graphics dependency (SixLabors or Skia) and its licence terms.
- Never call `AdjustToContents` or any font-measuring API. Column widths are copied from the template only. This keeps Windows and macOS output identical and avoids font-lookup failures in a self-contained single-file publish.
- Write one smoke test that creates, saves and reads an xlsx on net10.0.

### In `Litos.Gui`
- A new folder `Buzzies/Learning/`.
- `Buzzies/Routines/` additions.
- One window and one `MainWindow` partial.
- Details in §3.

## 2. `Litos.Office` – files, types and signatures

Namespaces are `Litos.Office.*`.

### Data/
- **`CellValue.cs`**
  - `enum CellKind { Empty, Text, Number, Date, Bool }`.
  - `readonly record struct CellValue(CellKind Kind, string? Text, decimal? Number, DateTime? Date, bool? Bool)`, with `Parse`-free factories and `ToDisplay()`.
  - Numbers are `decimal`, converted from Excel's double via `Convert.ToDecimal(double)` (15 significant digits).
  - Text is never auto-parsed into number or date. A text cell that looks numeric is reported as "number stored as text" and is never silently coerced.
- **`Table.cs`**
  - `sealed record Column(string Name, ColumnType Type)`.
  - `sealed class Table(IReadOnlyList<Column> Columns, IReadOnlyList<CellValue[]> Rows)`.
  - Helpers: `IndexOf(name)` (case-sensitive exact, plus a case-insensitive fallback only for inference) and `Select(rowFilter)`.
- **`Limits.cs`**
  - `record OfficeLimits(long MaxFileBytes = 25MB, long MaxUncompressedBytes = 200MB, int MaxZipEntries = 2000, int MaxCompressionRatio = 100, int MaxRows = 200_000, int MaxColumns = 100, int MaxCellChars = 32_767)`.
  - Tests pass tiny limits.

### Excel/
- **`WorkbookGate.cs`** – `static GateResult Check(ReadOnlySpan<byte>/Stream, string fileName, OfficeLimits)`. It runs *before* ClosedXML sees the bytes. It uses `ZipArchive` only and never extracts to disk. It rejects:
  - extension other than `.xlsx` (so `.xlsm` and `.xls` are out);
  - OLE magic `D0CF11E0` (encrypted or legacy);
  - an `EncryptedPackage` entry;
  - `[Content_Types].xml` containing `macroEnabled`, or an entry `xl/vbaProject.bin`;
  - any `xl/externalLinks/*` part;
  - any `.rels` relationship with `TargetMode="External"` whose type is not hyperlink;
  - `xl/embeddings/*`, `xl/activeX/*`, `xl/ctrlProps/*`;
  - too many entries; any entry with ratio above the limit; total uncompressed size above the limit, checked against the declared sizes *and* while counting bytes read.
  - It returns `GateResult(bool Ok, IReadOnlyList<Finding>)`. `Finding(Severity, Code, Message)` carries plain-language messages (e.g. "This workbook contains a macro; Litos only reads .xlsx files without macros").
- **`ExcelReader.cs`**
  - `static LoadedSheet Read(byte[] bytes, string sheetHint, OfficeLimits, CancellationToken)`, loaded from a `MemoryStream`.
  - Rules:
    - Sheet choice is `sheetHint` by exact name. If the hint is null, take the only non-empty sheet; if more than one sheet has data, return an error asking which.
    - The header row is the first non-empty row; header cells must be non-empty and unique.
    - Reject merged ranges that intersect the used range; reject any multi-row header (a second row with all-text cells directly under the header and sparse numerics is *not* detected, so say so in the docs).
    - A formula cell uses `CachedValue`. If the cached value is missing, report an "open and save in Excel" finding. Never recalculate.
    - Rows beyond the limit are an error, not a truncation.
    - Cancellation is checked every 1000 rows.
  - `LoadedSheet(Table Table, SheetStyle Style, IReadOnlyList<Finding> Findings, string Sha256)` where `Sha256` is the hash of the bytes parsed.
- **`SheetStyle.cs`** – a plain record: header row index, header-cell style snapshot, per-column data-row style snapshot (number format, bold, fill, font colour, alignment, border flags), widths, whether a total row was detected, its styles, freeze-pane row, autofilter flag, sheet name. These are the facts the template extractor and the inspector's `Formatting` summary use.
- **`ExcelInspector.cs`** – `static SheetSummary Summarize(Table, SheetStyle, SummaryOptions)`.
  - Per column: name, type, distinct count, null count, top-8 distinct values (each truncated to 40 chars), min and max for numbers and dates.
  - Plus the row count and at most 5 sample rows.
  - Every string goes through `UntrustedText.Sanitize`. That strips control characters, collapses newlines, truncates, and defuses the `<<<` marker.
- **`TemplateExtractor.cs`** – `static byte[] Extract(byte[] exampleBytes, string sheet, SheetStyle style)`.
  - Build the template in a new workbook with one sheet. The header row keeps its values and styles.
  - Row `h+1` is a *prototype data row*: empty cells carrying the data-row styles and number formats.
  - Row `h+2` is a *prototype total row* when the example had one, otherwise absent.
  - Widths, freeze pane and autofilter are kept.
  - No example data survives: assert that no cell below the header holds a value.
  - It returns bytes held in memory until Save.
- **`ExcelWriter.cs`** – `static void Write(string path, byte[] templateBytes, TemplateLayout, Table output, IReadOnlyList<ColumnMap> columns, bool grandTotal, string grandTotalLabel, CancellationToken)`. It:
  - opens the template, sets the header names (the output names, from `columns[].As`) and writes data from the header row + 1;
  - copies the prototype-row style to every data cell;
  - writes the total row with the total-prototype styles. If there is none, it copies the data styles and sets bold;
  - writes **values only**: numbers as decimal, no formulas, no recalculation. The "formulas in the example become values" assumption is listed in the review screen;
  - sets text with `new XLCellValue(string)` and, for any string starting with `= + - @ \t \r`, also `Style.IncludeQuotePrefix = true`. It never assigns to `FormulaA1`.

### Plan/
- **`WorkflowPlan.cs`** – typed records (§4).
  - `[JsonPolymorphic(TypeDiscriminatorPropertyName="op", UnknownDerivedTypeHandling=FailSerialization, IgnoreUnrecognizedTypeDiscriminators=false)]` on abstract `StepSpec`.
  - Derived: `FilterStep`, `GroupAggregateStep`, `SortStep`, `DerivedColumnStep`, `WriteWorkbookStep`.
  - `AllowOutOfOrderMetadataProperties=true`, camelCase, `JsonStringEnumConverter`, `UnmappedMemberHandling=Disallow`.
  - Decimals are written as JSON *strings* in `value` (`"120.50"`) so no float round-trip occurs.
- **`PlanJson.cs`** – `Serialize(plan)` (canonical, indented, stable ordering) and `TryParse(string, out plan, out errors)`. A `JsonException` becomes a readable error, e.g. "unknown operation 'shell' at steps[2]".
- **`PlanValidator.cs`** – `static IReadOnlyList<PlanIssue> Validate(WorkflowPlan, SchemaContext?)`. Rules are in §4. It is used at three moments: after assembly, on load for a scheduled run, and in tests.

### Exec/
- **`PlanExecutor.cs`** – `static ExecResult Execute(WorkflowPlan, Table source, CancellationToken)`.
  - `ExecResult(Table Output, RowAccounting Accounting, IReadOnlyList<PlanIssue> Issues)`.
  - `RowAccounting(int SourceRows, int Included, int Excluded, IReadOnlyDictionary<string,decimal> IncludedSums)`.
  - Semantics:
    - `filter` operates on typed cells. `equals`/`in` compare text ordinally and case-sensitively, trimming ends only. Number operators need a Number cell; a non-number cell is an issue (policy fail). Dates compare as `DateTime.Date` against an ISO `yyyy-MM-dd`.
    - A blank cell never matches `equals`/`in`; for the `not*` operators it counts as "not equal" but also raises the `blank_values` warning.
    - `groupAggregate` uses decimal sum, count, min, max and avg. Avg is computed as a decimal division, rounded half-away-from-zero to 10 places. Group keys use ordinal comparison, preserve first-appearance order, and a blank key fails by default.
    - `sort` is stable, ordinal for text, and nulls last.
    - `derivedColumn` allows only `add|sub|mul|div|round|year|month`. Operands are `{column}` or `{number}`. Divide by zero or an overflow is an issue and never a silent 0.
- **`ValidationRunner.cs`** – `static ValidationReport Run(WorkflowPlan, ValidationSpec, Table source, ExecResult, Table writtenBack)`.
  - It recomputes totals by a *separate* single-pass dictionary loop that shares no code with `PlanExecutor`, so the check is independent.
  - Rule catalogue (ids in `validation.json`):

    | Id | Check |
    |---|---|
    | `required_columns` | All plan columns exist. |
    | `valid_numbers` | Every cell in numeric columns is numeric. |
    | `known_values` | Filter columns hold only values seen at learn time (`knownValues`). A new value stops the run with a message naming the value and the row count. |
    | `blank_keys` | No group key is blank. |
    | `row_accounting` | Included + excluded = source rows. |
    | `sum_reconciliation` | Output total per aggregated column = independent sum of the included source rows. |
    | `no_duplicate_groups` | No group appears twice. |
    | `output_not_empty` | Warn if no rows are included. |
    | `roundtrip` | The temp output re-read equals the in-memory output. |

  - `ValidationReport(IReadOnlyList<ValidationItem>)` where `ValidationItem(string Id, Level Pass|Warn|Fail, string Message, string? Detail)`. `HasFailures` blocks publish.
- **`OutputPublisher.cs`**
  - `Publish(tempPath, outputDir, fileName) → finalPath`. Uses `File.Move(overwrite:false)`; if the name exists, it retries with ` (2)`, ` (3)` and so on.
  - `NameFor(pattern, runDate, inputStem)` allows only the tokens `{date}`, `{period}` (`yyyy-MM`) and `{input}`, sanitizes invalid file-name characters, and forces the `.xlsx` extension.
  - The temp file is `.<guid>.tmp` in the same directory, so the move is atomic. It is deleted in `finally`.
- **`OfficeWorkflowRunner.cs`** – the one entry point used by the tester, "Run again" and the scheduled executor.
  - `static RunResult Run(RunInputs inputs, CancellationToken ct)`.
  - `RunInputs(WorkflowPlan Plan, ValidationSpec Validation, byte[] TemplateBytes, TemplateLayout Layout, byte[] InputBytes, string InputFileName, string OutputDir, DateTimeOffset Now, OfficeLimits Limits)`.
  - `RunResult(RunState State /*Succeeded|Failed*/, ValidationReport Report, string? OutputPath, RowAccounting? Accounting, string? Summary)`.
  - Order: gate → read → required-column check → execute → validate (pre-write) → write temp → re-read and roundtrip-verify → publish only if there are no failures.
  - On failure there is no published file and the temp is deleted.
  - It never touches the input path, because it receives bytes.

### Learning/ (still pure)
- **`SemanticDiff.cs`** – `Diff(Table source, Table example, SheetStyle exampleStyle) → DiffReport`.
  - It detects and strips a grand-total row (last row whose label matches `^(grand\s+)?total` or whose numerics equal the column sums).
  - It maps each example column to `Passthrough(source col)`, `AggregateOf(...)`, `DerivedOf(...)` or `Unexplained`. It sets the mode: `RowSubset`, `Aggregate` or `Unsupported`.
  - It separates data differences from formatting differences, and fills `Notes` and `Blockers`.
- **`CandidateInference.cs`** – `Infer(Table source, Table example, DiffReport) → CandidateSet`. This is the core of decision 3 and is **replay-arbitrated**:
  1. Grouping keys: for each non-numeric example column, find source columns whose value set contains the example's values.
  2. Aggregates: for each numeric example column, try (source numeric column × {sum,count,avg,min,max}) and *virtual derived columns* (pairs of numeric columns × add/sub/mul/div, column × constant when the ratio is constant, year/month of date columns). A match requires equality within the tolerance for all groups. The tolerance is half a unit of the example's displayed decimals (0.005 by default), and count is exact.
  3. Filters: generate candidates per column:
     - `equals v` / `notEquals v` for columns with at most 20 distinct values;
     - `gt/gte/lt/lte` at up to 20 midpoints for numeric and date columns.
  4. Replay each candidate filter through `PlanExecutor` against the example. Cap work at about 5,000 candidate-replays.
  5. If no single filter explains it, try pairs among the 30 filters that improve the number of matched groups. Pairs are capped at 400.
  6. Sort: candidates are group-key asc, group-key desc, a numeric column desc, and "source order". Keep those that match the example's row order.
  - Output: `CandidateSet(Mode, IReadOnlyList<CandidateSlot> Slots, Notes, Blockers)`.
    - `CandidateSlot(SlotKind Kind /*Filter|Group|Aggregate|Derived|Sort*/, IReadOnlyList<Candidate> Options, string? PreferredId, bool Ambiguous)`.
    - `Candidate(string Id, string Description, StepSpec[] Steps, string Evidence, int Rank)`. Evidence is e.g. "3 of 4 rows kept, 1 dropped; every dropped row has Status = Cancelled".
  - **Ambiguity rule.** Two filters that give identical kept-rows but use *different columns* are ambiguous. A different operator on the *same* column that is equivalent over all seen values (`equals Completed` vs `notEquals Cancelled` with only those two statuses) collapses into one candidate. The preferred one is `equals`/`in`. Rank order: fewer terms, then `equals`/`in` over `notEquals`/`notIn`, then discrete over thresholds, then column-name hints (status, state, type, kind).
  - **Unreproducible dropped rows** (no column separates them) become a Blocker: "N rows disappeared with no common rule; Litos can't learn manual exclusions yet". The wizard does not offer Save.
- **`PlanAssembler.cs`** – `Build(CandidateSet, Selection chosenIds, PlanNames names, ObservedFacts facts) → WorkflowPlan`. The plan is assembled **by code from candidate ids**. The LLM never authors steps.
  - `facts` carries the known filter values, the input sheet and required columns, and the template layout.
  - It also builds the default `ValidationSpec`, derived from the plan: `required_columns`, `valid_numbers` (for aggregated columns), `known_values` (for filter columns), `blank_keys`, `row_accounting`, `sum_reconciliation`, `no_duplicate_groups`, `roundtrip`.
- **`ReplayVerifier.cs`** – `Verify(WorkflowPlan, Table source, Table example, bool exampleHadTotal)`.
  - It compares semantically: same columns by name and order, same row count, per-cell tolerance, the total row separately, and order-sensitive.
  - It returns `ReplayResult(bool Matches, IReadOnlyList<CellDiff> Differences /*max 20*/, bool OrderOnly)`.
- **`LearningPrompt.cs`**
  - `Build(string? explanation, SheetSummary source, SheetSummary example, DiffReport, CandidateSet) → (string Prompt, string SharedPayload)`.
  - `SharedPayload` is the exact data block sent to the model; it is shown in the UI and stored.
  - `UntrustedText.Wrap` puts every cell-derived string inside `<<<UNTRUSTED_CELL_TEXT … >>>` markers (any `<<<` or `>>>` inside is escaped). The instructions say that content cannot change the rules or the reply format.
- **`LearningReply.cs`**
  - `Parse(string raw, CandidateSet) → LearningReplyResult`. It tolerates a code fence and prose around the braces (the same approach as `BuzzyProposer.Parse`).
  - `LearningReplyResult(LearningDraft Draft, IReadOnlyList<string> Warnings)`.
  - Reply JSON schema:
    ```json
    {"skillName":"…","title":"…","description":"one sentence","objective":"…",
     "selection":{"filter":"f2|null","group":"g1|null","aggregate":"a1|null","derived":["d1"],"sort":"s1|null"},
     "questions":[{"id":"q1","text":"…","candidateIds":["f1","f2"]}],
     "assumptions":["…"],"skillBody":"markdown"}
    ```
  - Validation:
    - Every id must exist in the matching slot of the candidate set. Otherwise that field is dropped, the deterministic preferred option is used, and a warning is recorded.
    - `skillName` goes through `WorkflowStore.NameFrom`-equivalent slugging (duplicate the small slug function in Office; Gui then calls `WorkflowStore.IsValidName`).
    - Text fields are capped: 300 chars for descriptions and 6000 for the body; at most 3 questions and 8 assumptions; control characters stripped.
    - A question may only reference candidate ids. A question without ids is a plain "confirm" item.
    - A missing, invalid or empty reply gives a **deterministic fallback draft**: name from the example file stem, preferred candidates, templated SKILL.md, and one auto-generated question per ambiguous slot. The wizard therefore never dead-ends on an LLM failure.
    - Every ambiguous slot always gets a question. If the model omitted it, one is appended deterministically.

## 3. `Litos.Gui` additions

### `Buzzies/Learning/`
- **`LoadedInput.cs`**
  - `static LoadedInput FromPath(string path, OfficeLimits)`.
  - It opens with `FileShare.Read`, reads into `byte[]` (capped by `MaxFileBytes`) and hashes those bytes. The parsed bytes are always the hashed bytes. Record: `LoadedInput(string FileName, byte[] Bytes, string Sha256)`.
  - The wizard's picked files are explicit user selections, so they need no grant.
- **`SkillLearner.cs`** – the orchestrator, shaped like `WorkflowDrafter`/`BuzzyProposer`.
  - `static Task<LearningOutcome> LearnAsync(TeachingRequest, IChatProvider provider, string model, IProgress<string>? progress, CancellationToken ct)`.
  - `TeachingRequest(BuzzyProfile Profile, LoadedInput Source, LoadedInput Example, string? Explanation)`. The model is `BuzzyModels.Resolve(profile.Model, _session.Model)`, passed in by the caller.
  - It runs gate → read → summarize → diff → infer → prompt → **one** `provider.StreamAsync(new ChatRequest([ChatMessage.User(prompt)], [], model), ct)` collecting `TextDelta` → `LearningReply.Parse` → assemble → replay.
  - `LearningOutcome(LearningState State /*NeedsClarification|ReplayPassed|ReplayFailed|Unsupported*/, CandidateSet Candidates, LearningDraft Draft, WorkflowPlan? Plan, ReplayResult? Replay, IReadOnlyList<LearningQuestion> Questions, string SharedPayload, byte[] TemplateBytes, TemplateLayout Layout, IReadOnlyList<string> Notes, IReadOnlyList<string> Blockers, Table SourceTable, Table ExampleTable)`.
  - The tables stay in memory for re-replay while the wizard is open and are never persisted.
  - An LLM failure (exception or timeout) falls back to the deterministic draft with a note. A cancel propagates as `OperationCanceledException`, and no state is saved.
- **`TeachingFlow.cs`** – a **pure, Avalonia-free state machine** that the window drives. This is where the testing leverage is.
  - Holds the `LearningOutcome`, the current `Selection` (slot → candidate id), the names, the pending questions and their answers, and the test result.
  - `LearnAsync(...)`.
  - `Choose(slotKind, candidateId)`, `Rename(skillName|outputColumn|sheetName|filePattern, text)`. Each of these re-assembles the plan, re-validates it and re-replays it.
  - `CanTest` is true when there are no unanswered ambiguities, the replay matches and there are no blockers.
  - `TestAsync(string freshPath, string outputDir, CancellationToken)` calls `OfficeWorkflowRunner`. Output goes to `workspace/teach-tests/<yyyyMMdd-HHmmss>/`. It rejects a fresh file whose SHA-256 equals the source or example hash, with the message "that's the file you taught from".
  - `CanSave` requires: test passed, no fail items, and the user's "I checked the output" flag set.
  - `Save()` calls `LearnedSkillStore.Save`.
  - `State` follows the lifecycle: Draft → NeedsClarification → ReplayPassed → TestPassed → Approved.
- **`LearnedSkillStore.cs`**
  - `Save(string buzzyId, string name, string description, string skillBody, WorkflowPlan, ValidationSpec, byte[] template, TemplateLayout, ManifestFacts, DateTimeOffset now) → WorkflowVersion`.
  - **Storage layout**:
    ```
    skills/<name>/
      SKILL.md                      (latest; written by WorkflowStore.Save – frontmatter name/description/version/saved)
      versions/v3.md                (same text; what WorkflowStore.Read/LatestVersion/List and routine pinning use)
      versions/v3/                  (sidecar DIRECTORY – invisible to EnumerateFiles("v*.md"))
        workflow.json
        validation.json
        report-template.xlsx
        manifest.json
        shared-with-model.txt       (the exact bounded payload that was sent; the user-visible record)
    ```
  - Procedure:
    1. `next = WorkflowStore.LatestVersion + 1`.
    2. Write the sidecar into `versions/.v{next}.tmp/` and rename it to `versions/v{next}/`.
    3. Call `WorkflowStore.Save` last.
    4. Assert `saved.Version == next`. On mismatch, delete the sidecar and throw.
    5. A failure at step 2 or 3 leaves no `vN.md`, so no half-saved version is selectable.
  - `Load(store, buzzyId, name, version) → LearnedSkill?`. It returns null if `versions/vN/workflow.json` is absent. That is the branch discriminator in the executor (decision 5).
  - `Load` verifies `manifest.json`:
    - `approval.state == "Approved"`;
    - the SHA-256 of `workflow.json`, `validation.json` and `report-template.xlsx` equals the stored hashes.
  - A mismatch returns `LoadError("approval invalidated: the workflow files were changed after approval")`. This is how "any plan change invalidates the approval" is enforced; it is not an attack defence against a local user.
  - **SKILL.md body.**
    - It starts with "Learned Excel workflow. Litos runs it with fixed rules; do not reproduce it by hand."
    - Then it has sections Objective, Inputs, Rules, Output, Validation, Assumptions, Limitations. The Rules section is generated from the plan, and the LLM prose is used only for the objective and the assumptions.
  - **`manifest.json`** (schemaVersion 1):
    - `skill`, `version`, `kind:"office-excel"`, `createdAt`;
    - `inputs[]`: `{sheet, columns, rowCountSeen, sha256, fileName}`;
    - `example`: `{fileName, sha256, columns, rowCount}`;
    - `template`: `{file, sha256, layout}`;
    - `planSha256`, `validationSha256`;
    - `approval`: `{state, replayPassed, testedAt, testInputSha256, testSummary, approvedAt}`;
    - `model`: `{name, dataSharedWithModel:true}`;
    - `requiredTools: []`.
  - **No example file bytes and no source data rows are stored anywhere.** The only cell text persisted is the bounded summary in `shared-with-model.txt`, plus the learned known-values lists in the plan. The learned filter values are structural rules, not rows.
  - **Delete learning data:** `Delete(buzzyId, name)` removes the skill folder through `BuzzyStore.DeleteTreeWithoutFollowingLinks`. It is wired as a context-menu or review item "Forget this skill".
- **`LearningPaths.cs`**
  - Gui-side authorization helpers over `WorkspacePathGuard.Resolve(workspace, profile.Folders, path, Access.Read)`.
  - `ResolveInputFolder(profile, folder)` for routines, and `OutputDirFor(store, profile) = workspace/reports`.

### `Buzzies/Routines/`
- **`RoutineInputSpec.cs`** – `record RoutineInputSpec(string Folder, string Pattern, bool OnlyUnprocessed)`, with `Routine.Input` as a computed property built from the new fields.
- **`RoutineInputSelector.cs`** (pure, with a `Func<string,bool> isProcessed(sha)` parameter).
  - `static SelectionResult Select(string resolvedFolder, string pattern, bool onlyUnprocessed, Func<string, bool> isProcessed, OfficeLimits)`.
  - It enumerates files of the folder only, non-recursive. It skips `~$*`, dotfiles, hidden files, reparse points, non-`.xlsx` and zero-length files. The glob is a `*`/`?` matcher, case-insensitive.
  - It hashes each candidate's bytes only when `onlyUnprocessed` is on, so a large folder is not read in full.
  - Result: `None`, `One(path, sha, bytes)`, `Ambiguous(IReadOnlyList<string> names)`, or `Error(message)`. More than one candidate is **always** `Ambiguous`; it never picks the newest.
- **`LearnedWorkflowRunner.cs`**
  - `Task<RunOutcome> RunAsync(RunRequest, BuzzyStore, BuzzyDb, LearnedSkill, CancellationToken)`.
  - It re-resolves the folder through `WorkspacePathGuard` on every run. It applies the `MaxMinutes` timeout through a linked CTS and runs `OfficeWorkflowRunner` on the thread pool.
  - Outcome mapping:
    - `None` → `RunStatus.Skipped`, error text "No new file matching `<pattern>` in `<folder>`".
    - `Ambiguous` → `Failed`, listing up to 5 file names and "move or delete the extras, or run one by hand".
    - Gate, validation or schema-drift failure → `Failed`, with the first failing `ValidationItem` message. Missing columns read "Column 'Status' is missing from <file>. The file layout changed since this skill was learned."
    - Success → `Succeeded`. Summary example: "Report written: 3 groups, 3 rows included, 1 excluded, total 250.00". `Outputs` is `["reports/<name>.xlsx"]`, relative to the workspace (reuse `BuzzyRunExecutor.ShowablePath`). Tokens are 0.
    - On success it calls `db.MarkProcessed(routine.Id, sha, path, runId, now)`. A failure never marks the file, so fixing the file and re-running works.
    - Cancellation → `Cancelled`.

### `BuzzyRunExecutor` branch
- **Constructor:** `BuzzyRunExecutor(Func<CancellationToken, Task<BuzzyRunEnvironment>> environment, BuzzyDb? database = null)`. The construction site (MainWindow wiring) passes `_routines.Database`. I did not read it, so patch it there.
- **Placement:** right after the existing "pinned workflow must still exist" block (line ~84), and **before** `BuzzyToolPolicy.Build`. The model-empty check at lines 66–68 must move *below* the branch:
  ```csharp
  if (workflow is not null)
  {
      var learned = LearnedSkillStore.Load(store, profile.Id, workflow.Name, workflow.Version);
      if (learned.Error is not null) return Fail(learned.Error);
      if (learned.Skill is { } skill)
          return await new LearnedWorkflowRunner(...).RunAsync(request, store, database, skill, ct);
  }
  ```
- A routine without a learned sidecar falls through to the existing LLM path unchanged. This preserves the existing behaviour and tests.
- A learned routine requires `database != null`; otherwise it returns a clear Failed outcome.

### `BuzzyDb` v3 migration
- `SchemaVersion = 3`. Add after the `version < 2` block:
  ```csharp
  if (version < 3)
  {
      Execute(connection, "ALTER TABLE routines ADD COLUMN input_folder TEXT;", transaction);
      Execute(connection, "ALTER TABLE routines ADD COLUMN input_pattern TEXT;", transaction);
      Execute(connection, "ALTER TABLE routines ADD COLUMN only_unprocessed INTEGER NOT NULL DEFAULT 1;", transaction);
      Execute(connection, """
          CREATE TABLE IF NOT EXISTS routine_processed_inputs (
              routine_id TEXT NOT NULL, sha256 TEXT NOT NULL, path TEXT NOT NULL,
              run_id TEXT, processed_at TEXT NOT NULL,
              PRIMARY KEY (routine_id, sha256));
          """, transaction);
  }
  ```
- `Routine` record: append `string? InputFolder = null, string? InputPattern = null, bool OnlyUnprocessed = true` after `SkillVersion`. `FollowsWorkflow` is unchanged. Add `bool UsesInputFolder => InputFolder is not null`.
- `SaveRoutine` insert and update add the three columns. `QueryRoutines` maps them: `NullableString` for the first two and `GetInt32 != 0` for the flag.
- New methods:
  - `IsProcessed(routineId, sha)`;
  - `MarkProcessed(routineId, sha, path, runId, at)` using `INSERT OR IGNORE`;
  - `ClearProcessed(routineId)`.
- `DeleteRoutine` and `DeleteForBuzzy` also delete the matching processed rows.
- Existing positional `new Routine(...)` calls keep compiling because the new parameters have defaults.

### Wizard, menu and routine editor
- **`TeachBuzzyWindow.cs`** (code-only, thin; all logic in `TeachingFlow`).
  - Pages:
    1. File pickers and explanation text. A disclosure line names the model and says that bounded samples go to it, with a "Show what will be sent" expander (`SharedPayload`).
    2. A progress page with **Cancel**.
    3. Review: Learned (title), Inputs, Rules (one `ComboBox` per slot among candidates), Output (name pattern and sheet name textboxes, editable), Assumptions, Validation list, Questions with option buttons. A badge reads "Needs your answer" until a question is answered.
    4. Test: fresh-file picker → validation report list (pass / warn / fail icons) → **Open output** → the "I checked the output" checkbox → **Save skill**.
    5. Done: **Run again…** and **Schedule…**.
  - A failed test shows a red "Not learned yet" state, never a success state.
  - Use `Margin` on the ScrollViewer child, not `Padding`.
  - Returns `TeachOutcome(WorkflowVersion? Saved, bool ThenSchedule)`.
- **`MainWindow.Teach.cs`** (new partial).
  - `TeachBuzzyAsync(BuzzyProfile)`:
    - `RejectIfNoModel()`;
    - resolve `model = BuzzyModels.Resolve(profile.Model, _session.Model)` with `_session.ChatProvider`;
    - show the window;
    - on a saved skill, call `AddToolLine`.
  - **Run again** runs `LearnedSkillStore.Load` + `OfficeWorkflowRunner` directly with a picked file and shows the same report window. It also calls `MarkProcessed` for any routine of that skill that uses an input folder, so the scheduled run does not redo the same file.
  - **Schedule** calls the existing `RepeatAsync(profile, request, saved)`. `RepeatDraft` gets an overload that, for a learned skill, sets:
    - `Prompt` to the fixed text "Run the learned workflow";
    - the trigger to Monthly-like (the existing Daily/Weekly options, with the user choosing in the editor);
    - `InputFolder`/`InputPattern`/`OnlyUnprocessed` to blank/`*.xlsx`/true.
- **`MainWindow.Buzzies.cs` `BuzzyMenu`:** add `Item("Teach Buzzy…", () => _ = TeachBuzzyAsync(profile))` after the "Soul, memory and skills…" item.
- **`RoutineEditorWindow` (not read, so patch it carefully):**
  - When the draft or routine is learned (`SkillName` has a sidecar), hide the prompt, token and model fields.
  - Show a folder picker, a glob textbox and an "only unprocessed files" checkbox.
  - On save, validate with `WorkspacePathGuard.Resolve(... Access.Read)`, and reject a missing folder or one that is not inside the workspace or a granted folder, with the guard's message.
  - Routines never get write grants; outputs always go to `workspace/reports/`.

## 4. Typed plan JSON schema v1

```json
{
  "schemaVersion": 1,
  "skillId": "monthly-regional-sales",
  "version": 1,
  "inputs": [{"id":"sales","format":"xlsx","sheet":"Sales","requiredColumns":["Region","Amount","Status"]}],
  "steps": [
    {"op":"filter","column":"Status","operator":"equals","value":"Completed","valueType":"text","knownValues":["Completed","Cancelled"]},
    {"op":"derivedColumn","name":"Revenue","expr":{"fn":"mul","args":[{"column":"Qty"},{"column":"Price"}]}},
    {"op":"groupAggregate","groupBy":["Region"],"aggregates":[{"column":"Amount","function":"sum","as":"Total Sales"}]},
    {"op":"sort","keys":[{"column":"Region","direction":"ascending"}]},
    {"op":"writeWorkbook","template":"report-template.xlsx","sheet":"Summary",
     "columns":[{"from":"Region","as":"Region"},{"from":"Total Sales","as":"Total Sales"}],
     "includeGrandTotal":true,"grandTotalLabel":"Grand Total"}
  ],
  "output": {"format":"xlsx","fileNamePattern":"Sales_Report_{period}.xlsx","overwrite":false},
  "policies": {"unknownValue":"fail","blankKey":"fail","invalidNumber":"fail"}
}
```

- `validation.json` holds `{"schemaVersion":1,"rules":[{"id":"required_columns"},{"id":"known_values","column":"Status"},…]}`.
- The writeWorkbook `columns` list covers select, reorder and rename. There is no separate select op.
- Operators:
  - **text:** `equals | notEquals | in | notIn`, using `value` or `values`;
  - **number/date:** `gt | gte | lt | lte` (and `equals`).
- Number values are strings parsed with `decimal.Parse(NumberStyles.Number, InvariantCulture)`. Date values are `yyyy-MM-dd`, parsed with `ParseExact`.
- Aggregate functions: `sum|count|avg|min|max`. `count` may omit `column`.
- Derived `fn`: `add|sub|mul|div|round|year|month`. Operands are `{column}` or `{number}`. There is no free-form expression string and no concat in v1.

### Validator rules (`PlanValidator`)
1. `schemaVersion == 1`.
2. `skillId` passes `WorkflowStore.IsValidName`.
3. Exactly one input, `format == "xlsx"`. Sheet name at most 31 chars, no `[]:*?/\`.
4. Required columns are non-empty, unique and at most 100.
5. Steps are 1–12 and **the last step is the only `writeWorkbook`**. At most one `groupAggregate`, and it comes before `sort` and `writeWorkbook`.
6. Unknown `op`, unknown JSON members and unknown enum values are errors.
7. Column references are resolved against a running schema: the required input columns, plus derived names, plus the group-by and `as` names after the group step. A reference to a column that does not exist at that point is an error.
8. Operator and valueType must agree. Number or date operators need valid literals. `in`/`notIn` have at most 200 values, each at most 200 chars.
9. Names (`as`, derived name, output columns) are 1–64 chars, unique, with no control characters and no leading `= + - @`.
10. Aggregates: `sum|avg|min|max` need a numeric column; `count` needs none or any.
11. Derived: the argument count matches the function; `div` rejects a constant 0.
12. writeWorkbook:
    - `template` must be the bare file name `report-template.xlsx`, with no path separators and no `..`;
    - `sheet` is a valid sheet name;
    - every `from` exists in the table after the previous steps;
    - the label is at most 64 chars.
13. `output.fileNamePattern` ends with `.xlsx`, contains only the tokens `{date}`, `{period}`, `{input}`, has no path separators and is at most 100 chars. `overwrite` must be `false`.
14. `policies` must equal `fail` for `unknownValue` and `invalidNumber` in v1, and `fail` or `keep` for `blankKey`.
15. Plan JSON is at most 64 KB.
16. `knownValues` is allowed only on `equals|in|notEquals|notIn` filters.
17. With `SchemaContext` (on test or run): each required column exists in the real sheet, and the sheet exists.

## 5. Data flow

### Learn
1. The user picks the source, the example and optional text.
2. `LoadedInput.FromPath` reads and hashes the bytes (source and example).
3. `WorkbookGate.Check` on each file. Any failure stops with a plain message, and nothing is sent anywhere.
4. `ExcelReader` → two `Table`s plus `SheetStyle`.
5. `ExcelInspector` → two `SheetSummary`s (bounded).
6. `SemanticDiff` → `DiffReport`.
7. `CandidateInference` → `CandidateSet`. If the mode is `Unsupported` or there are blockers, the flow ends there. **No LLM call is made**, and the user sees the reasons.
8. `LearningPrompt.Build` → the prompt. The wizard shows the payload preview and **the user clicks Learn** (consent, spec §11).
9. One tool-less `StreamAsync`, with Cancel wired to the CTS and a 2-minute timeout.
10. `LearningReply.Parse` validates it against the candidates. Failures degrade to the deterministic draft.
11. `PlanAssembler.Build` → plan. `PlanValidator.Validate` → no errors. `ReplayVerifier.Verify(plan, sourceTable, exampleTable)`.
12. The result is `ReplayPassed` or `NeedsClarification`. A replay mismatch is shown as "Not reproduced", with the first differences, and the skill cannot be saved.

### Edit and clarify
Each `Choose` or `Rename` repeats step 11 in memory with no LLM call.

### Test
1. The user picks a fresh file. The flow rejects it if its hash equals the source or example hash.
2. `OfficeWorkflowRunner.Run` → report, accounting and an output path under `workspace/teach-tests/<stamp>/`.
3. The window shows the report and **Open output**. Any Fail means no file was published.
4. A pass plus the "I checked the output" checkbox enables Save.

### Save
`LearnedSkillStore.Save` as in §3, then the approval state is written to the manifest.

### Run again (manual)
Load skill → verify manifest hashes → `OfficeWorkflowRunner` with the picked file → output in `workspace/reports/`.

### Scheduled run
1. `RoutineScheduler` → `BuzzyRunExecutor.RunAsync`.
2. The pinned `WorkflowStore.Read` runs as today.
3. The learned branch loads the skill and verifies the manifest.
4. It resolves the input folder through `WorkspacePathGuard`, and applies `RoutineInputSelector` with the processed-hash check.
5. `OfficeWorkflowRunner` runs under the routine's time limit.
6. It marks the input processed on success only.
7. It returns the `RunOutcome` with `Outputs`.
8. The existing `routine_runs` row records it (the occurrence key stays the scheduler's own).

## 6. Error handling

| Situation | Behaviour |
|---|---|
| Gate rejects a file | Plain-language reason naming the file; no model call; no state change. |
| Unsupported layout (merged cells, multi-row header, duplicate headers, several data sheets) | A blocker in the review: "Litos can't learn this file yet because…". |
| LLM call fails or times out | Deterministic fallback draft plus a note. A cancel unwinds with no residue. |
| Replay mismatch | `ReplayFailed` view with up to 20 differences; Save disabled. |
| Ambiguous slot unanswered | Test disabled; the question is highlighted. |
| New input: missing column, unknown filter value, non-numeric amount, blank group key | `Fail` item with an actionable message; no output published; the temp file is deleted. |
| Disk, permission or IO error on write | Failed outcome with the message; the temp is removed. |
| Input file locked | Retried once after 500 ms, then a Failed outcome saying the file is in use. |
| Manifest or plan hash mismatch, plan invalid on load | Run refused: "approval invalidated"; the user must re-teach or re-approve. |
| Cancellation (timeout, app closing) | `Cancelled`; the temp is deleted in `finally`; nothing is half-published. |
| App closed or machine asleep during a schedule | The existing scheduler's Missed or Interrupted handling applies; the run is never reported as done. |

## 7. Security mapping to spec §11

| §11 requirement | Where it is enforced |
|---|---|
| Selected inputs, approved folders and workspace only; canonical paths; no symlink escapes | `WorkspacePathGuard.Resolve` for the routine folder (every run) and for files enumerated from it. `RoutineInputSelector` also skips reparse points. The output directory is always the workspace `reports/` or `teach-tests/`, inside the workspace. Office code receives bytes and a directory and never resolves model-supplied paths. |
| No shell, arbitrary code or MCP in unattended runs | The learned branch never builds a tool registry or an agent loop. The plan is typed records, not code. |
| Cell text is untrusted | It reaches the model only via `UntrustedText.Wrap`, truncated and sanitized. It can only select among pre-computed candidate ids. It never reaches the plan or `SKILL.md` prose as instructions (the body is generated from the plan). |
| Reject macros, encrypted files, dangerous relationships, external links | `WorkbookGate`, before parsing. |
| Limits on ZIP expansion, rows, memory, time | `OfficeLimits` plus the routine's `MaxMinutes` and `CancellationToken` checks. DTD processing is off by default in the Open XML SDK; tests include a billion-laughs XML part. |
| Formula injection | The writer never sets `FormulaA1`, only literal values; leading `= + - @` get a quote prefix. Reads use cached values and never evaluate. |
| Temp output then publish after validation; inputs untouched | `OutputPublisher` and `OfficeWorkflowRunner` ordering. Inputs are read as bytes and never opened for writing. `overwrite:false` with unique naming. |
| Cloud-model disclosure and authorization; credentials out of prompts | The wizard names the model, shows the exact payload and requires the Learn click. `shared-with-model.txt` is the record. No secrets are in the prompt. |
| Delete learning data; audit; don't retain sensitive examples | `Delete` forgets the whole skill. Only summaries and hashes persist, never the example or the source bytes. |
| Failed validation publishes nothing | `HasFailures` blocks the publish; tested. |

## 8. Test plan

### `Litos.Office.Tests`
Fixtures are generated in code with ClosedXML into temp dirs or memory. Hostile ones are hand-built with `ZipArchive`.

- **WorkbookGateTests:** xlsm content type; `vbaProject.bin`; OLE magic; an `externalLinks` part; an external non-hyperlink relationship; an oversized file; high compression ratio (tiny limits); too many entries; a clean file passes.
- **ExcelReaderTests:**
  - types; header uniqueness; merged-cell rejection; blank-header rejection;
  - cached-value reading and the missing-cache finding;
  - the row limit; the cancellation token; the sheet hint rules.
- **ExcelInspectorTests:** truncation, sampling bounds and sanitization (control characters, newlines, an embedded `<<<` marker, a 10,000-char cell).
- **PlanJsonAndValidatorTests:**
  - a round trip of the §4 example;
  - one test per validator rule, including unknown op, unknown member, unknown column, a path-traversal template, a bad token, `overwrite:true` and a bad policy.
- **PlanExecutorTests:** the §6 spec fixture and the fresh-input fixture (expected East 50, North 120, South 80, total 250, 3 in, 1 out). Also:
  - decimal exactness (0.1 + 0.2 sums);
  - blank-key failure;
  - non-numeric failure;
  - each operator; stable sort; derived ops; divide by zero.
- **ValidationRunnerTests:** each rule passes and fails; independence (a deliberately corrupted output is caught).
- **ExcelWriterTests:** header style, number formats, widths and the grand-total row come from the template; text starting with `=cmd|…` has no formula and a quote prefix; a template has no example data.
- **OutputPublisherTests:** no overwrite (`(2)` suffix); failed validation leaves no file; tokens; name sanitization; the temp is cleaned on exception and on cancellation; the input hash is unchanged afterwards.
- **OfficeWorkflowRunnerTests (end to end):** the §6 round trip on the example, then on the fresh input; schema drift; an unknown status; a malicious cell (`=HYPERLINK(...)`, "ignore previous instructions", a `<<<` marker) has no effect.
- **SemanticDiffTests, CandidateInferenceTests, PlanAssemblerTests, ReplayVerifierTests:**
  - the §6 example gives a Status filter plus sum by Region;
  - the ambiguous case: two columns both separate kept from dropped rows → an `Ambiguous` slot;
  - `equals Completed` vs `notEquals Cancelled` over equivalent domains collapses;
  - the manual-exclusion case is a blocker with no LLM call;
  - numeric threshold; avg and count; derived column; sort detection; grand-total detection;
  - an order-only mismatch.
- **LearningPromptTests and LearningReplyTests:**
  - the payload bounds; no raw whole-sheet data;
  - a valid reply; a code-fenced reply; prose around the JSON;
  - an invalid candidate id is dropped and falls back; a hostile skill name is slugged; an oversized body is truncated;
  - garbage gives the deterministic fallback; an omitted question for an ambiguous slot is appended.

### `Litos.Gui.Tests` (no headless Avalonia)
- **BuzzyDbV3Tests:** create a v2 database with raw SQL, open it with `BuzzyDb`, and check that the columns, the table and `user_version` are 3, and that old routines survive with the defaults. Round-trip the new fields. `MarkProcessed` is idempotent. Deleting a routine or a Buzzy clears its processed rows.
- **RoutineInputSelectorTests:** none, one, several (ambiguous, listing names); an already-processed hash is skipped; the glob; `~$` and hidden files; a junction is skipped; the folder is outside the grant (denied).
- **LearnedSkillStoreTests:** the layout; `WorkflowStore.LatestVersion`, `List` and `Read` are unaffected by the sidecar; two saves give v1 and v2 and a pinned routine still reads v1; the manifest hash mismatch refuses a run; a failed save leaves no `vN.md`.
- **SkillLearnerTests:** use `ScriptedChatProvider` (RoutineTestSupport). Check one tool-less request; the prompt holds only bounded samples; the model name used; a garbage reply gives the fallback; a provider exception gives the fallback; cancellation leaves no state.
- **TeachingFlowTests:** the lifecycle transitions; Save is blocked until the test has passed and the checkbox is set; a fresh file equal to the source is refused; `Choose` re-replays; renames are validated.
- **BuzzyRunExecutorLearnedTests:** build `BuzzyRunExecutor` with an environment whose provider throws if called. A pinned learned routine succeeds with `Outputs`, which proves no LLM. Also: a missing pinned version fails as before; no input gives `Skipped`; two inputs fail with the listed names; schema drift fails with a message; a non-learned workflow still takes the LLM path; and an empty model still works for a learned run.

### Manual live pass (buffer day)
Run the §6 scenario with a real mailbox-free fixture. Check that Excel opens the output with no repair prompt, check macOS, and run one scheduled routine with the "Interval" trigger.

## 9. Build sequence (each step ends green: `dotnet build` and its tests)

| # | Step | Days |
|---|---|---|
| 0 | Scaffold `Litos.Office` and `Litos.Office.Tests`, slnx entries, packages, licence and transitive check, smoke test | 0.25 |
| 1 | `CellValue`, `Table`, `Limits`, `WorkbookGate`, `ExcelReader`, `SheetStyle`, `ExcelInspector`, `UntrustedText`, `XlsxFixtures` helper + tests | 1.25 |
| 2 | `WorkflowPlan`, `PlanJson`, `PlanValidator` + tests | 0.75 |
| 3 | `PlanExecutor`, `ValidationRunner` + tests (the spec §6 fixtures) | 1.25 |
| 4 | `TemplateExtractor`, `ExcelWriter`, `OutputPublisher`, `OfficeWorkflowRunner` + tests; the §6 end-to-end passes | 1.25 |
| 5 | `SemanticDiff`, `CandidateInference`, `PlanAssembler`, `ReplayVerifier` + tests | 2.0 |
| 6 | `LearningPrompt`, `LearningReply` + tests | 0.5 |
| 7 | Gui: `LoadedInput`, `SkillLearner` + tests | 0.5 |
| 8 | Gui: `LearnedSkillStore`, manifest, SKILL.md generation + tests | 0.5 |
| 9 | `BuzzyDb` v3, `Routine` fields, `RoutineInputSelector`, `LearnedWorkflowRunner`, `BuzzyRunExecutor` branch, `RoutineEditorWindow` fields + tests | 1.0 |
| 10 | `TeachingFlow`, `TeachBuzzyWindow`, `MainWindow.Teach.cs`, the menu item, Run again and Schedule wiring + tests for `TeachingFlow` | 1.5 |
| 11 | Hardening tests (hostile files, malicious cells), the live pass, fixes | 0.75 |

That totals 11.5 days of work, within the 11–13 day budget.

**Cut order if time runs short:** (1) derived-column inference (keep the executor op); (2) the filter-pair search; (3) the `RoutineEditorWindow` fields (use a fixed `*.xlsx` pattern and a folder picker only).

## 10. Not verified (check when implementing)

- **`RoutineEditorWindow`.** I did not read it. The exact shape of the edit and any layout constraints are unknown.
- **`BuzzyRunExecutor` construction site.** I did not read where it is created (probably `MainWindow`), or how the existing executor tests build `BuzzyRunEnvironment`.
- **ClosedXML 0.105.1 transitive dependencies.** I could not confirm them: their licences (SixLabors) and whether `DocumentFormat.OpenXml` unifies cleanly with 3.4.1.
- **Cached values for formula cells.** I could not confirm that `XLCell.CachedValue` is available and unevaluated on 0.105.1. If it is not, use the OpenXML `CellValue` of the cached `<v>` element through the DocumentFormat.OpenXml package that is already referenced.
- **`UntrustedContent.Wrap`.** The existing marker format (`<<<EXTERNAL_UNTRUSTED_CONTENT … >>>`) is visible only through the regex in `WorkflowTrace`. I have not read the class itself. I chose a separate Office-local wrapper to keep `Litos.Office` free of Litos.Agent. If you prefer parity, Gui can re-wrap the payload with the existing helper.