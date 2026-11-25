#!/usr/bin/env python3
"""Prints the result tables of docs/benchmark.md from the files in benchmark/results/.

  python3 benchmark/summary.py

Run it after source_benchmark.py, labels.py score, and copying the live results from the workflow's artifacts,
then paste the tables into docs/benchmark.md.
"""
from __future__ import annotations

import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
RESULTS = HERE / "results"


def pct(v) -> str:
    return "–" if v is None else f"{100 * v:.0f}%"


def main() -> None:
    apps = {a["id"]: a for a in json.loads((HERE / "apps.json").read_text(encoding="utf-8"))["apps"]}
    source = {e["id"]: e for e in json.loads((RESULTS / "source-summary.json").read_text(encoding="utf-8"))["apps"]}
    precision = json.loads((RESULTS / "precision.json").read_text(encoding="utf-8"))

    print("### Source scan\n")
    print("| App | Styles | Crashed | Time | Same twice | Score | Screens | Controls | Problems | Precision (AI-reviewed) |")
    print("|---|---|---|---:|---|---:|---:|---:|---:|---:|")
    for id_, a in apps.items():
        s = source[id_]
        p = precision["byApp"].get(id_, {})
        checked = p.get("real", 0) + p.get("falseAlarm", 0)
        print(f"| {a['name']} | {', '.join(a['styles'])} | {'yes' if s['crashed'] else 'no'} | {s['seconds'][0]:.1f} s | "
              f"{'yes' if s.get('deterministic') else 'no'} | {s.get('score')} ({s.get('grade')}) | {s.get('screens')} | "
              f"{s.get('controls')} | {s.get('problems')} | {pct(p.get('precision'))} ({p.get('real', 0)}/{checked}) |")
    o = precision["overall"]
    print(f"| **All {len(apps)} apps** | | {sum(1 for s in source.values() if s['crashed'])} crashes | "
          f"{sum(s['seconds'][0] for s in source.values()):.1f} s | | | {sum(s.get('screens', 0) for s in source.values())} | "
          f"{sum(s.get('controls', 0) for s in source.values())} | {sum(s.get('problems', 0) for s in source.values())} | "
          f"**{pct(o['precision'])}** ({o['real']}/{o['real'] + o['falseAlarm']}) |")

    print("\n### Problems by rule (all apps)\n")
    totals: dict[str, int] = {}
    for s in source.values():
        for rule, n in s.get("problemsByRule", {}).items():
            totals[rule] = totals.get(rule, 0) + n
    print("| Rule | Problems | AI-reviewed | Precision |")
    print("|---|---:|---:|---:|")
    for rule in sorted(totals):
        p = precision["byRule"].get(rule, {})
        n = p.get("real", 0) + p.get("falseAlarm", 0)
        print(f"| {rule} | {totals[rule]} | {n} | {pct(p.get('precision')) if n else '–'} |")

    print("\n### Live ground truth\n")
    print("| App | UIA elements | Controls compared | Live scan: same ID as truth | Live scan missed | "
          "Source scan sees the control | Source: same ID | Source false alarms | Locators checked | Locators unique |")
    print("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
    for f in sorted((RESULTS / "live").glob("*.json")):
        r = json.loads(f.read_text(encoding="utf-8"))
        lv, sv, lc = r["liveVsTruth"], r["sourceVsTruth"], r["locators"]
        print(f"| {r['name']} | {r['groundTruthElements']} | {lv['comparedControls']} | {pct(lv['idAgreement'])} | "
              f"{lv['missedByLiveScan']} | {pct(sv['sourceSeesControl'])} ({sv['matchedToSource']}/{sv['pairableControls']}) | "
              f"{pct(sv['idAgreement'])} | {sv['falseAlarms']} | {lc['locators']} ({lc['elementsChecked']} elements) | "
              f"{pct(lc['unique'])} |")


if __name__ == "__main__":
    main()
