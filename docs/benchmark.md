# Benchmark: how well does it work on real WPF apps?

Until now WPF Locator Doctor had only been tried on its own practice app. This benchmark runs it on 11 real open-source
WPF apps and checks its answers against an independent source of truth, so you can see how far to trust it.

It is repeatable: [`.github/workflows/benchmark.yml`](../.github/workflows/benchmark.yml) runs it every Monday, when started
by hand, and on every pull request that touches the scanning code. Everything it needs is in [`benchmark/`](../benchmark).

## In short

- **No crashes.** The source scan read all 11 apps (1,208 screens, 9,674 controls) in about 10 seconds in total, and gave
  the same report both times it was run. (macOS and Linux could differ when two classes shared a name; see
  [What we found](#what-we-found).)
- **97% of the sampled problems were judged real.** Of 220 problems picked at random (20 per app), 213 were judged real and
  7 false alarms. The only reviewer was the AI assistant that built this benchmark. The sample only looks for false alarms,
  not for problems the scan misses. (If the labels are right, with this sample size the true figure is very likely between
  94% and 98%; each app counts equally, however many problems it has.) The first run found 94%; three fixes since then (see
  [What we found](#what-we-found)) removed 6 of its 13 false alarms. 46 of the 220 were also redrawn, so the two figures are
  not an exact like-for-like comparison.
  "Control has no AutomationId", the most common problem, was judged right 106 times out of 107.
- **The live scan agreed with UI Automation on every control.** On 4 running apps, it found the same 180 controls, with the same
  IDs, as an independent dump of the UI Automation tree.
- **Every locator it recommended worked.** All 566 locators that `recommend_locators` gave for 262 elements found exactly the
  intended element when run with plain FlaUI.
- **Where it is weaker:** the "custom control is invisible" check (0 of 3 sampled were real problems), and readable names
  set by a control library's styles, which the source scan doesn't read. The reasons are listed under
  [What we found](#what-we-found).

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

**3. Sample reviewed by the AI assistant** ([`benchmark/labels/`](../benchmark/labels), [`benchmark/labels.py`](../benchmark/labels.py)).
For each app, 20 problems were picked at random from the source scan and each was labelled **real** (a tester would agree it
is a problem) or **false alarm** (it isn't, for example because the ID or name is set somewhere the scan doesn't look), after
reading the XAML and, where needed, the C# classes and the control library's styles. Each label has a one-line reason.
`python3 benchmark/labels.py score` recomputes the precision from the labels. The labels were made by one reviewer (the AI
assistant that built this benchmark), so a second opinion on any of them is welcome: they are plain JSON.

## Results

### Source scan

Times are from one run on a Mac; the GitHub Actions Linux runner takes about 10 seconds in total.

| App | Styles | Crashed | Time | Same twice | Score | Screens | Controls | Problems | Precision (AI-reviewed) |
|---|---|---|---:|---|---:|---:|---:|---:|---:|
| ILSpy | plain WPF, controls built in code, docking layout | no | 0.6 s | yes | 54.8 (D) | 104 | 89 | 144 | 90% (18/20) |
| dnSpyEx | MVVM, controls built in code, plug-in extensions | no | 3.6 s | yes | 57.1 (D) | 113 | 960 | 1339 | 95% (19/20) |
| ScreenToGif | plain WPF, custom controls, heavy code-behind | no | 0.8 s | yes | 69 (C) | 61 | 1091 | 1407 | 100% (20/20) |
| Snoop | plain WPF, custom controls, tool windows | no | 0.4 s | yes | 65.2 (C) | 24 | 141 | 220 | 95% (19/20) |
| MahApps.Metro demo | themed control library, custom window chrome | no | 0.3 s | yes | 47.7 (D) | 38 | 709 | 1154 | 100% (20/20) |
| MaterialDesignInXamlToolkit demo | themed control library, MVVM | no | 0.3 s | yes | 49.8 (D) | 53 | 1174 | 1594 | 95% (19/20) |
| HandyControl demo | themed control library, MVVM, shared projects | no | 0.3 s | yes | 41.3 (D) | 137 | 1023 | 1558 | 100% (20/20) |
| WPF UI gallery (lepoco/wpfui) | themed control library, MVVM, navigation pages | no | 0.3 s | yes | 48.4 (D) | 82 | 402 | 353 | 95% (19/20) |
| Microsoft WPF-Samples | plain WPF, x:Uid localization, hundreds of small samples | no | 1.2 s | yes | 68.7 (C) | 506 | 2942 | 3512 | 95% (19/20) |
| NuGet Package Explorer | MVVM, plain WPF | no | 0.3 s | yes | 61.7 (C) | 24 | 261 | 329 | 100% (20/20) |
| Playnite (desktop app) | MVVM, themes where views live in styles | no | 0.4 s | yes | 52.2 (D) | 66 | 882 | 1338 | 100% (20/20) |
| **All 11 apps** | | 0 crashes | 8.5 s | | | 1208 | 9674 | 12948 | **97%** (213/220) |

The low scores are expected: open-source WPF apps rarely set AutomationIds (ILSpy and Snoop set none at all).

### Precision by problem type

| Rule | Problems (all apps) | AI-reviewed | Precision |
|---|---:|---:|---:|
| WT001 Control has no AutomationId | 6602 | 107 | 99% |
| WT002 ID is borrowed from x:Name | 2474 | 33 | 100% |
| WT003 ID looks machine-generated | 35 | 0 | – |
| WT004 Same ID used more than once | 1 | 0 | – |
| WT005 List rows can't be told apart | 742 | 14 | 93% |
| WT006 Custom control is invisible to test tools | 72 | 3 | 0% |
| WT007 ID is set on an element test tools can't see | 16 | 2 | 100% |
| WT008 Control has no readable name | 3006 | 61 | 97% |

The sample is random, so rare rules got few or no checks. WT006 was looked at more closely below.

### Live ground truth

| App (first screen) | UI Automation elements | Controls compared | Live scan: same ID as the truth | Controls the live scan missed | Source scan finds the control | Source scan: same ID | Locators checked | Locators that find exactly the element |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| MahApps.Metro demo | 257 | 104 | 100% | 0 | 89% (62/70) | 100% | 199 (114 elements) | 100% |
| MaterialDesignInXamlToolkit demo | 73 | 18 | 100% | 0 | 67% (8/12) | 100% | 88 (46 elements) | 100% |
| WPF-Samples GlobalizationHomepage (uses x:Uid) | 100 | 42 | 100% | 0 | 95% (39/41) | 100% | 198 (70 elements) | 100% |
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
  In the first run WPF-Samples was at 74%, because 10 hyperlinks' `x:Uid` was counted as their ID. After the fix below, the
  Windows run measured 100% (39 of 39).
- **Locators:** every locator the tool recommended (by ID, by ID inside a parent, by name and type, and by position) found
  exactly the intended element. No source-scan false alarms ("no ID" when the control does have one) were found on these screens.

Full details, including examples of every disagreement, are in [`benchmark/results/live/`](../benchmark/results/live).

## What we found

### Fixed in this change

- **XAML files saved in a Windows code page were skipped.** Three files in WPF-Samples declare
  `encoding="Windows-1252"`; modern .NET can't read that until the code pages are registered, so the scan noted them as
  unreadable and left them out. The source scanner now registers them, with a test.

### Fixed after the first run

These three were found by the first run and fixed in the source scan afterwards, each with tests. Together they removed
6 of the 13 false alarms in the sample. Because the fixes changed the problem lists of five apps, those apps'
samples were redrawn, and the 46 new picks were reviewed by the AI assistant in the same way.

- **`Label Target="SomeName"` is now understood.** WPF accepts a plain element name in `Target`, as well as
  `{Binding ElementName=...}` and `{x:Reference ...}`, and the label then names the control. The scan used to report
  "no readable name" for these boxes: 29 problems, most of them in NuGet Package Explorer.
- **A menu item with only a built-in `Command` is now named after the command.** A `MenuItem` with no `Header` and
  `Command="ApplicationCommands.Copy"` (or `Command="Copy"`, or `{x:Static ApplicationCommands.Copy}`) shows the command's
  text, "Copy". This works for WPF's own command classes (`ApplicationCommands`, `NavigationCommands`, `EditingCommands`,
  `ComponentCommands`, `MediaCommands`), with the English texts taken from WPF's source; the app's own commands still need a `Header`. 23 "no readable name" problems went.
- **`x:Uid` no longer counts as the AutomationId of a `Hyperlink`.** The live test showed 10 hyperlinks on the WPF-Samples
  globalization page with an `x:Uid` and no ID in UI Automation, and
  [WPF's source](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Automation/Peers/FrameworkContentElementAutomationPeer.cs)
  confirms that hyperlinks (and `Table`, `TableCell`) skip `x:Uid` but do use `x:Name`. The 10 are now reported as
  "no AutomationId" instead of "ID looks machine-generated". Other text parts, such as `Run` and `Paragraph`, are never shown to
  UI Automation as elements of their own, so they are no longer listed. No app here has a named hyperlink on a scanned screen,
  so the `x:Name` part rests on WPF's source and a unit test, not a live test.

Fixed separately, after CI on Linux gave a different dnSpyEx report from macOS:

- **The report could depend on the order the disk lists files in.** When two C# classes have the same name, the first one read
  wins, so dnSpyEx's `ZoomingUIElement` was reported from a different file on Linux. C# files are now read in a fixed order,
  with a test. This also added one WT006 in Playnite, whose `<cv:MainWindow>` had been mistaken for its window class. Classes
  are still matched by short name only, so a wrong match between same-named classes remains possible.

### Follow-ups (accuracy problems, not fixed yet)

The scoring rules were left alone; these are the problems worth fixing next, most valuable first.

1. **Names set by styles aren't seen.** MaterialDesign's text box, combo box and password box styles set
   `AutomationProperties.Name` from `HintAssist.Hint`, and some app styles set `Content` (dnSpyEx's `EllipsisButton` sets
   `"..."`). The scan reports "no readable name" for these: about 103 problems in the MaterialDesign demo alone.
2. **WT006 "custom control is invisible" over-reports.** All 3 sampled cases were wrappers or display-only controls whose
   insides are still visible to tests (ILSpy's `SharpTreeNodeView`, Snoop's value editors, WPF UI's `TypographyControl`).
   Looking over all 71 in the first run, most are layout panels, page headers and editor shells of this kind; a few are real, such as dnSpyEx's
   window buttons built on `ButtonBase` and ScreenToGif's `ColorBox`. The rule should treat controls that contain other controls
   differently from controls a test must click. Also, the message "It isn't used in any XAML file" was wrong 33 times out of 46:
   those classes are used inside templates, styles or resource dictionaries, which the usage count skips.
3. **Markup is shown as if it were a name** in messages and suggested IDs, for example
   `MenuItem "{x:Static properties:Resources.Theme}"` or `"{StaticResource RemoveIcon}"` (taken from a button's icon), and
   a menu header written as `<AccessText>D</AccessText>elete` is shown as "elete". This doesn't change the score, but the
   messages and suggested IDs look odd. The `Label Target` fix makes a few more of these visible, because a label whose
   text is a translated resource now names its control with that markup.
4. **An empty combo box is reported as having rows that can't be told apart.** A `ComboBox` with no items, no
   `ItemsSource` and no name for code to fill it has no rows at all. This is 3 of the 30 such combo boxes reported in
   WPF-Samples; the two in the samples (one in each run) were judged false alarms.
5. **The live benchmark only reads each app's first screen** (about 180 controls in all). Opening more screens (tabs, navigation
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

When a rule change alters the golden files, `labels.py score` lists any labelled problem that no longer exists; run
`python3 benchmark/labels.py sample` to draw replacements, label them, and score again.
