#!/usr/bin/env python3
"""
Assembles the PTC-persistent-kernel benchmark CSV (ReadMe_PTCPersistentKernel.md's
Milestone-1 checkpoint, "before Milestone 2, hand-drive a handful of tasks...").

This does NOT drive Litos.Gui for you -- there's no harness for that yet. It assumes
you've already hand-run each (instance, toggle) pair through Litos.Gui per the manifest
below, and just assembles the resulting metrics + pass/fail into one CSV, per task shape.

Workflow
--------
1. Fill in manifest.json (see manifest.example.json alongside this file) with, per instance:
   - instance_id, shape (single_file / multi_file_conditional / fan_out)
   - repo, base_commit (from the SWE-bench Lite dataset)
   - for each toggle in {off, on}: session_dir (the {sessionId}/ folder Litos wrote),
     patch_path (the `git diff` you saved after that run)
2. Run this script. For each (instance, toggle) pair it:
   a. extracts token/round/kernel metrics via extract_run_metrics.py
   b. applies patch_path to a fresh checkout of repo@base_commit
   c. runs that instance's FAIL_TO_PASS + PASS_TO_PASS tests (via the `swebench` harness
      if installed, else a fallback that just runs pytest on the listed test IDs)
   d. records pass/fail
3. Writes results.csv, one row per (instance, toggle), grouped by shape.

Usage
-----
  pip install datasets swebench    # swebench optional -- see --no-harness fallback
  python run_benchmark.py manifest.json --swe-bench-lite-cache ./swebench_lite.json \\
      --workdir ./scratch_checkouts --out results.csv
"""
import argparse
import csv
import json
import shutil
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from extract_run_metrics import extract  # noqa: E402


def load_manifest(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def load_swebench_lite(cache_path: Path) -> dict:
    """
    Loads SWE-bench Lite instance metadata (repo, base_commit, FAIL_TO_PASS,
    PASS_TO_PASS) keyed by instance_id, from a local cache file you produce once via:

      python -c "
      from datasets import load_dataset
      import json
      ds = load_dataset('princeton-nlp/SWE-bench_Lite', split='test')
      data = {r['instance_id']: dict(r) for r in ds}
      json.dump(data, open('swebench_lite.json', 'w'))
      "

    Cached locally so this script doesn't require network/HF access on every run.
    """
    if not cache_path.exists():
        print(f"warning: {cache_path} not found -- pass/fail scoring will be skipped, "
              f"metrics-only mode. See load_swebench_lite()'s docstring to build it.",
              file=sys.stderr)
        return {}
    return json.loads(cache_path.read_text(encoding="utf-8"))


def fresh_checkout(repo: str, base_commit: str, workdir: Path, tag: str) -> Path:
    """
    repo is SWE-bench's "owner/name" form (e.g. "django/django"). Clones (or reuses
    a cached bare clone + worktree, if you want to speed this up later) and checks
    out base_commit into workdir/tag.
    """
    dest = workdir / tag
    if dest.exists():
        shutil.rmtree(dest)
    dest.parent.mkdir(parents=True, exist_ok=True)

    url = f"https://github.com/{repo}.git"
    subprocess.run(["git", "clone", "--quiet", url, str(dest)], check=True)
    subprocess.run(["git", "-C", str(dest), "checkout", "--quiet", base_commit], check=True)
    return dest


def apply_patch(checkout: Path, patch_path: Path) -> bool:
    result = subprocess.run(
        ["git", "-C", str(checkout), "apply", "--whitespace=nowarn", str(patch_path.resolve())],
        capture_output=True, text=True,
    )
    if result.returncode != 0:
        print(f"  patch failed to apply cleanly: {result.stderr.strip()}", file=sys.stderr)
        return False
    return True


def run_tests_fallback(checkout: Path, fail_to_pass: list, pass_to_pass: list) -> tuple:
    """
    Minimal fallback when the `swebench` harness/Docker images aren't set up: runs
    pytest directly on the union of FAIL_TO_PASS/PASS_TO_PASS test IDs inside the
    checkout's own environment. Less faithful than the real harness (no guaranteed
    matching env), but enough for a directional go/no-go read on a handful of instances.
    Prefer --use-harness for anything you'd actually publish numbers from.
    """
    all_tests = fail_to_pass + pass_to_pass
    if not all_tests:
        return (None, "no tests listed")

    result = subprocess.run(
        [sys.executable, "-m", "pytest", "-q", *all_tests],
        cwd=str(checkout), capture_output=True, text=True, timeout=600,
    )
    passed = result.returncode == 0
    tail = "\n".join(result.stdout.splitlines()[-15:])
    return (passed, tail)


def run_tests_harness(instance_id: str, patch_text: str) -> tuple:
    """
    Uses the real `swebench` package's evaluation entry point (Docker-based, matches
    the published leaderboard methodology exactly). Requires `pip install swebench`
    and Docker running locally. See github.com/SWE-bench/SWE-bench for image pull details
    -- first run per repo pulls a multi-GB image.
    """
    try:
        from swebench.harness.run_evaluation import main as swebench_main  # noqa
    except ImportError:
        return (None, "swebench package not installed -- pip install swebench, or use --no-harness")

    # swebench's CLI entry point expects a predictions file on disk keyed by instance_id;
    # shelling out to its own CLI is more stable across swebench versions than importing
    # its internals directly.
    import tempfile
    with tempfile.TemporaryDirectory() as td:
        preds_path = Path(td) / "predictions.jsonl"
        preds_path.write_text(json.dumps({
            "instance_id": instance_id,
            "model_patch": patch_text,
            "model_name_or_path": "litos-benchmark",
        }) + "\n", encoding="utf-8")

        result = subprocess.run(
            [sys.executable, "-m", "swebench.harness.run_evaluation",
             "--dataset_name", "princeton-nlp/SWE-bench_Lite",
             "--predictions_path", str(preds_path),
             "--max_workers", "1",
             "--run_id", f"litos-{instance_id}"],
            capture_output=True, text=True, timeout=1800,
        )
        passed = "resolved" in result.stdout.lower() and "\"resolved\": true" in result.stdout.lower()
        return (passed, result.stdout[-2000:])


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("manifest", type=Path)
    ap.add_argument("--swe-bench-lite-cache", type=Path, default=Path("swebench_lite.json"))
    ap.add_argument("--workdir", type=Path, default=Path("./scratch_checkouts"))
    ap.add_argument("--out", type=Path, default=Path("results.csv"))
    ap.add_argument("--use-harness", action="store_true",
                     help="Score pass/fail via the real Docker-based swebench harness "
                          "instead of the pytest fallback")
    ap.add_argument("--skip-scoring", action="store_true",
                     help="Only extract token/round metrics, skip patch-apply/test-run entirely")
    args = ap.parse_args()

    manifest = load_manifest(args.manifest)
    swe_meta = {} if args.skip_scoring else load_swebench_lite(args.swe_bench_lite_cache)
    args.workdir.mkdir(parents=True, exist_ok=True)

    rows = []
    for inst in manifest["instances"]:
        instance_id = inst["instance_id"]
        shape = inst.get("shape", "unspecified")
        print(f"=== {instance_id} ({shape}) ===")

        for toggle in ("off", "on"):
            run = inst.get(f"{toggle}_run")
            if not run:
                print(f"  [{toggle}] no run recorded in manifest, skipping")
                continue

            session_dir = Path(run["session_dir"])
            m = extract(session_dir, toggle=toggle, instance_id=instance_id)
            row = m.as_row()
            row["shape"] = shape
            row["pass"] = ""

            patch_path = run.get("patch_path")
            if not args.skip_scoring and patch_path and swe_meta.get(instance_id):
                meta = swe_meta[instance_id]
                checkout = fresh_checkout(meta["repo"], meta["base_commit"], args.workdir,
                                           tag=f"{instance_id}-{toggle}")
                applied = apply_patch(checkout, Path(patch_path))
                if not applied:
                    row["pass"] = "patch_failed"
                elif args.use_harness:
                    passed, _log = run_tests_harness(instance_id, Path(patch_path).read_text(encoding="utf-8"))
                    row["pass"] = passed if passed is not None else "harness_unavailable"
                else:
                    fail_to_pass = json.loads(meta.get("FAIL_TO_PASS", "[]"))
                    pass_to_pass = json.loads(meta.get("PASS_TO_PASS", "[]"))
                    passed, _log = run_tests_fallback(checkout, fail_to_pass, pass_to_pass)
                    row["pass"] = passed if passed is not None else "no_tests_listed"

            print(f"  [{toggle}] rounds={row['rounds']} tokens={row['total_tokens']} "
                  f"pass={row['pass']}")
            rows.append(row)

    if not rows:
        print("no rows produced -- check manifest.json", file=sys.stderr)
        return 1

    fieldnames = ["shape", "instance_id", "toggle", "pass", "rounds", "input_tokens",
                  "output_tokens", "total_tokens", "wall_clock_seconds",
                  "kernel_evals", "kernel_eval_errors", "kernel_eval_timeouts",
                  "kernel_eval_cancelled", "kernel_bridged_tool_calls",
                  "session_dir", "notes"]
    with args.out.open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for row in sorted(rows, key=lambda r: (r["shape"], r["instance_id"], r["toggle"])):
            writer.writerow(row)

    print(f"\nwrote {len(rows)} rows to {args.out}")
    print("Group by 'shape' and compare toggle=off vs toggle=on within each group -- "
          "never pool across shapes (H5, ReadMe_PTCPersistentKernel.md §1.1).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
