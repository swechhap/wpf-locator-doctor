using WpfLocatorDoctor.Core.Model;
using WpfLocatorDoctor.Core.Rules;

namespace WpfLocatorDoctor.Core.Live;

/// <summary>
/// One element of a running app's UI Automation tree, as the live scan needs to see it.
/// The Windows-only live scanner wraps FlaUI elements in this interface; tests use simple fakes.
/// Implementations should never throw: return null / false / empty when a value can't be read.
/// </summary>
public interface ILiveNode
{
    /// <summary>UI Automation control type name, e.g. "Button", "Edit", "Custom".</summary>
    string ControlType { get; }
    string? AutomationId { get; }
    string? Name { get; }
    string? ClassName { get; }
    bool IsKeyboardFocusable { get; }
    bool IsOffscreen { get; }
    double Width { get; }
    double Height { get; }
    /// <summary>Behaviours (patterns) the element supports, e.g. "Invoke", "Value".</summary>
    IReadOnlyList<string> Patterns { get; }
    IReadOnlyList<ILiveNode> Children { get; }

    // Current state, used by the AI assistant's live tools. The scan doesn't need them, so they have defaults.

    bool IsEnabled => true;
    /// <summary>A password box: its text is never read or shown.</summary>
    bool IsPassword => false;
    /// <summary>Text or value shown by the element (Value behaviour), e.g. a text box's text.</summary>
    string? Value => null;
    /// <summary>"On", "Off" or "Indeterminate" for check boxes and toggle buttons.</summary>
    string? ToggleState => null;
    /// <summary>"Expanded", "Collapsed", "PartiallyExpanded" or "LeafNode".</summary>
    string? ExpandState => null;
    /// <summary>For list rows, tabs and radio buttons: is it selected?</summary>
    bool? IsSelected => null;
    /// <summary>
    /// UI Automation's identity for the element, if known. The same control can be reached twice (an open menu's items appear under
    /// the menu and in its pop-up window), and this tells the two apart from two different controls.
    /// </summary>
    string? RuntimeId => null;
}

/// <summary>Safety limits for walking a live tree.</summary>
public sealed record LiveWalkLimits(int MaxElementsPerWindow = 5000, int MaxRowsPerList = 50);

/// <summary>
/// Turns one window's UI Automation tree into the elements the scorer understands.
/// Pure logic with no Windows dependency, so it is fully testable on any operating system.
/// </summary>
public static class LiveTreeWalker
{
    private readonly record struct Ctx(string[] Path, bool InRow, bool? RowsIdentifiable);

    private sealed class WalkState(ScreenScan screen, LiveWalkLimits limits)
    {
        public ScreenScan Screen { get; } = screen;
        public LiveWalkLimits Limits { get; } = limits;
        public int Count { get; set; }
        public bool LimitHit { get; set; }
        public int ListsTruncated { get; set; }
    }

    /// <summary>Minimum size (in pixels) for an empty element to count as an area tests can't look into.</summary>
    private const double OpaqueMinSize = 24;

    /// <summary>Walks a window and returns it as a screen. Names already in <paramref name="usedNames"/> get a " (2)" suffix.</summary>
    public static ScreenScan WalkWindow(ILiveNode window, LiveWalkLimits limits, ISet<string> usedNames)
    {
        var screen = new ScreenScan { Name = UniqueScreenName(window, usedNames), Origin = window.Name };
        var state = new WalkState(screen, limits);
        Visit(window, new Ctx([], false, null), state, isRoot: true);

        if (state.LimitHit)
            screen.Notes.Add($"Stopped after {limits.MaxElementsPerWindow} elements. Use --max-elements to look further.");
        if (state.ListsTruncated > 0)
            screen.Notes.Add($"{state.ListsTruncated} list(s) had more than {limits.MaxRowsPerList} rows; only the first {limits.MaxRowsPerList} were checked.");
        return screen;
    }

    /// <summary>Screen name from the window's AutomationId, else its class (if not plain "Window"), else its title.</summary>
    internal static string UniqueScreenName(ILiveNode window, ISet<string> used)
    {
        var baseName = !string.IsNullOrWhiteSpace(window.AutomationId) ? window.AutomationId!
                     : !string.IsNullOrWhiteSpace(window.ClassName) && window.ClassName != "Window" ? window.ClassName!
                     : !string.IsNullOrWhiteSpace(window.Name) ? window.Name!
                     : "Window";
        var name = baseName;
        for (var i = 2; !used.Add(name); i++) name = $"{baseName} ({i})";
        return name;
    }

    private static void Visit(ILiveNode el, Ctx ctx, WalkState st, bool isRoot)
    {
        if (st.Count >= st.Limits.MaxElementsPerWindow)
        {
            st.LimitHit = true;
            return;
        }

        var controlType = el.ControlType;
        if (ControlCatalog.LiveSkippedTypes.Contains(controlType)) return;

        var id = NullIfEmpty(el.AutomationId);
        if (id == "SystemMenuBar") return; // the window's system menu, not part of the app

        st.Count++;
        var className = NullIfEmpty(el.ClassName);
        var name = NullIfEmpty(el.Name);
        var patterns = el.Patterns;
        var children = el.Children;

        var isRow = !isRoot && ControlCatalog.LiveRowTypes.Contains(controlType);
        var isItemsHost = ControlCatalog.LiveItemsHostTypes.Contains(controlType);
        var interactive = !isRoot && !isRow && IsInteractive(controlType, className, el.IsKeyboardFocusable, patterns);

        bool? rowsIdentifiable = null;
        if (isItemsHost)
        {
            var rowIds = children
                .Where(c => ControlCatalog.LiveRowTypes.Contains(c.ControlType))
                .Take(st.Limits.MaxRowsPerList)
                .Select(r => NullIfEmpty(r.AutomationId))
                .ToList();
            if (rowIds.Count > 0)
                rowsIdentifiable = rowIds.All(r => r is not null) && rowIds.Distinct(StringComparer.Ordinal).Count() == rowIds.Count;
        }

        var kind = className ?? controlType;
        var path = (ctx.Path.Length >= 4 ? ctx.Path[^3..] : ctx.Path).Append(kind).ToArray();

        st.Screen.Elements.Add(new UiElementInfo
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
            IsOpaque = !isRoot && !ctx.InRow && LooksOpaque(el, id, name, children.Count),
            Patterns = [.. patterns],
            Path = string.Join(" > ", path),
        });

        // Parts inside self-contained controls (slider thumb, date picker button, ...) aren't scored.
        if (!isRoot && ControlCatalog.LiveLeafTypes.Contains(controlType)) return;

        var rowsSeen = 0;
        foreach (var child in children)
        {
            var childIsRow = isItemsHost && ControlCatalog.LiveRowTypes.Contains(child.ControlType);
            if (childIsRow && ++rowsSeen > st.Limits.MaxRowsPerList)
            {
                if (rowsSeen == st.Limits.MaxRowsPerList + 1) st.ListsTruncated++;
                continue;
            }

            // Only data rows (not grid headers) mark their contents as repeated.
            var childCtx = childIsRow ? new Ctx(path, true, rowsIdentifiable) : ctx with { Path = path };
            Visit(child, childCtx, st, isRoot: false);
        }
    }

    /// <summary>Something a user clicks, types into or selects (not a list row: rows are judged per list).</summary>
    internal static bool IsInteractive(string controlType, string? className, bool keyboardFocusable, IReadOnlyList<string> patterns) =>
        !ControlCatalog.LiveRowTypes.Contains(controlType) &&
        (ControlCatalog.LiveInteractiveTypes.Contains(controlType) ||
         className is "DatePicker" ||
         (controlType == "Custom" && keyboardFocusable &&
          patterns.Any(p => p is "Invoke" or "Toggle" or "Value" or "SelectionItem" or "ExpandCollapse" or "RangeValue")));

    /// <summary>An empty "custom" or "pane" area with no ID, name or children: possibly drawn content test tools can't look into.</summary>
    internal static bool LooksOpaque(ILiveNode el, string? id, string? name, int childCount) =>
        el.ControlType is "Custom" or "Pane" && id is null && name is null && childCount == 0 &&
        !el.IsOffscreen && el.Width >= OpaqueMinSize && el.Height >= OpaqueMinSize;

    internal static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
