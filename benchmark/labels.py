#!/usr/bin/env python3
"""Hand-checked sample of the source scan's problems, to measure precision (how many reported problems are real).

  python3 benchmark/labels.py sample [--per-app 20]
      Picks problems at random (the same ones every time for the same golden file) from benchmark/results/source/<id>.txt
      and adds them to benchmark/labels/<id>.json with an empty label. Labels already filled in are kept.

  python3 benchmark/labels.py score
      Recomputes precision from the labels: real / (real + false alarm), per app, per rule and overall.
      Writes benchmark/results/precision.json.

Each label is "real" (a tester would agree it is a problem) or "false alarm" (it isn't, for example the ID is set in a style),
with a short "why".
"""
from __future__ import annotations

import argparse
import json
import random
import sys
from collections import Counter, defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
GOLDEN = HERE / "results" / "source"
LABELS = HERE / "labels"


def findings(app_id: str) -> list[str]:
    lines = (GOLDEN / f"{app_id}.txt").read_text(encoding="utf-8").splitlines()
    return [line for line in lines if line.startswith("WT") and "\t" in line]


def apps() -> list[dict]:
    return json.loads((HERE / "apps.json").read_text(encoding="utf-8"))["apps"]


def sample(per_app: int) -> None:
    LABELS.mkdir(exist_ok=True)
    for app in apps():
        path = LABELS / f"{app['id']}.json"
        existing = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {"labels": []}
        known = {e["finding"]: e for e in existing["labels"]}
        rows = findings(app["id"])
        picked = random.Random(f"{app['id']}:{app['commit']}").sample(rows, min(per_app, len(rows)))
        labels = [known.get(r, {"finding": r, "label": None, "why": ""}) for r in sorted(picked)]
        doc = {
            "app": app["name"],
            "commit": app["commit"],
            "scanPath": app["scanPath"],
            "labels": labels,
        }
        path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
        print(f"{app['id']}: {len(labels)} sampled, {sum(1 for l in labels if l['label'])} already labelled")


def score() -> int:
    per_app, per_rule, overall = {}, defaultdict(Counter), Counter()
    stale, missing = [], []
    for app in apps():
        path = LABELS / f"{app['id']}.json"
        if not path.exists():
            missing.append(app["id"])
            continue
        current = set(findings(app["id"]))
        c = Counter()
        for e in json.loads(path.read_text(encoding="utf-8"))["labels"]:
            if e["finding"] not in current:
                stale.append(f"{app['id']}: {e['finding'][:100]}")
            if e["label"] not in ("real", "false alarm"):
                missing.append(f"{app['id']}: {e['finding'][:100]}")
                continue
            rule = e["finding"].split("\t", 1)[0]
            c[e["label"]] += 1
            per_rule[rule][e["label"]] += 1
            overall[e["label"]] += 1
        per_app[app["id"]] = {"real": c["real"], "falseAlarm": c["false alarm"], "precision": precision(c)}
    result = {
        "overall": {"real": overall["real"], "falseAlarm": overall["false alarm"], "precision": precision(overall)},
        "byApp": per_app,
        "byRule": {r: {"real": c["real"], "falseAlarm": c["false alarm"], "precision": precision(c)} for r, c in sorted(per_rule.items())},
    }
    (HERE / "results" / "precision.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps(result["overall"]))
    for k, v in per_app.items():
        print(f"  {k:<16} {v['real']:>3} real {v['falseAlarm']:>3} false  precision {v['precision']}")
    for k, v in result["byRule"].items():
        print(f"  {k:<16} {v['real']:>3} real {v['falseAlarm']:>3} false  precision {v['precision']}")
    if stale:
        print("\nLabelled problems no longer in the golden files (re-run 'sample' and label the new ones):")
        print("\n".join(f"  {s}" for s in stale))
    if missing:
        print("\nNot labelled yet:")
        print("\n".join(f"  {s}" for s in missing))
    return 1 if stale or missing else 0


def precision(c: Counter):
    n = c["real"] + c["false alarm"]
    return round(c["real"] / n, 3) if n else None


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("sample")
    s.add_argument("--per-app", type=int, default=20)
    sub.add_parser("score")
    args = ap.parse_args()
    if args.cmd == "sample":
        sample(args.per_app)
        return 0
    return score()


if __name__ == "__main__":
    sys.exit(main())
