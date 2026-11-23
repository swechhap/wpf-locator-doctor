#!/usr/bin/env python3
"""Source-scan benchmark: runs `wpf-locator-doctor scan-source` on every app in apps.json.

For each app it shallow-clones the pinned commit, scans it twice, and records:
  - whether the scan crashed (non-zero exit code or an unhandled error),
  - how long it took,
  - the score, grade and problem counts by rule,
  - whether the two runs gave the same JSON (apart from the time stamp).

The findings of each app are written to results/source/<id>.txt, one line per problem, sorted.
These are the golden files: they are kept in the repo, so a rule change shows up as a reviewable diff.
`--check` fails if a golden file would change (use it in CI); without it the files are rewritten.

Only the Python standard library is used. Works on Windows, macOS and Linux.

Usage:
  python3 benchmark/source_benchmark.py [--check] [--apps id1,id2] [--work DIR] [--tool CMD]

Build the CLI first: dotnet build src/WpfLocatorDoctor.Cli -c Release -f net10.0
"""
from __future__ import annotations

import argparse
import json
import os
import shlex
import subprocess
import sys
import tempfile
import time
from collections import Counter
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
RESULTS = HERE / "results"
GOLDEN_DIR = RESULTS / "source"
SUMMARY_JSON = RESULTS / "source-summary.json"
RULES = [f"WT{n:03d}" for n in range(1, 13)]


def load_apps(only: set[str] | None) -> list[dict]:
    apps = json.loads((HERE / "apps.json").read_text(encoding="utf-8"))["apps"]
    if only:
        unknown = only - {a["id"] for a in apps}
        if unknown:
            sys.exit(f"Unknown app id(s): {', '.join(sorted(unknown))}")
        apps = [a for a in apps if a["id"] in only]
    return apps


def git(*args: str, cwd: Path) -> str:
    return subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True).stdout.strip()


def clone(app: dict, work: Path) -> Path:
    """Shallow-clones the pinned commit, or reuses an earlier clone of it."""
    dest = work / app["id"]
    if (dest / ".git").exists():
        try:
            if git("rev-parse", "HEAD", cwd=dest) == app["commit"]:
                return dest
        except subprocess.CalledProcessError:
            pass
    if dest.exists():
        import shutil
        shutil.rmtree(dest)
    dest.mkdir(parents=True)
    git("init", "-q", cwd=dest)
    git("remote", "add", "origin", app["repo"], cwd=dest)
    git("fetch", "-q", "--depth", "1", "origin", app["commit"], cwd=dest)
    git("-c", "advice.detachedHead=false", "checkout", "-q", "FETCH_HEAD", cwd=dest)
    return dest


def run_scan(tool: list[str], target: Path, out: Path) -> dict:
    start = time.perf_counter()
    proc = subprocess.run([*tool, "scan-source", str(target), "--out", str(out), "--quiet"],
                          capture_output=True, text=True, encoding="utf-8", errors="replace")
    seconds = time.perf_counter() - start
    report = out / "testability-report.json"
    crashed = proc.returncode not in (0, 1) or "Unhandled exception" in proc.stderr or not report.exists()
    return {
        "exitCode": proc.returncode,
        "crashed": crashed,
        "seconds": round(seconds, 2),
        "stderr": proc.stderr.strip()[-2000:],
        "report": json.loads(report.read_text(encoding="utf-8")) if report.exists() else None,
    }


def normalize(report: dict) -> dict:
    """The report without what changes on every run or machine (time stamp, absolute paths, path separators)."""
    r = json.loads(json.dumps(report))
    r.pop("generatedAt", None)
    r["target"] = "<target>"
    for f in r.get("findings", []):
        if f.get("location"):
            f["location"]["file"] = f["location"]["file"].replace("\\", "/")
    for s in r.get("screens", []):
        if s.get("origin"):
            s["origin"] = s["origin"].replace("\\", "/")
    r["notes"] = [n.replace("\\", "/") for n in r.get("notes", [])]
    return r


def golden_text(app: dict, report: dict) -> str:
    """One line per fact, sorted, so a diff shows exactly which problems appeared or disappeared."""
    o = report["overall"]
    lines = [
        f"# {app['name']} @ {app['commit'][:10]}, scan path: {app['scanPath']}",
        f"score {fmt(o.get('score'))}  grade {o['grade']}  elements {o['elementCount']}  controls {o['interactiveCount']}  screens {len(report['screens'])}",
    ]
    for c in o["checks"]:
        lines.append(f"check {c['id']:<11} {fmt(c.get('percent')):>6}%  {c['detail']}")
    counts = Counter(f["ruleId"] for f in report["findings"])
    lines.append("problems " + "  ".join(f"{r}={counts[r]}" for r in RULES if counts[r]))
    for n in sorted(report.get("notes", [])):
        lines.append(f"note {n}")
    lines.append("")
    rows = []
    for f in report["findings"]:
        loc = f.get("location")
        where = f"{loc['file']}:{loc['line']}" if loc else f"[{f['screen']}]"
        rows.append(f"{f['ruleId']}\t{f['severity']}\t{where}\t{f['message']}")
    rows.sort(key=sort_key)
    lines.extend(rows)
    return "\n".join(lines) + "\n"


def sort_key(row: str):
    rule, sev, where, msg = row.split("\t", 3)
    file, _, line = where.rpartition(":")
    return (file.lower(), int(line) if line.isdigit() else 0, rule, msg)


def fmt(v) -> str:
    return "n/a" if v is None else f"{v:g}"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--check", action="store_true", help="fail if a golden file would change instead of rewriting it")
    ap.add_argument("--apps", help="comma-separated app ids (default: all)")
    # Outside this repo on purpose: the apps must not pick up this repo's Directory.Build.props when they are built.
    ap.add_argument("--work", default=os.environ.get("WLD_BENCH_WORK", str(Path(tempfile.gettempdir()) / "wld-bench")),
                    help="where to clone the apps (default: <temp>/wld-bench)")
    ap.add_argument("--tool", help="command that runs the CLI (default: the Release build of src/WpfLocatorDoctor.Cli)")
    args = ap.parse_args()

    if args.tool:
        tool = shlex.split(args.tool)
    else:
        dll = REPO / "src/WpfLocatorDoctor.Cli/bin/Release/net10.0/wpf-locator-doctor.dll"
        if not dll.exists():
            sys.exit(f"Build first: dotnet build src/WpfLocatorDoctor.Cli -c Release -f net10.0  (missing {dll})")
        tool = ["dotnet", str(dll)]

    work = Path(args.work).resolve()
    work.mkdir(parents=True, exist_ok=True)
    GOLDEN_DIR.mkdir(parents=True, exist_ok=True)
    apps = load_apps(set(args.apps.split(",")) if args.apps else None)

    summary, changed, failed = [], [], []
    for app in apps:
        print(f"== {app['id']}: cloning {app['repo']} @ {app['commit'][:10]}", flush=True)
        src = clone(app, work / "apps")
        target = src / app["scanPath"]
        runs = [run_scan(tool, target, work / "out" / app["id"] / f"run{i}") for i in (1, 2)]
        first = runs[0]
        entry = {
            "id": app["id"],
            "name": app["name"],
            "commit": app["commit"],
            "scanPath": app["scanPath"],
            "crashed": any(r["crashed"] for r in runs),
            "exitCode": first["exitCode"],
            "seconds": [r["seconds"] for r in runs],
        }
        if entry["crashed"]:
            entry["error"] = next(r["stderr"] for r in runs if r["crashed"])
            failed.append(app["id"])
            print(f"   CRASHED: {entry['error'][:500]}")
        else:
            a, b = (normalize(r["report"]) for r in runs)
            rep = first["report"]
            o = rep["overall"]
            counts = Counter(f["ruleId"] for f in rep["findings"])
            entry.update({
                "deterministic": a == b,
                "score": o.get("score"),
                "grade": o["grade"],
                "elements": o["elementCount"],
                "controls": o["interactiveCount"],
                "screens": len(rep["screens"]),
                "problems": len(rep["findings"]),
                "problemsByRule": {r: counts[r] for r in RULES if counts[r]},
                "checks": {c["id"]: c.get("percent") for c in o["checks"]},
                "notes": len(rep.get("notes", [])),
            })
            if not entry["deterministic"]:
                failed.append(app["id"])
                print("   NOT DETERMINISTIC: two runs gave different reports")
            text = golden_text(app, a)
            golden = GOLDEN_DIR / f"{app['id']}.txt"
            old = golden.read_text(encoding="utf-8") if golden.exists() else None
            if old != text:
                changed.append(app["id"])
                if args.check:
                    print(f"   CHANGED: {golden.relative_to(REPO)} differs from this run")
                    (work / "out" / app["id"] / "golden-new.txt").write_text(text, encoding="utf-8")
                else:
                    golden.write_text(text, encoding="utf-8", newline="\n")
            print(f"   score {fmt(entry['score'])} ({entry['grade']}), {entry['problems']} problems, "
                  f"{entry['seconds'][0]}s, deterministic={entry['deterministic']}")
        summary.append(entry)

    if not args.check and not args.apps:
        SUMMARY_JSON.write_text(json.dumps({"apps": summary}, indent=2) + "\n", encoding="utf-8", newline="\n")
    (work / "source-summary.json").write_text(json.dumps({"apps": summary}, indent=2) + "\n", encoding="utf-8")

    print()
    print(f"{'app':<16}{'crash':<7}{'secs':>6}  {'score':>6} grade {'problems':>9}  same-twice")
    for e in summary:
        print(f"{e['id']:<16}{('YES' if e['crashed'] else 'no'):<7}{e['seconds'][0]:>6}  {fmt(e.get('score')):>6} "
              f"{e.get('grade', '-'):^5} {e.get('problems', 0):>9}  {e.get('deterministic', '-')}")
    if changed:
        verb = "would change" if args.check else "updated"
        print(f"\nGolden files {verb}: {', '.join(changed)}"
              + (". Run without --check and review the diff." if args.check else ". Review the diff before committing."))
    if failed:
        print(f"\nFailed: {', '.join(failed)}")
    return 1 if failed or (args.check and changed) else 0


if __name__ == "__main__":
    sys.exit(main())
