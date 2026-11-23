using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

// The benchmark's ground truth: what UI Automation really shows for a running app, read with plain FlaUI.
// Deliberately independent of WPF Locator Doctor's code.
//
//   UiaDump dump  --pid <id> --out <truth.json> [--wait <seconds>]
//       Every element in the UI Automation control view of every window of the process.
//   UiaDump check --pid <id> --locators <locators.json> --out <results.json>
//       Runs each locator the way a FlaUI test would and reports how many elements it finds, and whether it is the intended one.

var options = ParseArgs(args);
if (args.Length == 0 || !options.TryGetValue("pid", out var pidText) || !int.TryParse(pidText, CultureInfo.InvariantCulture, out var pid))
{
    Console.Error.WriteLine("Usage: UiaDump dump --pid <id> --out <file> [--wait <s>] | UiaDump check --pid <id> --locators <file> --out <file>");
    return 2;
}

using var automation = new UIA3Automation();
automation.TransactionTimeout = TimeSpan.FromSeconds(60);
automation.ConnectionTimeout = TimeSpan.FromSeconds(60);
using var app = Application.Attach(pid);
var wait = TimeSpan.FromSeconds(options.TryGetValue("wait", out var w) ? int.Parse(w, CultureInfo.InvariantCulture) : 60);
app.WaitWhileMainHandleIsMissing(wait);
if (app.GetMainWindow(automation, wait) is null)
{
    Console.Error.WriteLine("The app's main window did not appear.");
    return 3;
}
app.WaitWhileBusy(wait);

var json = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

switch (args[0])
{
    case "dump":
    {
        var elements = Dumper.Dump(automation, app);
        File.WriteAllText(options["out"], JsonSerializer.Serialize(new { pid, windows = Dumper.Windows(automation, app).Count, elements }, json));
        Console.WriteLine($"Dumped {elements.Count} elements.");
        return 0;
    }
    case "check":
    {
        var targets = JsonSerializer.Deserialize<List<Target>>(File.ReadAllText(options["locators"]), json) ?? [];
        var results = Checker.Check(automation, app, targets);
        File.WriteAllText(options["out"], JsonSerializer.Serialize(results, json));
        Console.WriteLine($"Checked {results.Sum(r => r.Locators.Count)} locators for {results.Count} elements.");
        return 0;
    }
    default:
        Console.Error.WriteLine($"Unknown command {args[0]}");
        return 2;
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var d = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 1; i + 1 < args.Length; i += 2) d[args[i].TrimStart('-')] = args[i + 1];
    return d;
}

/// <summary>One element of the control view.</summary>
internal sealed record Element(
    int Index, int Parent, int Depth, int Window, string ControlType, string? AutomationId, string? Name, string? ClassName,
    string? FrameworkId, bool IsOffscreen, bool IsEnabled, bool IsKeyboardFocusable, string? RuntimeId, List<string> Patterns,
    int[] Rect);

internal static class Dumper
{
    private const int MaxElements = 30000;

    /// <summary>Top-level windows of the process, as the desktop lists them.</summary>
    public static List<AutomationElement> Windows(UIA3Automation automation, Application app)
    {
        var desktop = automation.GetDesktop();
        return [.. desktop.FindAllChildren(automation.ConditionFactory.ByProcessId(app.ProcessId))];
    }

    public static List<Element> Dump(UIA3Automation automation, Application app)
    {
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        var list = new List<Element>();
        var windows = Windows(automation, app);
        for (var wi = 0; wi < windows.Count; wi++)
        {
            var stack = new Stack<(AutomationElement El, int Parent, int Depth)>();
            stack.Push((windows[wi], -1, 0));
            while (stack.Count > 0 && list.Count < MaxElements)
            {
                var (el, parent, depth) = stack.Pop();
                var index = list.Count;
                list.Add(Read(el, index, parent, depth, wi));
                var children = new List<AutomationElement>();
                var child = Safe(() => walker.GetFirstChild(el), null);
                while (child is not null && children.Count < 5000)
                {
                    children.Add(child);
                    var current = child;
                    child = Safe(() => walker.GetNextSibling(current), null);
                }
                for (var i = children.Count - 1; i >= 0; i--) stack.Push((children[i], index, depth + 1));
            }
        }
        return list;
    }

    private static Element Read(AutomationElement el, int index, int parent, int depth, int window)
    {
        var p = el.Properties;
        var patterns = new List<string>();
        void Has(string name, Func<bool> supported)
        {
            if (Safe(supported, false)) patterns.Add(name);
        }
        Has("Invoke", () => el.Patterns.Invoke.IsSupported);
        Has("Toggle", () => el.Patterns.Toggle.IsSupported);
        Has("Value", () => el.Patterns.Value.IsSupported);
        Has("SelectionItem", () => el.Patterns.SelectionItem.IsSupported);
        Has("ExpandCollapse", () => el.Patterns.ExpandCollapse.IsSupported);
        Has("RangeValue", () => el.Patterns.RangeValue.IsSupported);
        Has("Selection", () => el.Patterns.Selection.IsSupported);
        Has("Grid", () => el.Patterns.Grid.IsSupported);
        var rect = Safe(() => p.BoundingRectangle.ValueOrDefault, default);
        return new Element(
            index, parent, depth, window,
            Safe(() => p.ControlType.ValueOrDefault.ToString(), "Unknown"),
            Empty(Safe(() => p.AutomationId.ValueOrDefault, null)),
            Empty(Safe(() => p.Name.ValueOrDefault, null)),
            Empty(Safe(() => p.ClassName.ValueOrDefault, null)),
            Empty(Safe(() => p.FrameworkId.ValueOrDefault, null)),
            Safe(() => p.IsOffscreen.ValueOrDefault, false),
            Safe(() => p.IsEnabled.ValueOrDefault, false),
            Safe(() => p.IsKeyboardFocusable.ValueOrDefault, false),
            RuntimeId(el),
            patterns,
            [rect.X, rect.Y, rect.Width, rect.Height]);
    }

    public static string? RuntimeId(AutomationElement el) =>
        Safe(() => el.Properties.RuntimeId.ValueOrDefault is { Length: > 0 } ids
            ? string.Join('.', ids.Select(i => i.ToString(CultureInfo.InvariantCulture)))
            : null, null);

    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    internal static T Safe<T>(Func<T> get, T fallback)
    {
        try { return get(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or TimeoutException or ArgumentException
                                       or FlaUI.Core.Exceptions.FlaUIException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }
}

/// <summary>A locator to check (the fields a test would use).</summary>
internal sealed record Locator(
    int Rank, double Confidence, string? AutomationId, string? Name, string? ControlType, string? ClassName, string? Within, int? Index,
    string? Window);

/// <summary>An element and the locators the tool recommended for it.</summary>
internal sealed record Target(string Element, string? RuntimeId, List<Locator> Locators);

internal sealed record LocatorResult(int Rank, double Confidence, string Locator, int Count, bool FindsTarget, string? Problem);

internal sealed record TargetResult(string Element, string? RuntimeId, List<LocatorResult> Locators);

internal static class Checker
{
    public static List<TargetResult> Check(UIA3Automation automation, Application app, List<Target> targets)
    {
        var cf = automation.ConditionFactory;
        var windows = AllWindows(automation, app);
        var results = new List<TargetResult>();
        foreach (var target in targets)
        {
            var list = new List<LocatorResult>();
            foreach (var l in target.Locators)
            {
                var text = Describe(l);
                try
                {
                    var (count, found) = Run(cf, windows, l);
                    var findsTarget = count == 1 && found is not null && target.RuntimeId is not null && Dumper.RuntimeId(found) == target.RuntimeId;
                    list.Add(new LocatorResult(l.Rank, l.Confidence, text, count, findsTarget, null));
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or TimeoutException
                                               or FlaUI.Core.Exceptions.FlaUIException)
                {
                    list.Add(new LocatorResult(l.Rank, l.Confidence, text, 0, false, ex.GetType().Name + ": " + ex.Message));
                }
            }
            results.Add(new TargetResult(target.Element, target.RuntimeId, list));
        }
        return results;
    }

    /// <summary>Top-level windows and the windows they own (UI Automation lists owned windows inside their owner).</summary>
    private static List<AutomationElement> AllWindows(UIA3Automation automation, Application app)
    {
        var result = new List<AutomationElement>();
        var queue = new Queue<(AutomationElement, int)>(Dumper.Windows(automation, app).Select(w => (w, 0)));
        while (queue.Count > 0)
        {
            var (w, depth) = queue.Dequeue();
            result.Add(w);
            if (depth < 5)
            {
                foreach (var owned in Dumper.Safe(() => w.FindAllChildren(automation.ConditionFactory.ByControlType(ControlType.Window)), []))
                    queue.Enqueue((owned, depth + 1));
            }
        }
        return result;
    }

    /// <summary>
    /// Count of matches and the element a test would get. With Index, the n-th match (count 1 if it exists).
    /// Searches the way the FlaUI code the tool gives does: from the window (or from the first element with the Within ID) down.
    /// </summary>
    private static (int Count, AutomationElement? Found) Run(ConditionFactory cf, List<AutomationElement> windows, Locator l)
    {
        var window = l.Window is null ? null
            : windows.FirstOrDefault(w => Dumper.Safe(() => w.Properties.AutomationId.ValueOrDefault, null) == l.Window)
              ?? windows.FirstOrDefault(w => Dumper.Safe(() => w.Properties.Name.ValueOrDefault, null) == l.Window);
        var scopes = window is not null ? [window] : l.Window is null ? windows : [];

        var conditions = new List<ConditionBase>();
        if (l.AutomationId is not null) conditions.Add(cf.ByAutomationId(l.AutomationId));
        if (l.Name is not null) conditions.Add(cf.ByName(l.Name));
        if (l.ControlType is not null) conditions.Add(cf.ByControlType(Enum.Parse<ControlType>(l.ControlType)));
        if (l.ClassName is not null) conditions.Add(cf.ByClassName(l.ClassName));
        if (conditions.Count == 0) return window is null ? (0, null) : (1, window);
        ConditionBase condition = conditions.Count == 1 ? conditions[0] : new AndCondition(conditions.ToArray());

        var matches = new List<AutomationElement>();
        foreach (var scope in scopes)
        {
            var from = scope;
            if (l.Within is not null)
            {
                from = scope.FindFirstDescendant(cf.ByAutomationId(l.Within));
                if (from is null) continue;
            }
            matches.AddRange(from.FindAllDescendants(condition));
        }

        if (l.Index is { } i) return i >= 1 && i <= matches.Count ? (1, matches[i - 1]) : (0, null);
        return (matches.Count, matches.Count == 1 ? matches[0] : null);
    }

    private static string Describe(Locator l)
    {
        var parts = new List<string>();
        if (l.AutomationId is not null) parts.Add($"AutomationId=\"{l.AutomationId}\"");
        if (l.Name is not null) parts.Add($"Name=\"{l.Name}\"");
        if (l.ControlType is not null) parts.Add($"ControlType={l.ControlType}");
        if (l.ClassName is not null) parts.Add($"ClassName={l.ClassName}");
        var text = string.Join(" + ", parts);
        if (l.Index is { } i) text += string.Create(CultureInfo.InvariantCulture, $", match #{i}");
        if (l.Within is not null) text = $"inside \"{l.Within}\": {text}";
        if (l.Window is not null) text += $" (window \"{l.Window}\")";
        return text;
    }
}
