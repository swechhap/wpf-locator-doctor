using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Identifiers;
using FlaUI.Core.Input;
using FlaUI.UIA2;
using FlaUI.UIA3;
using WpfLocatorDoctor.Core.Assistant;
using WpfLocatorDoctor.Core.Live;

namespace WpfLocatorDoctor.Live;

/// <summary>
/// Starts or attaches to a WPF app for the AI assistant's live tools, using FlaUI. Windows only.
/// It only reads the app and carries out actions; <see cref="LiveSession"/> in Core makes every decision.
/// </summary>
public sealed class FlaUiAppConnector : ILiveAppConnector
{
    public ILiveApp Open(LiveOpenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows())
            throw new DoctorInputException("The live tools use Windows UI Automation, so they only work on Windows.");

        AutomationBase automation = request.UseUia2 ? new UIA2Automation() : new UIA3Automation();
        automation.TransactionTimeout = TimeSpan.FromSeconds(60);
        automation.ConnectionTimeout = TimeSpan.FromSeconds(60);
        Application? app = null;
        var launched = request.ExePath is not null;
        try
        {
            if (launched)
            {
                app = string.IsNullOrWhiteSpace(request.Arguments)
                    ? Application.Launch(request.ExePath!)
                    : Application.Launch(request.ExePath!, request.Arguments);
            }
            else
            {
                app = int.TryParse(request.Process, out var pid)
                    ? Application.Attach(pid)
                    : Application.Attach(request.Process!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? request.Process : request.Process + ".exe");
            }

            app.WaitWhileMainHandleIsMissing(request.WaitForWindow);
            if (app.GetMainWindow(automation, request.WaitForWindow) is null)
                throw new DoctorInputException(
                    $"{app.Name}'s main window didn't appear within {request.WaitForWindow.TotalSeconds:0} seconds. Try a longer waitSeconds, or check that the app starts correctly.");
            app.WaitWhileBusy(request.WaitForWindow);
            return new FlaUiApp(app, automation, launched);
        }
        catch (Exception ex) when (ex is not DoctorInputException && IsAutomationProblem(ex))
        {
            Cleanup(app, automation, launched);
            throw new DoctorInputException(launched
                ? $"Couldn't start {request.ExePath}: {ex.Message}"
                : $"Couldn't attach to '{request.Process}': {ex.Message} Check the process name (without .exe) or ID, and that it is running.", ex);
        }
        catch
        {
            Cleanup(app, automation, launched);
            throw;
        }
    }

    private static void Cleanup(Application? app, AutomationBase automation, bool launched)
    {
        if (app is not null)
        {
            if (launched)
            {
                try { app.Kill(); } catch (InvalidOperationException) { } catch (Win32Exception) { }
            }
            app.Dispose();
        }
        automation.Dispose();
    }

    internal static bool IsAutomationProblem(Exception ex) =>
        ex is COMException or InvalidOperationException or ArgumentException or Win32Exception or FlaUIException
            or TimeoutException or UnauthorizedAccessException or System.NotSupportedException;
}

/// <summary>One app opened by <see cref="FlaUiAppConnector"/>.</summary>
internal sealed class FlaUiApp(Application app, AutomationBase automation, bool startedByTool) : ILiveApp
{
    private const int MaxNodes = LiveTree.MaxNodesPerWindow;
    private const int MaxScreenshotWidth = 1600;
    private readonly (PropertyId Property, string Name)[] _patternChecks = PatternChecks(automation);

    public string Name => app.Name;
    public int ProcessId => app.ProcessId;
    public bool StartedByTool => startedByTool;
    public bool HasExited => Safe(() => app.HasExited, true);

    public IReadOnlyList<ILiveNode> ReadWindows(bool includeChildren)
    {
        try
        {
            return AllWindows().Select(w => (ILiveNode)Read(w, includeChildren)).ToList();
        }
        catch (Exception ex) when (FlaUiAppConnector.IsAutomationProblem(ex))
        {
            if (HasExited) return [];
            throw new DoctorInputException($"Couldn't read the app's windows ({ex.Message}). If the app runs as administrator, run the server as administrator too.", ex);
        }
    }

    /// <summary>
    /// The app's top-level windows, plus the windows they own (a dialog opened with Owner = this). UI Automation lists an owned
    /// window inside its owner rather than next to it, so it is looked for there and treated as a window of its own.
    /// </summary>
    private List<AutomationElement> AllWindows()
    {
        var result = new List<AutomationElement>();
        var pending = new Queue<(AutomationElement Window, int Depth)>(app.GetAllTopLevelWindows(automation).Select(w => ((AutomationElement)w, 0)));
        var isWindow = automation.ConditionFactory.ByControlType(ControlType.Window);
        while (pending.Count > 0 && result.Count < 50)
        {
            var (window, depth) = pending.Dequeue();
            result.Add(window);
            if (depth >= 5) continue;
            foreach (var owned in Safe(() => window.FindAllChildren(isWindow), []))
                pending.Enqueue((owned, depth + 1));
        }
        return result;
    }

    /// <summary>Reads a window (and, if asked, everything in it) in one cross-process call, keeping each element usable for actions.</summary>
    private LiveElement Read(AutomationElement window, bool includeChildren)
    {
        var lib = automation.PropertyLibrary;
        var cache = new CacheRequest
        {
            TreeScope = includeChildren ? TreeScope.Subtree : TreeScope.Element,
            AutomationElementMode = AutomationElementMode.Full,
            TreeFilter = new PropertyCondition(lib.Element.IsControlElement, true),
        };
        foreach (var p in new[]
                 {
                     lib.Element.AutomationId, lib.Element.Name, lib.Element.ClassName, lib.Element.ControlType,
                     lib.Element.FrameworkId, lib.Element.IsKeyboardFocusable, lib.Element.IsOffscreen,
                     lib.Element.BoundingRectangle, lib.Element.IsEnabled, lib.Element.IsPassword, lib.Element.RuntimeId,
                     lib.Value.Value, lib.Toggle.ToggleState, lib.ExpandCollapse.ExpandCollapseState, lib.SelectionItem.IsSelected,
                 })
        {
            cache.Add(p);
        }
        foreach (var (property, _) in _patternChecks) cache.Add(property);

        using (cache.Activate())
        {
            var root = window.FindFirst(TreeScope.Element, TrueCondition.Default) ?? window;
            var count = 0;
            return Build(root, includeChildren, ref count);
        }
    }

    private LiveElement Build(AutomationElement el, bool includeChildren, ref int count)
    {
        count++;
        var rect = Safe(() => el.Properties.BoundingRectangle.ValueOrDefault, default);
        var node = new LiveElement(el)
        {
            ControlType = Safe(() => el.Properties.ControlType.ValueOrDefault.ToString(), "Unknown"),
            AutomationId = Safe(() => el.Properties.AutomationId.ValueOrDefault, null),
            Name = Safe(() => el.Properties.Name.ValueOrDefault, null),
            ClassName = Safe(() => el.Properties.ClassName.ValueOrDefault, null),
            IsKeyboardFocusable = Safe(() => el.Properties.IsKeyboardFocusable.ValueOrDefault, false),
            IsOffscreen = Safe(() => el.Properties.IsOffscreen.ValueOrDefault, false),
            IsEnabled = Safe(() => el.Properties.IsEnabled.ValueOrDefault, true),
            IsPassword = Safe(() => el.Properties.IsPassword.ValueOrDefault, false),
            RuntimeId = Safe(() => el.Properties.RuntimeId.ValueOrDefault is { Length: > 0 } ids ? string.Join('.', ids) : null, null),
            Width = rect.Width,
            Height = rect.Height,
            Patterns = ReadPatterns(el),
        };
        node.Value = node.IsPassword ? null : ReadText(el, automation.PropertyLibrary.Value.Value);
        node.ToggleState = Normalize(ReadText(el, automation.PropertyLibrary.Toggle.ToggleState), ["Off", "On", "Indeterminate"]);
        node.ExpandState = Normalize(ReadText(el, automation.PropertyLibrary.ExpandCollapse.ExpandCollapseState),
            ["Collapsed", "Expanded", "PartiallyExpanded", "LeafNode"]);
        node.IsSelected = node.Patterns.Contains("SelectionItem") && ReadText(el, automation.PropertyLibrary.SelectionItem.IsSelected) is { } s
            ? string.Equals(s, "True", StringComparison.OrdinalIgnoreCase)
            : null;

        if (includeChildren)
        {
            var children = new List<ILiveNode>();
            foreach (var child in Safe(() => el.CachedChildren ?? [], []))
            {
                if (count >= MaxNodes) break;
                // An owned window (dialog) is read as a window of its own; see AllWindows.
                if (Safe(() => child.Properties.ControlType.ValueOrDefault == ControlType.Window, false)) continue;
                children.Add(Build(child, includeChildren, ref count));
            }
            node.Children = children;
        }
        return node;
    }

    private List<string> ReadPatterns(AutomationElement el)
    {
        var list = new List<string>();
        foreach (var (property, name) in _patternChecks)
        {
            if (Safe(() => el.FrameworkAutomationElement.TryGetPropertyValue<bool>(property, out var available) && available, false))
                list.Add(name);
        }
        return list;
    }

    private static string? ReadText(AutomationElement el, PropertyId property) =>
        Safe(() => el.FrameworkAutomationElement.TryGetPropertyValue<object>(property, out var value) ? value?.ToString() : null, null);

    /// <summary>UI Automation may hand back an enum's number instead of its name.</summary>
    private static string? Normalize(string? value, string[] names) =>
        int.TryParse(value, out var n) && n >= 0 && n < names.Length ? names[n] : value;

    public void Perform(ILiveNode element, LiveActionKind action, string? text)
    {
        var el = Unwrap(element);
        try
        {
            switch (action)
            {
                case LiveActionKind.Invoke: el.Patterns.Invoke.Pattern.Invoke(); break;
                case LiveActionKind.Toggle: el.Patterns.Toggle.Pattern.Toggle(); break;
                case LiveActionKind.Select: el.Patterns.SelectionItem.Pattern.Select(); break;
                case LiveActionKind.Expand: el.Patterns.ExpandCollapse.Pattern.Expand(); break;
                case LiveActionKind.Collapse: el.Patterns.ExpandCollapse.Pattern.Collapse(); break;
                case LiveActionKind.SetValue: el.Patterns.Value.Pattern.SetValue(text ?? ""); break;
                case LiveActionKind.TypeText:
                    el.Focus();
                    Keyboard.Type(text ?? "");
                    break;
                case LiveActionKind.Focus: el.Focus(); break;
                case LiveActionKind.MouseClick: el.Click(); break;
                case LiveActionKind.SelectChildByName: SelectChild(el, text ?? ""); break;
                default: throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }
        }
        catch (Exception ex) when (ex is not DoctorInputException && FlaUiAppConnector.IsAutomationProblem(ex))
        {
            throw new DoctorInputException(
                $"The app refused ({action}): {ex.Message} The screen may have changed since the snapshot; take a new one and try again.", ex);
        }
    }

    /// <summary>Opens a combo box if needed, then selects the item with that name.</summary>
    private void SelectChild(AutomationElement container, string itemName)
    {
        var expandable = container.Patterns.ExpandCollapse.IsSupported;
        if (expandable) container.Patterns.ExpandCollapse.Pattern.Expand();
        var cf = automation.ConditionFactory;
        var item = container.FindFirstDescendant(cf.ByName(itemName).And(new PropertyCondition(automation.PropertyLibrary.PatternAvailability.IsSelectionItemPatternAvailable, true)));
        if (item is null)
        {
            var names = container.FindAllDescendants(new PropertyCondition(automation.PropertyLibrary.PatternAvailability.IsSelectionItemPatternAvailable, true))
                .Select(i => Safe(() => i.Name, null)).Where(n => !string.IsNullOrEmpty(n)).Take(15).ToList();
            if (expandable) Safe(() => { container.Patterns.ExpandCollapse.Pattern.Collapse(); return true; }, false);
            throw new DoctorInputException(names.Count == 0
                ? $"No item called \"{itemName}\" was found, and the items have no names."
                : $"No item called \"{itemName}\". Items: {string.Join(", ", names.Select(n => $"\"{n}\""))}.");
        }
        item.Patterns.SelectionItem.Pattern.Select();
        if (expandable && container.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.ValueOrDefault == ExpandCollapseState.Expanded)
            Safe(() => { container.Patterns.ExpandCollapse.Pattern.Collapse(); return true; }, false);
    }

    public byte[] CapturePng(ILiveNode element, ILiveNode window)
    {
        var el = Unwrap(element);
        try
        {
            Safe(() => { Unwrap(window).AsWindow().SetForeground(); return true; }, false);
            using var capture = Capture.Element(el);
            var bitmap = capture.Bitmap;
            using var scaled = bitmap.Width > MaxScreenshotWidth
                ? new Bitmap(bitmap, MaxScreenshotWidth, Math.Max(1, bitmap.Height * MaxScreenshotWidth / bitmap.Width))
                : null;
            using var stream = new MemoryStream();
            (scaled ?? bitmap).Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is not DoctorInputException && (FlaUiAppConnector.IsAutomationProblem(ex) || ex is ExternalException))
        {
            throw new DoctorInputException($"Couldn't take the screenshot: {ex.Message} The screen must be unlocked.", ex);
        }
    }

    public void Close()
    {
        if (HasExited) return;
        try
        {
            if (!app.Close()) app.Kill();
        }
        catch (Exception ex) when (FlaUiAppConnector.IsAutomationProblem(ex))
        {
            Safe(() => { app.Kill(); return true; }, false);
        }
    }

    public void Dispose()
    {
        app.Dispose();
        automation.Dispose();
    }

    private static AutomationElement Unwrap(ILiveNode node) =>
        node is LiveElement e ? e.Element : throw new ArgumentException("This element didn't come from this app.", nameof(node));

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

    /// <summary>Some elements refuse property requests (or vanish while being read); treat that as "no value".</summary>
    private static T Safe<T>(Func<T> get, T fallback)
    {
        try { return get(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or System.NotSupportedException or FlaUIException) { return fallback; }
    }

    /// <summary>An element read once, with all its values copied, plus the link to the real element for actions.</summary>
    private sealed class LiveElement(AutomationElement element) : ILiveNode
    {
        public AutomationElement Element { get; } = element;
        public required string ControlType { get; init; }
        public string? AutomationId { get; init; }
        public string? Name { get; init; }
        public string? ClassName { get; init; }
        public bool IsKeyboardFocusable { get; init; }
        public bool IsOffscreen { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }
        public required IReadOnlyList<string> Patterns { get; init; }
        public IReadOnlyList<ILiveNode> Children { get; set; } = [];
        public bool IsEnabled { get; init; }
        public bool IsPassword { get; init; }
        public string? Value { get; set; }
        public string? ToggleState { get; set; }
        public string? ExpandState { get; set; }
        public bool? IsSelected { get; set; }
        public string? RuntimeId { get; init; }
    }
}
