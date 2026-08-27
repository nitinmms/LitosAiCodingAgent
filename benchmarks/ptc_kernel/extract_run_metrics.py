#!/usr/bin/env python3
"""
Extracts token/round/reliability metrics from a single Litos.Gui session's
JSONL files, for the PTC-persistent-kernel benchmark (ReadMe_PTCPersistentKernel.md).

Reads:
  {sessionDir}/transcript.jsonl   -- always present. One row per TranscriptEntry.
  {sessionDir}/audit.jsonl        -- kernel-mode only. One row per KernelSession.AppendAudit call.

Writes: nothing by itself -- see run_benchmark.py, which calls this per session
and assembles the CSV. This module also runs standalone for a quick one-off check:

  python extract_run_metrics.py <sessionDir> [--toggle on|off] [--instance-id X]

Field shapes are read directly off the C# records as of this writing:
  - TranscriptEntry: Kind, Timestamp, Message, CallId, Usage {InputTokens, OutputTokens}, ...
    (src/Litos.Agent/Session/TranscriptEntry.cs)
  - Usage is only non-null on the assistant-message entry written per model round
    (src/Litos.Agent/AgentLoop.cs:137) -- so counting non-null-Usage entries IS the round count.
  - audit.jsonl records: each LINE is "<ISO-8601 timestamp> <json>" (a timestamp prefix
    followed by a space, NOT pure JSONL -- see KernelSession.AppendAudit's
    `DateTimeOffset.UtcNow.ToString("O") + " " + line`). The JSON itself is an anonymous
    object serialized with default System.Text.Json casing (whatever casing the C# anonymous
    object's property was declared with -- e.g. `evt`, `requestId`, `codeLength` are already
    lowercase-first because that's how KernelSession.cs wrote the anonymous object, but
    `IsError`/`Truncated` on eval_end are PascalCase because that's how those particular
    properties were named at the call site). Events: eval_start/eval_end/eval_timeout/
    eval_cancelled/tool_call/kernel_started/reset (src/Litos.Kernel/KernelSession.cs).
"""
import argparse
import json
import sys
from dataclasses import dataclass, field
from pathlib import Path


@dataclass
class RunMetrics:
    session_dir: str
    toggle: str = ""
    instance_id: str = ""

    # From transcript.jsonl
    rounds: int = 0                 # count of assistant entries carrying non-null Usage
    input_tokens: int = 0           # sum of Usage.InputTokens across those entries
    output_tokens: int = 0          # sum of Usage.OutputTokens across those entries
    wall_clock_seconds: float = 0.0 # last entry timestamp - first entry timestamp
    entry_count: int = 0

    # From audit.jsonl (kernel-mode only; zero/blank for OFF runs)
    kernel_evals: int = 0
    kernel_eval_errors: int = 0
    kernel_eval_timeouts: int = 0
    kernel_eval_cancelled: int = 0
    kernel_bridged_tool_calls: int = 0

    notes: list = field(default_factory=list)

    @property
    def total_tokens(self) -> int:
        return self.input_tokens + self.output_tokens

    def as_row(self) -> dict:
        return {
            "instance_id": self.instance_id,
            "toggle": self.toggle,
            "session_dir": self.session_dir,
            "rounds": self.rounds,
            "input_tokens": self.input_tokens,
            "output_tokens": self.output_tokens,
            "total_tokens": self.total_tokens,
            "wall_clock_seconds": round(self.wall_clock_seconds, 1),
            "kernel_evals": self.kernel_evals,
            "kernel_eval_errors": self.kernel_eval_errors,
            "kernel_eval_timeouts": self.kernel_eval_timeouts,
            "kernel_eval_cancelled": self.kernel_eval_cancelled,
            "kernel_bridged_tool_calls": self.kernel_bridged_tool_calls,
            "notes": "; ".join(self.notes),
        }


def _parse_timestamp(ts: str):
    # DateTimeOffset serializes as ISO-8601; Python's fromisoformat needs the
    # trailing "Z" normalized to "+00:00" on versions before 3.11.
    if ts.endswith("Z"):
        ts = ts[:-1] + "+00:00"
    from datetime import datetime
    return datetime.fromisoformat(ts)


def extract_transcript_metrics(transcript_path: Path, m: RunMetrics) -> None:
    if not transcript_path.exists():
        m.notes.append(f"missing transcript.jsonl at {transcript_path}")
        return

    first_ts = None
    last_ts = None

    with transcript_path.open("r", encoding="utf-8") as f:
        for lineno, line in enumerate(f, start=1):
            line = line.strip()
            if not line:
                continue
            try:
                entry = json.loads(line)
            except json.JSONDecodeError as e:
                m.notes.append(f"transcript.jsonl:{lineno} bad JSON ({e})")
                continue

            m.entry_count += 1

            ts_raw = entry.get("Timestamp") or entry.get("timestamp")
            if ts_raw:
                try:
                    ts = _parse_timestamp(ts_raw)
                    if first_ts is None:
                        first_ts = ts
                    last_ts = ts
                except ValueError:
                    pass

            usage = entry.get("Usage") or entry.get("usage")
            if usage:
                m.rounds += 1
                m.input_tokens += int(usage.get("InputTokens", usage.get("inputTokens", 0)) or 0)
                m.output_tokens += int(usage.get("OutputTokens", usage.get("outputTokens", 0)) or 0)

    if first_ts and last_ts:
        m.wall_clock_seconds = (last_ts - first_ts).total_seconds()

    if m.rounds == 0:
        m.notes.append("no entries with non-null Usage found -- check field-name casing "
                        "matches the actual JSON (System.Text.Json default vs PascalCase)")


def extract_audit_metrics(audit_path: Path, m: RunMetrics) -> None:
    if not audit_path.exists():
        # Expected for OFF (sequential-path) runs -- the kernel never started.
        return

    with audit_path.open("r", encoding="utf-8") as f:
        for lineno, line in enumerate(f, start=1):
            line = line.strip()
            if not line:
                continue
            # Each line is "<ISO-8601 timestamp> <json>", not pure JSON -- split off the
            # timestamp prefix on the first space before parsing (KernelSession.AppendAudit).
            _, _, json_part = line.partition(" ")
            if not json_part:
                m.notes.append(f"audit.jsonl:{lineno} no timestamp-prefix space found, skipping")
                continue
            try:
                rec = json.loads(json_part)
            except json.JSONDecodeError as e:
                m.notes.append(f"audit.jsonl:{lineno} bad JSON ({e})")
                continue

            evt = rec.get("evt")
            if evt == "eval_start":
                m.kernel_evals += 1
            elif evt == "eval_end" and rec.get("IsError") is True:
                m.kernel_eval_errors += 1
            elif evt == "eval_timeout":
                m.kernel_eval_timeouts += 1
            elif evt == "eval_cancelled":
                m.kernel_eval_cancelled += 1
            elif evt == "tool_call":
                m.kernel_bridged_tool_calls += 1


def extract(session_dir: Path, toggle: str = "", instance_id: str = "") -> RunMetrics:
    m = RunMetrics(session_dir=str(session_dir), toggle=toggle, instance_id=instance_id)
    extract_transcript_metrics(session_dir / "transcript.jsonl", m)
    extract_audit_metrics(session_dir / "audit.jsonl", m)
    return m


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("session_dir", type=Path, help="Path to {sessionId}/ folder "
                     "(containing transcript.jsonl and optionally audit.jsonl)")
    ap.add_argument("--toggle", choices=["on", "off"], default="", help="Record which toggle state this run used")
    ap.add_argument("--instance-id", default="", help="SWE-bench instance id this run corresponds to")
    args = ap.parse_args()

    if not args.session_dir.is_dir():
        print(f"error: {args.session_dir} is not a directory", file=sys.stderr)
        return 1

    m = extract(args.session_dir, toggle=args.toggle, instance_id=args.instance_id)
    print(json.dumps(m.as_row(), indent=2))
    if m.notes:
        print("\nNOTES:", file=sys.stderr)
        for n in m.notes:
            print(f"  - {n}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
