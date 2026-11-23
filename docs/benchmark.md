# Benchmark: how well does it work on real WPF apps?

Until now WPF Locator Doctor had only been tried on its own practice app. This benchmark runs it on 11 real open-source
WPF apps and checks its answers against an independent source of truth, so you can see how far to trust it.

It is repeatable: [`.github/workflows/benchmark.yml`](../.github/workflows/benchmark.yml) runs it every Monday, when started
by hand, and on every pull request that touches the scanning code. Everything it needs is in [`benchmark/`](../benchmark).

## In short

- **No crashes.** The source scan read all 11 apps (1,208 screens, 9,673 controls) in about 10 seconds in total, and gave
  exactly the same report every time, on macOS and on Linux.
- **94% of the problems it reports are real.** Of 220 problems picked at random (20 per app) and checked by hand, 207 were
  real and 13 were false alarms (with this sample size the true figure is very likely between 90% and 97%; each app counts
  equally, however many problems it has).
  "Control has no AutomationId", the most common problem, was right 97 times out of 98.
- **The live scan agreed with UI Automation on every control.** On 4 running apps, it found the same 180 controls, with the same
  IDs, as an independent dump of the UI Automation tree.
- **Every locator it recommended worked.** All 566 locators that `recommend_locators` gave for 262 elements found exactly the
  intended element when run with plain FlaUI.
- **Where it is weaker:** deciding whether a control has a readable name (88% right), and the "custom control is invisible"
  check (0 of 3 sampled were real problems). The reasons are listed under [What we found](#what-we-found).

## The apps

Each app is pinned to one commit in [`benchmark/apps.json`](../benchmark/apps.json), so results only change when this tool changes.
All licenses allow cloning and running the code for testing.

| App | License | Commit | Pinned from | Folder scanned | Live test |
|---|---|---|---|---|---|
| ILSpy | MIT | [1377eb6](https://github.com/icsharpcode/ILSpy/tree/1377eb6e7351b21112858c8c1df39848f40181ec) | tag v10.1.1 (the last WPF release; ILSpy moved to Avalonia in v11) | `ILSpy` |  |
| dnSpyEx | GPL-3.0 | [bb09d2c](https://github.com/dnSpyEx/dnSpy/tree/bb09d2c54718e0f73d01d2e1085ddb8772aee555) | branch master, 2026-11-18 | `.` |  |
| ScreenToGif | MS-PL | [a4d0a67](https://github.com/NickeManarin/ScreenToGif/tree/a4d0a67c2131cd048ceec86cd40afc2f1a06f2fd) | branch master, 2026-07-28 | `ScreenToGif` |  |
| Snoop | MS-PL | [2bf48f6](https://github.com/snoopwpf/snoopwpf/tree/2bf48f60c2bb1753001f6be9122395def9070fa9) | branch develop, 2026-08-23 | `Snoop.Core` |  |
| MahApps.Metro demo | MIT | [9466e8a](https://github.com/MahApps/MahApps.Metro/tree/9466e8acd96591e1f552841bc5357f376552bb57) | branch develop, 2026-11-02 | `src/MahApps.Metro.Samples/MahApps.Metro.Demo` | yes |
| MaterialDesignInXamlToolkit demo | MIT | [5b62695](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit/tree/5b626957f891f02dcb179cd0be754d8e782767aa) | branch master, 2026-11-23 | `src/MainDemo.Wpf` | yes |
| HandyControl demo | MIT | [e9ad3d7](https://github.com/HandyOrg/HandyControl/tree/e9ad3d701b90e724399a413569fbd7f35a141053) | branch master, 2026-10-15 | `src/Shared/HandyControlDemo_Shared` |  |
| WPF UI gallery (lepoco/wpfui) | MIT | [35eae0e](https://github.com/lepoco/wpfui/tree/35eae0e6975625ac3ff4b3426e89dcae66c8da06) | branch main, 2026-11-17 | `src/Wpf.Ui.Gallery` | yes |
| Microsoft WPF-Samples | MIT | [811d01e](https://github.com/microsoft/WPF-Samples/tree/811d01e95c8c929e68539d698d0a0609e94fd185) | branch main, 2026-09-16 | `.` | yes (GlobalizationHomepage sample) |
| NuGet Package Explorer | MIT | [e2b0d9c](https://github.com/NuGetPackageExplorer/NuGetPackageExplorer/tree/e2b0d9c8e0264d1580c1fc33a4bd866294856357) | branch main, 2026-10-08 | `PackageExplorer` |  |
| Playnite (desktop app) | MIT | [8c08a30](https://github.com/JosefNemec/Playnite/tree/8c08a305d68e287e71984ea691239212a6046cf7) | branch master, 2026-11-18 | `source/Playnite.DesktopApp` |  |

Together they cover plain WPF (ILSpy, ScreenToGif, Snoop, WPF-Samples), MVVM (dnSpyEx, NuGet Package Explorer, Playnite, the
control-library demos), themed control libraries (MahApps.Metro, MaterialDesign, HandyControl, WPF UI), `x:Uid` localization
(WPF-Samples' globalization samples; no larger open-source WPF app using `x:Uid` turned up), and apps that build much of their UI
in C# code (ILSpy, dnSpyEx).

## What was measured

**1. Source scan of every app** (any operating system, [`benchmark/source_benchmark.py`](../benchmark/source_benchmark.py)).
The script clones each app at its pinned commit, runs `scan-source` twice and records whether it crashed, how long it took,
the score and the number of problems of each type, and whether both runs gave the same report. The problems for each app are
kept in [`benchmark/results/source/`](../benchmark/results/source), one per line. These are the "golden files": if a code change
makes the tool report something different, the workflow fails and shows the difference, so every change in results is reviewed
on purpose.

**2. Live ground truth** (Windows, [`benchmark/live_benchmark.py`](../benchmark/live_benchmark.py)). For the 4 apps that are
easiest to build and start, the workflow builds the app, starts it and, on its first screen, compares three things:

- **The truth:** every element UI Automation shows, read by a small separate program,
  [`UiaDump`](../benchmark/live/UiaDump/Program.cs), that uses plain FlaUI and none of this project's code.
- **The live scan:** what `scan-live` reports for the same running app.
- **The source scan:** what `scan-source` reports for the app's XAML.

Controls are paired up by type and name (and ID where there is one), then the script
[`compare_live.py`](../benchmark/compare_live.py) checks whether each pair has the same AutomationId. A "control" here is
anything UI Automation calls a button, text box, check box, radio button, combo box, list, data grid, tree, tab, menu item,
slider, hyperlink, spinner, split button, calendar or document. Then, for each element (up to 600 per app), it asks the same
logic as `recommend_locators` for its locators and runs every one with plain FlaUI, to check it finds exactly that element.

**3. Hand-checked sample** ([`benchmark/labels/`](../benchmark/labels), [`benchmark/labels.py`](../benchmark/labels.py)).
For each app, 20 problems were picked at random from the source scan and each was labelled **real** (a tester would agree it
is a problem) or **false alarm** (it isn't, for example because the ID or name is set somewhere the scan doesn't look), after
reading the XAML and, where needed, the C# classes and the control library's styles. Each label has a one-line reason.
`python3 benchmark/labels.py score` recomputes the precision from the labels. The labels were made by one reviewer (the AI
assistant that built this benchmark), so a second opinion on any of them is welcome: they are plain JSON.

## Results

### Source scan

Times are from the GitHub Actions Linux runner.

| App | Styles | Crashed | Time | Same twice | Score | Screens | Controls | Problems | Precision (hand-checked) |
|---|---|---|---:|---|---:|---:|---:|---:|---:|
| ILSpy | plain WPF, controls built in code, docking layout | no | 0.8 s | yes | 54.7 (D) | 104 | 89 | 145 | 90% (18/20) |
| dnSpyEx | MVVM, controls built in code, plug-in extensions | no | 3.9 s | yes | 57.1 (D) | 113 | 960 | 1339 | 95% (19/20) |
| ScreenToGif | plain WPF, custom controls, heavy code-behind | no | 1.0 s | yes | 69 (C) | 61 | 1091 | 1407 | 100% (20/20) |
| Snoop | plain WPF, custom controls, tool windows | no | 0.5 s | yes | 65.2 (C) | 24 | 141 | 220 | 95% (19/20) |
| MahApps.Metro demo | themed control library, custom window chrome | no | 0.4 s | yes | 47.7 (D) | 38 | 709 | 1157 | 100% (20/20) |
| MaterialDesignInXamlToolkit demo | themed control library, MVVM | no | 0.4 s | yes | 49.8 (D) | 53 | 1174 | 1594 | 95% (19/20) |
| HandyControl demo | themed control library, MVVM, shared projects | no | 0.4 s | yes | 41.3 (D) | 137 | 1023 | 1558 | 100% (20/20) |
| WPF UI gallery (lepoco/wpfui) | themed control library, MVVM, navigation pages | no | 0.4 s | yes | 48.2 (D) | 82 | 402 | 358 | 90% (18/20) |
| Microsoft WPF-Samples | plain WPF, x:Uid localization, hundreds of small samples | no | 1.5 s | yes | 68.7 (C) | 506 | 2942 | 3518 | 95% (19/20) |
| NuGet Package Explorer | MVVM, plain WPF | no | 0.4 s | yes | 60.1 (C) | 24 | 261 | 366 | 75% (15/20) |
| Playnite (desktop app) | MVVM, themes where views live in styles | no | 0.6 s | yes | 52.2 (D) | 66 | 881 | 1337 | 100% (20/20) |
| **All 11 apps** | | 0 crashes | 10.4 s | | | 1208 | 9673 | 12999 | **94%** (207/220) |

The low scores are expected: open-source WPF apps rarely set AutomationIds (ILSpy and Snoop set none at all).

### Precision by problem type

| Rule | Problems (all apps) | Hand-checked | Precision |
|---|---:|---:|---:|
| WT001 Control has no AutomationId | 6592 | 98 | 99% |
| WT002 ID is borrowed from x:Name | 2474 | 36 | 100% |
| WT003 ID looks machine-generated | 45 | 0 | – |
| WT004 Same ID used more than once | 1 | 0 | – |
| WT005 List rows can't be told apart | 742 | 14 | 93% |
| WT006 Custom control is invisible to test tools | 71 | 3 | 0% |
| WT007 ID is set on an element test tools can't see | 16 | 2 | 100% |
| WT008 Control has no readable name | 3058 | 67 | 88% |

The sample is random, so rare rules got few or no checks. WT006 was looked at more closely below.

### Live ground truth

| App (first screen) | UI Automation elements | Controls compared | Live scan: same ID as the truth | Controls the live scan missed | Source scan finds the control | Source scan: same ID | Locators checked | Locators that find exactly the element |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| MahApps.Metro demo | 257 | 104 | 100% | 0 | 89% (62/70) | 100% | 199 (114 elements) | 100% |
| MaterialDesignInXamlToolkit demo | 73 | 18 | 100% | 0 | 67% (8/12) | 100% | 88 (46 elements) | 100% |
| WPF-Samples GlobalizationHomepage (uses x:Uid) | 100 | 42 | 100% | 0 | 95% (39/41) | 74% | 198 (70 elements) | 100% |
| WPF UI gallery | 95 | 16 | 100% | 0 | 38% (5/13) | 100% | 81 (32 elements) | 100% |

How to read it:

- **Live scan vs. the truth:** every control UI Automation showed was in the live scan, with the same ID or the same lack of one.
  The live scan leaves out, on purpose, the parts inside title bars, scroll bars and other controls (for example a combo box's
  drop-down button); those were not counted.
- **Source scan finds the control:** of the on-screen controls that have an ID or a name to pair by, how many the source scan
  found in the XAML. The misses are controls that come from a control library's templates, such as WPF UI's navigation and title
  bar buttons, MaterialDesign's `PART_Toggle`, and MahApps' window buttons. The source scan can't see those, as the
  README's Known limits say. Controls with no ID and no name can't be paired with the XAML at all, and are left out of this
  column (38 on MahApps' first screen, mostly icon-only buttons).
- **Source scan: same ID:** when it did find the control, whether it expected the same AutomationId UI Automation reported.
  The one app below 100% is explained by the `x:Uid` on hyperlinks issue below.
- **Locators:** every locator the tool recommended (by ID, by ID inside a parent, by name and type, and by position) found
  exactly the intended element. No source-scan false alarms ("no ID" when the control does have one) were found on these screens.

Full details, including examples of every disagreement, are in [`benchmark/results/live/`](../benchmark/results/live).

## What we found

### Fixed in this change

- **XAML files saved in a Windows code page were skipped.** Three files in WPF-Samples declare
  `encoding="Windows-1252"`; modern .NET can't read that until the code pages are registered, so the scan noted them as
  unreadable and left them out. The source scanner now registers them, with a test.

### Follow-ups (accuracy problems, not fixed here)

The scoring rules were left alone; these are the problems worth fixing next, most valuable first.

1. **`Label Target="SomeName"` isn't understood.** WPF accepts a plain name in `Target`, and the label then names the control.
   The scan only understands `Target="{Binding ElementName=SomeName}"`, so it reports "no readable name" on correctly labelled
   boxes: 4 of the 5 false alarms in NuGet Package Explorer, and about 29 problems across the apps.
2. **Names set by styles aren't seen.** MaterialDesign's text box, combo box and password box styles set
   `AutomationProperties.Name` from `HintAssist.Hint`, and some app styles set `Content` (dnSpyEx's `EllipsisButton` sets
   `"..."`). The scan reports "no readable name" for these: about 103 problems in the MaterialDesign demo alone.
3. **A menu item with only a `Command` gets the command's text as its name** (for example `Command="ApplicationCommands.Copy"`
   shows "Copy"). The scan reports it as unnamed: about 19 problems.
4. **`x:Uid` (and probably `x:Name`) on a `Hyperlink` doesn't become its AutomationId.** Hyperlinks are content elements, and
   WPF only falls back to `x:Uid` / `x:Name` for real controls. The live test showed it: 10 hyperlinks on the WPF-Samples
   globalization page have an `x:Uid` that the source scan counts as their ID, but UI Automation shows no ID for them.
5. **WT006 "custom control is invisible" over-reports.** All 3 sampled cases were wrappers or display-only controls whose
   insides are still visible to tests (ILSpy's `SharpTreeNodeView`, Snoop's value editors, WPF UI's `TypographyControl`).
   Looking over all 71, most are layout panels, page headers and editor shells of this kind; a few are real, such as dnSpyEx's
   window buttons built on `ButtonBase` and ScreenToGif's `ColorBox`. The rule should treat controls that contain other controls
   differently from controls a test must click. Also, the message "It isn't used in any XAML file" was wrong 33 times out of 46:
   those classes are used inside templates, styles or resource dictionaries, which the usage count skips.
6. **Markup is shown as if it were a name** in messages and suggested IDs, for example
   `MenuItem "{x:Static properties:Resources.Theme}"` or `"{StaticResource RemoveIcon}"` (taken from a button's icon), and
   a menu header written as `<AccessText>D</AccessText>elete` is shown as "elete". This doesn't change the score, but the
   messages and suggested IDs look odd.
7. **The live benchmark only reads each app's first screen** (about 180 controls in all). Opening more screens (tabs, navigation
   pages, dialogs) would make the live numbers stronger.

### Expected limits, confirmed

- Controls that come from a control library's templates (title bar and navigation buttons, `PART_` parts) are invisible to the
  source scan, and controls built in C# code are not in the XAML. The live scan sees both.
- A file that isn't valid XML (one sample in WPF-Samples is deliberately not) is reported as unreadable and skipped, without
  stopping the scan.

## Running it yourself

Source scan (Windows, macOS or Linux; needs git, Python 3 and the .NET 10 SDK):

```bash
dotnet build src/WpfLocatorDoctor.Cli -c Release -f net10.0
python3 benchmark/source_benchmark.py
```

Without `--check` it rewrites the golden files; review the diff with `git diff benchmark/results` before committing.
Clones go to a temporary folder (`--work` changes it).

Live ground truth (Windows with a desktop session; needs the .NET 8, 9 and 10 SDKs to build the apps):

```bash
dotnet build benchmark/live/UiaDump -c Release
dotnet build benchmark/live/LiveBench -c Release
python benchmark/live_benchmark.py --apps wpfui
```

It writes `benchmark/results/live/<app>.json`. In GitHub Actions the same files, plus the raw dumps, are in each run's artifacts.

Precision and the tables on this page:

```bash
python3 benchmark/labels.py score
python3 benchmark/summary.py
```

When a rule change alters the golden files, `labels.py score` lists any hand-checked problem that no longer exists; run
`python3 benchmark/labels.py sample` to draw replacements, label them, and score again.
