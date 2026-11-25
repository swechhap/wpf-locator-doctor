"""Checks that each download carries the license files it must.

Usage: python check_licenses.py <.nupkg, .zip, .mcpb or publish folder> ...

Every download must contain LICENSE and THIRD-PARTY-NOTICES.txt, the same as in this repository, and every
library it ships (read from its .deps.json) must be listed in THIRD-PARTY-NOTICES.txt with that exact version.
The exact version matters because a library's license can change between versions. A program file that the
.deps.json doesn't mention must belong to a listed library, by name.
"""

import json
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
REQUIRED = ("LICENSE", "THIRD-PARTY-NOTICES.txt")
SHIPPED = ("runtime", "runtimeTargets", "native", "resources")


def read_files(path):
    """Returns {path inside the download: function that reads its bytes}."""
    if path.is_dir():
        return {p.relative_to(path).as_posix(): p.read_bytes for p in path.rglob("*") if p.is_file()}
    archive = zipfile.ZipFile(path)
    return {n: (lambda n=n: archive.read(n)) for n in archive.namelist() if not n.endswith("/")}


def normal(data):
    return data.decode("utf-8-sig").replace("\r\n", "\n").strip()


def shipped_libraries(deps):
    target = deps["targets"][deps["runtimeTarget"]["name"]]
    return {
        name.replace("/", " ")
        for name, parts in target.items()
        if deps["libraries"][name]["type"] != "project" and any(parts.get(k) for k in SHIPPED)
    }


def declared_files(deps):
    target = deps["targets"][deps["runtimeTarget"]["name"]]
    return {f.rsplit("/", 1)[-1] for parts in target.values() for k in SHIPPED for f in parts.get(k) or {}}


def check(path, listed):
    if not path.exists():
        return [f"{path} doesn't exist."], 0
    files = read_files(path)
    problems = []
    for name in REQUIRED:
        copies = [f for f in files if f.rsplit("/", 1)[-1] == name]
        if not copies:
            problems.append(f"{name} is missing.")
        for copy in copies:
            if normal(files[copy]()) != normal((ROOT / name).read_bytes()):
                problems.append(f"{copy} is not the same as the {name} in this repository.")
    deps_files = [f for f in files if f.endswith(".deps.json")]
    if not deps_files:
        problems.append("There is no .deps.json file, so its libraries can't be checked.")
    libraries, declared = set(), set()
    for f in deps_files:
        deps = json.loads(files[f]().decode("utf-8-sig"))
        libraries |= shipped_libraries(deps)
        declared |= declared_files(deps)
        # The small .exe that starts the app has the app's own name.
        declared.add(f.rsplit("/", 1)[-1].removesuffix(".deps.json") + ".exe")
    for library in sorted(libraries - listed):
        problems.append(f"'{library}' is not in THIRD-PARTY-NOTICES.txt. Check its license, then add it and its license text.")
    listed_names = [library.split(" ")[0] for library in listed]
    for f in sorted(files):
        name = f.rsplit("/", 1)[-1]
        if name.lower().endswith((".dll", ".exe")) and name not in declared:
            if not any(name.startswith(known + ".") for known in listed_names):
                problems.append(f"{f} isn't in the .deps.json file or THIRD-PARTY-NOTICES.txt. Find out where it comes from.")
    return problems, len(libraries)


def main(paths):
    if not paths:
        print("Name at least one download to check.")
        return 2
    notices = normal((ROOT / "THIRD-PARTY-NOTICES.txt").read_bytes())
    # Only the list at the top counts, not lines in the license texts below it.
    index = notices.split("=" * 80, 1)[0]
    listed = {line[2:].strip() for line in index.splitlines() if line.startswith("- ")}
    failed = False
    for path in paths:
        problems, count = check(Path(path), listed)
        print(f"{'FAIL' if problems else 'OK  '} {path} ({count} libraries)")
        for problem in problems:
            print(f"     {problem}")
        failed |= bool(problems)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
