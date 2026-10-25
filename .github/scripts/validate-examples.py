#!/usr/bin/env python3
"""Validate every examples/**/ectype-role.json against docs/schemas/ectype-role.schema.json and
every examples/**/*ectype-stats.json against docs/schemas/ectype-stats.schema.json.

Then check each example project against the VoidManager rule: VoidManager is optional, so an
example names no VoidManager type unless its csproj sets <UseVoidManager>true</UseVoidManager>
(which adds the reference from examples/Directory.Build.props), never references VoidManager.dll
directly, depends on VoidManager only softly, and declares no type deriving from a VoidManager
type. Such a type cannot load without VoidManager, and a VoidPlugin makes VoidManager a hard
dependency."""
import json
import re
import sys
from pathlib import Path

from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parents[2]

SETS = [
    ("docs/schemas/ectype-role.schema.json", "ectype-role.json"),
    ("docs/schemas/ectype-stats.schema.json", "*ectype-stats.json"),
]

# VoidManager's public base classes a mod would derive from.
VM_BASES = ("VoidPlugin", "ModSettingsMenu", "PlayerSettingsMenu", "ChatCommand", "PublicCommand",
            "ModMessage")

CS_TOKENS = re.compile(
    r'//[^\n]*'                      # line comment
    r'|/\*.*?\*/'                    # block comment
    r'|@"(?:[^"]|"")*"'              # verbatim string
    r'|\$?"(?:\\.|[^"\\\n])*"'       # string, interpolated or not
    r"|'(?:\\.|[^'\\\n])+'",         # char literal
    re.S)


def code_only(text):
    """C# source with comments and string and char literals blanked."""
    return CS_TOKENS.sub(lambda m: " " if m.group(0).startswith("/") else '""', text)


def schema_checks():
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
    return bad


def voidmanager_checks():
    bad = 0
    projects = sorted((ROOT / "examples").glob("*/*.csproj"))
    if not projects:
        print("no example projects found under examples/")
        return 1
    for csproj in projects:
        folder = csproj.parent
        name = folder.relative_to(ROOT).as_posix()
        proj = csproj.read_text(encoding="utf-8")
        opted_in = re.search(r"<UseVoidManager>\s*true\s*</UseVoidManager>", proj, re.I) is not None
        problems = []

        if re.search(r'<Reference\s+Include="VoidManager[",]', proj):
            problems.append("references VoidManager directly; set <UseVoidManager>true</UseVoidManager> "
                            "instead")

        names_vm = False
        for path in sorted(folder.rglob("*.cs")):
            if {"bin", "obj"} & set(path.relative_to(folder).parts):
                continue
            raw = path.read_text(encoding="utf-8")
            code = code_only(raw)
            where = path.relative_to(ROOT).as_posix()
            if re.search(r"\bVoidManager\b", code):
                names_vm = True
            for m in re.finditer(r"\b(?:class|struct)\s+\w+[^{;]*?:([^{;]*)\{", code):
                for base in VM_BASES:
                    if re.search(r"\b" + base + r"\b", m.group(1)):
                        problems.append(f"{where}: declares a type deriving from VoidManager's {base}")
            for m in re.finditer(r"\[\s*BepInDependency\s*\(([^)]*)\)", raw):
                if "VoidManager" in m.group(1) and "SoftDependency" not in m.group(1):
                    problems.append(f"{where}: hard [BepInDependency] on VoidManager; use "
                                    "BepInDependency.DependencyFlags.SoftDependency")

        if names_vm and not opted_in:
            problems.append("names VoidManager without <UseVoidManager>true</UseVoidManager> in "
                            + csproj.name)
        if opted_in and not names_vm:
            problems.append("sets UseVoidManager but names no VoidManager type; remove it")

        if problems:
            bad += 1
            for p in problems:
                print(f"{name}: {p}")
        else:
            print(f"{name}: VoidManager {'opted in' if opted_in else 'not used'}: ok")
    return bad


def main():
    bad = schema_checks() + voidmanager_checks()
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
