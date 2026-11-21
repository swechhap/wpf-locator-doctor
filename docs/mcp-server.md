# The MCP server: WPF Locator Doctor for AI assistants

The MCP server lets an AI assistant (Claude Code, GitHub Copilot in VS Code, Cursor, and others) use WPF Locator Doctor for you.
You ask in plain words, for example *"How testable is this WPF app, and what should we fix first?"*, and the assistant
calls the tools below, reads the answers, and can make the XAML changes for you to review.

MCP (Model Context Protocol) is the standard way to give an AI assistant extra tools. The server is a small program that runs
on your computer. The assistant starts it and talks to it through standard input and output, so nothing is sent anywhere
except to the assistant you already use.

## The tools

These work on Windows, macOS and Linux, because they read the project's source code (XAML and C# files).

| Tool | What it does |
|---|---|
| `score_source` | Scores the project from 0 to 100 with a grade (A to F), for the whole app and for each screen, shows the six checks behind the score and the top problems. Can save the full Markdown and JSON reports. **Start here.** |
| `list_problems` | Lists the problems, most severe first, each with its file, line and a plain-English fix. Can show only one screen, one severity, or one problem type (WT001 to WT012). |
| `suggest_xaml_fixes` | Works out the exact XAML change for each missing, borrowed, machine-made or duplicated AutomationId: a new ID that is unique on its screen, and the line as it is now and as it should be. Also covers list rows that can't be told apart and custom controls test tools can't see. **It changes no files**; the assistant applies the edits, so you can review them. |
| `compare_to_baseline` | Compares the project now with an earlier JSON report: did the score go up or down, which IDs disappeared (tests that use them will break), and which are new. |
| `explain_rules` | Explains every check, problem type and grade in plain English. |

Tools for the **running** app (start or attach to it, look at the current screen, recommend and check locators, click and type,
take a screenshot, score the live app) are being built next. They will need Windows, like the live scan.

## Set it up

You need the [.NET SDK](https://dotnet.microsoft.com/download), version 10 or newer.

> A ready-made package (so you won't need to build anything) is planned. Until then, build the server once from this repository.

### 1. Build the server

```bash
git clone https://github.com/swechhap/wpf-locator-doctor.git
```

```bash
dotnet build wpf-locator-doctor/src/WpfLocatorDoctor.Mcp -c Release
```

The server is now at `wpf-locator-doctor/src/WpfLocatorDoctor.Mcp/bin/Release/net10.0/wpf-locator-doctor-mcp.dll`.
In the examples below, replace `/path/to/wpf-locator-doctor-mcp.dll` with the full path to that file.
On Windows, a path looks like `C:\\src\\wpf-locator-doctor\\...\\wpf-locator-doctor-mcp.dll` inside JSON files (each `\` written twice).

To check it works, run it with `--help`. It prints how to set it up and exits:

```bash
dotnet /path/to/wpf-locator-doctor-mcp.dll --help
```

### 2. Add it to your assistant

**Claude Code.** Run this in your WPF project's folder:

```bash
claude mcp add wpf-locator-doctor -- dotnet /path/to/wpf-locator-doctor-mcp.dll
```

Add `--scope project` to save it in the project's `.mcp.json` file, so everyone on the team gets it.
Type `/mcp` in Claude Code to check that `wpf-locator-doctor` is connected.

**VS Code (GitHub Copilot).** Create `.vscode/mcp.json` in your project:

```json
{
  "servers": {
    "wpf-locator-doctor": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["/path/to/wpf-locator-doctor-mcp.dll"]
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
      "command": "dotnet",
      "args": ["/path/to/wpf-locator-doctor-mcp.dll"]
    }
  }
}
```

**Other assistants.** Any assistant that supports MCP servers over stdio works the same way: the command is `dotnet` and the
only argument is the path to `wpf-locator-doctor-mcp.dll`.

## Things to ask

- *"Score the WPF project in src/MyApp and tell me which screen is worst."*
- *"List the high-severity locator problems on MainWindow."*
- *"Add the missing AutomationIds to MainWindow.xaml."* (the assistant uses `suggest_xaml_fixes`, then edits the file)
- *"Save a testability report to reports/baseline, then check after my changes that no IDs disappeared."*
- *"What does WT005 mean, and why does it matter?"*

Give the assistant the project folder (the one with the `.csproj`). Paths are best given in full; a relative path is read
from the folder the assistant started the server in.

## What an answer looks like

The tools answer in JSON with a plain-English `summary` first. Shortened example from `suggest_xaml_fixes` on the practice app:

```json
{
  "summary": "14 fix(es): 5 add-id, 5 pin-id, 1 replace-generated-id, 1 fix-duplicate-id, 1 row-ids, 1 automation-peer. ...",
  "projectFolder": "/src/wpf-locator-doctor/samples/PracticeApp",
  "fixes": [
    {
      "kind": "replace-generated-id",
      "rule": "WT003",
      "screen": "MainWindow",
      "element": "Button \"Clear\"",
      "file": "MainWindow.xaml",
      "line": 27,
      "currentId": "Button_1",
      "suggestedId": "ClearButton",
      "instruction": "\"Button_1\" looks machine-generated. Set AutomationProperties.AutomationId=\"ClearButton\" instead, and update any test that used \"Button_1\".",
      "edit": {
        "line": 27,
        "before": "            <Button x:Uid=\"Button_1\" Content=\"Clear\" Margin=\"4,0\" />",
        "after": "            <Button AutomationProperties.AutomationId=\"ClearButton\" x:Uid=\"Button_1\" Content=\"Clear\" Margin=\"4,0\" />"
      }
    }
  ]
}
```

The kinds of fix:

| Kind | When | What it suggests |
|---|---|---|
| `add-id` | The control has no AutomationId | A new, readable ID based on its x:Name, label or tooltip, e.g. `SaveButton` |
| `pin-id` | The ID is borrowed from `x:Name` or `x:Uid` | The **same** value set on purpose, so existing tests keep working and a rename can't break them |
| `replace-generated-id` | The ID looks machine-made (`Button_12`, a GUID) | A meaningful ID; update tests that used the old one |
| `fix-duplicate-id` | Two controls share an ID | The first keeps it; the others get new ones |
| `row-ids` | Rows in a list can't be told apart | The XAML style that gives each row an ID from its data |
| `automation-peer` | A custom control is invisible to test tools | The C# line that makes it visible |

## Good to know

- **Nothing changes without you.** Only `score_source` writes files, and only when you ask it to save reports (to the folder you name).
  `suggest_xaml_fixes` only suggests; the assistant makes the edits in your editor, where you can review them.
- **Big projects.** Lists are cut to 50 items by default (up to 500 with `limit`), and the summary says when there are more.
- **Same rules as the command line.** The scores and problems are exactly what `wpf-locator-doctor scan-source` reports.
  See the [README](../README.md) for how the score works.

## How it is built and tested

- `src/WpfLocatorDoctor.Core/Assistant/` holds every decision: what each tool answers, which ID to suggest, and the exact line edit.
  It is tested on any operating system (`ProjectDoctorTests`, `XamlFixPlannerTests`). One test applies every suggested edit to a
  copy of the practice app and checks that the ID problems are gone and the score went up.
- `src/WpfLocatorDoctor.Mcp/` only describes the tools to the assistant and starts the server. It uses the official
  [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk).
- `tests/WpfLocatorDoctor.Mcp.Tests/` starts the real server and talks to it with the official MCP client, as an assistant would:
  the tool list, the answers, and the error messages. This runs on Linux and Windows in GitHub Actions on every push.
- `src/WpfLocatorDoctor.Mcp/.mcp/server.json` describes the server for the official [MCP Registry](https://registry.modelcontextprotocol.io).
  It will be published there once the package is released.
