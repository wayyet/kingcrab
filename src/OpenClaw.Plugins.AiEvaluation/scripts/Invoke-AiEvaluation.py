#!/usr/bin/env python3
"""
One-stop AI evaluation pipeline orchestration script.

Executes the standard five-step evaluation flow:
1. Fetch test cases → 2. Send to target sandbox → 3. Read execution trace →
4. Query scoring criteria → 5. Generate evaluation report

Usage:
    python Invoke-AiEvaluation.py --config-path ./evaluation-config.json --output-dir ./reports/
"""

import argparse
import json
import os
import subprocess
import sys
from datetime import datetime


def run_step(step_name: str, args: list[str]) -> tuple[int, str, str]:
    """Run a subprocess step and return (exit_code, stdout, stderr)."""
    print(f"\n[Step] {step_name}")
    try:
        result = subprocess.run(args, capture_output=True, text=True, timeout=600)
        if result.returncode == 0:
            print(f"  OK")
        else:
            print(f"  FAILED (exit {result.returncode}): {result.stderr[:200]}")
        return result.returncode, result.stdout.strip(), result.stderr.strip()
    except subprocess.TimeoutExpired:
        print(f"  TIMEOUT")
        return -1, "", "Timeout"
    except Exception as e:
        print(f"  ERROR: {e}")
        return -1, "", str(e)


def main():
    parser = argparse.ArgumentParser(description="AI Evaluation Pipeline")
    parser.add_argument("--config-path", required=True, help="Evaluation config JSON file")
    parser.add_argument("--testcase-path", default="", help="Test case file or directory")
    parser.add_argument("--output-dir", required=True, help="Output directory for results")
    args = parser.parse_args()

    # Load config
    with open(args.config_path, "r", encoding="utf-8") as f:
        config = json.load(f)

    os.makedirs(args.output_dir, exist_ok=True)
    timestamp = datetime.utcnow().strftime("%Y%m%d-%H%M%S")
    script_dir = os.path.dirname(os.path.abspath(__file__))

    endpoints = config.get("endpoints", {})
    eval_cfg = config.get("evaluation", {})
    timeout = str(eval_cfg.get("timeoutSeconds", 120))

    results = {}

    # Step 1: Fetch test cases
    if args.testcase_path and os.path.exists(args.testcase_path):
        print("[Step 1/5] Using provided test cases")
        results["testcases"] = []
        if os.path.isdir(args.testcase_path):
            for f in sorted(os.listdir(args.testcase_path)):
                if f.endswith(".json"):
                    with open(os.path.join(args.testcase_path, f), "r") as tf:
                        results["testcases"].append(tf.read())
        else:
            with open(args.testcase_path, "r") as tf:
                results["testcases"] = [tf.read()]
    elif endpoints.get("generator", {}).get("wsUrl"):
        gen = endpoints["generator"]
        code, out, err = run_step("1/5 Fetch test cases", [
            sys.executable, os.path.join(script_dir, "Send-SandboxMessage.py"),
            "--ws-url", gen["wsUrl"],
            "--message", "Generate structured test cases for evaluation.",
            "--timeout", timeout
        ])
        results["testcases"] = [out] if code == 0 else []

    # Step 2: Send to target
    target_responses = []
    if endpoints.get("target", {}).get("wsUrl"):
        tgt = endpoints["target"]
        tgt_timeout = str(tgt.get("requestTimeoutSeconds", timeout))
        for i, tc in enumerate(results.get("testcases", [])):
            tc_file = os.path.join(args.output_dir, f"tc-{i:03d}.json")
            with open(tc_file, "w", encoding="utf-8") as f:
                f.write(tc if isinstance(tc, str) else json.dumps(tc))
            code, out, err = run_step(f"2/5 Send test case {i}", [
                sys.executable, os.path.join(script_dir, "Send-SandboxMessage.py"),
                "--ws-url", tgt["wsUrl"],
                "--testcase-file", tc_file,
                "--timeout", tgt_timeout
            ])
            if code == 0:
                target_responses.append(out)
        results["targetResponses"] = target_responses

    # Step 3: Read trace
    if endpoints.get("trace", {}).get("wsUrl"):
        tr = endpoints["trace"]
        code, out, err = run_step("3/5 Read trace", [
            sys.executable, os.path.join(script_dir, "Read-SandboxTrace.py"),
            "--ws-url", tr["wsUrl"],
            "--max-entries", "200",
            "--timeout", timeout
        ])
        if code == 0:
            trace_file = os.path.join(args.output_dir, f"trace-{timestamp}.json")
            with open(trace_file, "w", encoding="utf-8") as f:
                f.write(out)
            results["trace"] = trace_file

    # Step 4: Query criteria
    if endpoints.get("ontology", {}).get("wsUrl"):
        ont = endpoints["ontology"]
        code, out, err = run_step("4/5 Query criteria", [
            sys.executable, os.path.join(script_dir, "Get-ScoringCriteria.py"),
            "--ws-url", ont["wsUrl"],
            "--timeout", timeout
        ])
        if code == 0:
            criteria_file = os.path.join(args.output_dir, f"criteria-{timestamp}.json")
            with open(criteria_file, "w", encoding="utf-8") as f:
                f.write(out)
            results["criteria"] = criteria_file

    # Step 5: Generate report
    if endpoints.get("evalReport", {}).get("wsUrl"):
        rep = endpoints["evalReport"]
        code, out, err = run_step("5/5 Generate report", [
            sys.executable, os.path.join(script_dir, "New-EvaluationReport.py"),
            "--ws-url", rep["wsUrl"],
            "--trace-summary", f"See trace-{timestamp}.json",
            "--output-path", os.path.join(args.output_dir, f"evaluation-{timestamp}.json"),
            "--timeout", timeout
        ])
        results["report"] = os.path.join(args.output_dir, f"evaluation-{timestamp}.json")

    # Summary
    summary = {
        "pipeline": "ai-evaluation",
        "timestamp": timestamp,
        "configPath": args.config_path,
        "outputDir": args.output_dir,
        "steps": {
            "testcases": {"count": len(results.get("testcases", []))},
            "targetResponses": {"count": len(results.get("targetResponses", []))},
            "trace": {"saved": "trace" in results},
            "criteria": {"saved": "criteria" in results},
            "report": {"path": os.path.join(args.output_dir, f"evaluation-{timestamp}.json")}
        }
    }

    summary_file = os.path.join(args.output_dir, f"pipeline-summary-{timestamp}.json")
    with open(summary_file, "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)

    print(f"\n=== Pipeline Complete ===")
    print(json.dumps(summary, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
