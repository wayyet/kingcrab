from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path


def resolve_absolute_path(path_text: str, base_path: Path) -> Path:
    path = Path(path_text)
    if path.is_absolute():
        return path.resolve()
    return (base_path / path).resolve()


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Root-level wrapper for the ontology_extraction projection validator."
    )
    parser.add_argument("paths", nargs="*", help="Projection JSON paths to validate.")
    parser.add_argument("--schema-path", dest="schema_path", help="Optional schema path.")
    args = parser.parse_args()

    repo_root = Path(__file__).resolve().parent.parent
    validator_path = repo_root / "src" / "OpenClaw.Gateway" / "skills" / "ontology_extraction" / "scripts" / "validate-projection.py"

    if not validator_path.exists():
        raise FileNotFoundError(f"Validator script not found: {validator_path}")

    current_base = Path.cwd().resolve()
    invoke_args = [sys.executable, str(validator_path)]

    for input_path in args.paths:
        invoke_args.append(str(resolve_absolute_path(input_path, current_base)))

    if args.schema_path and args.schema_path.strip():
        invoke_args.extend([
            "--schema-path",
            str(resolve_absolute_path(args.schema_path, current_base)),
        ])

    completed = subprocess.run(invoke_args, check=False)
    return completed.returncode


if __name__ == "__main__":
    sys.exit(main())