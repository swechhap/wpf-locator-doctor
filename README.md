# WPF Locator Doctor

[![M8ven Score](https://m8ven.ai/badge/mcp/swechhap-wpf-locator-doctor-p376m3?v=bdd0d74fbf257e75b17de6b1acc36e3c)](https://m8ven.ai/mcp/swechhap-wpf-locator-doctor-p376m3?s=readme)

**Diagnose, heal and fix the locators in your WPF UI automation tests.**

Finding good locators in a WPF app usually means hours in inspect tools (Inspect.exe, FlaUInspect, Accessibility Insights),
and tests still break when IDs are missing, duplicated or renamed. WPF Locator Doctor checks your app for you.

**Today: the testability scorecard.** It checks whether the controls a tester needs (buttons, text boxes, lists, menus, tabs…) have good **AutomationIds**:
are they there, set on purpose, unique, still the same as last time, and actually visible to test tools.
It gives each screen and the whole app a score from 0 to 100 and a grade from A to F. Every problem comes with the file, the line, and the line of XAML to add.

**Coming next:** an MCP server so AI assistants (Claude Code, Copilot, Cursor…) can find locators for you, self-healing for broken locators in
FlaUI / Appium tests, and automatic pull requests that fix the test code or add the missing IDs to the app.
See [docs/research-analysis.md](docs/research-analysis.md) for the research and plan.

Works with any WPF app (.NET Framework or .NET). Built on Windows UI Automation and [FlaUI](https://github.com/FlaUI/FlaUI).

## Two ways to scan

| | **Source scan** (`scan-source`) | **Live scan** (`scan-live`) |
|---|---|---|
| Reads | The project's XAML and C# files | The running app, through Windows UI Automation (the same thing test tools use) |
| Runs on | Windows, macOS, Linux | Windows only, with an unlocked desktop |
| Covers | Every screen in the project, even ones nobody opened | Only windows that are open, and list rows that are on screen |
| Good for | Quick checks on every code change; exact file and line to fix | The true picture of what tests will see, including third-party controls |

## Quick start

You need the [.NET SDK](https://dotnet.microsoft.com/download) (version 10 or newer).

Scan a project's source (any operating system):

```bash
dotnet run --project src/WpfLocatorDoctor.Cli -f net10.0 -- scan-source path/to/YourWpfProject
```

Scan the running app (Windows):

```bash
dotnet run --project src/WpfLocatorDoctor.Cli -f net10.0-windows -- scan-live --exe path/to/YourApp.exe --source path/to/YourWpfProject
```

Try it on the included practice app, which has one example of every problem:

```bash
dotnet run --project src/WpfLocatorDoctor.Cli -f net10.0 -- scan-source samples/PracticeApp
```

Both scans write two files into `./testability-report/`:

- `testability-report.md`: the report for people (grade, what each check found, every problem with its fix).
- `testability-report.json`: the same data for tools. Pass it as `--baseline` next time to check that no IDs disappeared.

## Using it in a build pipeline

```bash
dotnet run --project src/WpfLocatorDoctor.Cli -f net10.0 -- scan-source src/MyApp --min-score 75 --baseline last-report.json --quiet
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
src/WpfLocatorDoctor.Live     A thin Windows-only layer: starts or attaches to the app and reads its UI
                              through FlaUI, then hands each element to the Core rules.
src/WpfLocatorDoctor.Cli      The wpf-locator-doctor command (CliApp.cs holds the logic; Program.cs only starts it).
samples/PracticeApp           A small WPF app with one example of each problem (and good examples).
tests/                        Automated tests (run with: dotnet test WpfLocatorDoctor.slnx).
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
| The live scan against a real running app | GitHub Actions starts the practice app on Windows and scans it | `.github/workflows/ci.yml` (`live-scan` job) |

Quality checks run on every build: .NET's recommended code-analysis rules are on, and any warning fails the build.
GitHub Actions builds and tests on Linux and Windows for every push and pull request.

## Known limits

- The source scan can't see controls created only in C# code, and it guesses third-party controls (Telerik, DevExpress…) from their class names.
- The live scan only sees open windows and the list rows on screen. Open other screens and scan again.
- The live scan runs in CI on every push: it starts the practice app on Windows, scans it, and fails the build if the scan breaks.
  It has not yet been tried on large real-world apps, so expect rough edges there.

## License

[MIT](LICENSE). Free to use, change and share, including in commercial projects.
