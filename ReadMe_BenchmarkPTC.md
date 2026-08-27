# Benchmarking the PTC Persistent Kernel — Procedure

Companion evaluation-plan document for
[ReadMe_PTCPersistentKernel.md](ReadMe_PTCPersistentKernel.md), specifically the
"first benchmark, before Milestone 2" checkpoint (§8.6, line ~1459 as of this writing) and
the deferred item named in §8.8's resolved-review-pass note: *"a full benchmark task suite
... §1.1 defers these to a companion evaluation-plan document once Milestone 1 exists to
measure."* This is that document.

Status: **procedure defined, not yet run.** No results exist yet under this document.

## 1. What this measures

Per §1.1's hypotheses, in priority order for this checkpoint:

- **H1 — context/token reduction.** Does kernel mode send substantially less raw tool output
  back to the model than the sequential path, for the same task?
- **H2 — round reduction.** Does kernel mode complete in fewer `AgentLoop.RunTurnAsync`
  round-trips than the sequential path, for the same task?
- **H4 — Roslyn reliability.** Do model-generated C# scripts compile and run successfully at
  an acceptable rate, and recover cleanly when they don't?
- **H5 — workload dependence.** Is any advantage found uniform across task shapes, or
  concentrated in some and absent/negative in others? Reported *per shape*, never pooled —
  this is a hard requirement of §1.1, not a nice-to-have breakdown.

H3 (persistence value beyond one-shot execution) is **not** targeted by this checkpoint — it
needs multi-round-within-session reuse scenarios, which is a follow-on task-design question,
not something the SWE-bench-derived tasks below naturally exercise. Out of scope here.

This is a **go/no-go checkpoint**, not a publishable benchmark result. Per §8.6: *"Do not
proceed to Milestone 2 on schedule if this shows no advantage on any measured task
shape."* Sample sizes below (15-20 instances) are sized for a directional read at that
decision, not for statistical power in the way a released number would need.

## 2. Benchmark source: SWE-bench Lite

**Dataset**: [`princeton-nlp/SWE-bench_Lite`](https://huggingface.co/datasets/princeton-nlp/SWE-bench_Lite)
on Hugging Face, free, no signup required beyond a HF account for `datasets` access. 300
instances, each a real closed GitHub issue against one of 11 popular Python repos (Django,
sympy, scikit-learn, matplotlib, astropy, etc.), paired with the base commit, the issue text,
a golden reference patch (never shown to the model — used only for the dataset's own
provenance, not for scoring here), and a `FAIL_TO_PASS`/`PASS_TO_PASS` test-ID list used to
verify a candidate patch.

**Why this dataset despite the language mismatch**: the kernel's job is *orchestrating
tools* (`read_file`, `search_code`, `shell`, etc.) via C#/Roslyn scripts, not writing the
target repo's own language. SWE-bench's task *shape* — locate relevant files across a real
repo, understand an issue, make a coordinated multi-file change, pass held-out tests — is
exactly §1's motivating case ("read file A, and if it imports X also read file B"), and no
free benchmark tests "programmatic vs. sequential tool-calling" directly, so a real
multi-step coding-task suite is the closest available proxy. The repo being Python is
irrelevant to what's being measured.

**No harness drives Litos for you.** SWE-bench's own harness scores *patches*; it has no
concept of an interactive agent UI. This procedure is manual-drive / automated-measure: you
hand-run Litos.Gui, but every metric is extracted from files Litos already writes, with zero
new code instrumented into the app itself.

### 2.1 One-time setup: cache the dataset locally

```bash
pip install datasets
python -c "
from datasets import load_dataset
import json
ds = load_dataset('princeton-nlp/SWE-bench_Lite', split='test')
data = {r['instance_id']: dict(r) for r in ds}
json.dump(data, open('swebench_lite.json', 'w'))
"
```

Do this once; `run_benchmark.py` (§5) reads from this local cache rather than hitting HF on
every run.

## 3. Sample: 15-20 instances, stratified by task shape

Pull from the cached dataset, sorted into §1.1's three task-shape categories (the same
categories Hybrid-mode routing would eventually key on, per §1's discussion):

| Shape | Definition | Target count |
|---|---|---|
| `single_file` | Golden patch touches exactly one file | 5-7 |
| `multi_file_conditional` | Golden patch touches 2-4 files, and the issue text implies a decision ("if X, also update Y") rather than a fixed known set | 5-7 |
| `fan_out` | Golden patch touches many files, or clearly requires enumerating/searching across a package before editing (e.g. a rename, a deprecation propagated across call sites) | 5-7 |

Classify using each instance's own `patch` field (file count is a cheap proxy; skim the
issue text to confirm the shape actually matches the category's *intent*, not just the file
count — a 3-file patch that's really "apply the same one-line fix in 3 places" is
`fan_out`-shaped even if it could numerically pass for `multi_file_conditional`). Pick
instances you can plausibly complete by hand in a few minutes each — SWE-bench Lite was
itself curated for lower setup/dependency friction, but some instances still assume a
build/test environment that's painful to reproduce locally; skip and replace those rather
than forcing them.

Record the chosen instance IDs and their assigned shape in `manifest.json` (§5) as you go.

## 4. Per-instance procedure (the manual part)

Repeat this twice per instance — once per toggle state — always in this order (OFF then ON,
so a warm-cache/practice effect from the first run doesn't systematically favor the same
side across every instance):

1. **Fresh checkout.** `git clone` the instance's `repo` and `git checkout` its
   `base_commit` into a clean scratch directory. Never reuse a directory across runs — the
   OFF run's edits must not be visible to the ON run or vice versa.
2. **New Litos.Gui session.** `/new`, pointed at that checkout directory. Set the kernel
   toggle to the state for this run (OFF first, then ON on the second pass).
3. **Paste the issue text** (the instance's `problem_statement` field) as the user's first
   turn. Let the turn run to completion without intervening — no manual hints, no follow-up
   nudges. If it stalls or errors out irrecoverably, record that as a failure for this run
   rather than coaching it through.
4. **Save the diff.** In the checkout directory: `git diff > patch.txt`. This is the
   candidate patch, saved *before* touching anything else.
5. **Note the session folder.** Litos writes each session under
   `%USERPROFILE%\.litos\sessions\local\{sessionId}\` (`SessionOwner.Local`'s fixed
   `"local"` segment — confirmed against `src/Litos.Agent/Session/SessionOwner.cs` and
   `JsonlTranscriptStore`'s default root). Find the session you just ran via `/resume`'s
   session list in the UI, or by sorting that folder by modification time. You need this
   path, not its contents — extraction is automated (§6).
6. **Record both** (`session_dir`, `patch_path`) into `manifest.json` under this instance's
   `off_run` or `on_run`, per `manifest.example.json`'s shape.

Nothing here needs new tooling — steps 1-4 are checkout/copy-paste/toggle/`git diff`, repeated
per (instance × toggle) pair.

## 5. Tooling: `benchmarks/ptc_kernel/`

| File | Role |
|---|---|
| `extract_run_metrics.py` | Reads one session's `transcript.jsonl` + `audit.jsonl`, returns tokens/rounds/wall-clock/kernel-eval counts. Runnable standalone for a one-off check. |
| `run_benchmark.py` | Reads `manifest.json`, calls the extractor per (instance, toggle) pair, optionally applies the saved patch to a fresh checkout and scores pass/fail, writes `results.csv`. |
| `manifest.example.json` | Template — copy to `manifest.json` and fill in as you complete each instance's runs (§4). |

### 5.1 What gets extracted, and from where

Both files are read as-is, no new instrumentation added to `Litos.Gui`/`Litos.Kernel` — the
data already exists because of work already landed in Milestones 1-2:

- **`transcript.jsonl`** (`{sessionId}/transcript.jsonl`, camelCase JSON per
  `TranscriptJsonContext`'s `JsonKnownNamingPolicy.CamelCase`): each assistant-turn entry
  carries a non-null `usage: {inputTokens, outputTokens}` — real provider-reported usage
  (`AgentLoop.cs`'s `TranscriptEntry.FromMessage(m.Message, m.Usage)`), not an estimate.
  Counting entries with non-null `usage` **is** the round count; summing their token fields
  is the token count. Entry timestamps give wall-clock duration.
- **`audit.jsonl`** (`{sessionId}/audit.jsonl`, kernel-mode runs only — absent entirely for
  OFF runs since the kernel subprocess never starts): one line per
  `KernelSession.AppendAudit` call, format `"<ISO-8601 timestamp> <json>"` — note the
  timestamp *prefix*, this is not pure JSONL. Gives eval count (`eval_start`), bridged
  tool-call count (`tool_call`, calls made *inside* a kernel script that never became their
  own transcript round — this is the concrete token/round savings mechanism H1/H2 are
  testing), and reliability signals (`eval_timeout`, `eval_cancelled`, `eval_end`'s
  `IsError`).

### 5.2 Running it

```bash
cd benchmarks/ptc_kernel
cp manifest.example.json manifest.json    # then fill in as you complete runs, per §4

# Metrics only, no pass/fail scoring (fastest — use this while you're still mid-run):
python run_benchmark.py manifest.json --skip-scoring --out results_partial.csv

# Full run once all instances are recorded, scoring via the lightweight pytest fallback:
python run_benchmark.py manifest.json --swe-bench-lite-cache swebench_lite.json --out results.csv

# Or, for scoring that exactly matches the published SWE-bench methodology (needs
# `pip install swebench` and Docker running locally — first run per repo pulls a
# multi-GB image):
python run_benchmark.py manifest.json --swe-bench-lite-cache swebench_lite.json --use-harness --out results.csv
```

`results.csv` columns: `shape, instance_id, toggle, pass, rounds, input_tokens,
output_tokens, total_tokens, wall_clock_seconds, kernel_evals, kernel_eval_errors,
kernel_eval_timeouts, kernel_eval_cancelled, kernel_bridged_tool_calls, session_dir, notes`.

## 6. Analysis — how to read `results.csv`

1. **Group by `shape`, never pool.** For each shape, compare the OFF row against the ON row
   per instance (paired, not independent samples — same task, same base commit, only the
   toggle differs). §1.1/H5 requires this breakdown explicitly; an average across shapes can
   hide a real regression on the common case behind a win on an uncommon one.
2. **Within each shape**, look at:
   - Median/mean `total_tokens` and `rounds`, OFF vs. ON.
   - `pass` rate, OFF vs. ON — a token/round win paired with a lower pass rate is not a win,
     it's a correctness regression (H4 territory).
   - `kernel_eval_errors` + `kernel_eval_timeouts` as a share of `kernel_evals` — this is the
     direct H4 reliability number, independent of whether the *task* ultimately passed (a
     script can error, get corrected in the next eval, and the task still passes).
3. **The go/no-go call**: per §8.6's own framing, if *no* shape shows a token or round
   advantage for ON over OFF at comparable-or-better pass rates, the honest next step is
   revisiting the design (which shapes if any justify kernel mode's added complexity;
   whether Hybrid's routing question, deferred in §1, should be reopened instead) — not
   proceeding to Milestone 3 on schedule regardless.
4. **Record the outcome back in [ReadMe_PTCPersistentKernel.md](ReadMe_PTCPersistentKernel.md)'s
   own status log** once this checkpoint is run — the checkbox at line ~98
   ("Checkpoint — first benchmark") and the go/no-go framing at §8.6/line ~1468 both expect
   this document's result to close that loop, not just live here separately.

## 7. Known limitations of this procedure

- **Manual drive, not a scripted harness.** Every run is hand-executed through the real
  `Litos.Gui` UI; there is no automated agent-driving harness yet. This bounds sample size
  to what's practical to hand-run (§1's 15-20), not a statistically large suite.
- **Single run per (instance, toggle) pair by default.** If a provider's sampling
  introduces run-to-run variance in round count or tool-call sequencing even at low
  temperature, a single pass can't distinguish real effect from noise. Repeat 2-3× for any
  instance whose result looks surprising before trusting it.
- **pytest fallback scoring is less faithful than the real harness.** It runs the listed
  tests directly in the checkout's own ambient environment rather than the exact pinned
  Docker image SWE-bench's leaderboard uses — fine for a go/no-go read, not for a number
  you'd publish or compare against published SWE-bench scores.
- **H3 (persistence value) is untested by this design.** These are one-shot issue-fix tasks;
  they don't exercise a kernel reused productively across multiple rounds within one
  session. A separate task design would be needed to test H3 specifically.
