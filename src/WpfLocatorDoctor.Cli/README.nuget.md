# WPF Locator Doctor

**Testability scorecard for WPF apps.** It checks whether the controls your UI tests need (buttons, text boxes, lists, menus, tabs…) have good **AutomationIds**: are they there, set on purpose, unique, still the same as last time, and visible to test tools. Each screen and the whole app get a score from 0 to 100 and a grade from A to F. Every problem comes with the file, the line, and the line of XAML to add.

## Install

```bash
dotnet tool install -g wpf-locator-doctor --prerelease
```

You need the .NET SDK, version 10 or newer. Works on Windows, macOS and Linux.

## Use

```bash
wpf-locator-doctor scan-source path/to/YourWpfProject
```

It prints a summary and writes two files into `./testability-report/`:

- `testability-report.md`: the report for people (grade, what each check found, every problem with its fix).
- `testability-report.json`: the same data for tools. Pass it as `--baseline` next time to check that no IDs disappeared.

In a build pipeline, fail the build when the score drops:

```bash
wpf-locator-doctor scan-source src/MyApp --min-score 75 --baseline last-report.json --quiet
```

Exit codes: `0` OK · `1` score below `--min-score` · `2` wrong usage · `3` the scan failed.

## Live scan of a running app (Windows)

This package contains the **source scan**, which reads your XAML and C# files. The **live scan** reads the running app through Windows UI Automation, the same thing test tools use. It is Windows-only, so it ships separately: download `wpf-locator-doctor-live-win-x64.zip` from the [releases page](https://github.com/swechhap/wpf-locator-doctor/releases). It needs the .NET 10 Desktop Runtime.

## More

This is an early preview. Commands and report formats may change before 1.0. An MCP server, so AI assistants can find locators for you, is coming next.

Source code, full documentation and issues: https://github.com/swechhap/wpf-locator-doctor
