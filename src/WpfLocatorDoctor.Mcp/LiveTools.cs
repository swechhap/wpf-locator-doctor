using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WpfLocatorDoctor.Core.Assistant;
using WpfLocatorDoctor.Core.Live;

namespace WpfLocatorDoctor.Mcp;

/// <summary>
/// Tools for a running WPF app (Windows build only). Each one is a thin wrapper: the work and every decision happen in
/// <see cref="LiveSession"/>, which works with one app at a time.
/// </summary>
[McpServerToolType]
public sealed class LiveTools(LiveSession session)
{
    private const string RefHelp = "A ref from snapshot_screen, e.g. \"e5\".";
    private const string WindowHelp = "Optional. Only this window: its AutomationId or title (e.g. \"SettingsWindow\" or \"Settings\").";
    private const string IdHelp = "AutomationId to look for (exact).";
    private const string NameHelp = "Name (the visible text or label) to look for (exact).";
    private const string TypeHelp = "UI Automation control type, e.g. Button, Edit, CheckBox, ComboBox, List, ListItem, DataItem, MenuItem, TabItem, Text.";
    private const string ClassHelp = "Class name, e.g. a custom control's class.";
    private const string WithinHelp = "Optional. Search only inside the element with this AutomationId (e.g. one list row).";
    private const string IndexHelp = "Optional. Take the n-th match (1 = first). Positions break easily; use as a last resort.";

    [McpServerTool(Name = "open_app", Title = "Start or attach to a WPF app", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Starts a WPF app from its .exe, or attaches to one that is already running, so the other live tools can work with it. " +
                 "One app at a time: an app opened earlier is closed first (if this server started it). Then call snapshot_screen.")]
    public AppStatus OpenApp(
        [Description("Absolute path of the app's .exe to start. Give this or process.")] string? exePath = null,
        [Description("Optional. Command-line arguments for the .exe.")] string? arguments = null,
        [Description("Name (e.g. \"MyApp\") or process ID of a running app to attach to. Give this or exePath.")] string? process = null,
        [Description("Use the older UI Automation 2 engine. Some WPF controls report better through it. Default false (UI Automation 3).")] bool useUia2 = false,
        [Description("Seconds to wait for the main window (default 30).")] int waitSeconds = 30)
        => session.Open(exePath, arguments, process, useUia2, waitSeconds);

    [McpServerTool(Name = "close_app", Title = "Close the app", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Closes the app if this server started it (unsaved work in it is lost), or lets go of it if it was attached to.")]
    public LiveNote CloseApp() => session.Close();

    [McpServerTool(Name = "snapshot_screen", Title = "See what is on screen", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Shows the app's open windows as an outline of their interesting elements: things to click, type into or select, lists and " +
                 "rows, labels, and anything with an AutomationId. Each line is 'ref Type \"Name\" #AutomationId = \"value\" [states] ⚠ problems', " +
                 "indented under its parent. Use the refs with recommend_locators, act and take_screenshot; they last until the next snapshot. " +
                 "Text box values are shown (never passwords), so the outline may contain customer data.")]
    public ScreenSnapshot SnapshotScreen(
        [Description(WindowHelp)] string? window = null,
        [Description("Only interesting elements (default true). False shows every element, including layout parts.")] bool interestingOnly = true,
        [Description("Most elements to show (default 150, at most 1000).")] int maxElements = LiveSession.DefaultSnapshotSize)
        => session.Snapshot(window, interestingOnly, maxElements);

    [McpServerTool(Name = "recommend_locators", Title = "Recommend locators for an element", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Ranks the ways a UI test could find one element, best first: AutomationId, AutomationId inside a uniquely identified parent " +
                 "(e.g. a list row), name plus control type, and position as a last resort. Each has a confidence (high/medium/low) with the " +
                 "reason, the locator to pass to check_locator, and ready-to-paste FlaUI and Appium C#. Only locators that find exactly this " +
                 "element are offered. When none is strong, says which AutomationId to add to the app.")]
    public LocatorAdvice RecommendLocators([Description(RefHelp)] string @ref) => session.RecommendLocators(@ref);

    [McpServerTool(Name = "check_locator", Title = "Check a locator", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Checks on the app as it is now how many elements a locator finds. Exactly one is what a test needs. When it finds nothing, " +
                 "lists close matches (e.g. a renamed control). Give at least one of automationId, name, controlType or className; all given must match.")]
    public LocatorCheck CheckLocator(
        [Description(IdHelp)] string? automationId = null,
        [Description(NameHelp)] string? name = null,
        [Description(TypeHelp)] string? controlType = null,
        [Description(ClassHelp)] string? className = null,
        [Description(WithinHelp)] string? within = null,
        [Description(IndexHelp)] int? index = null,
        [Description(WindowHelp)] string? window = null)
        => session.CheckLocator(new LocatorQuery(automationId, name, controlType, className, within, index, window));

    [McpServerTool(Name = "act", Title = "Click, type or select", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Does one thing to one element of the app, given by ref or by a locator that finds exactly one element: " +
                 "\"click\" (uses the element's own click, toggle, select or expand behaviour when it has one; else the mouse), " +
                 "\"type\" (replaces the text), \"select\" (a row, tab or option; or, with text, the item of that name in a list or combo box), " +
                 "\"expand\", \"collapse\" or \"focus\". This changes the app, as a user would. Reports windows that opened or closed. " +
                 "Take a new snapshot afterwards.")]
    public Task<ActionResult> Act(
        [Description("\"click\", \"type\", \"select\", \"expand\", \"collapse\" or \"focus\".")] string action,
        [Description("Optional. " + RefHelp + " Or give a locator instead.")] string? @ref = null,
        [Description("For type: the text. For select on a list or combo box: the item's name.")] string? text = null,
        [Description("Optional. " + IdHelp)] string? automationId = null,
        [Description("Optional. " + NameHelp)] string? name = null,
        [Description("Optional. " + TypeHelp)] string? controlType = null,
        [Description(WithinHelp)] string? within = null,
        [Description(WindowHelp)] string? window = null,
        CancellationToken cancellationToken = default)
        => session.ActAsync(action, @ref, new LocatorQuery(automationId, name, controlType, null, within, null, window), text, cancellationToken);

    [McpServerTool(Name = "wait_for", Title = "Wait for an element or window", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Waits until an element appears, disappears or becomes enabled (or, with only window given, until that window opens or closes). " +
                 "Use after act when the app needs time, instead of guessing. Running out of time is reported (met: false), not an error.")]
    public Task<WaitResult> WaitFor(
        [Description("Optional. " + IdHelp)] string? automationId = null,
        [Description("Optional. " + NameHelp)] string? name = null,
        [Description("Optional. " + TypeHelp)] string? controlType = null,
        [Description("Optional. " + ClassHelp)] string? className = null,
        [Description(WithinHelp)] string? within = null,
        [Description("The window to look in, or (with nothing else given) the window to wait for: its AutomationId or title.")] string? window = null,
        [Description("\"appears\" (default), \"disappears\" or \"enabled\".")] string? until = null,
        [Description("Most seconds to wait (default 10, at most 60).")] int timeoutSeconds = LiveSession.DefaultWaitSeconds,
        CancellationToken cancellationToken = default)
        => session.WaitForAsync(new LocatorQuery(automationId, name, controlType, className, within, null, window), until, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "take_screenshot", Title = "Take a screenshot", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Takes a PNG picture of an element (by ref), a window, or the app's first window, brought to the front. " +
                 "Pictures can show customer data. Optionally also saves it to a file.")]
    public CallToolResult TakeScreenshot(
        [Description("Optional. " + RefHelp)] string? @ref = null,
        [Description(WindowHelp)] string? window = null,
        [Description("Optional. Absolute path of a .png file, or a folder, to also save the picture to.")] string? saveTo = null)
    {
        var shot = session.Screenshot(@ref, window, saveTo);
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = shot.Summary },
                ImageContentBlock.FromBytes(shot.Png, "image/png"),
            ],
        };
    }

    [McpServerTool(Name = "score_live_app", Title = "Score the running app", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Scores the app's open windows as UI tests see them (0-100 and A-F per window and overall, with the top problems). " +
                 "Covers controls built in code and third-party controls, which the source scan can't see. Give the source folder " +
                 "to also learn where each ID comes from. Optionally saves the Markdown and JSON reports.")]
    public ScoreReport ScoreLiveApp(
        [Description("Optional. Absolute path of the app's WPF project folder, to match IDs to the XAML.")] string? sourceFolder = null,
        [Description("Optional. Absolute path of a folder to write testability-report.md and testability-report.json into.")] string? saveReportTo = null)
        => session.Score(sourceFolder, saveReportTo);
}
