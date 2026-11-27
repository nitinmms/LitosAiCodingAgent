# Litos Buzzies — Learning by Example (Simple MVP)

> Show it once. Let your Buzzy do it next time.

**Status:** Proposed implementation specification  
**Target:** Litos Work / Litos.Gui (Avalonia, .NET 10) on Windows and macOS  
**Scope:** Excel, Word and PowerPoint; Excel first.

## 1. Product objective

Let a user teach a persistent Litos Buzzy a repetitive Office workflow by supplying the original document(s), a completed example and, optionally, a short explanation. The Buzzy infers a reusable procedure, tests it on fresh data, and saves it as a skill that can be run again or scheduled.

This is learning by example, not recording human actions or fine-tuning model weights. A before/after pair cannot uniquely establish the business rules; the Buzzy must expose assumptions and ask for clarification when needed.

### Minimum wow moment

A user supplies last month's raw sales workbook and finished report. Buddy learns how to exclude cancelled orders, group sales by region and format the results. The user supplies this month's raw workbook and Buddy creates a fresh report, reconciles the totals, and offers to repeat the task every month.

### Non-goals

- Screen recording, keystroke capture, mouse replay, Office add-ins or computer-use agents.
- Docker, MXC, cloud computers or a new agent framework.
- Arbitrary Office workflow support, VBA, Power Query, external workbook links, or exact visual reconstruction.
- Multi-agent delegation, marketplace, model training or a new billing platform.

## 2. User experience

Add **Teach Buzzy** to the existing Litos.Gui chat interface. Use a simple wizard:

```text
Teach Buddy a workflow

1. Original files       [Add files / drag and drop]
2. Completed example    [Add file]
3. Explain the work     [Optional text]

                        [Learn Workflow]
```

Then show a review screen with the inferred objective, input schema, transformation steps, output template, assumptions, unsupported features and validation rules. Ask focused questions only when materially necessary.

```text
Learned: Monthly Regional Sales Report
Inputs: Sales workbook with Region, Amount, Status
Rules: Exclude Cancelled; sum Amount by Region
Output: New workbook using the approved report template
Validation: Included + excluded rows = source rows;
            output total = sum of included Amount

[Edit Rules] [Test on New File] [Save Skill]
```

The user chooses a fresh input and destination. Display the generated artifact and validation results. Only after a successful test and explicit user approval enable **Run Again** and **Schedule Routine**. A failed test must not be presented as successful learning.

## 3. Supported workflows

| App | Initial supported examples | Execution |
|---|---|---|
| Excel (.xlsx) | Select/filter rows, group/sum/count, derived columns, consolidate compatible sheets, simple formatting | ClosedXML + deterministic .NET transforms |
| Word (.docx) | Populate approved report/letter templates, replace content controls/placeholders, repeat simple table rows | Open XML SDK |
| PowerPoint (.pptx) | Populate approved slide templates, replace text and simple table values, clone supported slide layouts | Open XML SDK |

Excel is the first release gate. Word and PowerPoint use the same teaching flow but have narrower template-oriented support. For Word and PowerPoint, the completed document alone is not enough to infer where future values come from; require input data or an explicit mapping. Preserve unsupported document parts where safe, otherwise reject the operation. Do not claim pixel-perfect rendering or formula recalculation by Open XML.

## 4. Architecture

```text
Litos.Gui (Avalonia)
  Teach Buzzy Wizard / Skill Review / Artifact View
                  |
          Learning Service (.NET)
                  |
      Document Inspector -> Semantic Diff
                  |
       Existing Litos Agent / LLM Provider
                  |
      Draft Skill + Typed Workflow Plan
                  |
       Plan Validator / User Approval
                  |
      Deterministic Office Tool Executor
                  |
         Artifact + Validation Report
                  |
       Existing Buzzy Skill Store / Scheduler
```

Keep the first implementation in the existing Litos GUI process. Reuse agent instantiation, provider configuration, JSONL sessions, Buzzy workspaces and SQLite scheduling where available. The LLM proposes a plan; trusted code executes it. Do not allow the LLM to generate and run arbitrary C#, Python or shell commands as part of unattended Office workflows.

### Suggested projects (adapt to existing repository conventions)

- `Litos.Office.Core`: file inspectors, document models and deterministic transformation operations.
- `Litos.Office.Learning`: semantic diff, prompt assembly, plan generation, validation and skill persistence.
- `Litos.Office.Tools`: expose approved inspect/learn/test/run operations to the existing agent tool registry.
- `Litos.Gui`: teaching wizard, review, test results and routine setup.
- `Litos.Office.Tests`: fixtures, regression tests and security tests.

These are proposed boundaries, not claims about existing repository APIs. Inspect the actual Litos source before implementation.

### Dependencies

- .NET 10 and Avalonia (existing application).
- ClosedXML for ordinary Excel workbooks.
- DocumentFormat.OpenXml for Word/PowerPoint and package inspection.
- System.Text.Json for typed plans and schema versioning.
- SQLite for run history, approved plan versions and routine state.

Pin tested package versions and review dependency licences before distribution. Office installation is not required for execution; users can open output in Office for visual verification. No Office COM automation or Office.js required.

## 5. Learning pipeline

1. Import copies of explicitly selected files into a teaching session. Preserve original files and calculate hashes.
2. Inspect supported OOXML package content. Build compact structured summaries: worksheet names, headers, types, row counts, formula patterns, representative rows, styles; or document/slide placeholders, tables and text structure.
3. Compare before and after at the semantic level, not raw ZIP/XML byte level. Separate data changes from formatting and template changes.
4. Give the selected LLM the summaries, optional explanation, allowed operation catalogue and required plan schema. Minimize data sent to the model; do not upload whole files silently.
5. Infer candidate rules and identify uncertainties: e.g. a missing row may be a status filter, manual exclusion or date cutoff. Ask the user rather than inventing a rule.
6. Validate the typed plan against an allowlist and user-granted paths. Reject unknown operations, unsafe parameters and unsupported features.
7. Replay on the original input into a temporary output; compare semantic results against the completed example. A matching example is necessary but does not prove generalization.
8. Test on fresh input with independent expected results or user-confirmed validation checks. Show differences, warnings and artifact preview.
9. Persist the approved version of the skill and its template; schedule only after separate confirmation.

For large workbooks, perform calculations and schema comparison locally. Send bounded samples, aggregates and relevant changed regions to the LLM instead of entire sheets. Sampling can miss exceptions, so validate against all rows deterministically.

## 6. Example: Excel learning

Original workbook `Sales_Raw.xlsx`:

| Region | Amount | Status |
|---|---:|---|
| North | 100 | Completed |
| South | 200 | Completed |
| North | 50 | Cancelled |
| North | 300 | Completed |

Completed example `Sales_Final.xlsx`:

| Region | Total Sales |
|---|---:|
| North | 400 |
| South | 200 |
| Grand Total | 600 |

The Buzzy infers exclusion of cancelled records, grouping and sum. User confirms these rules (including how to handle unknown status, blank region, negative amounts and currency). New inputs may have different row counts and ordering; do not hardcode cell ranges, example totals or example filenames.

### Example typed workflow plan

Illustrative Litos-owned schema, not a library API:

```json
{
  "schemaVersion": 1,
  "skillId": "monthly-regional-sales",
  "version": 1,
  "inputs": [{"id":"sales", "format":"xlsx", "sheet":"Sales", "requiredColumns":["Region","Amount","Status"]}],
  "steps": [
    {"op":"filter", "column":"Status", "operator":"equals", "value":"Completed"},
    {"op":"groupAggregate", "groupBy":["Region"], "aggregates":[{"column":"Amount","function":"sum","as":"Total Sales"}]},
    {"op":"sort", "column":"Region", "direction":"ascending"},
    {"op":"writeWorkbook", "template":"report-template.xlsx", "sheet":"Summary", "includeGrandTotal":true}
  ],
  "validation": ["required_columns", "valid_amounts", "known_statuses", "row_accounting", "sum_reconciliation"],
  "output": {"format":"xlsx", "fileNamePattern":"Sales_Report_{period}.xlsx", "overwrite":false}
}
```

The example uses an explicit Completed-only filter; confirm whether other non-cancelled statuses should be included before approving. Use decimal arithmetic for monetary aggregation, explicit culture/date parsing, and report malformed or missing values rather than silently treating them as zero.

### Fresh-input test fixture

New input: North Completed 120; South Completed 80; South Cancelled 40; East Completed 50. Expected groups: East 50, North 120, South 80; grand total 250; 3 included and 1 excluded row. Compare output values and template-required formatting independently from the model.

## 7. Word and PowerPoint learning

### Word: Monthly inspection report

Supply a source Excel table or structured JSON and a completed Word report. Identify reusable headings, styles, tables and variable fields. Create a template using named content controls or explicit placeholders (e.g. `{{InspectionDate}}`). Approve source-to-field mapping and repeatable table rules. Test on a fresh inspection dataset. Preserve headers, footers and styles. Flag unsupported fields, tracked changes and embedded objects.

### PowerPoint: Monthly management review

Supply source KPI data and a completed presentation. Infer which slides and text fields are reusable, and map metrics to named shapes/table cells. Preserve slide masters, layouts, themes and images; start with updating existing slides, not unrestricted slide design. Test on different KPI values and check for missing placeholders or overflow risk. Advanced charts, linked objects and animations remain out of scope; require human visual review for newly learned templates.

## 8. Skill storage and lifecycle

```text
~/.litos/buzzies/buddy/
  skills/monthly-regional-sales/
    SKILL.md
    workflow.json
    report-template.xlsx
    validation.json
    manifest.json
    tests/expected-summary.json
  workspace/
  artifacts/
  sessions/
```

`SKILL.md` is the human-readable procedure. `workflow.json` is the executable, schema-validated plan; do not execute arbitrary instructions from Markdown. `manifest.json` includes skill version, input/output contracts, template hashes, required tools, approval state and timestamps. SQLite stores approved version pointers and run history. Store teaching source files only with permission and retention controls; avoid saving real customer data as test fixtures by default.

Example lifecycle: Draft → Needs clarification → Example replay passed → Fresh-input test passed → Approved → Scheduled. Any plan change invalidates the prior approval and requires revalidation. Failed runs retain diagnostics and never silently replace an approved skill.

## 9. Suggested .NET contracts

```csharp
public interface IOfficeWorkflowLearner
{
    Task<LearningResult> LearnAsync(TeachingRequest request, CancellationToken ct);
}

public interface IOfficeWorkflowExecutor
{
    Task<WorkflowRunResult> ExecuteAsync(ApprovedWorkflow plan,
        WorkflowInputs inputs, CancellationToken ct);
}

public interface IOfficeWorkflowValidator
{
    Task<ValidationResult> ValidateAsync(WorkflowPlan plan,
        CancellationToken ct);
}
```

These are illustrative signatures; define the DTOs and integrate with actual Litos abstractions. Use dependency injection and separate inspectors/executors by document type. Tool entry points should include `inspect_office_document`, `learn_office_workflow`, `test_office_workflow`, `run_approved_office_workflow` and `list_learned_skills`. Only trusted application code can issue an approved plan reference. Do not let the model self-approve or modify stored approvals.

## 10. Repetition and scheduling

After approval, the user may run the skill manually or select a folder and schedule (e.g. first weekday of each month). Define how files are selected, the reporting period, output location, time zone and missed-run behavior. Do not assume the newest file is the correct one when multiple candidates exist.

Reuse the Buzzy scheduler; in the simple Litos.Gui MVP, scheduled execution works while the app is running (including tray mode) and the computer is awake. On restart, show missed runs and offer a controlled catch-up. Use a durable run key (skill version + period + input hashes), one concurrent run per skill, cancellation, atomic output publication and duplicate prevention. Notify the user of success, failure or schema drift. No source modification or overwriting outputs by default.

## 11. Security and privacy

- Grant access only to selected inputs, approved folders and the Buzzy output workspace; validate canonical paths and prevent symlink/reparse-point escapes. Application-level file restrictions are not OS sandboxing.
- Unattended Office execution uses only allowlisted native tools. No shell, arbitrary code, untrusted MCP execution or automatic external communications.
- Treat workbook cells, document text and embedded instructions as untrusted data; they cannot grant permissions or alter the approved plan.
- Reject macro-enabled files, encrypted files, dangerous package relationships and unsupported external links in the initial version. Never launch macros or fetch external resources from documents.
- Limit ZIP expansion, XML size, row count, memory and runtime; use safe XML/package parsing and patched libraries. Test malformed files and cancellation.
- Guard against formula injection in text imported into spreadsheets; emit literal strings unless an approved formula is required. Reconcile results without assuming cached formulas are current.
- Store artifacts separately; use temporary output and publish only after validation. No destructive changes to input documents.
- Explicitly disclose whether a cloud model will receive document-derived data and obtain authorization; local models remain an option if they can reliably generate structured plans. Keep credentials out of prompts and logs.
- Provide a delete-learning-data option, audit records and a user-visible record of what was shared with the model. Do not retain sensitive examples unnecessarily.

## 12. Acceptance criteria

| Test | Expected result |
|---|---|
| Learn from original and completed Excel | Produces editable candidate rules and typed plan |
| Ambiguous before/after pair | Asks focused clarification, does not silently guess |
| Replay example | Semantic outputs match approved example |
| Fresh workbook with different rows | Correct group totals and reconciliation |
| Missing column or unknown status | Stops with actionable diagnostic |
| Unsupported operation or external link | Rejected or explicitly flagged before execution |
| Malicious cell instruction | Cannot invoke shell or access unrelated files |
| Unauthorized path / symlink | Access denied |
| Word template with new values | Correct placeholders and preserved structure |
| PowerPoint template with new values | Correct named shapes, valid package, visual review |
| Scheduled repeat | Runs approved version once, records result |
| App closed or machine asleep | Does not falsely claim execution; missed run visible |
| Failed validation | No published final artifact; originals unchanged |
| Windows and macOS | Same fixture results with supported .NET libraries |

## 13. Implementation sequence and estimates

| Stage | Deliverable | Developer days (rough) |
|---|---|---:|
| 1 | Excel inspector, semantic diff and bounded summaries | 2–3 |
| 2 | Typed Excel plan, deterministic executor and fixtures | 2–3 |
| 3 | Learning prompt, clarification, review and validation | 2–3 |
| 4 | Avalonia wizard, skill persistence and routine integration | 2–3 |
| 5 | Word template support | 2–3 |
| 6 | PowerPoint template support | 3–5 |
| 7 | Cross-platform security and regression testing | 2–4 |

**Planning estimate:** 8–12 developer days for an Excel prototype through stage 4, 15–24 for all stages. Estimates assume the existing agent, scheduler, GUI and provider infrastructure are reusable; production hardening, compatibility and visual QA can take longer. The first Excel demo should precede all Word/PowerPoint work.

## 14. Product validation and monetization

Ship initially to a small private beta, with no payment integration needed for the experiment. Measure first successful teaching session, fresh-input pass rate, time to first useful output, manual correction rate, repeat usage and scheduled runs that finish successfully. Suggested initial success hypothesis: at least 8 of 10 curated supported Excel workflows pass fresh-input validation after at most one clarification; validate separately with real users, not just fixtures.

For commercial release, keep the earlier Litos Work BYOM pricing hypothesis (₹499/month or $9/month Pro). Free users can save 3 skills and 2 routines; Pro can save more or unlimited subject to operational limits. Dodo Payments remains the proposed billing/licensing provider; verify current product and licensing terms before implementation. Local/BYOM execution avoids direct Litos inference costs, not all support or operating costs. Do not paywall basic validation, privacy controls or access to users' own artifacts.

## 15. Future roadmap

Only after proving repeatable Office workflows: richer Excel operations, reusable Word and PowerPoint templates, cross-document workflow chains, optional Office.js add-ins for live snapshots, and curated workflow packages in the Buzzy Store. Screen observation and desktop replay are separate future capabilities and are not required for this MVP.

## 16. Implementation references

- ClosedXML: https://github.com/ClosedXML/ClosedXML
- Open XML SDK: https://github.com/dotnet/Open-XML-SDK
- Avalonia: https://docs.avaloniaui.net/
- Litos: https://litosai.dev/

These links are starting points for implementation; package/API versions and support details should be verified during development.

## Definition of done

A user can teach Buddy a supported Excel task with two example files and optional text, review the inferred rules, test on genuinely new input, inspect a validated output, save the approved skill and repeat it manually or on a schedule. No Office add-in, recording or arbitrary code execution is needed. Extend that exact experience to Word and PowerPoint templates after the Excel release gate.
