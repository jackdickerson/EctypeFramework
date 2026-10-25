#!/usr/bin/env python3
"""Check release/release.json parses, CHANGELOG.md and release/CHANGELOG.md each have a
heading for PLUGIN_VERSION, and CHANGELOG.md names the current NET_CONTRACT_VERSION as
"network contract <n>", so a contract bump cannot ship without a changelog line."""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def main():
    try:
        json.loads((ROOT / "release/release.json").read_text(encoding="utf-8-sig"))
        print("release/release.json: parses")
    except (OSError, ValueError) as e:
        print(f"release/release.json: {e}")
        return 1

    info = (ROOT / "src/PluginInfo.cs").read_text(encoding="utf-8")
    m = re.search(r'PLUGIN_VERSION\s*=\s*"([^"]+)"', info)
    if not m:
        print("src/PluginInfo.cs: PLUGIN_VERSION not found")
        return 1
    version = m.group(1)

    failed = False
    for name in ("CHANGELOG.md", "release/CHANGELOG.md"):
        changelog = (ROOT / name).read_text(encoding="utf-8")
        if re.search(r"^##\s+" + re.escape(version) + r"\s*$", changelog, re.M):
            print(f"{name}: has '## {version}'")
        else:
            print(f"{name}: no '## {version}' heading (PLUGIN_VERSION in src/PluginInfo.cs)")
            failed = True

    m = re.search(r"NET_CONTRACT_VERSION\s*=\s*(\d+)\s*;", info)
    if not m:
        print("src/PluginInfo.cs: NET_CONTRACT_VERSION not found")
        return 1
    contract = m.group(1)
    changelog = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
    if re.search(r"[Nn]etwork contract " + contract + r"\b", changelog):
        print(f"CHANGELOG.md: names network contract {contract}")
    else:
        print(f"CHANGELOG.md: does not name 'network contract {contract}' "
              "(NET_CONTRACT_VERSION in src/PluginInfo.cs); add it to the entry that changed it")
        failed = True
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())