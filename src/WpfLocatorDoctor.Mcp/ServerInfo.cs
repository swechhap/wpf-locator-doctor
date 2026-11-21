using WpfLocatorDoctor.Core.Scoring;

namespace WpfLocatorDoctor.Mcp;

/// <summary>What the server tells the assistant (and a person running it by hand) about itself.</summary>
internal static class ServerInfo
{
    public const string Name = "wpf-locator-doctor";

    public static string Version => typeof(Scorecard).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Sent to the assistant when it connects: how the tools fit together.</summary>
    public const string Instructions = """
        WPF Locator Doctor checks the AutomationIds that UI tests (FlaUI, Appium, WinAppDriver…) use to find controls in a WPF app.
        Typical flow:
        1. score_source on the project folder (absolute path) for the grade, the worst screens and the top problems.
           Pass saveReportTo to keep the JSON report as a baseline.
        2. list_problems for a screen to see each problem with its file, line and fix.
        3. suggest_xaml_fixes for exact, ready-to-apply line edits with unique AutomationIds. Apply them, then score_source again.
        4. compare_to_baseline with the saved report to confirm no existing ID disappeared (that would break existing tests).
        explain_rules explains every check and problem type (WT001-WT012) in plain English.
        Prefer IDs set on purpose (AutomationProperties.AutomationId) over x:Name or x:Uid, and never rename an ID tests already use
        without saying so.
        """;

    public const string HelpText = """
        wpf-locator-doctor-mcp: an MCP server that lets AI assistants score a WPF app's testability and fix its locators.

        It talks over standard input/output, so you don't run it yourself: add it to your assistant instead.
          Claude Code:  claude mcp add wpf-locator-doctor -- dotnet <path to wpf-locator-doctor-mcp.dll>
          VS Code:      add it under "servers" in .vscode/mcp.json
          Cursor:       add it under "mcpServers" in .cursor/mcp.json
        See https://github.com/swechhap/wpf-locator-doctor/blob/master/docs/mcp-server.md

        Tools: score_source, list_problems, suggest_xaml_fixes, compare_to_baseline, explain_rules.
        """;
}
