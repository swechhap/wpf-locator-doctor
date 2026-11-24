# WPF Locator Doctor: MCP server

<!-- mcp-name: io.github.swechhap/wpf-locator-doctor -->

**Lets your AI assistant check and fix how testable a WPF app is.** UI tests (FlaUI, Appium, WinAppDriver…) find controls by
their **AutomationId**. This MCP server gives assistants such as Claude Code, GitHub Copilot in VS Code and Cursor the tools to
score a WPF project from 0 to 100 (A to F), list its locator problems with file and line, and plan the exact XAML change for
each missing, borrowed, machine-made or duplicated AutomationId.

This package reads the project's source code (XAML and C#), so it runs on Windows, macOS and Linux. The tools for a **running**
app (look at the screen, recommend and check locators, click and type, take screenshots, score the live app) need Windows and
are in the Windows download on the [releases page](https://github.com/swechhap/wpf-locator-doctor/releases).

## Set it up

You need the .NET SDK, version 10 or newer. `dnx` downloads and runs the server; nothing else to install.
If your shell can't find `dnx` (for example with .NET from Homebrew), use `dotnet dnx` instead.

**Claude Code** (in your WPF project's folder):

```bash
claude mcp add wpf-locator-doctor -- dnx wpf-locator-doctor-mcp --yes
```

**VS Code** (`.vscode/mcp.json`):

```json
{
  "servers": {
    "wpf-locator-doctor": { "type": "stdio", "command": "dnx", "args": ["wpf-locator-doctor-mcp", "--yes"] }
  }
}
```

**Cursor** (`.cursor/mcp.json`):

```json
{
  "mcpServers": {
    "wpf-locator-doctor": { "command": "dnx", "args": ["wpf-locator-doctor-mcp", "--yes"] }
  }
}
```

## Tools

| Tool | What it does |
|---|---|
| `score_source` | Score and grade for the app and each screen, the checks behind it and the top problems. Can save the reports. |
| `list_problems` | Every problem with its file, line and fix; filter by screen, severity or problem type. |
| `suggest_xaml_fixes` | Exact line edits that add unique AutomationIds. Changes no files itself. |
| `compare_to_baseline` | Did the score go up or down since an earlier report, and did any ID disappear (breaking tests)? |
| `explain_rules` | Every check and problem type in plain English. |

Then ask, for example: *"Score the WPF project in src/MyApp and add the missing AutomationIds to the worst screen."*

Full guide: [docs/mcp-server.md](https://github.com/swechhap/wpf-locator-doctor/blob/master/docs/mcp-server.md)
