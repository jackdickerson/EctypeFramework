#!/usr/bin/env python3
"""Check that the framework-owned stat declarations, docs/framework-stats.json and the
framework rows of docs/stat-names.csv agree.

- Every `public const string` stat name in src/FrameworkStats.cs (all of them except
  `Owner`) has an entry in the JSON, and every JSON entry names one of those constants.
- The framework rows of docs/stat-names.csv have the same names and the same ids as the JSON.
"""
import csv
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "src" / "FrameworkStats.cs"
JSON_PATH = ROOT / "docs" / "framework-stats.json"
CSV_PATH = ROOT / "docs" / "stat-names.csv"

CONST = re.compile(r'public\s+const\s+string\s+(\w+)\s*=\s*"([^"]*)"\s*;')


def main():
    errors = []

    consts = {m.group(1): m.group(2) for m in CONST.finditer(SRC.read_text(encoding="utf-8"))}
    consts.pop("Owner", None)
    declared = set(consts.values())

    doc = json.loads(JSON_PATH.read_text(encoding="utf-8"))
    entries = {}
    for entry in doc["stats"]:
        name = entry.get("name")
        if name in entries:
            errors.append("framework-stats.json lists '%s' twice" % name)
        entries[name] = entry

    for name in sorted(declared - set(entries)):
        errors.append("src/FrameworkStats.cs declares '%s' but framework-stats.json has no entry" % name)
    for name in sorted(set(entries) - declared):
        errors.append("framework-stats.json entry '%s' names no constant in src/FrameworkStats.cs" % name)

    rows = {}
    with CSV_PATH.open(encoding="utf-8", newline="") as f:
        for row in csv.DictReader(f):
            if row["Source"] == "framework":
                rows[row["Name"]] = row["Id"]

    for name in sorted(set(entries) - set(rows)):
        errors.append("framework-stats.json entry '%s' has no framework row in stat-names.csv" % name)
    for name in sorted(set(rows) - set(entries)):
        errors.append("stat-names.csv framework row '%s' has no entry in framework-stats.json" % name)
    for name in sorted(set(entries) & set(rows)):
        if str(entries[name].get("id")) != rows[name]:
            errors.append("'%s': framework-stats.json id %s, stat-names.csv id %s"
                          % (name, entries[name].get("id"), rows[name]))

    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    print("check-stats: %d framework stats agree across source, JSON and CSV" % len(entries))
    return 0


if __name__ == "__main__":
    sys.exit(main())
