using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Identifiers;
using FlaUI.UIA2;
using FlaUI.UIA3;
using WpfLocatorDoctor.Core.Live;
using WpfLocatorDoctor.Core.Model;

namespace WpfLocatorDoctor.Live;

public sealed class LiveScanOptions
{
    /// <summary>Start this .exe and scan it.</summary>
    public string? ExePath { get; set; }
    public string? Arguments { get; set; }
    /// <summary>Or attach to a running process: its ID or name (e.g. "MyApp" or "MyApp.exe").</summary>
    public string? Process { get; set; }
    /// <summary>Use the older UIA2 engine instead of UIA3. Some WPF controls report better through UIA2.</summary>
    public bool UseUia2 { get; set; }
    /// <summary>How long to wait for the main window.</summary>
    public TimeSpan WaitForWindow { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Safety limit so huge screens don't run forever.</summary>
    public int MaxElementsPerWindow { get; set; } = 5000;
    /// <summary>How many rows to look at in each list or grid.</summary>
    public int MaxRowsPerList { get; set; } = 50;
    /// <summary>Leave the app running after scanning (only matters if this tool started it).</summary>
    public bool KeepOpen { get; set; }
}

/// <summary>
/// Reads the running WPF app through UI Automation (via FlaUI): exactly what test tools see.
/// Windows only. Needs an unlocked desktop session.
/// This class only starts/attaches to the app and reads the tree; the decisions about each element
/// are made by <see cref="LiveTreeWalker"/> in the Core library, where they are unit-tested.
/// </summary>
public sealed class LiveAppScanner
{
    public ScanResult Scan(LiveScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Live scanning uses Windows UI Automation and only works on Windows.");
        if (options.ExePath is null && options.Process is null)
            throw new ArgumentException("Give either an .exe to start or a running process to attach to.", nameof(options));

        using AutomationBase automation = options.UseUia2 ? new UIA2Automation() : new UIA3Automation();
        automation.TransactionTimeout = TimeSpan.FromSeconds(60);
        automation.ConnectionTimeout = TimeSpan.FromSeconds(60);

        var (app, launched) = StartOrAttach(options);
        var result = new ScanResult { Mode = ScanMode.Live, Target = options.ExePath ?? options.Process! };
        var limits = new LiveWalkLimits(options.MaxElementsPerWindow, options.MaxRowsPerList);

        try
        {
            app.WaitWhileMainHandleIsMissing(options.WaitForWindow);
            var main = app.GetMainWindow(automation, options.WaitForWindow);
            if (main is null)
            {
                result.Notes.Add("The app's main window did not appear in time. Try a longer --wait, or check that the app starts correctly.");
                return result;
            }
            app.WaitWhileBusy(options.WaitForWindow);

            var windows = app.GetAllTopLevelWindows(automation);
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var window in windows)
                result.Screens.Add(ScanWindow(window, automation, limits, usedNames));

            if (windows.Length == 0) result.Notes.Add("No windows were found for this process.");
            result.Notes.Add("Only windows that were open, and list rows that were on screen, were checked. Open other screens and scan again to cover them.");
        }
        catch (COMException ex)
        {
            result.Notes.Add($"UI Automation stopped with an error ({ex.Message}). If the app runs as administrator, run this tool as administrator too.");
        }
        catch (UnauthorizedAccessException)
        {
            result.Notes.Add("Access was denied. If the app runs as administrator, run this tool as administrator too.");
        }
        finally
        {
            if (launched && !options.KeepOpen)
            {
                try { app.Close(); } catch (InvalidOperationException) { }
            }
            app.Dispose();
        }

        return result;
    }

    private static (Application App, bool Launched) StartOrAttach(LiveScanOptions options)
    {
        if (options.ExePath is not null)
        {
            var app = string.IsNullOrWhiteSpace(options.Arguments)
                ? Application.Launch(options.ExePath)
                : Application.Launch(options.ExePath, options.Arguments);
            return (app, true);
        }
        return int.TryParse(options.Process, out var pid)
            ? (Application.Attach(pid), false)
            : (Application.Attach(options.Process!), false);
    }

    private static ScreenScan ScanWindow(Window window, AutomationBase automation, LiveWalkLimits limits, ISet<string> usedNames)
    {
        var lib = automation.PropertyLibrary;
        var patternChecks = FlaUiNode.PatternChecks(automation);

        // Read the whole window in one cross-process call instead of one call per property.
        var cache = new CacheRequest
        {
            TreeScope = TreeScope.Subtree,
            AutomationElementMode = AutomationElementMode.None,
            TreeFilter = new PropertyCondition(lib.Element.IsControlElement, true),
        };
        foreach (var p in new[]
                 {
                     lib.Element.AutomationId, lib.Element.Name, lib.Element.ClassName, lib.Element.ControlType,
                     lib.Element.FrameworkId, lib.Element.IsKeyboardFocusable, lib.Element.IsOffscreen,
                     lib.Element.BoundingRectangle,
                 })
        {
            cache.Add(p);
        }
        foreach (var (property, _) in patternChecks) cache.Add(property);

        using (cache.Activate())
        {
            var root = window.FindFirst(TreeScope.Element, TrueCondition.Default) ?? window;
            return LiveTreeWalker.WalkWindow(new FlaUiNode(root, patternChecks), limits, usedNames);
        }
    }

    /// <summary>Adapts a cached FlaUI element to <see cref="ILiveNode"/>. Every read is guarded, because UI Automation can fail per element.</summary>
    private sealed class FlaUiNode(AutomationElement el, (PropertyId Property, string Name)[] patternChecks) : ILiveNode
    {
        private IReadOnlyList<ILiveNode>? _children;
        private IReadOnlyList<string>? _patterns;
        private System.Drawing.Rectangle? _rect;
        private string? _controlType;

        public string ControlType => _controlType ??= Safe(() => el.Properties.ControlType.ValueOrDefault.ToString(), "Unknown");
        public string? AutomationId => Safe(() => el.Properties.AutomationId.ValueOrDefault, null);
        public string? Name => Safe(() => el.Properties.Name.ValueOrDefault, null);
        public string? ClassName => Safe(() => el.Properties.ClassName.ValueOrDefault, null);
        public bool IsKeyboardFocusable => Safe(() => el.Properties.IsKeyboardFocusable.ValueOrDefault, false);
        public bool IsOffscreen => Safe(() => el.Properties.IsOffscreen.ValueOrDefault, false);
        public double Width => Rect.Width;
        public double Height => Rect.Height;

        private System.Drawing.Rectangle Rect => _rect ??= Safe(() => el.Properties.BoundingRectangle.ValueOrDefault, default);

        public IReadOnlyList<string> Patterns => _patterns ??= ReadPatterns();

        public IReadOnlyList<ILiveNode> Children => _children ??=
            Safe(() => el.CachedChildren ?? [], [])
                .Select(c => (ILiveNode)new FlaUiNode(c, patternChecks))
                .ToArray();

        private List<string> ReadPatterns()
        {
            var list = new List<string>();
            foreach (var (property, name) in patternChecks)
            {
                if (Safe(() => el.FrameworkAutomationElement.TryGetPropertyValue<bool>(property, out var available) && available, false))
                    list.Add(name);
            }
            return list;
        }

        public static (PropertyId Property, string Name)[] PatternChecks(AutomationBase automation)
        {
            var a = automation.PropertyLibrary.PatternAvailability;
            return
            [
                (a.IsInvokePatternAvailable, "Invoke"),
                (a.IsTogglePatternAvailable, "Toggle"),
                (a.IsValuePatternAvailable, "Value"),
                (a.IsSelectionItemPatternAvailable, "SelectionItem"),
                (a.IsExpandCollapsePatternAvailable, "ExpandCollapse"),
                (a.IsRangeValuePatternAvailable, "RangeValue"),
                (a.IsSelectionPatternAvailable, "Selection"),
                (a.IsGridPatternAvailable, "Grid"),
                (a.IsScrollPatternAvailable, "Scroll"),
            ];
        }

        /// <summary>Some elements refuse property requests (or vanish mid-scan); treat that as "no value".</summary>
        private static T Safe<T>(Func<T> get, T fallback)
        {
            try { return get(); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException) { return fallback; }
        }
    }
}
