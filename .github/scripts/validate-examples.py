#!/usr/bin/env python3
"""Validate every examples/**/ectype-class.json against docs/schemas/ectype-class.schema.json."""
import json
import sys
from pathlib import Path

from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parents[2]


def main():
    schema = json.loads((ROOT / "docs/schemas/ectype-class.schema.json").read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema)
    manifests = sorted((ROOT / "examples").rglob("ectype-class.json"))
    if not manifests:
        print("no ectype-class.json found under examples/")
        return 1
    bad = 0
    for path in manifests:
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