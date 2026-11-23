# Why WPF Locator Doctor matters

*For automation testers working on WPF apps, and for the developers they work with.*

## The problem in one sentence

A UI test can only click a button it can find, and in most WPF apps, finding things is the hardest part of test automation.

## What testing a WPF app is like today

UI test tools (FlaUI, Appium with WinAppDriver, Ranorex, TestComplete and others) find a control by its **AutomationId**,
a label a developer puts on it. If the label is good, the test is simple and solid. If it isn't, problems follow:

- **No ID at all.** The tester falls back to the control's text ("Save"), its position ("the third button"), or a long path
  through the window. These break as soon as someone changes a word, translates the app, or moves a panel.
- **IDs that look fine but aren't.** In WPF, if nobody set an AutomationId, test tools quietly get the developer's `x:Name`
  instead, or an `x:Uid` made by a translation tool (`Button_12`). Tests work today, then break when a developer renames a field
  or the translation tool numbers things differently. Nobody knew a test depended on it.
- **The same ID twice.** Two buttons called `ActionButton` on one screen. The test clicks whichever it finds first.
- **List rows that all look the same.** Every row of an orders list has the same "Remove" button, and nothing tells row 1
  from row 42.
- **Controls test tools can't see at all.** A custom control (a star rating, a chart, a fancy toggle) that wasn't built to
  announce itself to Windows UI Automation is simply invisible. No locator will ever find it.
- **Hours in inspect tools.** Testers open Inspect.exe, FlaUInspect or Accessibility Insights, click around screen by screen,
  and write down what they find. It's slow, and it's only a snapshot of one moment.
- **Flaky tests that aren't really flaky.** Many "random" failures come back to a weak locator. The test gets the blame, and
  the tester spends the morning re-recording it.

## The harder problem: the conversation with developers

Most of these problems can only be fixed in the app's code, by a developer adding one line of XAML. But asking for it is hard:

- "Can you add IDs to the app?" is vague. Which controls? How many? Which screen first?
- "The tests are flaky" sounds like a testing problem, not a code problem.
- Testers often don't have the file name and line number, so the request turns into a meeting.
- Developers rename and refactor without knowing which names tests rely on, so the same problems come back.

Without numbers and specifics, testability stays a favour testers ask for, instead of a quality bar the team agrees on.

## What WPF Locator Doctor does

It checks a WPF app the way a test tool sees it, and gives every screen and the whole app a **score from 0 to 100 and a grade
from A to F**, based on six checks:

| Check | The question it answers |
|---|---|
| ID coverage | Do the controls a tester needs (buttons, text boxes, lists, menus, tabs…) have an AutomationId? |
| ID quality | Were IDs set on purpose for testing, or borrowed from `x:Name`, or made by a tool? |
| Uniqueness and rows | Is each ID used only once per screen? Can each row of a list be told apart? |
| Stability | Are the IDs still the same as last time? (Compared with an earlier report.) |
| Visible to test tools | Can test tools see every control, including custom ones? |
| Readable names | Does each control have a readable name? (This also helps screen-reader users.) |

Every problem comes with **the file, the line, and the exact line of XAML to add**. For example:

> **High: Button has no AutomationId.** (`MainWindow.xaml:29`)
> Fix: add `AutomationProperties.AutomationId="RefreshButton"` to this element.

## How it changes the conversation with developers

Instead of "the tests are flaky, can you add IDs?", a tester can say:

> "Our app scores **C (68/100)** for testability. The Orders screen is the worst at 52. There are 14 high-priority problems,
> each with the file, line and fix. Most are one line of XAML. Can we fix the Orders screen this sprint?"

That is a request a developer can say yes to. It is specific, it's measured, and it shows the work is small.

Then the team can keep it from slipping back:

- **Agree on a minimum score** and let the build check it: `--min-score 75` fails the build if the score drops below 75.
- **Catch renamed or removed IDs before tests break**: `--baseline last-report.json` lists every ID that disappeared since the
  last report, so a developer sees "this rename will break tests" in their own pull request.
- **Share the report**: the Markdown report is easy to read for people, and the JSON report is there for tools and dashboards.

Testability becomes part of the definition of done, like code coverage or linting, rather than something testers chase.

## Two ways to use it

| | **Command line (CLI)** | **MCP server for AI assistants** |
|---|---|---|
| Who it suits | Anyone, and build pipelines | People who work with an AI assistant (Claude Code, GitHub Copilot in VS Code, Cursor, Claude Desktop…) |
| How you use it | Run `wpf-locator-doctor scan-source path/to/YourApp` and read the report | Ask in plain words: *"How testable is this app, and what should we fix first?"* |
| What you get | A score, a report with every problem and fix, and exit codes for the build | The same score and fixes, plus the assistant can **make the XAML edits for you to review**, and check no existing ID disappeared |
| Running app (Windows) | `scan-live` scores the open windows as test tools see them | The assistant can open the app, see the screen, recommend the most reliable locator for any control (checked to find exactly one), click, type, wait and take screenshots |

Both read the source code on Windows, macOS and Linux. Reading the running app needs Windows.

## Where it helps

- **Starting automation on an existing app.** Scan it first. You'll know in seconds which screens are ready to automate and
  which need IDs, instead of finding out one failed locator at a time.
- **Planning with developers.** Bring the score and the worst screens to sprint planning. Ask for the top problems, not "IDs
  everywhere".
- **Every pull request.** Run the scan in the build so new screens arrive with good IDs and renames don't silently break tests.
- **Writing new tests.** With the MCP server on Windows, ask for the best locator for a control and get ready-to-paste FlaUI or
  Appium C#, already checked against the running app.
- **Fixing flaky tests.** Check whether the locator is the cause: does it find exactly one element, or two, or none?
- **Accessibility.** Readable names and controls that Windows can see are the same things screen readers need. Fixing them helps
  real users too.

## Honest limits

- The source scan can't see controls created only in C# code, and it guesses third-party controls (Telerik, DevExpress…) from
  their class names. The live scan covers both, on Windows.
- The live scan sees only the windows that are open and the list rows on screen. Open other screens and scan again.
- It is new. It is tested on every change against the included practice app, but it hasn't yet been tried on many large
  real-world apps. If you try it on yours, please [open an issue](https://github.com/swechhap/wpf-locator-doctor/issues) with
  what you find. That feedback shapes what comes next.

## Coming next

Self-healing for broken locators in FlaUI and Appium tests, and automatic pull requests that fix the test code or add the missing
IDs to the app. See [research-analysis.md](research-analysis.md).

## Get started

Command line (needs the [.NET SDK](https://dotnet.microsoft.com/download) 10 or newer):

```bash
dotnet tool install -g wpf-locator-doctor
```

```bash
wpf-locator-doctor scan-source path/to/YourWpfProject
```

MCP server, for example in Claude Code:

```bash
claude mcp add --scope user wpf-locator-doctor -- dotnet dnx wpf-locator-doctor-mcp --yes
```

For the Windows tools that work with the running app, download `wpf-locator-doctor-mcp-win-x64.zip` or the `.mcpb` bundle from
the [releases page](https://github.com/swechhap/wpf-locator-doctor/releases). Full setup for VS Code, Cursor and Claude Desktop:
[mcp-server.md](mcp-server.md).

It's free and open source under the [MIT license](../LICENSE).
