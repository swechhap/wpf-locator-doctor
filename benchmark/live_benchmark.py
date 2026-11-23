#!/usr/bin/env python3
"""Live ground-truth benchmark (Windows only). For each app in apps.json that has a "live" section:

  1. shallow-clones the pinned commit and builds the app,
  2. starts it and waits for its main window,
  3. UiaDump (plain FlaUI, none of this project's code) writes the ground truth: every UI Automation element,
  4. LiveBench runs this project's live scan, source scan and recommend_locators logic on the same running app,
  5. UiaDump checks every recommended locator with plain FlaUI,
  6. compare_live.py compares them and writes results/live/<id>.json.

Usage (from the repo root, after building benchmark/live/UiaDump and benchmark/live/LiveBench in Release):
  python benchmark/live_benchmark.py [--apps id1,id2] [--work DIR] [--settle SECONDS]
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
sys.path.insert(0, str(HERE))
from source_benchmark import clone, load_apps  # noqa: E402

UIADUMP = REPO / "benchmark/live/UiaDump/bin/Release/net10.0-windows/UiaDump.exe"
LIVEBENCH = REPO / "benchmark/live/LiveBench/bin/Release/net10.0-windows/LiveBench.exe"
RESULTS = HERE / "results" / "live"


def run(cmd: list[str], log: Path, timeout: int, cwd: Path | None = None) -> None:
    print("   $ " + " ".join(f'"{c}"' if " " in c else c for c in cmd), flush=True)
    with log.open("a", encoding="utf-8") as f:
        f.write("\n$ " + " ".join(cmd) + "\n")
        f.flush()
        proc = subprocess.run(cmd, stdout=f, stderr=subprocess.STDOUT, timeout=timeout, cwd=cwd)
    if proc.returncode != 0:
        print(log.read_text(encoding="utf-8", errors="replace")[-4000:])
        raise RuntimeError(f"{Path(cmd[0]).name} exited with {proc.returncode}")


def build_app(app: dict, src: Path, out: Path, log: Path) -> Path:
    live = app["live"]
    # The benchmark's runner has its own SDKs; an app's pinned SDK version would only get in the way.
    for gj in src.glob("global.json"):
        gj.unlink()
    if out.exists():
        shutil.rmtree(out)
    run(["dotnet", "build", str(src / live["project"]), "-c", "Release", "-f", live["framework"], "-o", str(out),
         "-p:TreatWarningsAsErrors=false", "-p:RunAnalyzers=false", "-nologo", "-v:q"], log, timeout=1800)
    exe = out / live["exe"]
    if not exe.exists():
        raise RuntimeError(f"Built, but {exe} is missing")
    return exe


def run_app(app: dict, work: Path, settle: int) -> dict:
    live = app["live"]
    src = clone(app, work / "apps")
    raw = work / "live" / app["id"]
    if raw.exists():
        shutil.rmtree(raw)
    raw.mkdir(parents=True)
    log = raw / "log.txt"
    exe = build_app(app, src, work / "build" / app["id"], log)

    print(f"   starting {exe.name}", flush=True)
    proc = subprocess.Popen([str(exe)], cwd=exe.parent)
    pid = str(proc.pid)
    try:
        run([str(UIADUMP), "dump", "--pid", pid, "--out", str(raw / "truth.json"), "--wait", "90"], log, timeout=600)
        # Give start-up animations, navigation and data loading time to finish, then read the truth again.
        time.sleep(settle)
        run([str(UIADUMP), "dump", "--pid", pid, "--out", str(raw / "truth.json")], log, timeout=900)
        source = src / live.get("sourcePath", app["scanPath"])
        run([str(LIVEBENCH), "--pid", pid, "--out", str(raw), "--source", str(source)], log, timeout=1800)
        run([str(UIADUMP), "check", "--pid", pid, "--locators", str(raw / "locators.json"),
             "--out", str(raw / "locator-results.json")], log, timeout=1800)
        if proc.poll() is not None:
            raise RuntimeError(f"The app exited during the benchmark (exit code {proc.returncode})")
    finally:
        subprocess.run(["taskkill", "/T", "/F", "/PID", pid], capture_output=True)

    RESULTS.mkdir(parents=True, exist_ok=True)
    out = RESULTS / f"{app['id']}.json"
    run([sys.executable, str(HERE / "compare_live.py"), str(raw), str(raw / "result.json")], log, timeout=600)
    result = json.loads((raw / "result.json").read_text(encoding="utf-8"))
    result = {"id": app["id"], "name": live.get("name", app["name"]), "commit": app["commit"], **result}
    out.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    print((raw / "log.txt").read_text(encoding="utf-8", errors="replace").splitlines()[-3:])
    return result


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--apps", help="comma-separated app ids (default: every app with a live section)")
    ap.add_argument("--work", default=os.environ.get("WLD_BENCH_WORK", str(Path(tempfile.gettempdir()) / "wld-bench")))
    ap.add_argument("--settle", type=int, default=15, help="seconds to wait after the main window appears")
    args = ap.parse_args()
    if os.name != "nt":
        sys.exit("The live benchmark needs Windows (UI Automation).")
    for tool in (UIADUMP, LIVEBENCH):
        if not tool.exists():
            sys.exit(f"Build first: dotnet build {tool.parent.parent.parent.parent} -c Release  (missing {tool})")

    apps = [a for a in load_apps(set(args.apps.split(",")) if args.apps else None) if "live" in a]
    work = Path(args.work).resolve()
    failed = []
    for app in apps:
        print(f"== {app['id']}", flush=True)
        try:
            run_app(app, work, args.settle)
        except Exception as ex:  # report and carry on with the other apps
            print(f"   FAILED: {ex}", flush=True)
            failed.append(app["id"])
    if failed:
        print(f"Failed: {', '.join(failed)}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
