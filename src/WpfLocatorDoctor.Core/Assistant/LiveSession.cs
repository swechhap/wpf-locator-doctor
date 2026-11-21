using System.Globalization;
using System.Text;
using WpfLocatorDoctor.Core.Live;
using WpfLocatorDoctor.Core.Model;
using WpfLocatorDoctor.Core.Rules;
using WpfLocatorDoctor.Core.Scoring;
using WpfLocatorDoctor.Core.Source;

namespace WpfLocatorDoctor.Core.Assistant;

/// <summary>Settings for <see cref="LiveSession"/>.</summary>
public sealed class LiveSessionOptions
{
    /// <summary>Environment variable with the apps the tools may open, e.g. "MyApp;PracticeApp". Empty means any app.</summary>
    public const string AllowedAppsVariable = "WPF_LOCATOR_DOCTOR_ALLOWED_APPS";

    /// <summary>Process or .exe names (without ".exe") the tools may open or attach to. Empty means any app.</summary>
    public IReadOnlyList<string> AllowedApps { get; init; } = [];
    public TimeProvider Time { get; init; } = TimeProvider.System;
    /// <summary>How the session waits. Tests replace it so they don't really wait.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
    /// <summary>How often wait_for looks again.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>Pause after an action so the app can react before windows are compared.</summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromMilliseconds(400);

    public static LiveSessionOptions FromEnvironment() =>
        new() { AllowedApps = ParseAllowedApps(Environment.GetEnvironmentVariable(AllowedAppsVariable)) };

    /// <summary>"MyApp.exe; Other" → ["MyApp", "Other"].</summary>
    public static IReadOnlyList<string> ParseAllowedApps(string? value) =>
        (value ?? "").Split([';', ',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StripExe)
            .Where(s => s.Length > 0)
            .ToList();

    internal static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}

/// <summary>
/// The AI assistant's tools for a running app: start or attach, look at the screen, recommend and check locators,
/// act, wait, take screenshots and score. One app at a time. Every decision is made here; the
/// <see cref="ILiveAppConnector"/> only reads the app and carries out actions, so all of this is tested without Windows.
/// Calls are handled one at a time.
/// </summary>
public sealed class LiveSession : IDisposable
{
    public const int DefaultSnapshotSize = 150;
    public const int MaxSnapshotSize = 1000;
    public const int DefaultWaitSeconds = 10;
    public const int MaxWaitSeconds = 60;
    private const int RowsShownPerList = 20;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] WaitConditions = ["appears", "disappears", "enabled"];

    private readonly ILiveAppConnector _connector;
    private readonly LiveSessionOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Located> _refs = new(StringComparer.OrdinalIgnoreCase);
    private int _nextRef = 1;
    private ILiveApp? _app;

    public LiveSession(ILiveAppConnector connector, LiveSessionOptions? options = null)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _options = options ?? new LiveSessionOptions();
    }

    // ------------------------------------------------------------------ open / close

    /// <summary>Starts an .exe, or attaches to a running process (name or ID). Any app opened before is closed (if the tool started it) or let go.</summary>
    public AppStatus Open(string? exePath = null, string? arguments = null, string? process = null, bool useUia2 = false, int waitSeconds = 30)
    {
        var hasExe = !string.IsNullOrWhiteSpace(exePath);
        var hasProcess = !string.IsNullOrWhiteSpace(process);
        if (hasExe == hasProcess)
            throw new DoctorInputException("Give either exePath (to start the app) or process (the name or ID of an app that is already running), not both.");

        string? fullExe = null;
        if (hasExe)
        {
            fullExe = Path.GetFullPath(exePath!.Trim().Trim('"'));
            if (!fullExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new DoctorInputException($"{fullExe} isn't an .exe file. Give the path of the app's .exe (usually in bin/Debug or bin/Release).");
            if (!File.Exists(fullExe))
                throw new DoctorInputException($"Not found: {fullExe}. Build the app first, and use an absolute path.");
            CheckAllowed(Path.GetFileNameWithoutExtension(fullExe));
        }
        else if (!int.TryParse(process, NumberStyles.None, Inv, out _))
        {
            CheckAllowed(LiveSessionOptions.StripExe(process!.Trim()));
        }

        _gate.Wait();
        try
        {
            CloseCurrent();
            var wait = TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 1, 120));
            var app = _connector.Open(new LiveOpenRequest(fullExe, arguments, hasProcess ? process!.Trim() : null, useUia2, wait));
            if (!IsAllowed(app.Name))
            {
                if (app.StartedByTool) TryClose(app);
                app.Dispose();
                throw new DoctorInputException(NotAllowedMessage(app.Name));
            }
            _app = app;

            var windows = app.ReadWindows(includeChildren: false).Select(LiveTree.DescribeWindow).ToList();
            var how = app.StartedByTool ? "Started" : "Attached to";
            var engine = useUia2 ? "UI Automation 2" : "UI Automation 3";
            var summary = string.Create(Inv, $"{how} {app.Name} (process {app.ProcessId}) using {engine}. ") +
                          (windows.Count == 0 ? "It has no open windows yet." : $"Open windows: {string.Join(", ", windows)}.") +
                          " Next: snapshot_screen to see the controls.";
            return new AppStatus(summary, app.Name, app.ProcessId, app.StartedByTool, windows);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the app if the tool started it; otherwise lets go of it and leaves it running.</summary>
    public LiveNote Close()
    {
        _gate.Wait();
        try
        {
            if (_app is null) return new LiveNote("No app is open.");
            var name = _app.Name;
            var started = _app.StartedByTool;
            CloseCurrent();
            return new LiveNote(started ? $"Closed {name}." : $"Let go of {name}; it is still running because the tool didn't start it.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        CloseCurrent();
        _gate.Dispose();
    }

    // ------------------------------------------------------------------ snapshot

    /// <summary>
    /// The app's open windows as an outline of their interesting elements (things to click, type into or select, lists and rows,
    /// labels, anything with an AutomationId), each with a ref for the other tools and any locator problems.
    /// </summary>
    public ScreenSnapshot Snapshot(string? window = null, bool interestingOnly = true, int maxElements = DefaultSnapshotSize)
    {
        _gate.Wait();
        try
        {
            var app = RequireApp();
            var all = ReadWindowsOrThrow(app);
            var windows = string.IsNullOrWhiteSpace(window) ? all : [ResolveWindow(all, window)];
            var max = maxElements < 1 ? DefaultSnapshotSize : Math.Min(maxElements, MaxSnapshotSize);
            ResetRefs();

            var builder = new SnapshotBuilder(this, interestingOnly, max);
            foreach (var w in windows) builder.AddWindow(w);

            var shownText = builder.Total > builder.Shown
                ? $"Showing {builder.Shown} of {builder.Total} elements; pass window, or raise maxElements (up to {MaxSnapshotSize}), to see more."
                : $"{builder.Shown} element(s) shown.";
            var problems = new List<string>();
            if (builder.MissingIds > 0) problems.Add($"{builder.MissingIds} control(s) without an AutomationId");
            if (builder.DuplicateIds.Count > 0) problems.Add($"{builder.DuplicateIds.Count} AutomationId(s) used more than once ({string.Join(", ", builder.DuplicateIds.Take(5))})");
            if (builder.GeneratedIds > 0) problems.Add($"{builder.GeneratedIds} machine-made-looking ID(s)");
            if (builder.UnidentifiableLists > 0) problems.Add($"{builder.UnidentifiableLists} list(s) whose rows can't be told apart");
            if (builder.OpaqueAreas > 0) problems.Add($"{builder.OpaqueAreas} area(s) test tools can't see inside");

            var summary = $"{windows.Count} window(s): {string.Join(", ", windows.Select(LiveTree.DescribeWindow))}. {shownText} " +
                          (problems.Count == 0 ? "No locator problems among them." : $"Locator problems (marked ⚠): {string.Join("; ", problems)}.") +
                          " Use a ref (e.g. e3) with recommend_locators, act or take_screenshot; refs last until the next snapshot.";
            var notes = new List<string>();
            if (builder.RowsHidden > 0) notes.Add($"Only the first {RowsShownPerList} rows of each list are shown ({builder.RowsHidden} more hidden).");
            if (all.Count > windows.Count) notes.Add($"Other windows not shown: {string.Join(", ", all.Except(windows).Select(LiveTree.DescribeWindow))}.");
            return new ScreenSnapshot(summary, windows.Select(LiveTree.DescribeWindow).ToList(), builder.Shown, builder.Total, builder.Lines, notes);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class SnapshotBuilder(LiveSession session, bool interestingOnly, int max)
    {
        public List<string> Lines { get; } = [];
        public int Shown { get; private set; }
        public int Total { get; private set; }
        public int MissingIds { get; private set; }
        public int GeneratedIds { get; private set; }
        public int UnidentifiableLists { get; private set; }
        public int OpaqueAreas { get; private set; }
        public int RowsHidden { get; private set; }
        public SortedSet<string> DuplicateIds { get; } = new(StringComparer.Ordinal);
        private Dictionary<string, int> _idCounts = [];

        public void AddWindow(ILiveNode window)
        {
            _idCounts = LiveTree.Descendants(window, window)
                .Select(l => LiveTreeWalker.NullIfEmpty(l.Node.AutomationId))
                .Where(id => id is not null)
                .GroupBy(id => id!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            Total++;
            Add(new Located(window, window, []), 0, issues: []);
            Visit(window, window, [], 1);
        }

        private void Visit(ILiveNode parent, ILiveNode window, ILiveNode[] ancestors, int depth)
        {
            var isList = ControlCatalog.LiveItemsHostTypes.Contains(parent.ControlType);
            var rows = 0;
            foreach (var node in parent.Children)
            {
                var type = node.ControlType;
                if (ControlCatalog.LiveSkippedTypes.Contains(type) || node.AutomationId == "SystemMenuBar") continue;
                if (isList && ControlCatalog.LiveRowTypes.Contains(type) && ++rows > RowsShownPerList)
                {
                    RowsHidden++;
                    continue;
                }

                var id = LiveTreeWalker.NullIfEmpty(node.AutomationId);
                var name = LiveTreeWalker.NullIfEmpty(node.Name);
                var interactive = LiveTreeWalker.IsInteractive(type, node.ClassName, node.IsKeyboardFocusable, node.Patterns);
                var opaque = LiveTreeWalker.LooksOpaque(node, id, name, node.Children.Count);
                var interesting = !interestingOnly || interactive || opaque || id is not null ||
                                  ControlCatalog.LiveRowTypes.Contains(type) || ControlCatalog.LiveItemsHostTypes.Contains(type) ||
                                  (name is not null && type is "Text" or "Group" or "Image" or "StatusBar" or "ToolBar" or "Header" or "HeaderItem");

                var childDepth = depth;
                if (interesting)
                {
                    Total++;
                    var issues = Issues(node, id, interactive, opaque);
                    Add(new Located(node, window, ancestors), depth, issues);
                    childDepth++;
                }

                // Inside a self-contained control (a button's text, a slider's thumb) there is nothing a test should use.
                if (interestingOnly && ControlCatalog.LiveLeafTypes.Contains(type)) continue;
                Visit(node, window, [.. ancestors, node], childDepth);
            }
        }

        private List<string> Issues(ILiveNode node, string? id, bool interactive, bool opaque)
        {
            var issues = new List<string>();
            if (interactive && id is null)
            {
                issues.Add("no AutomationId");
                MissingIds++;
            }
            if (id is not null && (interactive || ControlCatalog.LiveRowTypes.Contains(node.ControlType)))
            {
                if (IdHeuristics.LooksGenerated(id))
                {
                    issues.Add("ID looks machine-made");
                    GeneratedIds++;
                }
                if (_idCounts.TryGetValue(id, out var n) && n > 1)
                {
                    issues.Add(string.Create(Inv, $"ID used {n} times"));
                    DuplicateIds.Add(id);
                }
            }
            if (ControlCatalog.LiveItemsHostTypes.Contains(node.ControlType))
            {
                var rowIds = node.Children.Where(c => ControlCatalog.LiveRowTypes.Contains(c.ControlType))
                    .Select(c => LiveTreeWalker.NullIfEmpty(c.AutomationId)).ToList();
                if (rowIds.Count > 1 && (rowIds.Any(r => r is null) || rowIds.Distinct(StringComparer.Ordinal).Count() < rowIds.Count))
                {
                    issues.Add("rows can't be told apart");
                    UnidentifiableLists++;
                }
            }
            if (opaque)
            {
                issues.Add("test tools can't see inside");
                OpaqueAreas++;
            }
            return issues;
        }

        private void Add(Located located, int depth, List<string> issues)
        {
            if (Shown >= max) return;
            Shown++;
            var reference = session.Register(located);
            Lines.Add(new string(' ', depth * 2) + Line(reference, located.Node, issues));
        }
    }

    /// <summary>One outline line: <c>e7 Edit "Search" #SearchBox = "abc" [disabled] ⚠ ID used 2 times</c>.</summary>
    internal static string Line(string reference, ILiveNode node, IReadOnlyList<string>? issues = null)
    {
        var sb = new StringBuilder(reference).Append(' ').Append(LiveTree.Describe(node));
        if (!node.IsPassword && LiveTreeWalker.NullIfEmpty(node.Value) is { } value && value != node.Name)
            sb.Append(" = \"").Append(LiveTree.Shorten(value, 60)).Append('"');
        var states = new List<string>();
        if (!node.IsEnabled) states.Add("disabled");
        if (node.IsPassword) states.Add("password");
        switch (node.ToggleState)
        {
            case "On": states.Add("checked"); break;
            case "Off": states.Add("unchecked"); break;
            case "Indeterminate": states.Add("partly checked"); break;
        }
        switch (node.ExpandState)
        {
            case "Expanded": states.Add("expanded"); break;
            case "Collapsed": states.Add("collapsed"); break;
            case "PartiallyExpanded": states.Add("partly expanded"); break;
        }
        if (node.IsSelected == true) states.Add("selected");
        if (node.IsOffscreen) states.Add("off screen");
        if (states.Count > 0) sb.Append(" [").Append(string.Join(", ", states)).Append(']');
        if (issues is { Count: > 0 }) sb.Append(" ⚠ ").Append(string.Join("; ", issues));
        return sb.ToString();
    }

    // ------------------------------------------------------------------ locators

    /// <summary>Ranked locators for an element from the latest snapshot (or check), with reasons, confidence and code.</summary>
    public LocatorAdvice RecommendLocators(string reference)
    {
        _gate.Wait();
        try
        {
            RequireApp();
            var target = ResolveRef(reference);
            var options = LocatorAdvisor.Recommend(target);
            var appId = LocatorAdvisor.SuggestAppId(target);
            var best = options.Count > 0 ? options[0] : null;
            var element = LiveTree.Describe(target.Node);

            string? appFix = null;
            if (appId is not null)
            {
                var id = LiveTreeWalker.NullIfEmpty(target.Node.AutomationId);
                appFix = id is null
                    ? $"For a strong locator, give this control AutomationProperties.AutomationId=\"{appId}\" in the app's XAML."
                    : $"For a strong locator, change its AutomationId from \"{id}\" to a unique, meaningful one such as \"{appId}\" (set with AutomationProperties.AutomationId), and update tests that use the old one.";
                appFix += " If you have the source code, suggest_xaml_fixes finds the exact line.";
            }

            var summary = best is null
                ? $"No locator finds only {element}."
                : string.Create(Inv, $"Best locator for {element}: {best.Locator.Describe()} ({best.Strength} confidence, {best.Confidence:0.##}). ") +
                  (options.Count > 1 ? $"{options.Count - 1} other option(s) below, best first. " : "") +
                  "Each finds exactly this element on the latest read of the window.";
            if (best is not null && best.Strength != "high" && appFix is not null) summary += " None is strong: see appFix.";
            return new LocatorAdvice(summary, element, LiveTree.DescribeWindow(target.Window), options, appId, appFix);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Checks, on a fresh read of the app, how many elements a locator finds. Exactly one is what a test needs.</summary>
    public LocatorCheck CheckLocator(LocatorQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQuery(query, allowWindowOnly: false);
        _gate.Wait();
        try
        {
            var app = RequireApp();
            var windows = ReadWindowsOrThrow(app);
            var searched = LocatorMatcher.WindowsFor(windows, query);
            if (searched.Count == 0) throw new DoctorInputException(NoWindowMessage(query.Window!, windows));

            var matches = LocatorMatcher.Find(windows, query);
            var lines = matches.Take(10).Select(RegisterLine).ToList();
            var close = matches.Count == 0 ? LocatorMatcher.CloseMatches(windows, query).Select(RegisterLine).ToList() : [];
            var what = query.Describe();
            var summary = matches.Count switch
            {
                1 => $"{what} finds exactly one element: {lines[0]}. Safe to use.",
                0 => $"{what} finds nothing." + (close.Count > 0 ? " Close matches below: the control may have been renamed." : " The element may not be on screen yet (see wait_for), or the window may be different."),
                _ => string.Create(Inv, $"{what} finds {matches.Count} elements, so a test would get whichever comes first. ") +
                     "Narrow it down (add controlType, within or index), or use recommend_locators on one of the matches.",
            };
            if (matches.Count > lines.Count) summary += string.Create(Inv, $" Showing the first {lines.Count}.");
            return new LocatorCheck(summary, matches.Count == 1, matches.Count, lines, close);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ act

    /// <summary>Clicks, types, selects, expands, collapses or focuses one element, given by ref or by a locator that finds exactly one.</summary>
    public async Task<ActionResult> ActAsync(string action, string? reference, LocatorQuery? locator, string? text, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var app = RequireApp();
            Located target;
            if (!string.IsNullOrWhiteSpace(reference))
            {
                target = ResolveRef(reference);
            }
            else if (locator is not null && locator.HasElementCriteria)
            {
                var windows = ReadWindowsOrThrow(app);
                var matches = LocatorMatcher.Find(windows, locator);
                if (matches.Count != 1)
                    throw new DoctorInputException(matches.Count == 0
                        ? $"{locator.Describe()} finds nothing, so there is nothing to {action}. Take a snapshot to see what is on screen."
                        : string.Create(Inv, $"{locator.Describe()} finds {matches.Count} elements. Narrow it down so it finds exactly one, or use a ref from snapshot_screen."));
                target = matches[0];
            }
            else
            {
                throw new DoctorInputException("Say which element: a ref from snapshot_screen (e.g. \"e5\"), or a locator such as automationId.");
            }

            var (kind, how) = LiveActionPlanner.Plan(target.Node, action, text);
            var element = LiveTree.Describe(target.Node);
            var before = app.ReadWindows(includeChildren: false).Select(LiveTree.DescribeWindow).ToList();
            app.Perform(target.Node, kind, text);
            await DelayAsync(_options.SettleTime, cancellationToken).ConfigureAwait(false);

            if (app.HasExited)
                return new ActionResult($"Did it: {how} on {element}. The app then closed.", element, how, [], [], before);
            var after = app.ReadWindows(includeChildren: false).Select(LiveTree.DescribeWindow).ToList();
            var opened = after.Except(before).ToList();
            var closed = before.Except(after).ToList();
            var summary = $"Did it: {how} on {element}.";
            if (opened.Count > 0) summary += $" New window(s): {string.Join(", ", opened)}.";
            if (closed.Count > 0) summary += $" Closed window(s): {string.Join(", ", closed)}.";
            summary += " Take a new snapshot to see the result (refs from the old one may now be out of date).";
            return new ActionResult(summary, element, how, after, opened, closed);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ wait

    /// <summary>
    /// Waits until an element (or, with only a window given, a window) appears, disappears or becomes enabled.
    /// Running out of time is an answer (Met = false), not an error.
    /// </summary>
    public async Task<WaitResult> WaitForAsync(LocatorQuery query, string? until = null, int timeoutSeconds = DefaultWaitSeconds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQuery(query, allowWindowOnly: true);
        var condition = string.IsNullOrWhiteSpace(until) ? "appears" : until.Trim().ToLowerInvariant();
        if (!WaitConditions.Contains(condition))
            throw new DoctorInputException($"Unknown condition '{until}'. Use one of: {string.Join(", ", WaitConditions)}.");
        var windowOnly = !query.HasElementCriteria;
        var timeout = TimeSpan.FromSeconds(timeoutSeconds < 1 ? DefaultWaitSeconds : Math.Min(timeoutSeconds, MaxWaitSeconds));
        var what = windowOnly ? $"window \"{query.Window}\"" : query.Describe();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var app = RequireApp();
            var start = _options.Time.GetUtcNow();
            while (true)
            {
                var elapsed = _options.Time.GetUtcNow() - start;
                var seconds = Math.Round(elapsed.TotalSeconds, 1);
                if (app.HasExited)
                {
                    var met = condition == "disappears";
                    return new WaitResult($"The app closed{(met ? $", so {what} is gone" : "")}.", met, seconds, null, []);
                }

                var windows = app.ReadWindows(includeChildren: !windowOnly);
                var matches = windowOnly
                    ? LocatorMatcher.WindowsFor(windows, query).Select(w => new Located(w, w, [])).ToList()
                    : LocatorMatcher.Find(windows, query);
                if (condition == "disappears" && matches.Count == 0)
                    return new WaitResult(string.Create(Inv, $"{what} is gone (after {seconds:0.0} s)."), true, seconds, null, []);
                var hit = condition switch
                {
                    "disappears" => null,
                    "enabled" => matches.FirstOrDefault(m => m.Node.IsEnabled),
                    _ => matches.FirstOrDefault(),
                };
                if (hit is not null)
                {
                    var line = RegisterLine(hit);
                    var extra = matches.Count > 1 ? string.Create(Inv, $" Note: {matches.Count} elements match; this is the first.") : "";
                    var verb = condition == "enabled" ? "is enabled" : "appeared";
                    return new WaitResult(string.Create(Inv, $"{what} {verb} (after {seconds:0.0} s): {line}.{extra}"), true, seconds, line, []);
                }

                if (elapsed >= timeout)
                {
                    var close = condition == "appears" && !windowOnly
                        ? LocatorMatcher.CloseMatches(windows, query).Select(RegisterLine).ToList()
                        : [];
                    var state = condition switch
                    {
                        "disappears" => "is still there",
                        "enabled" => matches.Count == 0 ? "didn't appear" : "is still disabled",
                        _ => "didn't appear",
                    };
                    var summary = string.Create(Inv, $"Gave up after {timeout.TotalSeconds:0} s: {what} {state}. ") +
                                  $"Open windows: {string.Join(", ", windows.Select(LiveTree.DescribeWindow))}." +
                                  (close.Count > 0 ? " Close matches below: the control may have been renamed." : "");
                    return new WaitResult(summary, false, seconds, null, close);
                }

                await DelayAsync(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ screenshot

    /// <summary>A PNG of an element (by ref), a window, or the app's first window. Optionally also saved to a file.</summary>
    public ScreenshotResult Screenshot(string? reference = null, string? window = null, string? saveTo = null)
    {
        _gate.Wait();
        try
        {
            var app = RequireApp();
            Located target;
            if (!string.IsNullOrWhiteSpace(reference))
            {
                target = ResolveRef(reference);
            }
            else
            {
                var windows = app.ReadWindows(includeChildren: false);
                if (windows.Count == 0) throw new DoctorInputException("The app has no open windows.");
                var w = string.IsNullOrWhiteSpace(window) ? windows[0] : ResolveWindow(windows, window);
                target = new Located(w, w, []);
            }

            var png = app.CapturePng(target.Node, target.Window);
            string? saved = null;
            if (!string.IsNullOrWhiteSpace(saveTo))
            {
                saved = ScreenshotPath(saveTo);
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                File.WriteAllBytes(saved, png);
            }
            var what = ReferenceEquals(target.Node, target.Window) ? $"window {LiveTree.DescribeWindow(target.Window)}" : LiveTree.Describe(target.Node);
            var summary = $"Screenshot of {what}." + (saved is null ? "" : $" Saved to {saved}.") +
                          " It may show customer data, so treat it with care.";
            return new ScreenshotResult(summary, png, saved);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ScreenshotPath(string saveTo)
    {
        var full = Path.GetFullPath(saveTo.Trim());
        var isFolder = Directory.Exists(full) || saveTo.EndsWith('/') || saveTo.EndsWith('\\') || Path.GetExtension(full).Length == 0;
        if (isFolder) return Path.Combine(full, $"screenshot-{_options.Time.GetLocalNow():yyyyMMdd-HHmmss}.png");
        return full.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? full : full + ".png";
    }

    // ------------------------------------------------------------------ score

    /// <summary>Scores the open windows of the running app; with the source folder, IDs are matched to the XAML to learn where they came from.</summary>
    public ScoreReport Score(string? sourceFolder = null, string? saveReportTo = null)
    {
        var source = string.IsNullOrWhiteSpace(sourceFolder) ? null : ProjectDoctor.ScanSource(sourceFolder);
        _gate.Wait();
        try
        {
            var app = RequireApp();
            var windows = ReadWindowsOrThrow(app);
            var scan = new ScanResult { Mode = ScanMode.Live, Target = app.Name };
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var w in windows) scan.Screens.Add(LiveTreeWalker.WalkWindow(w, new LiveWalkLimits(), used));
            scan.Notes.Add("Only windows that were open, and list rows that were on screen, were checked. Open other screens and score again to cover them.");
            if (source is not null) SourceEnricher.Enrich(scan, source);
            return ProjectDoctor.BuildScoreReport(Scorer.Evaluate(scan), saveReportTo);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ helpers

    private ILiveApp RequireApp()
    {
        if (_app is null) throw new DoctorInputException("No app is open. Call open_app first, with exePath (to start it) or process (to attach to it).");
        if (_app.HasExited)
        {
            var name = _app.Name;
            CloseCurrent();
            throw new DoctorInputException($"{name} has closed. Call open_app to start it again.");
        }
        return _app;
    }

    private static IReadOnlyList<ILiveNode> ReadWindowsOrThrow(ILiveApp app)
    {
        var windows = app.ReadWindows(includeChildren: true);
        return windows.Count > 0 ? windows : throw new DoctorInputException($"{app.Name} has no open windows right now.");
    }

    private static ILiveNode ResolveWindow(IReadOnlyList<ILiveNode> windows, string wanted) =>
        LiveTree.FindWindow(windows, wanted) ?? throw new DoctorInputException(NoWindowMessage(wanted, windows));

    private static string NoWindowMessage(string wanted, IReadOnlyList<ILiveNode> windows) =>
        $"No window called '{wanted}'. Open windows: {string.Join(", ", windows.Select(LiveTree.DescribeWindow))}.";

    private static void ValidateQuery(LocatorQuery query, bool allowWindowOnly)
    {
        if (!query.HasElementCriteria && !(allowWindowOnly && !string.IsNullOrWhiteSpace(query.Window)))
            throw new DoctorInputException(allowWindowOnly
                ? "Say what to wait for: automationId, name, controlType or className, or just a window."
                : "Give at least one of automationId, name, controlType or className.");
        if (query.Index is < 1) throw new DoctorInputException("index counts from 1 (the first match).");
    }

    private Located ResolveRef(string reference)
    {
        var key = reference.Trim();
        return _refs.TryGetValue(key, out var located)
            ? located
            : throw new DoctorInputException(_refs.Count == 0
                ? $"Unknown ref '{key}'. Refs come from snapshot_screen; take a snapshot first."
                : $"Unknown ref '{key}'. Refs come from the latest snapshot_screen (or check_locator / wait_for); take a new snapshot.");
    }

    private void ResetRefs()
    {
        _refs.Clear();
        _nextRef = 1;
    }

    private string Register(Located located)
    {
        var reference = string.Create(Inv, $"e{_nextRef++}");
        _refs[reference] = located;
        return reference;
    }

    private string RegisterLine(Located located)
    {
        var line = Line(Register(located), located.Node);
        return ReferenceEquals(located.Node, located.Window) ? line : $"{line} (in {LiveTree.DescribeWindow(located.Window)})";
    }

    private bool IsAllowed(string name) =>
        _options.AllowedApps.Count == 0 ||
        _options.AllowedApps.Contains(LiveSessionOptions.StripExe(name), StringComparer.OrdinalIgnoreCase);

    private void CheckAllowed(string name)
    {
        if (!IsAllowed(name)) throw new DoctorInputException(NotAllowedMessage(name));
    }

    private string NotAllowedMessage(string name) =>
        $"{name} isn't in the list of apps this server may open ({LiveSessionOptions.AllowedAppsVariable}: {string.Join(", ", _options.AllowedApps)}).";

    private void CloseCurrent()
    {
        var app = _app;
        _app = null;
        ResetRefs();
        if (app is null) return;
        if (app.StartedByTool && !app.HasExited) TryClose(app);
        app.Dispose();
    }

    private static void TryClose(ILiveApp app)
    {
        try { app.Close(); }
        catch (DoctorInputException) { }
        catch (InvalidOperationException) { }
    }

    private Task DelayAsync(TimeSpan time, CancellationToken cancellationToken) =>
        _options.Delay is { } delay ? delay(time, cancellationToken) : Task.Delay(time, _options.Time, cancellationToken);
}
