using WpfLocatorDoctor.Core.Scoring;

namespace WpfLocatorDoctor.Mcp;

/// <summary>What the server tells the assistant (and a person running it by hand) about itself.</summary>
internal static class ServerInfo
{
    public const string Name = "wpf-locator-doctor";

    public static string Version => typeof(Scorecard).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

#if LIVE_TOOLS
    /// <summary>True in the Windows build, which also has the tools for a running app.</summary>
    public const bool HasLiveTools = true;
#else
    public const bool HasLiveTools = false;
#endif

    private const string SourceInstructions = """
        WPF Locator Doctor checks the AutomationIds that UI tests (FlaUI, Appium, WinAppDriver…) use to find controls in a WPF app.
        From the source code (any operating system):
        1. score_source on the project folder (absolute path) for the grade, the worst screens and the top problems.
           Pass saveReportTo to keep the JSON report as a baseline.
        2. list_problems for a screen to see each problem with its file, line and fix.
        3. suggest_xaml_fixes for exact, ready-to-apply line edits with unique AutomationIds. Apply them, then score_source again.
        4. compare_to_baseline with the saved report to confirm no existing ID disappeared (that would break existing tests).
        explain_rules explains every check and problem type (WT001-WT012) in plain English.
        Prefer IDs set on purpose (AutomationProperties.AutomationId) over x:Name or x:Uid, and never rename an ID tests already use
        without saying so.
        """;

    private const string LiveInstructions = """

        With the running app (this Windows build):
        1. open_app with the .exe (or attach to a running process), then snapshot_screen to see the controls, each with a ref (e1, e2…).
        2. recommend_locators for a ref gives ranked locators with reasons, confidence and FlaUI / Appium code; check_locator
           confirms a locator finds exactly one element.
        3. act clicks, types, selects, expands or collapses; then wait_for what should happen, and take a new snapshot.
        4. take_screenshot when a picture helps; score_live_app scores the open windows (pass the source folder too).
        5. close_app when done.
        act changes the app as a user would: only do what the user asked, and never use it on production data.
        Screenshots and snapshots can contain customer data.
        """;

    private const string NoLiveInstructions = """

        This build reads source code only. The tools for a running app (snapshot, locators, click and type, screenshots)
        are in the Windows download on the GitHub releases page.
        """;

    /// <summary>Sent to the assistant when it connects: how the tools fit together.</summary>
    public static string Instructions => SourceInstructions + (HasLiveTools ? LiveInstructions : NoLiveInstructions);

    private const string SetupText = """
        wpf-locator-doctor-mcp: an MCP server that lets AI assistants score a WPF app's testability and fix its locators.

        It talks over standard input/output, so you don't run it yourself: add it to your assistant instead, for example
          Claude Code:  claude mcp add wpf-locator-doctor -- dnx wpf-locator-doctor-mcp --yes
          VS Code:      add it under "servers" in .vscode/mcp.json
          Cursor:       add it under "mcpServers" in .cursor/mcp.json
        See https://github.com/swechhap/wpf-locator-doctor/blob/master/docs/mcp-server.md

        Tools: score_source, list_problems, suggest_xaml_fixes, compare_to_baseline, explain_rules.
        """;

    public static string HelpText => SetupText + "\n" + (HasLiveTools
        ? "Running-app tools: open_app, snapshot_screen, recommend_locators, check_locator, act, wait_for, take_screenshot,\n" +
          "score_live_app, close_app.\n"
        : "The tools for a running app are in the Windows download on the GitHub releases page.\n");
}
