using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Identifiers;
using FlaUI.UIA2;
using FlaUI.UIA3;
using WpfLocatorDoctor.Core.Model;
using WpfLocatorDoctor.Core.Rules;

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
/// </summary>
public sealed class LiveAppScanner
{
    private sealed class WindowState
    {
        public required ScreenScan Screen { get; init; }
        public required AutomationBase Automation { get; init; }
        public required LiveScanOptions Options { get; init; }
        public int Count { get; set; }
        public bool LimitHit { get; set; }
        public int ListsTruncated { get; set; }
    }

    private readonly record struct Ctx(string[] Path, bool InRow, bool? RowsIdentifiable);

    public ScanResult Scan(LiveScanOptions options)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Live scanning uses Windows UI Automation and only works on Windows.");
        if (options.ExePath is null && options.Process is null)
            throw new ArgumentException("Give either an .exe to start or a running process to attach to.");

        using AutomationBase automation = options.UseUia2 ? new UIA2Automation() : new UIA3Automation();
        automation.TransactionTimeout = TimeSpan.FromSeconds(60);
        automation.ConnectionTimeout = TimeSpan.FromSeconds(60);

        Application app;
        var launched = false;
        if (options.ExePath is not null)
        {
            app = string.IsNullOrWhiteSpace(options.Arguments)
                ? Application.Launch(options.ExePath)
                : Application.Launch(options.ExePath, options.Arguments);
            launched = true;
        }
        else if (int.TryParse(options.Process, out var pid))
        {
            app = Application.Attach(pid);
        }
        else
        {
            app = Application.Attach(options.Process!);
        }

        var target = options.ExePath ?? options.Process!;
        var result = new ScanResult { Mode = ScanMode.Live, Target = target };

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
            {
                var screen = ScanWindow(window, automation, options, usedNames);
                result.Screens.Add(screen);
            }
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

    private static ScreenScan ScanWindow(Window window, AutomationBase automation, LiveScanOptions options, HashSet<string> usedNames)
    {
        var lib = automation.PropertyLibrary;
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
        foreach (var p in PatternChecks(automation)) cache.Add(p.Property);

        ScreenScan screen;
        using (cache.Activate())
        {
            var root = window.FindFirst(TreeScope.Element, TrueCondition.Default) ?? window;
            var name = UniqueScreenName(root, usedNames);
            screen = new ScreenScan { Name = name, Origin = SafeString(() => root.Name) };
            var state = new WindowState { Screen = screen, Automation = automation, Options = options };
            Visit(root, new Ctx([], false, null), state, isRoot: true);

            if (state.LimitHit)
                screen.Notes.Add($"Stopped after {options.MaxElementsPerWindow} elements. Use --max-elements to look further.");
            if (state.ListsTruncated > 0)
                screen.Notes.Add($"{state.ListsTruncated} list(s) had more than {options.MaxRowsPerList} rows; only the first {options.MaxRowsPerList} were checked.");
        }
        return screen;
    }

    private static string UniqueScreenName(AutomationElement root, HashSet<string> used)
    {
        var id = SafeString(() => root.Properties.AutomationId.ValueOrDefault);
        var cls = SafeString(() => root.Properties.ClassName.ValueOrDefault);
        var title = SafeString(() => root.Properties.Name.ValueOrDefault);
        var baseName = !string.IsNullOrWhiteSpace(id) ? id!
                     : !string.IsNullOrWhiteSpace(cls) && cls != "Window" ? cls!
                     : !string.IsNullOrWhiteSpace(title) ? title!
                     : "Window";
        var name = baseName;
        for (var i = 2; !used.Add(name); i++) name = $"{baseName} ({i})";
        return name;
    }

    private static void Visit(AutomationElement el, Ctx ctx, WindowState st, bool isRoot)
    {
        if (st.Count >= st.Options.MaxElementsPerWindow)
        {
            st.LimitHit = true;
            return;
        }

        var controlType = SafeControlType(el);
        if (ControlCatalog.LiveSkippedTypes.Contains(controlType)) return;

        var id = NullIfEmpty(SafeString(() => el.Properties.AutomationId.ValueOrDefault));
        if (id == "SystemMenuBar") return;

        st.Count++;
        var className = NullIfEmpty(SafeString(() => el.Properties.ClassName.ValueOrDefault));
        var name = NullIfEmpty(SafeString(() => el.Properties.Name.ValueOrDefault));
        var focusable = SafeBool(() => el.Properties.IsKeyboardFocusable.ValueOrDefault);
        var offscreen = SafeBool(() => el.Properties.IsOffscreen.ValueOrDefault);
        var rect = SafeRect(el);
        var patterns = SupportedPatterns(el, st.Automation);
        var children = SafeChildren(el);

        var isRow = !isRoot && ControlCatalog.LiveRowTypes.Contains(controlType);
        var isItemsHost = ControlCatalog.LiveItemsHostTypes.Contains(controlType);
        var hasAction = patterns.Any(p => p is "Invoke" or "Toggle" or "Value" or "SelectionItem" or "ExpandCollapse" or "RangeValue");
        var interactive = !isRoot && !isRow &&
                          (ControlCatalog.LiveInteractiveTypes.Contains(controlType) ||
                           className is "DatePicker" ||
                           controlType == "Custom" && focusable && hasAction);

        bool? rowsIdentifiable = null;
        AutomationElement[] rows = [];
        if (isItemsHost)
        {
            rows = children.Where(c => ControlCatalog.LiveRowTypes.Contains(SafeControlType(c))).ToArray();
            if (rows.Length > 0)
            {
                var rowIds = rows.Take(st.Options.MaxRowsPerList)
                    .Select(r => NullIfEmpty(SafeString(() => r.Properties.AutomationId.ValueOrDefault)))
                    .ToList();
                rowsIdentifiable = rowIds.All(r => r is not null) && rowIds.Distinct(StringComparer.Ordinal).Count() == rowIds.Count;
            }
        }

        var kind = className ?? controlType;
        var path = (ctx.Path.Length >= 4 ? ctx.Path[^3..] : ctx.Path).Append(kind).ToArray();

        var info = new UiElementInfo
        {
            Screen = st.Screen.Name,
            Kind = kind,
            ControlType = controlType,
            AutomationId = id,
            IdOrigin = id is null ? IdOrigin.None : IdHeuristics.LooksGenerated(id) ? IdOrigin.GeneratedLooking : IdOrigin.Unknown,
            Name = name,
            HasName = name is not null,
            IsInteractive = interactive,
            IsItemsHost = isItemsHost,
            InRepeatedTemplate = ctx.InRow,
            RowsIdentifiable = isItemsHost ? rowsIdentifiable : ctx.InRow ? ctx.RowsIdentifiable : null,
            IsOpaque = !isRoot && !ctx.InRow && controlType is "Custom" or "Pane" && id is null && name is null &&
                       children.Length == 0 && !offscreen && rect.Width >= 24 && rect.Height >= 24,
            Patterns = patterns,
            Path = string.Join(" > ", path),
        };
        st.Screen.Elements.Add(info);

        // Parts inside self-contained controls (slider thumb, date picker button, ...) aren't scored.
        if (!isRoot && ControlCatalog.LiveLeafTypes.Contains(controlType)) return;

        var rowsSeen = 0;
        foreach (var child in children)
        {
            var childIsRow = isItemsHost && ControlCatalog.LiveRowTypes.Contains(SafeControlType(child));
            if (childIsRow && ++rowsSeen > st.Options.MaxRowsPerList)
            {
                if (rowsSeen == st.Options.MaxRowsPerList + 1) st.ListsTruncated++;
                continue;
            }

            // Only data rows (not grid headers) mark their contents as repeated.
            var childCtx = childIsRow ? new Ctx(path, true, rowsIdentifiable) : ctx with { Path = path };
            Visit(child, childCtx, st, isRoot: false);
        }
    }

    private static (PropertyId Property, string Name)[] PatternChecks(AutomationBase automation)
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

    private static List<string> SupportedPatterns(AutomationElement el, AutomationBase automation)
    {
        var list = new List<string>();
        foreach (var (property, name) in PatternChecks(automation))
        {
            try
            {
                if (el.FrameworkAutomationElement.TryGetPropertyValue<bool>(property, out var available) && available)
                    list.Add(name);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException)
            {
                // Some elements refuse property requests; treat as "not supported".
            }
        }
        return list;
    }

    private static string SafeControlType(AutomationElement el)
    {
        try { return el.Properties.ControlType.ValueOrDefault.ToString(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException) { return "Unknown"; }
    }

    private static AutomationElement[] SafeChildren(AutomationElement el)
    {
        try { return el.CachedChildren ?? []; }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException) { return []; }
    }

    private static System.Drawing.Rectangle SafeRect(AutomationElement el)
    {
        try { return el.Properties.BoundingRectangle.ValueOrDefault; }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException) { return default; }
    }

    private static string? SafeString(Func<string?> get)
    {
        try { return get(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException) { return null; }
    }

    private static bool SafeBool(Func<bool> get)
    {
        try { return get(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException) { return false; }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
