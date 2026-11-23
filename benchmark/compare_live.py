#!/usr/bin/env python3
"""Compares what WPF Locator Doctor reports with the ground truth for one running app.

Input folder (written by live_benchmark.py on Windows):
  truth.json            UiaDump: every element UI Automation shows (independent of this project's code)
  live-elements.json    LiveBench: what the live scan recorded
  source-elements.json  LiveBench: what the source scan recorded
  live-report.json      LiveBench: the live scan's report
  locators.json         LiveBench: every locator recommend_locators gave
  locator-results.json  UiaDump: how many elements each locator really finds with plain FlaUI

Output: one JSON file with the numbers and examples of every kind of disagreement.

Usage: python3 benchmark/compare_live.py <input folder> <output.json>
"""
from __future__ import annotations

import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

# What counts as a control a tester needs: the standard UI Automation types for things you click, type into or select.
CONTROL_TYPES = {"Button", "Edit", "CheckBox", "RadioButton", "ComboBox", "List", "DataGrid", "Tree", "Tab", "TabItem",
                 "MenuItem", "Slider", "Hyperlink", "Spinner", "SplitButton", "Calendar", "Document"}
FURNITURE = {"TitleBar", "ScrollBar", "Thumb"}
LEAF = {"Button", "Edit", "CheckBox", "RadioButton", "ComboBox", "Slider", "Spinner", "Hyperlink", "SplitButton",
        "Calendar", "ScrollBar", "ProgressBar", "Document"}
ROWS = {"ListItem", "DataItem", "TreeItem"}
HOSTS = {"List", "DataGrid", "Tree", "ComboBox", "Table"}
MAX_ROWS = 50
EXAMPLES = 15


def load(folder: Path, name: str, default=None):
    p = folder / name
    return json.loads(p.read_text(encoding="utf-8-sig")) if p.exists() else default


def norm_name(s: str | None) -> str | None:
    """Readable names as both sides show them: no access-key underscore, single spaces, any case."""
    if not s:
        return None
    s = s.replace("_", "", 1) if re.match(r"^[^_]*_[^_]", s) else s
    s = re.sub(r"\s+", " ", s).strip().lower()
    return s or None


def is_bound(i: str | None) -> bool:
    return bool(i) and i.startswith("{")


def ancestors(truth: list[dict], e: dict):
    p = e["parent"]
    while p >= 0:
        a = truth[p]
        yield a
        p = a["parent"]


def by_design_reason(truth: list[dict], e: dict, row_position: dict[int, int]) -> str | None:
    """Why the live scan skips this control on purpose, or None if it should be there."""
    chain = list(ancestors(truth, e))
    root = chain[-1] if chain else e
    if root["controlType"] != "Window":
        return "in a top-level element that isn't a window (pop-up, tooltip)"
    if root.get("automationId") == "SystemMenuBar" or any(a.get("automationId") == "SystemMenuBar" for a in chain):
        return "window system menu"
    if any(a["controlType"] in FURNITURE for a in chain):
        return "inside a title bar or scroll bar"
    if any(a["controlType"] in LEAF for a in chain[:-1]):
        return "part of another control"
    if any(row_position.get(a["index"], 0) > MAX_ROWS for a in chain):
        return f"in a list row after the first {MAX_ROWS}"
    return None


def row_positions(truth: list[dict]) -> dict[int, int]:
    """1-based position of each row among the rows of its list."""
    pos, count = {}, Counter()
    for e in truth:
        if e["controlType"] in ROWS and e["parent"] >= 0 and truth[e["parent"]]["controlType"] in HOSTS:
            count[e["parent"]] += 1
            pos[e["index"]] = count[e["parent"]]
    return pos


def describe(e: dict) -> str:
    t = e.get("controlType", "?")
    n = e.get("name")
    i = e.get("automationId")
    return t + (f' "{n[:50]}"' if n else "") + (f" #{i}" if i else "")


def compare_live(truth: list[dict], live: list[dict]) -> dict:
    """Live scan vs ground truth, control by control (paired in reading order within each type + name)."""
    rows = row_positions(truth)
    gt_controls = [e for e in truth if e["parent"] >= 0 and e["controlType"] in CONTROL_TYPES]
    skipped = Counter()
    considered = []
    for e in gt_controls:
        reason = by_design_reason(truth, e, rows)
        if reason:
            skipped[reason] += 1
        else:
            considered.append(e)
    live_controls = [e for e in live if e["isInteractive"] and e["controlType"] in CONTROL_TYPES]

    groups_gt, groups_live = defaultdict(list), defaultdict(list)
    for e in considered:
        groups_gt[(e["controlType"], norm_name(e.get("name")))].append(e)
    for e in live_controls:
        groups_live[(e["controlType"], norm_name(e.get("name")))].append(e)

    pairs, gt_only, live_only = [], [], []
    for key in set(groups_gt) | set(groups_live):
        g, l = groups_gt.get(key, []), groups_live.get(key, [])
        pairs += list(zip(g, l))
        gt_only += g[len(l):]
        live_only += l[len(g):]

    outcome = Counter()
    examples = defaultdict(list)
    for g, l in pairs:
        gi, li = g.get("automationId"), l.get("automationId")
        if gi == li:
            k = "same ID" if gi else "both without an ID"
        elif gi and not li:
            k = "live scan lost the ID"
        elif li and not gi:
            k = "live scan reports an ID UI Automation doesn't have"
        else:
            k = "different ID"
        outcome[k] += 1
        if k not in ("same ID", "both without an ID"):
            examples[k].append(f"{describe(g)} vs live {describe(l)}")
    for e in gt_only:
        examples["control the live scan missed"].append(describe(e))
    for e in live_only:
        examples["control only the live scan has"].append(describe(e))

    with_id = sum(1 for e in considered if e.get("automationId"))
    return {
        "groundTruthControls": len(gt_controls),
        "skippedByDesign": dict(skipped),
        "comparedControls": len(considered),
        "liveScanControls": len(live_controls),
        "paired": len(pairs),
        "pairedOutcome": dict(outcome),
        "missedByLiveScan": len(gt_only),
        "onlyInLiveScan": len(live_only),
        "idAgreement": ratio(outcome["same ID"] + outcome["both without an ID"], len(pairs)),
        "groundTruthIdCoverage": ratio(with_id, len(considered)),
        "examples": {k: v[:EXAMPLES] for k, v in examples.items()},
    }


def compare_source(truth: list[dict], source: list[dict]) -> dict:
    """Source scan vs ground truth: for each control UI Automation shows, did the source scan see it, with the same ID?"""
    gt_controls = [e for e in truth if e["parent"] >= 0 and e["controlType"] in CONTROL_TYPES
                   and not any(a["controlType"] in FURNITURE for a in ancestors(truth, e))]
    class_names = {e.get("className") for e in truth if e.get("className")}
    gt_ids = {e["automationId"] for e in truth if e.get("automationId")}
    screens = defaultdict(list)
    for s in source:
        screens[s["screen"]].append(s)
    # Screens that are on screen now: their class appears in the UI Automation tree (user controls do; windows and pages
    # don't), they share an ID with it, or at least two (and a quarter) of their named controls match one on screen by type and name.
    gt_names = Counter((e["controlType"], norm_name(e.get("name"))) for e in gt_controls if norm_name(e.get("name")))

    def names_match(els):
        named = [e for e in els if e.get("isInteractive") and norm_name(e.get("name"))]
        hits = sum(1 for e in named if (e["controlType"], norm_name(e["name"])) in gt_names)
        return hits >= 2 and hits >= len(named) / 4

    relevant = {name for name, els in screens.items()
                if name in class_names or name.split(".")[-1] in class_names
                or any(e.get("automationId") in gt_ids for e in els if not is_bound(e.get("automationId")))
                or names_match(els)}
    pool = [s for name in relevant for s in screens[name]]
    by_id = defaultdict(list)
    for s in source:
        if s.get("automationId") and not is_bound(s["automationId"]):
            by_id[s["automationId"]].append(s)
    by_name = defaultdict(list)
    for s in pool:
        n = norm_name(s.get("name"))
        if n:
            by_name[(s["controlType"], n)].append(s)

    used = set()
    outcome = Counter()
    examples = defaultdict(list)

    def take(cands):
        for c in cands:
            if id(c) not in used:
                used.add(id(c))
                return c
        return None

    for g in gt_controls:
        gi = g.get("automationId")
        match = take(by_id.get(gi, [])) if gi else None
        if match is None:
            n = norm_name(g.get("name"))
            match = take(by_name.get((g["controlType"], n), [])) if n else None
        if match is None:
            k = ("ID not found in the XAML" if gi
                 else "not in the XAML (template, code or data)" if norm_name(g.get("name"))
                 else "can't be paired (no ID and no name)")
            outcome[k] += 1
            examples[k].append(describe(g))
            continue
        si = match.get("automationId")
        if is_bound(si):
            k = "data-bound ID in the XAML"
        elif gi == si:
            k = "same ID" if gi else "both without an ID"
        elif gi and not si:
            k = "source scan says no ID, but it has one (false alarm)"
        elif si and not gi:
            k = "source scan expects an ID that UI Automation doesn't show"
        else:
            k = "different ID"
        outcome[k] += 1
        if k not in ("same ID", "both without an ID", "data-bound ID in the XAML"):
            loc = match.get("location") or match.get("screen")
            examples[k].append(f"{describe(g)} vs source {match.get('kind')} id={si!r} at {loc}")

    unpairable = outcome["can't be paired (no ID and no name)"]
    matched = sum(v for k, v in outcome.items() if not k.startswith(("not in the XAML", "ID not found", "can't be paired")))
    agree = outcome["same ID"] + outcome["both without an ID"] + outcome["data-bound ID in the XAML"]
    gt_with_id = [g for g in gt_controls if g.get("automationId")]
    id_seen = sum(1 for g in gt_with_id if g["automationId"] in by_id)
    return {
        "groundTruthControls": len(gt_controls),
        "sourceScreensOnScreen": sorted(relevant),
        "matchedToSource": matched,
        "outcome": dict(outcome),
        "pairableControls": len(gt_controls) - unpairable,
        "sourceSeesControl": ratio(matched, len(gt_controls) - unpairable),
        "idAgreement": ratio(agree, matched),
        "groundTruthIdsFoundInXaml": ratio(id_seen, len(gt_with_id)),
        "falseAlarms": outcome["source scan says no ID, but it has one (false alarm)"],
        "examples": {k: v[:EXAMPLES] for k, v in examples.items()},
    }


def kind_of(loc: str) -> str:
    if loc.startswith("inside"):
        return "inside a parent's ID"
    if "match #" in loc:
        return "position"
    if "AutomationId" in loc:
        return "AutomationId + type" if "ControlType" in loc else "AutomationId"
    return "name + type"


def compare_locators(results: list[dict]) -> dict:
    total = Counter()
    by_kind = defaultdict(Counter)
    examples = defaultdict(list)
    best_ok = no_locator = 0
    for t in results:
        locs = sorted(t["locators"], key=lambda l: l["rank"])
        if not locs:
            no_locator += 1
            continue
        if locs[0]["findsTarget"]:
            best_ok += 1
        for l in locs:
            if l.get("problem"):
                k = "error"
            elif l["findsTarget"]:
                k = "finds exactly the element"
            elif l["count"] == 0:
                k = "finds nothing"
            elif l["count"] > 1:
                k = "finds several"
            else:
                k = "finds a different element"
            total[k] += 1
            by_kind[kind_of(l["locator"])][k] += 1
            if k != "finds exactly the element":
                examples[k].append(f"{t['element']}: {l['locator']} -> {l['count']} match(es)"
                                   + (f" ({l['problem']})" if l.get("problem") else ""))
    n = sum(total.values())
    return {
        "elementsChecked": len(results),
        "elementsWithoutLocator": no_locator,
        "locators": n,
        "outcome": dict(total),
        "unique": ratio(total["finds exactly the element"], n),
        "bestLocatorUnique": ratio(best_ok, len(results) - no_locator),
        "byKind": {k: dict(v) for k, v in by_kind.items()},
        "examples": {k: v[:EXAMPLES] for k, v in examples.items()},
    }


def ratio(a: int, b: int):
    return round(a / b, 4) if b else None


def main() -> int:
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    folder, out = Path(sys.argv[1]), Path(sys.argv[2])
    truth = load(folder, "truth.json")["elements"]
    live = load(folder, "live-elements.json", [])
    source = load(folder, "source-elements.json", [])
    report = load(folder, "live-report.json", {})
    checks = {c["id"]: c.get("percent") for c in report.get("overall", {}).get("checks", [])}
    result = {
        "groundTruthElements": len(truth),
        "groundTruthWindows": len({e["window"] for e in truth}),
        "liveScan": {
            "elements": len(live),
            "score": report.get("overall", {}).get("score"),
            "coveragePercent": checks.get("coverage"),
            "problemsByRule": dict(Counter(f["ruleId"] for f in report.get("findings", []))),
        },
        "liveVsTruth": compare_live(truth, live),
        "sourceVsTruth": compare_source(truth, source),
        "locators": compare_locators(load(folder, "locator-results.json", [])),
    }
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    lv, sv, lc = result["liveVsTruth"], result["sourceVsTruth"], result["locators"]
    print(f"live vs truth: {lv['paired']}/{lv['comparedControls']} controls paired, ID agreement {lv['idAgreement']}, "
          f"missed {lv['missedByLiveScan']}, extra {lv['onlyInLiveScan']}")
    print(f"source vs truth: sees {sv['sourceSeesControl']} of controls, ID agreement {sv['idAgreement']}, "
          f"false alarms {sv['falseAlarms']}")
    print(f"locators: {lc['locators']} checked, unique {lc['unique']}, best one unique {lc['bestLocatorUnique']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
