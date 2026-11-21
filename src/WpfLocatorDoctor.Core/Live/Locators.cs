using System.Globalization;
using System.Text;
using WpfLocatorDoctor.Core.Assistant;
using WpfLocatorDoctor.Core.Model;
using WpfLocatorDoctor.Core.Rules;

namespace WpfLocatorDoctor.Core.Live;

/// <summary>
/// How a UI test finds one element. Every field that is set must match.
/// <see cref="Within"/> first narrows the search to the inside of the element with that AutomationId (for example one list row);
/// <see cref="Index"/> (1-based) picks the n-th match; <see cref="Window"/> limits the search to one window
/// (its AutomationId or title).
/// </summary>
public sealed record LocatorQuery(
    string? AutomationId = null,
    string? Name = null,
    string? ControlType = null,
    string? ClassName = null,
    string? Within = null,
    int? Index = null,
    string? Window = null)
{
    /// <summary>True when something besides the window is given.</summary>
    public bool HasElementCriteria =>
        !string.IsNullOrEmpty(AutomationId) || !string.IsNullOrEmpty(Name) || !string.IsNullOrEmpty(ControlType) || !string.IsNullOrEmpty(ClassName);

    /// <summary>Short text, e.g. <c>AutomationId="SaveButton"</c> or <c>within "Row1": Name="Delete" + ControlType=Button</c>.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(AutomationId)) parts.Add($"AutomationId=\"{AutomationId}\"");
        if (!string.IsNullOrEmpty(Name)) parts.Add($"Name=\"{Name}\"");
        if (!string.IsNullOrEmpty(ControlType)) parts.Add($"ControlType={ControlType}");
        if (!string.IsNullOrEmpty(ClassName)) parts.Add($"ClassName={ClassName}");
        var text = parts.Count == 0 ? "the window" : string.Join(" + ", parts);
        if (Index is { } i) text += string.Create(CultureInfo.InvariantCulture, $", match #{i}");
        if (!string.IsNullOrEmpty(Within)) text = $"inside \"{Within}\": {text}";
        if (!string.IsNullOrEmpty(Window)) text += $" (window \"{Window}\")";
        return text;
    }
}

/// <summary>An element of a live tree with where it sits: its window and the elements above it (outermost first, window excluded).</summary>
public sealed record Located(ILiveNode Node, ILiveNode Window, IReadOnlyList<ILiveNode> Ancestors);

/// <summary>Walking and describing live trees. Pure logic: no Windows needed.</summary>
public static class LiveTree
{
    /// <summary>Safety limit for one window.</summary>
    public const int MaxNodesPerWindow = 20000;

    /// <summary>Every element under <paramref name="root"/> (not the root itself), in reading order, with its ancestors.</summary>
    public static IEnumerable<Located> Descendants(ILiveNode root, ILiveNode window, IReadOnlyList<ILiveNode>? rootAncestors = null)
    {
        var count = 0;
        var baseAncestors = ReferenceEquals(root, window) ? [] : (rootAncestors ?? []).Append(root).ToArray();
        var stack = new Stack<(ILiveNode Node, ILiveNode[] Ancestors)>();
        foreach (var child in root.Children.Reverse()) stack.Push((child, baseAncestors));
        while (stack.Count > 0 && count++ < MaxNodesPerWindow)
        {
            var (node, ancestors) = stack.Pop();
            yield return new Located(node, window, ancestors);
            var childAncestors = ancestors.Append(node).ToArray();
            foreach (var child in node.Children.Reverse()) stack.Push((child, childAncestors));
        }
    }

    /// <summary>Short description, e.g. <c>Button "Save" #SaveButton</c> or <c>Custom(StarRating) #CustomerRating</c>.</summary>
    public static string Describe(ILiveNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var kind = node.ControlType;
        var cls = LiveTreeWalker.NullIfEmpty(node.ClassName);
        if (cls is not null && kind is "Custom" or "Pane" or "Group" && cls != kind) kind += $"({cls})";
        var sb = new StringBuilder(kind);
        if (LiveTreeWalker.NullIfEmpty(node.Name) is { } name) sb.Append(" \"").Append(Shorten(name, 60)).Append('"');
        if (LiveTreeWalker.NullIfEmpty(node.AutomationId) is { } id) sb.Append(" #").Append(id);
        return sb.ToString();
    }

    /// <summary>How a window is shown: <c>"Orders" #OrdersWindow</c>.</summary>
    public static string DescribeWindow(ILiveNode window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var title = LiveTreeWalker.NullIfEmpty(window.Name);
        var id = LiveTreeWalker.NullIfEmpty(window.AutomationId);
        return (title, id) switch
        {
            (not null, not null) => $"\"{title}\" #{id}",
            (not null, null) => $"\"{title}\"",
            (null, not null) => $"#{id}",
            _ => window.ClassName ?? "Window",
        };
    }

    /// <summary>What a locator should call a window: its AutomationId if it has one, else its title.</summary>
    public static string? WindowKey(ILiveNode window) =>
        LiveTreeWalker.NullIfEmpty(window.AutomationId) ?? LiveTreeWalker.NullIfEmpty(window.Name);

    /// <summary>Finds a window by AutomationId, title or class name (ignoring case), then by part of its title.</summary>
    public static ILiveNode? FindWindow(IReadOnlyList<ILiveNode> windows, string wanted)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var w = wanted.Trim();
        bool Is(string? s) => string.Equals(s?.Trim(), w, StringComparison.OrdinalIgnoreCase);
        return windows.FirstOrDefault(x => Is(x.AutomationId))
            ?? windows.FirstOrDefault(x => Is(x.Name))
            ?? windows.FirstOrDefault(x => Is(x.ClassName))
            ?? windows.FirstOrDefault(x => x.Name?.Contains(w, StringComparison.OrdinalIgnoreCase) == true);
    }

    internal static string Shorten(string s, int max)
    {
        s = s.ReplaceLineEndings(" ");
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}

/// <summary>Finds the elements a <see cref="LocatorQuery"/> matches, the way a UI test would.</summary>
public static class LocatorMatcher
{
    /// <summary>All matches, in reading order. With an index, at most one.</summary>
    public static List<Located> Find(IReadOnlyList<ILiveNode> windows, LocatorQuery query)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(query);
        var matches = new List<Located>();
        foreach (var window in WindowsFor(windows, query))
        {
            IEnumerable<Located> candidates;
            if (string.IsNullOrEmpty(query.Within))
            {
                candidates = LiveTree.Descendants(window, window).Prepend(new Located(window, window, []));
            }
            else
            {
                var scopes = LiveTree.Descendants(window, window).Where(l => l.Node.AutomationId == query.Within).ToList();
                candidates = scopes.SelectMany(s => LiveTree.Descendants(s.Node, window, s.Ancestors));
            }
            matches.AddRange(candidates.Where(c => Matches(c.Node, query)));
        }

        if (query.Index is { } index)
            return index >= 1 && index <= matches.Count ? [matches[index - 1]] : [];
        return matches;
    }

    /// <summary>The windows a query searches: the one it names, or all of them.</summary>
    public static IReadOnlyList<ILiveNode> WindowsFor(IReadOnlyList<ILiveNode> windows, LocatorQuery query)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.Window)) return windows;
        return LiveTree.FindWindow(windows, query.Window) is { } w ? [w] : [];
    }

    public static bool Matches(ILiveNode node, LocatorQuery query)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(query);
        return (string.IsNullOrEmpty(query.AutomationId) || node.AutomationId == query.AutomationId) &&
               (string.IsNullOrEmpty(query.Name) || node.Name == query.Name) &&
               (string.IsNullOrEmpty(query.ControlType) || string.Equals(node.ControlType, query.ControlType, StringComparison.OrdinalIgnoreCase)) &&
               (string.IsNullOrEmpty(query.ClassName) || node.ClassName == query.ClassName);
    }

    /// <summary>
    /// When nothing matches: elements that nearly do (same ID or name but different case, or one containing the other).
    /// Helps spot a renamed control.
    /// </summary>
    public static List<Located> CloseMatches(IReadOnlyList<ILiveNode> windows, LocatorQuery query, int max = 5)
    {
        ArgumentNullException.ThrowIfNull(query);
        static bool Near(string? actual, string? wanted) =>
            !string.IsNullOrEmpty(actual) && !string.IsNullOrEmpty(wanted) &&
            (string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase) ||
             (wanted.Length >= 3 && actual.Contains(wanted, StringComparison.OrdinalIgnoreCase)) ||
             (actual.Length >= 3 && wanted.Contains(actual, StringComparison.OrdinalIgnoreCase)));

        return WindowsFor(windows, query)
            .SelectMany(w => LiveTree.Descendants(w, w))
            .Where(l => Near(l.Node.AutomationId, query.AutomationId) || Near(l.Node.Name, query.Name ?? query.AutomationId))
            .Take(max)
            .ToList();
    }
}

/// <summary>One way to find an element, with how much to trust it. Strength is "high", "medium" or "low".</summary>
public sealed record LocatorOption(int Rank, string Strength, double Confidence, LocatorQuery Locator, string Reason, string FlaUi, string Appium);

/// <summary>
/// Ranks the ways a test could find one element, best first. Only locators that find exactly this one element
/// (on the latest read of its window) are offered.
/// </summary>
public static class LocatorAdvisor
{
    public static IReadOnlyList<LocatorOption> Recommend(Located target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var node = target.Node;
        var all = LiveTree.Descendants(target.Window, target.Window).Select(l => l.Node).Prepend(target.Window).ToList();
        var windowKey = LiveTree.WindowKey(target.Window);
        var id = LiveTreeWalker.NullIfEmpty(node.AutomationId);
        var name = LiveTreeWalker.NullIfEmpty(node.Name);
        var type = node.ControlType;
        var options = new List<(double Confidence, LocatorQuery Locator, string Reason)>();

        int Count(IEnumerable<ILiveNode> scope, LocatorQuery q) => scope.Count(n => LocatorMatcher.Matches(n, q));

        // 1. The AutomationId on its own: what tests should use.
        if (id is not null)
        {
            var byId = new LocatorQuery(AutomationId: id, Window: windowKey);
            var idCount = Count(all, byId);
            var generated = IdHeuristics.LooksGenerated(id);
            if (idCount == 1 && !generated)
                options.Add((0.95, byId, "The AutomationId is unique on this window and looks meaningful. IDs don't change when the text, the language or the layout changes."));
            else if (idCount == 1)
                options.Add((0.4, byId, $"Unique now, but \"{id}\" looks machine-made (for example generated by a translation tool), so it may change in the next build."));
            else
            {
                var byIdAndType = byId with { ControlType = type };
                if (Count(all, byIdAndType) == 1)
                    options.Add((generated ? 0.35 : 0.6, byIdAndType,
                        $"\"{id}\" is used by {idCount} elements on this window; adding the control type makes it unique, but it breaks if another {type} gets the same ID."));
            }
        }

        // 2. Inside the nearest ancestor that has a unique AutomationId (for example a list row or a panel).
        var scope = target.Ancestors.LastOrDefault(a =>
            LiveTreeWalker.NullIfEmpty(a.AutomationId) is { } aid && all.Count(n => n.AutomationId == aid) == 1);
        if (scope is not null && (id is null || Count(all, new LocatorQuery(AutomationId: id)) > 1))
        {
            var inside = LiveTree.Descendants(scope, target.Window).Select(l => l.Node).ToList();
            var scopeGenerated = IdHeuristics.LooksGenerated(scope.AutomationId);
            var scoped = id is not null && Count(inside, new LocatorQuery(AutomationId: id)) == 1
                ? new LocatorQuery(AutomationId: id, Within: scope.AutomationId, Window: windowKey)
                : name is not null && Count(inside, new LocatorQuery(Name: name, ControlType: type)) == 1
                    ? new LocatorQuery(Name: name, ControlType: type, Within: scope.AutomationId, Window: windowKey)
                    : null;
            if (scoped is not null)
            {
                var byId = scoped.AutomationId is not null;
                var confidence = (byId ? 0.85 : 0.6) - (scopeGenerated || (byId && IdHeuristics.LooksGenerated(id)) ? 0.3 : 0);
                options.Add((confidence, scoped,
                    $"Looks inside \"{scope.AutomationId}\" ({scope.ControlType}) first, where this element's {(byId ? "AutomationId" : "name")} is unique. " +
                    (byId ? "Good for an ID repeated in every row of a list." : "The name is shown text, so it changes if the wording or language changes.")));
            }
        }

        // 3. The visible name plus the control type.
        if (name is not null)
        {
            var byName = new LocatorQuery(Name: name, ControlType: type, Window: windowKey);
            if (Count(all, byName) == 1)
                options.Add((0.5, byName, "The name is the text the user sees, so this breaks if the wording changes or the app is translated."));
        }

        // 4. Position among elements of the same type: works today, breaks easily.
        var sameType = all.Where(n => n.ControlType == type).ToList();
        var position = sameType.FindIndex(n => ReferenceEquals(n, node)) + 1;
        if (position > 0)
            options.Add((0.2, new LocatorQuery(ControlType: type, Index: position, Window: windowKey),
                $"The {Ordinal(position)} {type} on the window. Breaks as soon as a {type} is added, removed or moved. Use only as a last resort."));

        return options
            .OrderByDescending(o => o.Confidence)
            .Select((o, i) => new LocatorOption(i + 1, Strength(o.Confidence), o.Confidence, o.Locator, o.Reason, FlaUiCode(o.Locator), AppiumCode(o.Locator)))
            .ToList();
    }

    /// <summary>
    /// An AutomationId to add to the app's XAML for this element, unique on its window, e.g. "SaveButton".
    /// Null when the element already has a good, unique one.
    /// </summary>
    public static string? SuggestAppId(Located target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var node = target.Node;
        var all = LiveTree.Descendants(target.Window, target.Window).Select(l => l.Node).ToList();
        var id = LiveTreeWalker.NullIfEmpty(node.AutomationId);
        if (id is not null && !IdHeuristics.LooksGenerated(id) && all.Count(n => n.AutomationId == id) == 1) return null;

        var info = new UiElementInfo
        {
            Screen = LiveTree.WindowKey(target.Window) ?? "Window",
            Kind = LiveTreeWalker.NullIfEmpty(node.ClassName) ?? node.ControlType,
            ControlType = node.ControlType,
            Name = node.Name,
            HasName = !string.IsNullOrWhiteSpace(node.Name),
        };
        var baseId = IdHeuristics.SuggestId(info);
        var used = all.Select(n => n.AutomationId).Where(a => a is not null).ToHashSet(StringComparer.Ordinal);
        var suggestion = baseId;
        for (var i = 2; used.Contains(suggestion); i++) suggestion = string.Create(CultureInfo.InvariantCulture, $"{baseId}{i}");
        return suggestion;
    }

    public static string Strength(double confidence) => confidence >= 0.8 ? "high" : confidence >= 0.45 ? "medium" : "low";

    /// <summary>C# for FlaUI, starting from the window element.</summary>
    public static string FlaUiCode(LocatorQuery q)
    {
        ArgumentNullException.ThrowIfNull(q);
        var conditions = new List<string>();
        if (q.AutomationId is not null) conditions.Add($"cf.ByAutomationId({CsString(q.AutomationId)})");
        if (q.Name is not null) conditions.Add($"cf.ByName({CsString(q.Name)})");
        if (q.ControlType is not null) conditions.Add($"cf.ByControlType(ControlType.{q.ControlType})");
        if (q.ClassName is not null) conditions.Add($"cf.ByClassName({CsString(q.ClassName)})");
        var condition = conditions.Count == 0 ? "cf => cf.ByControlType(ControlType.Window)"
            : "cf => " + conditions[0] + string.Concat(conditions.Skip(1).Select(c => $".And({c})"));
        var from = q.Within is null ? "window" : $"window.FindFirstDescendant(cf => cf.ByAutomationId({CsString(q.Within)}))";
        return q.Index is { } i
            ? string.Create(CultureInfo.InvariantCulture, $"{from}.FindAllDescendants({condition})[{i - 1}]")
            : $"{from}.FindFirstDescendant({condition})";
    }

    /// <summary>C# for Appium (Windows / NovaWindows driver).</summary>
    public static string AppiumCode(LocatorQuery q)
    {
        ArgumentNullException.ThrowIfNull(q);
        var from = q.Within is null ? "driver" : $"driver.FindElement(MobileBy.AccessibilityId({CsString(q.Within)}))";
        if (q.AutomationId is not null && q.Name is null && q.ControlType is null && q.ClassName is null && q.Index is null)
            return $"{from}.FindElement(MobileBy.AccessibilityId({CsString(q.AutomationId)}))";

        var predicates = new List<string>();
        if (q.AutomationId is not null) predicates.Add($"@AutomationId={XPathLiteral(q.AutomationId)}");
        if (q.Name is not null) predicates.Add($"@Name={XPathLiteral(q.Name)}");
        if (q.ClassName is not null) predicates.Add($"@ClassName={XPathLiteral(q.ClassName)}");
        var step = (q.ControlType ?? "*") + (predicates.Count > 0 ? $"[{string.Join(" and ", predicates)}]" : "");
        var xpath = (q.Within is null ? "//" : ".//") + step;
        if (q.Index is { } i) xpath = string.Create(CultureInfo.InvariantCulture, $"({xpath})[{i}]");
        return $"{from}.FindElement(By.XPath({CsString(xpath)}))";
    }

    private static string CsString(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string XPathLiteral(string s) =>
        !s.Contains('\'', StringComparison.Ordinal) ? $"'{s}'"
        : !s.Contains('"', StringComparison.Ordinal) ? $"\"{s}\""
        : "concat('" + s.Replace("'", "', \"'\", '", StringComparison.Ordinal) + "')";

    private static string Ordinal(int n) => n.ToString(CultureInfo.InvariantCulture) + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch
    {
        1 => "st",
        2 => "nd",
        3 => "rd",
        _ => "th",
    });
}

/// <summary>Decides how to carry out what the assistant asked ("click", "type"…) with what the element supports.</summary>
public static class LiveActionPlanner
{
    public static readonly string[] Actions = ["click", "type", "select", "expand", "collapse", "focus"];

    /// <summary>The way to do <paramref name="action"/> on <paramref name="node"/>, and a few words on how, e.g. "invoked it (its click behaviour)".</summary>
    public static (LiveActionKind Kind, string How) Plan(ILiveNode node, string action, string? text)
    {
        ArgumentNullException.ThrowIfNull(node);
        var what = LiveTree.Describe(node);
        var verb = action?.Trim().ToLowerInvariant();
        if (verb is null || !Actions.Contains(verb))
            throw new DoctorInputException($"Unknown action '{action}'. Use one of: {string.Join(", ", Actions)}.");
        if (!node.IsEnabled && verb != "focus")
            throw new DoctorInputException($"{what} is disabled, so it can't be used now. Wait for it to be enabled (wait_for with until \"enabled\").");

        var p = node.Patterns;
        bool Has(string pattern) => p.Contains(pattern);

        switch (verb)
        {
            case "click":
                if (Has("Invoke")) return (LiveActionKind.Invoke, "invoked it (its own click behaviour)");
                if (Has("Toggle")) return (LiveActionKind.Toggle, $"toggled it (it was {(node.ToggleState ?? "unknown").ToLowerInvariant()})");
                if (Has("SelectionItem")) return (LiveActionKind.Select, "selected it");
                if (Has("ExpandCollapse"))
                    return node.ExpandState == "Expanded"
                        ? (LiveActionKind.Collapse, "collapsed it")
                        : (LiveActionKind.Expand, "expanded it");
                if (node.IsOffscreen)
                    throw new DoctorInputException($"{what} has no click behaviour and is off screen, so the mouse can't reach it. Scroll it into view first.");
                return (LiveActionKind.MouseClick, "clicked it with the mouse (it has no click behaviour of its own)");

            case "type":
                if (text is null) throw new DoctorInputException("Give the text to type.");
                if (Has("Value")) return (LiveActionKind.SetValue, node.IsPassword ? "set the password" : "set its text");
                if (node.IsKeyboardFocusable) return (LiveActionKind.TypeText, "typed the text with the keyboard");
                throw new DoctorInputException($"{what} doesn't accept text. Pick a text box (Edit) or a combo box.");

            case "select":
                if (Has("SelectionItem")) return (LiveActionKind.Select, "selected it");
                if (string.IsNullOrEmpty(text))
                    throw new DoctorInputException($"{what} can't be selected itself. To pick one of its items, give the item's text.");
                if (Has("Selection") || node.ControlType is "ComboBox" or "List" or "Tab" or "DataGrid" or "Tree")
                    return (LiveActionKind.SelectChildByName, $"selected the item \"{text}\"");
                throw new DoctorInputException($"{what} isn't a list, so it has no items to select.");

            case "expand":
            case "collapse":
                if (!Has("ExpandCollapse")) throw new DoctorInputException($"{what} can't be expanded or collapsed.");
                return verb == "expand" ? (LiveActionKind.Expand, "expanded it") : (LiveActionKind.Collapse, "collapsed it");

            default:
                return (LiveActionKind.Focus, "moved the keyboard focus to it");
        }
    }
}
