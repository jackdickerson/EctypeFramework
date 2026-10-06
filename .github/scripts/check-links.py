#!/usr/bin/env python3
"""Fail when a relative Markdown link under README.md, docs/ or examples/ does not resolve."""
import re
import sys
from pathlib import Path
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parents[2]
LINK = re.compile(r"(?<!\!)\[[^\]]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)|!\[[^\]]*\]\(([^)\s]+)\)")
FENCE = re.compile(r"^\s*(```|~~~)")


def files():
    yield ROOT / "README.md"
    for top in ("docs", "examples"):
        yield from sorted((ROOT / top).rglob("*.md"))


def links(text):
    in_fence = False
    for number, line in enumerate(text.splitlines(), 1):
        if FENCE.match(line):
            in_fence = not in_fence
            continue
        if in_fence:
            continue
        line = re.sub(r"`[^`]*`", "", line)
        for m in LINK.finditer(line):
            yield number, m.group(1) or m.group(2)


def main():
    bad = 0
    checked = 0
    for md in files():
        if not md.exists():
            continue
        for number, target in links(md.read_text(encoding="utf-8")):
            if re.match(r"^[a-zA-Z][a-zA-Z0-9+.-]*:", target) or target.startswith("#"):
                continue
            path = unquote(target.split("#", 1)[0].split("?", 1)[0])
            if not path:
                continue
            dest = (ROOT / path.lstrip("/")) if path.startswith("/") else (md.parent / path)
            checked += 1
            if not dest.resolve().exists():
                print(f"{md.relative_to(ROOT).as_posix()}:{number}: broken link {target}")
                bad += 1
    print(f"{checked} relative links checked, {bad} broken")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())