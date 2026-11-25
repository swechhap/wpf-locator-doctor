# WPF Locator Doctor

[![M8ven Score](https://m8ven.ai/badge/mcp/swechhap-wpf-locator-doctor-p376m3?v=bdd0d74fbf257e75b17de6b1acc36e3c)](https://m8ven.ai/mcp/swechhap-wpf-locator-doctor-p376m3?s=readme)

**Diagnose, heal and fix the locators in your WPF UI automation tests.**

Finding good locators in a WPF app usually means hours in inspect tools (Inspect.exe, FlaUInspect, Accessibility Insights),
and tests still break when IDs are missing, duplicated or renamed. WPF Locator Doctor checks your app for you.

**Today: the testability scorecard.** It checks whether the controls a tester needs (buttons, text boxes, lists, menus, tabs…) have good **AutomationIds**:
are they there, set on purpose, unique, still the same as last time, and actually visible to test tools.
It gives each screen and the whole app a score from 0 to 100 and a grade from A to F. Every problem comes with the file, the line, and the line of XAML to add.

**New: an MCP server** so AI assistants (Claude Code, Copilot, Cursor…) can score your project and add the missing IDs for you,
and, on Windows, explore the running app: see the screen as test tools do, get ranked locators that are checked to find exactly
one element, click, type, wait and take screenshots.
See [Use it from an AI assistant](#use-it-from-an-ai-assistant-mcp-server).

**Coming next:** self-healing for broken locators in FlaUI / Appium tests, and automatic pull requests that fix the test code or
add the missing IDs to the app.
See [docs/research-analysis.md](docs/research-analysis.md) for the research and plan.

Works with any WPF app (.NET Framework or .NET). Built on Windows UI Automation and [FlaUI](https://github.com/FlaUI/FlaUI).

**Why it matters:** the problems WPF automation testers face every day, how a score with exact fixes helps them agree with
developers on what to change, and where the CLI and the MCP server fit: [docs/why-it-matters.md](docs/why-it-matters.md).

## What you get

This is a real scan of the included practice app (shortened):

```text
$ wpf-locator-doctor scan-source samples/PracticeApp

Overall: grade B, score 86.5/100. Good. A few gaps to fix.

  ID coverage                 80.6%   25 of 31 controls have an AutomationId.
  ID quality                  90.4%   19 set on purpose, 5 borrowed from x:Name, 1 look generated.
  Uniqueness and rows         87.1%   1 ID(s) used more than once; 1 of 2 data lists have identifiable rows.
  Visible to test tools       93.8%   2 invisible control(s), 0 opaque area(s).
  Readable names              89.7%   26 of 29 controls have a readable name.

Screens (worst first):
  B     80.5  MainWindow  (21 controls, 14 problems)
  A     92.5  AddressPanel  (3 controls, 3 problems)
  A      100  SettingsWindow  (7 controls, 0 problems)

Problems: 18 (6 high, 4 medium, 8 low)
  HIGH   WT001 Button has no AutomationId.  [MainWindow.xaml:29]
  HIGH   WT004 "ActionButton" is used 2 times on this screen (line 44, line 45).  [MainWindow.xaml:44]
  HIGH   WT006 StarRating derives from Control and doesn't override OnCreateAutomationPeer, so test tools can't see it.  [Controls/StarRating.cs:13]
  MEDIUM WT003 Button "Clear" has the ID "Button_1", which looks machine-generated.  [MainWindow.xaml:27]
  MEDIUM WT005 Rows in ListBox "Recent orders" have no unique ID.  [MainWindow.xaml:57]
  …
```

The full report (`testability-report.md`) tells you exactly what to add:

> - **High: Control has no AutomationId.** Button has no AutomationId. (`MainWindow.xaml:29`)
>   - Fix: Add `AutomationProperties.AutomationId="RefreshButton"` to this element.
> - **Medium: ID looks machine-generated.** Button "Clear" has the ID "Button_1", which looks machine-generated. (`MainWindow.xaml:27`)
>   - Fix: Replace it with a meaningful ID, e.g. `AutomationProperties.AutomationId="ClearButton"`.

## Two ways to scan

| | **Source scan** (`scan-source`) | **Live scan** (`scan-live`) |
|---|---|---|
| Reads | The project's XAML and C# files | The running app, through Windows UI Automation (the same thing test tools use) |
| Runs on | Windows, macOS, Linux | Windows only, with an unlocked desktop |
| Covers | Every screen in the project, even ones nobody opened | Only windows that are open, and list rows that are on screen |
| Good for | Quick checks on every code change; exact file and line to fix | The true picture of what tests will see, including third-party controls |

## Quick start

### Source scan (Windows, macOS, Linux)

Install from [NuGet](https://www.nuget.org/packages/wpf-locator-doctor). You need the [.NET SDK](https://dotnet.microsoft.com/download), version 10 or newer:

```bash
dotnet tool install -g wpf-locator-doctor
```

Then scan a project:

```bash
wpf-locator-doctor scan-source path/to/YourWpfProject
```

### Live scan (Windows)

Download `wpf-locator-doctor-live-win-x64.zip` from the [latest release](https://github.com/swechhap/wpf-locator-doctor/releases), unzip it, and run:

```bash
wpf-locator-doctor.exe scan-live --exe path/to/YourApp.exe --source path/to/YourWpfProject
```

It needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). The Windows download also includes the source scan.

### Run from the source code

Clone this repo and try it on the included practice app, which has one example of every problem:

```bash
dotnet run --project src/WpfLocatorDoctor.Cli -f net10.0 -- scan-source samples/PracticeApp
```

For the live scan from source, use `-f net10.0-windows` and `scan-live` instead.

### Reports

Both scans write two files into `./testability-report/`:

- `testability-report.md`: the report for people (grade, what each check found, every problem with its fix).
- `testability-report.json`: the same data for tools. Pass it as `--baseline` next time to check that no IDs disappeared.

## Use it from an AI assistant (MCP server)

The MCP server gives your AI assistant tools to work on your app's locators. Then just ask, for example:
*"Score src/MyApp and add the missing AutomationIds to the worst screen."* or
*"Start the app, open the settings window and give me the best locator for its OK button."*

| Where | Tools | Get it |
|---|---|---|
| Any OS (reads source code) | `score_source`, `list_problems`, `suggest_xaml_fixes` (exact line edits with unique new IDs), `compare_to_baseline`, `explain_rules` | NuGet package `wpf-locator-doctor-mcp`, run with `dnx` |
| Windows (adds the running app) | all of the above, plus `open_app`, `snapshot_screen`, `recommend_locators`, `check_locator`, `act`, `wait_for`, `take_screenshot`, `score_live_app`, `close_app` | `wpf-locator-doctor-mcp-win-x64.zip` or `.mcpb` on the [releases page](https://github.com/swechhap/wpf-locator-doctor/releases) |

Add it to Claude Code (VS Code and Cursor are similar; see the guide):

```bash
claude mcp add wpf-locator-doctor -- dnx wpf-locator-doctor-mcp --yes
```

On Windows, point it at the downloaded server instead:

```bash
claude mcp add wpf-locator-doctor -- C:\tools\wpf-locator-doctor-mcp\wpf-locator-doctor-mcp.exe
```

If your shell can't find `dnx` (for example with .NET from Homebrew), use `dotnet dnx` instead.
The server is also listed in the [MCP Registry](https://registry.modelcontextprotocol.io) as `io.github.swechhap/wpf-locator-doctor`.
Full guide, with VS Code and Cursor setup, safety settings and example answers: [docs/mcp-server.md](docs/mcp-server.md).

## Using it in a build pipeline

```bash
wpf-locator-doctor scan-source src/MyApp --min-score 75 --baseline last-report.json --quiet
```

Exit codes: `0` OK · `1` score below `--min-score` · `2` wrong usage · `3` the scan failed.

## How the score works

Each check gives a percentage. The score is the weighted average of the checks that could be measured.

| Check | Weight | What it asks |
|---|---:|---|
| ID coverage | 30 | Of everything a user can click, type into or select, how much has an AutomationId? |
| ID quality | 15 | Were IDs set on purpose (best), borrowed from `x:Name` (OK), borrowed from `x:Uid`, or do they look machine-generated like `Button_12` (bad)? |
| Uniqueness and rows | 20 | Is each ID used once per screen? Can the rows in each list be told apart? |
| Stability | 15 | Are the IDs from the previous report still there? (Only measured with `--baseline`.) |
| Visible to test tools | 10 | Are there custom controls that test tools can't see at all? |
| Readable names | 10 | Does each control have a readable label? (Also matters for screen-reader users.) |

Grades: **A** 90+ · **B** 75–89 · **C** 60–74 · **D** 40–59 · **F** below 40.

Run `wpf-locator-doctor rules` to list every problem type (WT001–WT012) with a plain-English explanation.

### Why "where the ID came from" matters

When a test asks a WPF control for its AutomationId, WPF returns the first of these that is set:

1. `AutomationProperties.AutomationId`: set on purpose for testing. **Best.**
2. `x:Uid`: meant for translation tools, often auto-generated (`Button_12`), so it changes.
3. `x:Name`: the developer's own name for the control, which they may rename without knowing a test uses it.

### Why some controls are invisible to test tools

A custom control built directly on `Control`, `ContentControl`, `ButtonBase`, `RangeBase`, `TextBoxBase` or `HeaderedContentControl`
doesn't announce itself to UI Automation unless it overrides `OnCreateAutomationPeer`. Test tools can't see it, and any AutomationId set on it goes nowhere.
The source scan reads the C# code to find these.

## Project layout

```
src/WpfLocatorDoctor.Core     The engine: XAML + C# scanning, scoring, reports, and the rules for judging
                              each element of a running app. No Windows dependency, so it runs (and is tested) anywhere.
src/WpfLocatorDoctor.Live     A thin Windows-only layer: starts or attaches to the app, reads its UI and carries out
                              actions through FlaUI, and hands everything to the Core rules.
src/WpfLocatorDoctor.Cli      The wpf-locator-doctor command (CliApp.cs holds the logic; Program.cs only starts it).
src/WpfLocatorDoctor.Mcp      The MCP server for AI assistants. Only describes the tools; the answers come from Core (Assistant/).
                              Built twice: cross-platform (source tools, NuGet) and Windows (adds the live tools).
samples/PracticeApp           A small WPF app with one example of each problem (and good examples).
tests/                        Automated tests (run with: dotnet test WpfLocatorDoctor.slnx).
benchmark/                    Runs the tool on 11 open-source WPF apps and checks it against UI Automation (docs/benchmark.md).
docs/benchmark.md             Benchmark results: crashes, speed, precision, live agreement, and follow-ups.
docs/why-it-matters.md        Why testability matters, for testers and the developers they work with.
docs/mcp-server.md            How to set up and use the MCP server.
docs/research-analysis.md     Research behind this project.
```

The rule of thumb: anything that makes a decision lives in Core and has tests. The Windows-only layer only reads
values from the screen, so as little as possible depends on having Windows to test it.

## Tests and code quality

```bash
dotnet test WpfLocatorDoctor.slnx
```

| What | How it is tested | Where |
|---|---|---|
| Reading XAML and C# (source scan) | Small XAML/C# snippets, temporary project folders, and the practice app | `XamlSourceScannerTests`, `CodeBehindAnalyzerTests`, `EdgeCaseTests`, `PracticeAppTests` |
| Scores, grades and problem reports | Hand-built scans with known answers | `ScorerTests`, `EdgeCaseTests` |
| Live scan decisions (what counts as a control, rows, opaque areas, limits) | A fake UI tree, so no Windows or running app is needed | `LiveTreeWalkerTests` |
| ID suggestions and reports (Markdown, JSON, terminal) | Direct checks of the output text | `HeuristicsAndReportTests`, `EdgeCaseTests` |
| The command line: every command, option, error message and exit code | Runs the command in-process against temporary folders | `tests/WpfLocatorDoctor.Cli.Tests` |
| The MCP tools' answers: scores, filters, suggested IDs, exact XAML edits, baseline comparison, error messages | The practice app and temporary projects; one test applies every suggested edit and re-scores | `ProjectDoctorTests`, `XamlFixPlannerTests` |
| The live MCP tools' decisions: what a snapshot shows, locator ranking and code, matching, how to click or type, waiting, allowed apps | A fake app, so no Windows or running app is needed | `LiveSessionTests` |
| The MCP server as an assistant sees it: tool list, JSON answers, error messages | Starts the real server and talks to it with the official MCP client | `tests/WpfLocatorDoctor.Mcp.Tests` |
| The live scan and the live MCP tools against a real running app | GitHub Actions starts the practice app on Windows, scans it, then drives it through the MCP server (snapshot, locators, type, menu, wait, screenshot, score) | `.github/workflows/ci.yml` (`live-scan` job), `LiveToolsTests` |

Quality checks run on every build: .NET's recommended code-analysis rules are on, and any warning fails the build.
GitHub Actions builds and tests on Linux and Windows for every push and pull request.

## Known limits

- The source scan can't see controls created only in C# code, and it guesses third-party controls (Telerik, DevExpress…) from their class names.
- The live scan only sees open windows and the list rows on screen. Open other screens and scan again.
- The live scan runs in CI on every push: it starts the practice app on Windows, scans it, and fails the build if the scan breaks.
- A [benchmark on 11 open-source WPF apps](docs/benchmark.md) (ILSpy, dnSpyEx, MahApps.Metro, MaterialDesign and others) shows how
  far to trust the results, and where that evidence stops:
  - **Source scan:** no crashes on any of the 11 apps. In a sample of 220 reported problems, 213 (97%) were judged real. The only
    reviewer was the AI assistant that built the benchmark, so a second opinion is welcome. The sample only looks for false alarms;
    it doesn't measure the problems the scan misses.
  - **Uneven by rule:** machine-generated IDs (WT003) and duplicate IDs (WT004) weren't in the sample, and the "invisible custom
    control" check (WT006) was wrong in all 3 cases sampled, because it over-reports controls that only wrap other controls
    or display something.
    Readable names set by a control library's styles can be reported as missing.
  - **Live scan:** tested on the first screen of 4 of the apps only. It matched UI Automation on all 180 controls compared, and all 566
    recommended locators found exactly the intended element. On those screens the source scan found 38% to 95% of the controls it
    could be compared on; most of the rest come from control libraries' templates.

## License

[MIT](LICENSE). Free to use, change and share, including in commercial projects.
The downloads also contain libraries under their own licenses: see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
