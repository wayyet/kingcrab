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
        description="Root-level wrapper for validating skill projection contract index files."
    )
    parser.add_argument("paths", nargs="*", help="contract-index.json paths to validate.")
    parser.add_argument("--schema-path", dest="schema_path", help="Optional schema path.")
    args = parser.parse_args()

    repo_root = Path(__file__).resolve().parent.parent
    validator_path = repo_root / "src" / "OpenClaw.Gateway" / "skills" / "ncrew-ontology" / "scripts" / "validate-projection.py"

    if not validator_path.exists():
        raise FileNotFoundError(f"Validator script not found: {validator_path}")

    current_base = Path.cwd().resolve()
    default_schema_path = repo_root / "docs" / "skill-projection-contract-index.schema.json"
    default_input_path = repo_root / "src" / "OpenClaw.Gateway" / "skills" / "software-developer" / "contracts" / "projections" / "ncrew-ontology" / "contract-index.json"

    invoke_args = [
        sys.executable,
        str(validator_path),
    ]

    input_paths = list(args.paths) or [str(default_input_path)]
    for input_path in input_paths:
        invoke_args.append(str(resolve_absolute_path(input_path, current_base)))

    resolved_schema_path = resolve_absolute_path(args.schema_path, current_base) if args.schema_path else default_schema_path.resolve()
    invoke_args.extend([
        "--schema-path",
        str(resolved_schema_path),
    ])

    completed = subprocess.run(invoke_args, check=False)
    return completed.returncode


if __name__ == "__main__":
    sys.exit(main())