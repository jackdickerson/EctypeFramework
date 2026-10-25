#!/usr/bin/env python3
"""Validate every examples/**/ectype-role.json against docs/schemas/ectype-role.schema.json and
every examples/**/*ectype-stats.json against docs/schemas/ectype-stats.schema.json."""
import json
import sys
from pathlib import Path

from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parents[2]

SETS = [
    ("docs/schemas/ectype-role.schema.json", "ectype-role.json"),
    ("docs/schemas/ectype-stats.schema.json", "*ectype-stats.json"),
]


def main():
    bad = 0
    for schema_path, pattern in SETS:
        schema = json.loads((ROOT / schema_path).read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
        validator = Draft202012Validator(schema)
        documents = sorted((ROOT / "examples").rglob(pattern))
        if not documents:
            print(f"no {pattern} found under examples/")
            return 1
        for path in documents:
            name = path.relative_to(ROOT).as_posix()
            errors = sorted(validator.iter_errors(json.loads(path.read_text(encoding="utf-8"))),
                            key=lambda e: list(e.absolute_path))
            if errors:
                bad += 1
                for e in errors:
                    where = "/".join(str(p) for p in e.absolute_path) or "(root)"
                    print(f"{name}: {where}: {e.message}")
            else:
                print(f"{name}: ok")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
