using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfLocatorDoctor.Core.Assistant;

namespace WpfLocatorDoctor.Mcp;

/// <summary>
/// Tools that read a WPF project's source code (XAML and C#). They work on Windows, macOS and Linux.
/// Each one is a thin wrapper: the work and every decision happen in <see cref="ProjectDoctor"/>.
/// </summary>
[McpServerToolType]
public sealed class SourceTools
{
    private const string PathHelp =
        "Absolute path of the WPF project folder (the one with the .csproj), a .csproj file, or a single .xaml file.";
    private const string ScreenHelp =
        "Optional. Only this screen: its name (e.g. \"MainWindow\") or XAML file (e.g. \"Views/MainWindow.xaml\").";

    [McpServerTool(Name = "score_source", Title = "Score a WPF project's testability", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Scores how easy a WPF project is to automate, from its XAML and C# source: a 0-100 score and A-F grade for the app " +
                 "and each screen, the six checks behind the score (ID coverage, ID quality, uniqueness, stability, visibility, names), " +
                 "problem counts and the top problems. Start here. Optionally saves the full Markdown and JSON reports; the JSON report " +
                 "is the baseline for compare_to_baseline later.")]
    public static ScoreReport ScoreSource(
        [Description(PathHelp)] string path,
        [Description("Optional. Absolute path of an earlier testability-report.json, to also measure whether its IDs are still there.")] string? baselineReport = null,
        [Description("Optional. Absolute path of a folder to write testability-report.md and testability-report.json into.")] string? saveReportTo = null)
        => ProjectDoctor.Score(path, baselineReport, saveReportTo);

    [McpServerTool(Name = "list_problems", Title = "List locator problems", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the locator problems in a WPF project, most severe first, each with its file, line and a plain-English fix. " +
                 "Filter by screen, minimum severity or problem type (WT001-WT012; see explain_rules).")]
    public static ProblemList ListProblems(
        [Description(PathHelp)] string path,
        [Description(ScreenHelp)] string? screen = null,
        [Description("Optional. \"high\", \"medium\" or \"low\" (default: all). Shows problems of this severity or worse.")] string? minSeverity = null,
        [Description("Optional. Only this problem type, e.g. \"WT001\" (no AutomationId).")] string? rule = null,
        [Description("Most problems to return (default 50, at most 500).")] int limit = ProjectDoctor.DefaultLimit)
        => ProjectDoctor.ListProblems(path, screen, minSeverity, rule, limit);

    [McpServerTool(Name = "suggest_xaml_fixes", Title = "Suggest XAML AutomationId fixes", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Plans the XAML changes that fix missing, borrowed (x:Name / x:Uid), machine-generated and duplicated AutomationIds, " +
                 "plus list rows that can't be told apart and custom controls test tools can't see. Each fix has a suggested ID that is " +
                 "unique on its screen and, where possible, an exact edit: the line number, the line now ('before') and the line to " +
                 "write ('after'). Does not change any file; apply the edits yourself, then run score_source again.")]
    public static FixPlan SuggestXamlFixes(
        [Description(PathHelp)] string path,
        [Description(ScreenHelp)] string? screen = null,
        [Description("Most fixes to return (default 50, at most 500).")] int limit = ProjectDoctor.DefaultLimit)
        => ProjectDoctor.SuggestFixes(path, screen, limit);

    [McpServerTool(Name = "compare_to_baseline", Title = "Compare with an earlier report", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Compares the project now with an earlier testability-report.json: how the score changed, which AutomationIds " +
                 "disappeared (tests using them will break) and which were added, screen by screen. Use it to check a change " +
                 "didn't break existing tests.")]
    public static BaselineComparison CompareToBaseline(
        [Description(PathHelp)] string path,
        [Description("Absolute path of the earlier testability-report.json (from score_source's saveReportTo or the CLI's --out).")] string baselineReport)
        => ProjectDoctor.CompareToBaseline(path, baselineReport);

    [McpServerTool(Name = "explain_rules", Title = "Explain the checks and problem types", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Explains, in plain English, the six checks behind the score (with their weights), every problem type (WT001-WT012) " +
                 "and why it matters, the grades, and why it matters where an AutomationId comes from.")]
    public static RuleGuide ExplainRules() => ProjectDoctor.Rules();
}
