using WpfLocatorDoctor.Core.Model;
using WpfLocatorDoctor.Core.Rules;
using WpfLocatorDoctor.Core.Scoring;

namespace WpfLocatorDoctor.Core.Assistant;

/// <summary>
/// Works out the exact XAML changes that fix a screen's locator problems: which AutomationId to give each control
/// (unique on the screen) and, where the file can be read, the line as it is now and as it should be.
/// </summary>
public static class XamlFixPlanner
{
    private const string IdAttribute = "AutomationProperties.AutomationId";

    /// <summary>Fixes for one screen, in the order the elements appear in the file.</summary>
    /// <param name="screen">The scanned screen.</param>
    /// <param name="readLines">Returns the lines of a file given its path as shown in the scan (relative to the project folder), or null if it can't be read.</param>
    public static List<XamlFix> PlanScreen(ScreenScan screen, Func<string, string[]?> readLines)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(readLines);

        var elements = screen.Elements.Where(e => !IdHeuristics.IsTemplatePartId(e.AutomationId)).ToList();
        var taken = new HashSet<string>(
            elements.Where(e => !string.IsNullOrWhiteSpace(e.AutomationId)).Select(e => e.AutomationId!.Trim()),
            StringComparer.Ordinal);
        string Unique(string candidate)
        {
            var id = candidate;
            for (var n = 2; !taken.Add(id); n++) id = candidate + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return id;
        }

        // The first element keeps a duplicated ID; the others get a new one.
        var duplicates = elements
            .Where(e => e.VisibleToAutomation && !e.InRepeatedTemplate && !string.IsNullOrWhiteSpace(e.AutomationId))
            .GroupBy(e => e.AutomationId!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.Skip(1))
            .ToHashSet();

        var fixes = new List<XamlFix>();
        foreach (var e in elements)
        {
            var hasId = !string.IsNullOrWhiteSpace(e.AutomationId);
            var where = e.Describe();

            if (duplicates.Contains(e))
            {
                var id = Unique(IdHeuristics.SuggestId(e));
                fixes.Add(Make(e, "fix-duplicate-id", RuleBook.Duplicate, id,
                    $"\"{e.AutomationId}\" is also used by another element on this screen. Give this one {IdAttribute}=\"{id}\" and update any test that meant this element.",
                    readLines));
                continue;
            }

            if (e.IsInteractive && e.VisibleToAutomation)
            {
                if (!hasId)
                {
                    var id = Unique(IdHeuristics.SuggestId(e));
                    var inRow = e.InRepeatedTemplate ? " It is inside a list row template, so every row gets this ID; tests should find the row first, then this control inside it." : "";
                    fixes.Add(Make(e, "add-id", RuleBook.MissingId, id, $"Add {IdAttribute}=\"{id}\" to {where}.{inRow}", readLines));
                }
                else if (e.IdOrigin is IdOrigin.XName or IdOrigin.Uid)
                {
                    // Keep the value tests already use, but make it explicit so renaming x:Name / regenerating x:Uid can't change it.
                    var from = e.IdOrigin == IdOrigin.XName ? "x:Name" : "x:Uid";
                    fixes.Add(Make(e, "pin-id", e.IdOrigin == IdOrigin.XName ? RuleBook.BorrowedFromXName : RuleBook.BorrowedFromUid, e.AutomationId!,
                        $"{where} borrows its ID from {from}. Add {IdAttribute}=\"{e.AutomationId}\" (the same value), so existing tests keep working and renaming {from} can't break them.",
                        readLines));
                }
                else if (e.IdOrigin == IdOrigin.GeneratedLooking)
                {
                    var id = Unique(IdHeuristics.SuggestId(e));
                    fixes.Add(Make(e, "replace-generated-id", RuleBook.GeneratedLooking, id,
                        $"\"{e.AutomationId}\" looks machine-generated. Set {IdAttribute}=\"{id}\" instead, and update any test that used \"{e.AutomationId}\".",
                        readLines));
                }
            }

            if (e.IsItemsHost && e.RowsIdentifiable == false)
            {
                fixes.Add(new XamlFix("row-ids", RuleBook.RowsNotIdentifiable.Id, e.Screen, where, e.Location?.File, LineOf(e), e.AutomationId, null,
                    Scorer.RowStyleSnippet(e.Kind), null));
            }
        }
        return fixes;
    }

    /// <summary>C# fixes for custom controls that test tools can't see (they need an automation peer).</summary>
    public static List<XamlFix> PlanCustomControls(ScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return scan.CustomControls
            .Where(c => c.Status == PeerStatus.Invisible)
            .Select(c => new XamlFix("automation-peer", RuleBook.InvisibleCustomControl.Id, "(code)", c.ClassName,
                c.Location?.File, c.Location?.Line is > 0 ? c.Location.Line : null, null, null,
                $"In {c.ClassName}, add: protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this); " +
                "(needs using System.Windows.Automation.Peers;). A custom peer that reports the right control type and actions is even better.",
                null))
            .ToList();
    }

    private static XamlFix Make(UiElementInfo e, string kind, RuleInfo rule, string newId, string instruction, Func<string, string[]?> readLines)
    {
        LineEdit? edit = null;
        if (e.Location is { Line: > 0 } loc && readLines(loc.File) is { } lines)
            edit = BuildEdit(lines, loc.Line, loc.Column, newId);
        return new XamlFix(kind, rule.Id, e.Screen, e.Describe(), e.Location?.File, LineOf(e), e.AutomationId, newId, instruction, edit);
    }

    private static int? LineOf(UiElementInfo e) => e.Location?.Line is > 0 ? e.Location.Line : null;

    /// <summary>
    /// Sets AutomationProperties.AutomationId on the element whose start tag begins at (line, column), where column points at
    /// the tag name (as XML line info reports it). Replaces the value if the attribute is already there, otherwise adds it
    /// right after the tag name. Returns null if the text doesn't look like the expected tag.
    /// </summary>
    public static LineEdit? BuildEdit(string[] lines, int line, int column, string newId)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(newId);
        if (line < 1 || line > lines.Length) return null;
        var first = lines[line - 1];
        var nameStart = column - 1;
        if (nameStart < 1 || nameStart >= first.Length || first[nameStart - 1] != '<') return null;

        var nameEnd = nameStart;
        while (nameEnd < first.Length && !char.IsWhiteSpace(first[nameEnd]) && first[nameEnd] is not '/' and not '>') nameEnd++;
        if (nameEnd == nameStart) return null;

        var value = EscapeAttribute(newId);

        // Walk the start tag (it may span several lines) looking for an existing AutomationId attribute.
        char? quote = null;
        for (var l = line - 1; l < lines.Length; l++)
        {
            var text = lines[l];
            for (var i = l == line - 1 ? nameEnd : 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (quote is not null)
                {
                    if (ch == quote) quote = null;
                    continue;
                }
                if (ch is '"' or '\'') { quote = ch; continue; }
                if (ch == '>') return Insert();
                if (string.CompareOrdinal(text, i, IdAttribute, 0, IdAttribute.Length) == 0 &&
                    (i == 0 || char.IsWhiteSpace(text[i - 1])))
                {
                    var eq = i + IdAttribute.Length;
                    while (eq < text.Length && char.IsWhiteSpace(text[eq])) eq++;
                    if (eq >= text.Length || text[eq] != '=') continue;
                    var open = eq + 1;
                    while (open < text.Length && char.IsWhiteSpace(text[open])) open++;
                    if (open >= text.Length || text[open] is not '"' and not '\'') return null;
                    var close = text.IndexOf(text[open], open + 1);
                    if (close < 0) return null;
                    var after = string.Concat(text.AsSpan(0, open + 1), value, text.AsSpan(close));
                    return new LineEdit(l + 1, text, after);
                }
            }
        }
        return Insert();

        LineEdit Insert() => new(line, first, string.Concat(first.AsSpan(0, nameEnd), $" {IdAttribute}=\"{value}\"", first.AsSpan(nameEnd)));
    }

    private static string EscapeAttribute(string s) =>
        s.Replace("&", "&amp;", StringComparison.Ordinal)
         .Replace("\"", "&quot;", StringComparison.Ordinal)
         .Replace("<", "&lt;", StringComparison.Ordinal);
}
