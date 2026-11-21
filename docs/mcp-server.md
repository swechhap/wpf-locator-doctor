# The MCP server: WPF Locator Doctor for AI assistants

The MCP server lets an AI assistant (Claude Code, GitHub Copilot in VS Code, Cursor, Claude Desktop and others) use WPF Locator
Doctor for you. You ask in plain words, for example *"How testable is this WPF app, and what should we fix first?"* or
*"Start the app and find me a reliable locator for the Save button"*, and the assistant calls the tools below, reads the answers,
and can make the XAML changes for you to review.

MCP (Model Context Protocol) is the standard way to give an AI assistant extra tools. The server is a small program that runs
on your computer. The assistant starts it and talks to it through standard input and output, so nothing is sent anywhere
except to the assistant you already use.

## Two builds

| | Source tools (any OS) | Windows build |
|---|---|---|
| Reads | The project's XAML and C# files | The source **and** the running app (through Windows UI Automation, like test tools) |
| Tools | 5 (below) | the same 5, plus 9 for the running app |
| Runs on | Windows, macOS, Linux | Windows only, with the .NET 10 Desktop Runtime (or newer) |
| Get it | NuGet package `wpf-locator-doctor-mcp`, run with `dnx` | `wpf-locator-doctor-mcp-win-x64.zip` or `.mcpb` on the [releases page](https://github.com/swechhap/wpf-locator-doctor/releases) |

Why two? Reading a running app needs Windows-only parts of .NET, and a .NET tool on NuGet has to run everywhere.
So the NuGet package has what works everywhere, and the Windows download has everything.

## The tools

### From the source code (both builds)

| Tool | What it does |
|---|---|
| `score_source` | Scores the project from 0 to 100 with a grade (A to F), for the whole app and for each screen, shows the six checks behind the score and the top problems. Can save the full Markdown and JSON reports. **Start here.** |
| `list_problems` | Lists the problems, most severe first, each with its file, line and a plain-English fix. Can show only one screen, one severity, or one problem type (WT001 to WT012). |
| `suggest_xaml_fixes` | Works out the exact XAML change for each missing, borrowed, machine-made or duplicated AutomationId: a new ID that is unique on its screen, and the line as it is now and as it should be. Also covers list rows that can't be told apart and custom controls test tools can't see. **It changes no files**; the assistant applies the edits, so you can review them. |
| `compare_to_baseline` | Compares the project now with an earlier JSON report: did the score go up or down, which IDs disappeared (tests that use them will break), and which are new. |
| `explain_rules` | Explains every check, problem type and grade in plain English. |

### With the running app (Windows build)

| Tool | What it does |
|---|---|
| `open_app` | Starts the app from its `.exe`, or attaches to one that is already running (by name or process ID). One app at a time. |
| `snapshot_screen` | Shows the open windows as an outline of the elements that matter: things to click, type into or select, lists and rows, labels, anything with an AutomationId. Each line has a **ref** (`e1`, `e2`…) for the other tools, the current value and state, and the locator problems (⚠). Size-limited (150 elements by default). |
| `recommend_locators` | For one ref: the ways a test could find it, **best first**, each with a confidence (high / medium / low), the reason, and ready-to-paste FlaUI and Appium C#. Only locators that find exactly that element are offered. If none is strong, it says which AutomationId to add to the app. |
| `check_locator` | Checks, on the app as it is now, how many elements a locator finds. Exactly one is what a test needs. If it finds none, it lists close matches (a renamed control, say). |
| `act` | Clicks, types, selects, expands, collapses or focuses one element (by ref, or by a locator that finds exactly one). It uses the element's own behaviour when it has one (a button's click, a check box's toggle) and the mouse only as a last resort. Reports windows that opened or closed. |
| `wait_for` | Waits until an element or window appears, disappears or becomes enabled (up to 60 seconds). Running out of time is an answer, not an error. |
| `take_screenshot` | A PNG of an element or window, shown to the assistant. Can also save it to a file. |
| `score_live_app` | Scores the open windows as test tools see them, including controls built in code and third-party controls the source scan can't see. Give the source folder too, to learn where each ID comes from. |
| `close_app` | Closes the app if the server started it, or lets go of it if it was attached to. |

A snapshot line looks like this:

```
e7 Button "Delete" #ActionButton ⚠ ID used 2 times
e9 CheckBox "Only my orders" #OnlyMineCheckBox [unchecked]
e12 List "Recent orders" #OrdersList ⚠ rows can't be told apart
```

And a recommendation (shortened):

```json
{
  "summary": "Best locator for Button \"Remove\" #RemoveButton: inside \"OrderRow42\": AutomationId=\"RemoveButton\" (window \"OrdersWindow\") (high confidence, 0.85). ...",
  "locators": [
    {
      "rank": 1,
      "strength": "high",
      "confidence": 0.85,
      "locator": { "automationId": "RemoveButton", "within": "OrderRow42", "window": "OrdersWindow" },
      "reason": "Looks inside \"OrderRow42\" (ListItem) first, where this element's AutomationId is unique. Good for an ID repeated in every row of a list.",
      "flaUi": "window.FindFirstDescendant(cf => cf.ByAutomationId(\"OrderRow42\")).FindFirstDescendant(cf => cf.ByAutomationId(\"RemoveButton\"))",
      "appium": "driver.FindElement(MobileBy.AccessibilityId(\"OrderRow42\")).FindElement(MobileBy.AccessibilityId(\"RemoveButton\"))"
    }
  ]
}
```

How locators are ranked:

| Locator | Confidence | Why |
|---|---|---|
| AutomationId, unique on the window, looks meaningful | high | Doesn't change with the text, the language or the layout |
| AutomationId inside a uniquely identified parent (e.g. a list row) | high | The standard way to reach a control repeated in every row |
| AutomationId plus control type (the ID alone isn't unique) | medium | Works, but breaks if another control of that type gets the same ID |
| Name (visible text) plus control type | medium | Breaks when the wording changes or the app is translated |
| Machine-made-looking AutomationId (e.g. `Button_12`) | low | Likely to change in the next build |
| Position (the n-th Button) | low | Breaks as soon as a control is added, removed or moved |

## Set it up

### Source tools (any OS)

You need the [.NET SDK](https://dotnet.microsoft.com/download), version 10 or newer. `dnx` (part of the SDK) downloads and runs
the server; there is nothing else to install. Add `--prerelease` after `--yes` while only preview versions are published.

**Claude Code.** Run this in your WPF project's folder:

```bash
claude mcp add wpf-locator-doctor -- dnx wpf-locator-doctor-mcp --yes
```

Add `--scope project` to save it in the project's `.mcp.json` file, so everyone on the team gets it.
Type `/mcp` in Claude Code to check that `wpf-locator-doctor` is connected.

**VS Code (GitHub Copilot).** Create `.vscode/mcp.json` in your project:

```json
{
  "servers": {
    "wpf-locator-doctor": {
      "type": "stdio",
      "command": "dnx",
      "args": ["wpf-locator-doctor-mcp", "--yes"]
    }
  }
}
```

Then open Copilot Chat in **Agent** mode; the tools appear under the tools button.

**Cursor.** Create `.cursor/mcp.json` in your project (or `~/.cursor/mcp.json` for all projects):

```json
{
  "mcpServers": {
    "wpf-locator-doctor": {
      "command": "dnx",
      "args": ["wpf-locator-doctor-mcp", "--yes"]
    }
  }
}
```

### Windows build (adds the running-app tools)

You need the [.NET Desktop Runtime](https://dotnet.microsoft.com/download), version 10 or newer, and an unlocked desktop
(the tools work the app like a user would).

**Claude Desktop (or another app that installs `.mcpb` bundles).** Download `wpf-locator-doctor-mcp-win-x64.mcpb` from the
[releases page](https://github.com/swechhap/wpf-locator-doctor/releases) and open it. It asks for the optional list of allowed apps (see below).

**Claude Code, VS Code, Cursor.** Download `wpf-locator-doctor-mcp-win-x64.zip`, unzip it (for example to
`C:\tools\wpf-locator-doctor-mcp`), and use the `.exe` as the command:

```bash
claude mcp add wpf-locator-doctor -- C:\tools\wpf-locator-doctor-mcp\wpf-locator-doctor-mcp.exe
```

```json
{
  "servers": {
    "wpf-locator-doctor": {
      "type": "stdio",
      "command": "C:\\tools\\wpf-locator-doctor-mcp\\wpf-locator-doctor-mcp.exe"
    }
  }
}
```

(In JSON files, each `\` is written twice. For Cursor, use `"mcpServers"` instead of `"servers"` and leave out `"type"`.)

### Build it from this repository instead

Useful before the first release with the MCP server, or to try changes:

```bash
git clone https://github.com/swechhap/wpf-locator-doctor.git
```

```bash
dotnet build wpf-locator-doctor/src/WpfLocatorDoctor.Mcp -c Release
```

The source-tools server is then `src/WpfLocatorDoctor.Mcp/bin/Release/net10.0/wpf-locator-doctor-mcp.dll` (command `dotnet`,
argument: that path). On Windows, the build with every tool is `src/WpfLocatorDoctor.Mcp/bin/Release/net10.0-windows/wpf-locator-doctor-mcp.exe`.

To check it works, run it with `--help`. It prints how to set it up and which tools it has, and exits.

## Things to ask

- *"Score the WPF project in src/MyApp and tell me which screen is worst."*
- *"List the high-severity locator problems on MainWindow."*
- *"Add the missing AutomationIds to MainWindow.xaml."* (the assistant uses `suggest_xaml_fixes`, then edits the file)
- *"Save a testability report to reports/baseline, then check after my changes that no IDs disappeared."*
- *"What does WT005 mean, and why does it matter?"*
- *"Start bin/Debug/net8.0-windows/MyApp.exe, open Settings from the File menu, and give me the best locator for each control there."*
- *"Is `AutomationId=SubmitButton` still unique on the order screen?"*
- *"Score the running app together with its source in src/MyApp."*

Give the assistant full paths. A relative path is read from the folder the assistant started the server in.

## Safety

- **Nothing in your code changes without you.** `score_source` and `score_live_app` write files only when you ask them to save reports
  (to the folder you name), and `take_screenshot` only when you give `saveTo`. `suggest_xaml_fixes` only suggests; the assistant makes the edits in your editor,
  where you can review them.
- **`act` and `close_app` change the running app**, as a user would, so they are marked as such and assistants normally ask
  before using them. Use a test copy of the app with test data, never production.
- **Limit which apps it may touch.** Set the environment variable `WPF_LOCATOR_DOCTOR_ALLOWED_APPS` to the app names, separated
  by semicolons (e.g. `MyApp;PracticeApp`). Then `open_app` refuses anything else. In Claude Code:
  `claude mcp add wpf-locator-doctor -e WPF_LOCATOR_DOCTOR_ALLOWED_APPS=MyApp -- <command>`.
- **Screen content can be private.** Snapshots show what text boxes contain (never passwords), and screenshots show the screen.
  Both go to your assistant.
- **One app at a time.** Opening another app closes the first if the server started it. When the assistant stops the server,
  an app the server started is closed too.
- If the app runs as administrator, the server must too, or Windows won't let it read the app.

## Good to know

- **Big projects and screens.** Lists are cut to 50 items by default (up to 500 with `limit`), snapshots to 150 elements (up to 1000),
  and only the first 20 rows of each list are shown. Each answer says when there is more.
- **Same rules as the command line.** The scores and problems are exactly what `wpf-locator-doctor scan-source` and `scan-live` report.
  See the [README](../README.md) for how the score works.
- **Refs are short-lived.** A ref (`e5`) points to the element as it was in the latest snapshot. After the screen changes, take a new
  snapshot. For tests, use the locators from `recommend_locators`, never refs.

## How it is built and tested

- `src/WpfLocatorDoctor.Core/Assistant/` holds every decision: what each tool answers, which ID to suggest, the exact line edit,
  which locator is best, how to click something, when a wait is over. It is tested on any operating system: `ProjectDoctorTests`
  and `XamlFixPlannerTests` (source tools; one test applies every suggested edit to a copy of the practice app and checks the ID
  problems are gone), and `LiveSessionTests` (running-app tools, with a fake app).
- `src/WpfLocatorDoctor.Live/FlaUiAppConnector.cs` is the thin Windows-only part: it starts or attaches to the app, reads its
  windows in one go (FlaUI caching), and carries out the action Core chose.
- `src/WpfLocatorDoctor.Mcp/` only describes the tools to the assistant and starts the server. It uses the official
  [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk). It is built twice: `net10.0` (source tools; the NuGet package)
  and `net10.0-windows` (adds `LiveTools.cs`; the Windows download).
- `tests/WpfLocatorDoctor.Mcp.Tests/` starts the real server and talks to it with the official MCP client, as an assistant would:
  the tool list, the answers, and the error messages. On every push, GitHub Actions also starts the practice app on Windows and
  drives it through the server: snapshot, locators, typing, the menu, waiting for the settings window, a screenshot and a score
  (`LiveToolsTests`).

## How it is released

The release workflow (`.github/workflows/release.yml`) runs when a GitHub release is published:

1. It packs the NuGet package (a .NET tool marked as an MCP server, with `.mcp/server.json` inside) and publishes it once
   NuGet publishing is set up.
2. It builds the Windows server and attaches `wpf-locator-doctor-mcp-win-x64.zip` and `wpf-locator-doctor-mcp-win-x64.mcpb` to the
   release. The `.mcpb` is a zip with the server and a `manifest.json` (made from `src/WpfLocatorDoctor.Mcp/.mcp/mcpb-manifest.json`).
3. It writes the [MCP Registry](https://registry.modelcontextprotocol.io) entry, `server.json`, listing both the NuGet package and
   the `.mcpb` (with its SHA-256 checksum), and attaches it to the release. It publishes the entry to the registry only when the
   repository variable `MCP_REGISTRY_PUBLISH` is `true`. The registry checks that the NuGet package's README names the server
   (`mcp-name: io.github.swechhap/wpf-locator-doctor`), and that the `.mcpb` address contains "mcp".
