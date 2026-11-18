using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using WpfLocatorDoctor.Core.Model;
using WpfLocatorDoctor.Core.Rules;

namespace WpfLocatorDoctor.Core.Source;

/// <summary>
/// Reads a WPF project's XAML (and C#) files without running the app, and lists every element
/// a tester would care about, with where its AutomationId comes from.
/// </summary>
public sealed partial class XamlSourceScanner
{
    private const string WpfNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules", "packages", "TestResults",
    };

    /// <summary>Property elements whose content is styling or resources, not visible controls.</summary>
    private static readonly HashSet<string> SkippedProperties = new(StringComparer.Ordinal)
    {
        "Resources", "Style", "Template", "Triggers", "ItemContainerStyle", "RowStyle", "CellStyle",
        "ColumnHeaderStyle", "RowHeaderStyle", "ItemsPanel", "FocusVisualStyle", "Effect", "Background",
        "Foreground", "BorderBrush", "Fill", "Stroke", "OpacityMask", "RenderTransform", "LayoutTransform",
        "RowDefinitions", "ColumnDefinitions", "InputBindings", "CommandBindings", "DataContext", "Behaviors",
        "ItemContainerStyleSelector", "GroupStyle", "TaskbarItemInfo", "VisualStateGroups", "BitmapEffect",
    };

    /// <summary>Template properties that are stamped out once per row at runtime.</summary>
    private static readonly HashSet<string> RepeatedTemplateProperties = new(StringComparer.Ordinal)
    {
        "ItemTemplate", "CellTemplate", "CellEditingTemplate", "RowDetailsTemplate",
    };

    /// <summary>Tags that describe behaviour or styling rather than visible controls.</summary>
    private static readonly HashSet<string> SkippedTags = new(StringComparer.Ordinal)
    {
        "ControlTemplate", "Style", "Setter", "Trigger", "DataTrigger", "MultiTrigger", "MultiDataTrigger",
        "EventTrigger", "Storyboard", "ResourceDictionary", "Binding", "MultiBinding", "ItemsPanelTemplate",
        "ColumnDefinition", "RowDefinition",
    };

    /// <summary>Controls whose readable name must come from a label, not from their content.</summary>
    private static readonly HashSet<string> NeedsExternalLabel = new(StringComparer.Ordinal)
    {
        "TextBox", "PasswordBox", "RichTextBox", "ComboBox", "ListBox", "ListView", "DataGrid",
        "TreeView", "Slider", "DatePicker", "Calendar", "TabControl",
    };

    private static readonly Dictionary<string, string> RowContainerType = new(StringComparer.Ordinal)
    {
        ["ListBox"] = "ListBoxItem", ["ListView"] = "ListViewItem", ["DataGrid"] = "DataGridRow",
        ["TreeView"] = "TreeViewItem", ["ComboBox"] = "ComboBoxItem",
    };

    [GeneratedRegex(@"^\{\s*(?:StaticResource|DynamicResource)\s+(?:ResourceKey\s*=\s*)?([^\s}]+)\s*\}$")]
    private static partial Regex ResourceReference();

    [GeneratedRegex(@"ElementName\s*=\s*([A-Za-z_][\w]*)|x:Reference\s+([A-Za-z_][\w]*)")]
    private static partial Regex ElementReference();

    [GeneratedRegex(@"^\{\s*x:Type\s+(?:\w+:)?(\w+)\s*\}$")]
    private static partial Regex XTypeReference();

    private readonly record struct Ctx(
        string[] Path,
        bool Repeated,
        bool? RowsIdentifiable,
        bool? HostRows,
        bool NextTemplateRepeats);

    /// <summary>Things learned from all XAML files before scanning individual screens.</summary>
    private sealed class GlobalFacts
    {
        public HashSet<string> KeyedStylesWithId { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ImplicitRowStylesWithId { get; } = new(StringComparer.Ordinal);
        public HashSet<string> KeyedTemplatesWithBoundRoot { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, CustomControlInfo> CustomControls { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>Scans a folder (or a single .xaml / .csproj / .sln file's folder).</summary>
    public ScanResult Scan(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        string root;
        List<string> xamlFiles;

        if (File.Exists(fullPath) && fullPath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            root = System.IO.Path.GetDirectoryName(fullPath)!;
            xamlFiles = [fullPath];
        }
        else
        {
            root = File.Exists(fullPath) ? System.IO.Path.GetDirectoryName(fullPath)! : fullPath;
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException($"Folder not found: {root}");
            xamlFiles = EnumerateFiles(root, "*.xaml").ToList();
        }

        var result = new ScanResult { Mode = ScanMode.Source, Target = fullPath };

        var docs = new List<(string File, string Display, XDocument Doc)>();
        foreach (var file in xamlFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var display = System.IO.Path.GetRelativePath(root, file);
            try
            {
                docs.Add((file, display, XDocument.Load(file, LoadOptions.SetLineInfo)));
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                result.Notes.Add($"Could not read {display}: {ex.Message}");
            }
        }

        var facts = new GlobalFacts();
        foreach (var (_, _, doc) in docs) CollectGlobalFacts(doc, facts);

        var csFiles = EnumerateFiles(root, "*.cs")
            .Where(f => !f.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase))
            .Select(f => (f, System.IO.Path.GetRelativePath(root, f)));
        facts.CustomControls = CodeBehindAnalyzer.Analyze(csFiles);
        result.CustomControls.AddRange(facts.CustomControls.Values.Where(c => c.Status != PeerStatus.Unknown));

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, display, doc) in docs)
        {
            var screen = ScanDocument(doc, display, facts, result.CustomControlUsage, usedNames);
            if (screen is not null) result.Screens.Add(screen);
        }

        if (docs.Count == 0)
            result.Notes.Add("No XAML files were found.");
        return result;
    }

    /// <summary>Scans one XAML text (used by tests and by callers that already have the text).</summary>
    public ScanResult ScanText(string xaml, string displayName = "Screen.xaml", string? csharp = null)
    {
        var result = new ScanResult { Mode = ScanMode.Source, Target = displayName };
        var doc = XDocument.Parse(xaml, LoadOptions.SetLineInfo);
        var facts = new GlobalFacts();
        CollectGlobalFacts(doc, facts);
        if (csharp is not null)
        {
            facts.CustomControls = CodeBehindAnalyzer.AnalyzeText(csharp);
            result.CustomControls.AddRange(facts.CustomControls.Values.Where(c => c.Status != PeerStatus.Unknown));
        }
        var screen = ScanDocument(doc, displayName, facts, result.CustomControlUsage, new HashSet<string>(StringComparer.Ordinal));
        if (screen is not null) result.Screens.Add(screen);
        return result;
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files, subDirs;
            try
            {
                files = Directory.GetFiles(dir, pattern);
                subDirs = Directory.GetDirectories(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var f in files) yield return f;
            foreach (var d in subDirs)
            {
                if (!SkippedDirectories.Contains(System.IO.Path.GetFileName(d)))
                    stack.Push(d);
            }
        }
    }

    // ------------------------------------------------------------------ global pre-pass

    private static void CollectGlobalFacts(XDocument doc, GlobalFacts facts)
    {
        foreach (var style in doc.Descendants().Where(e => e.Name.LocalName == "Style"))
        {
            var setsId = style.Elements().Any(IsAutomationIdSetter) ||
                         style.Elements().Where(e => e.Name.LocalName == "Style.Setters").SelectMany(e => e.Elements()).Any(IsAutomationIdSetter);
            if (!setsId) continue;

            var key = style.Attribute(XName.Get("Key", XamlNs))?.Value;
            if (!string.IsNullOrEmpty(key))
            {
                facts.KeyedStylesWithId.Add(key);
            }
            else
            {
                var target = NormalizeTypeName(style.Attribute("TargetType")?.Value);
                if (target is not null) facts.ImplicitRowStylesWithId.Add(target);
            }
        }

        foreach (var template in doc.Descendants().Where(e => e.Name.LocalName is "DataTemplate" or "HierarchicalDataTemplate"))
        {
            var key = template.Attribute(XName.Get("Key", XamlNs))?.Value;
            if (string.IsNullOrEmpty(key)) continue;
            var templateRoot = template.Elements().FirstOrDefault(e => !e.Name.LocalName.Contains('.'));
            if (templateRoot is not null && GetIdInfo(templateRoot).Origin == IdOrigin.DataBound)
                facts.KeyedTemplatesWithBoundRoot.Add(key);
        }
    }

    private static bool IsAutomationIdSetter(XElement e) =>
        e.Name.LocalName == "Setter" &&
        (e.Attribute("Property")?.Value.Trim() ?? "") is "AutomationProperties.AutomationId" or "(AutomationProperties.AutomationId)";

    private static string? NormalizeTypeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var m = XTypeReference().Match(value.Trim());
        if (m.Success) return m.Groups[1].Value;
        var v = value.Trim();
        var colon = v.IndexOf(':');
        return colon >= 0 ? v[(colon + 1)..] : v;
    }

    // ------------------------------------------------------------------ per screen

    private static ScreenScan? ScanDocument(XDocument doc, string display, GlobalFacts facts, Dictionary<string, int> usage, HashSet<string> usedNames)
    {
        var root = doc.Root;
        if (root is null) return null;
        var rootTag = root.Name.LocalName;
        if (rootTag is "Application" or "ResourceDictionary") return null;

        var xClass = root.Attribute(XName.Get("Class", XamlNs))?.Value;
        var screenName = UniqueScreenName(xClass, display, usedNames);

        var screen = new ScreenScan { Name = screenName, Origin = display };

        // Text of every named element (for AutomationProperties.LabeledBy) and of every Label with a Target.
        var namedText = new Dictionary<string, string>(StringComparer.Ordinal);
        var labelTargets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in root.DescendantsAndSelf())
        {
            var text = e.Attribute("Content")?.Value ?? e.Attribute("Text")?.Value ?? FindText(e, 2);
            if (string.IsNullOrWhiteSpace(text)) continue;
            text = IdHeuristics.CleanLabel(text);

            var name = e.Attribute(XName.Get("Name", XamlNs))?.Value ?? e.Attribute("Name")?.Value;
            if (!string.IsNullOrEmpty(name)) namedText.TryAdd(name, text);

            if (e.Name.LocalName == "Label" && e.Attribute("Target")?.Value is { } target)
            {
                var m = ElementReference().Match(target);
                if (m.Success) labelTargets.TryAdd(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, text);
            }
        }

        var state = new DocState(screen, display, facts, usage, labelTargets, namedText, root);
        Visit(root, new Ctx([], false, null, null, true), state);
        return screen;
    }

    /// <summary>
    /// The class name without its namespace ("MainView"). If another screen already has that name,
    /// the full class name ("Shop.Customer.MainView"), and failing that the file path, keeps names unique,
    /// so per-screen scores and baselines never mix two screens up.
    /// </summary>
    private static string UniqueScreenName(string? xClass, string display, HashSet<string> used)
    {
        var shortName = !string.IsNullOrWhiteSpace(xClass)
            ? xClass[(xClass.LastIndexOf('.') + 1)..]
            : System.IO.Path.GetFileNameWithoutExtension(display);
        foreach (var candidate in new[] { shortName, xClass, display })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && used.Add(candidate)) return candidate;
        }
        // Only reachable if the same file is scanned twice.
        var name = display;
        for (var i = 2; !used.Add(name); i++) name = $"{display} ({i})";
        return name;
    }

    private sealed record DocState(
        ScreenScan Screen,
        string Display,
        GlobalFacts Facts,
        Dictionary<string, int> Usage,
        Dictionary<string, string> LabelTargets,
        Dictionary<string, string> NamedText,
        XElement Root);

    private static void Visit(XElement el, Ctx ctx, DocState st)
    {
        var local = el.Name.LocalName;
        var ns = el.Name.NamespaceName;

        // Property element, e.g. <ListBox.ItemTemplate>
        var dot = local.IndexOf('.');
        if (dot >= 0)
        {
            var prop = local[(dot + 1)..];
            if (prop == "Resources")
            {
                // Keyed DataTemplates in resources are usually row templates.
                foreach (var t in el.Elements().Where(e => e.Name.LocalName is "DataTemplate" or "HierarchicalDataTemplate"))
                    Visit(t, ctx with { Repeated = true, RowsIdentifiable = null, NextTemplateRepeats = true }, st);
                return;
            }
            if (SkippedProperties.Contains(prop)) return;

            var propCtx = ctx with { NextTemplateRepeats = RepeatedTemplateProperties.Contains(prop) };
            foreach (var child in el.Elements()) Visit(child, propCtx, st);
            return;
        }

        if (ns == XamlNs || SkippedTags.Contains(local)) return;

        if (local is "DataTemplate" or "HierarchicalDataTemplate")
        {
            var repeats = ctx.NextTemplateRepeats;
            var rows = repeats ? ctx.HostRows : ctx.RowsIdentifiable;
            var templateRoot = el.Elements().FirstOrDefault(e => !e.Name.LocalName.Contains('.'));
            if (repeats && templateRoot is not null && GetIdInfo(templateRoot).Origin == IdOrigin.DataBound)
                rows = true;
            var tctx = ctx with { Repeated = ctx.Repeated || repeats, RowsIdentifiable = rows, NextTemplateRepeats = true };
            foreach (var child in el.Elements()) Visit(child, tctx, st);
            return;
        }

        // Work out what kind of control this is.
        var isWpf = ns == WpfNs;
        string kind;
        string? effective;
        var visible = true;
        var invisibleControl = false;
        var noPeerLayout = false;
        var customInteractiveControl = false;

        if (isWpf)
        {
            kind = local;
            effective = local;
        }
        else
        {
            var prefix = el.GetPrefixOfNamespace(el.Name.Namespace);
            kind = string.IsNullOrEmpty(prefix) ? local : $"{prefix}:{local}";
            if (st.Facts.CustomControls.TryGetValue(local, out var cc))
            {
                st.Usage[local] = st.Usage.GetValueOrDefault(local) + 1;
                effective = cc.WpfBase;
                invisibleControl = cc.Status == PeerStatus.Invisible;
                noPeerLayout = cc.Status == PeerStatus.NoPeerLayout;
                visible = !invisibleControl && !noPeerLayout;
                // Built on Control/ContentControl/ButtonBase/...: a custom interactive control, with or without a peer.
                customInteractiveControl = cc.WpfBase is not null && ControlCatalog.NoPeerControlBases.Contains(cc.WpfBase);
            }
            else
            {
                effective = GuessThirdPartyType(local);
            }
        }

        var isRoot = el == st.Root;
        var interactive = invisibleControl || customInteractiveControl ||
                          (effective is not null && ControlCatalog.InteractiveTags.Contains(effective));
        var itemsHost = effective is not null && ControlCatalog.ItemsHostTags.Contains(effective);
        var rowItem = effective is not null && ControlCatalog.RowItemTags.Contains(effective);
        var (id, origin) = GetIdInfo(el);
        var xName = el.Attribute(XName.Get("Name", XamlNs))?.Value ?? el.Attribute("Name")?.Value;

        bool? hostRows = ctx.HostRows;
        bool? ownRows = null;
        if (itemsHost)
        {
            ownRows = ComputeRowsIdentifiable(el, effective!, st.Facts);
            hostRows = ownRows;
        }

        var path = ctx.Path.Length >= 4 ? ctx.Path[^3..] : ctx.Path;
        var newPath = path.Append(kind).ToArray();

        var record = isRoot || interactive || itemsHost || rowItem || id is not null || !isWpf && (invisibleControl || noPeerLayout);
        if (record && !(noPeerLayout && id is null && !isRoot))
        {
            var info = new UiElementInfo
            {
                Screen = st.Screen.Name,
                Kind = kind,
                ControlType = invisibleControl ? "Custom" : ControlCatalog.UiaControlType(effective ?? local),
                AutomationId = id,
                IdOrigin = origin,
                XName = xName,
                IsInteractive = interactive && !rowItem && !isRoot,
                IsItemsHost = itemsHost,
                InRepeatedTemplate = ctx.Repeated,
                RowsIdentifiable = itemsHost ? ownRows : ctx.Repeated ? ctx.RowsIdentifiable : null,
                VisibleToAutomation = visible,
                Path = string.Join(" > ", newPath),
                Location = LocationOf(el, st.Display),
            };
            var name = GetName(el, effective, st, xName);
            info.Name = name;
            info.HasName = !string.IsNullOrWhiteSpace(name);
            var toolTip = el.Attribute("ToolTip")?.Value;
            if (!string.IsNullOrWhiteSpace(toolTip) && !toolTip.StartsWith('{')) info.Hint = toolTip.Trim();
            st.Screen.Elements.Add(info);
        }

        var childCtx = ctx with { Path = newPath, HostRows = hostRows, NextTemplateRepeats = true };
        foreach (var child in el.Elements()) Visit(child, childCtx, st);
    }

    private static SourceLocation? LocationOf(XElement el, string display)
    {
        IXmlLineInfo li = el;
        return li.HasLineInfo() ? new SourceLocation(display, li.LineNumber, li.LinePosition) : new SourceLocation(display, 0, 0);
    }

    /// <summary>WPF's own order: AutomationProperties.AutomationId, then x:Uid, then x:Name.</summary>
    internal static (string? Id, IdOrigin Origin) GetIdInfo(XElement el)
    {
        var explicitId = el.Attribute("AutomationProperties.AutomationId")?.Value;
        if (!string.IsNullOrWhiteSpace(explicitId))
        {
            explicitId = explicitId.Trim();
            if (explicitId.StartsWith('{'))
                return (explicitId, explicitId.Contains("Binding", StringComparison.Ordinal) ? IdOrigin.DataBound : IdOrigin.Explicit);
            return (explicitId, IdHeuristics.LooksGenerated(explicitId) ? IdOrigin.GeneratedLooking : IdOrigin.Explicit);
        }

        var uid = el.Attribute(XName.Get("Uid", XamlNs))?.Value;
        if (!string.IsNullOrWhiteSpace(uid))
            return (uid, IdHeuristics.LooksGenerated(uid) ? IdOrigin.GeneratedLooking : IdOrigin.Uid);

        var name = el.Attribute(XName.Get("Name", XamlNs))?.Value ?? el.Attribute("Name")?.Value;
        if (!string.IsNullOrWhiteSpace(name))
            return (name, IdHeuristics.LooksGenerated(name) ? IdOrigin.GeneratedLooking : IdOrigin.XName);

        return (null, IdOrigin.None);
    }

    private static bool? ComputeRowsIdentifiable(XElement host, string hostType, GlobalFacts facts)
    {
        var hasStaticItems = host.Elements().Any(e => !e.Name.LocalName.Contains('.'));
        if (hasStaticItems) return null; // items are written out by hand; tests usually find them by text

        foreach (var prop in host.Elements().Where(e => e.Name.LocalName.EndsWith(".ItemContainerStyle", StringComparison.Ordinal) ||
                                                         e.Name.LocalName.EndsWith(".RowStyle", StringComparison.Ordinal)))
        {
            if (prop.Descendants().Any(IsAutomationIdSetter)) return true;
        }

        foreach (var attrName in new[] { "ItemContainerStyle", "RowStyle" })
        {
            var key = ResourceKey(host.Attribute(attrName)?.Value);
            if (key is not null && facts.KeyedStylesWithId.Contains(key)) return true;
        }

        if (RowContainerType.TryGetValue(hostType, out var rowType) && facts.ImplicitRowStylesWithId.Contains(rowType))
            return true;

        var templateKey = ResourceKey(host.Attribute("ItemTemplate")?.Value);
        if (templateKey is not null && facts.KeyedTemplatesWithBoundRoot.Contains(templateKey)) return true;

        foreach (var prop in host.Elements().Where(e => e.Name.LocalName.EndsWith(".ItemTemplate", StringComparison.Ordinal)))
        {
            var templateRoot = prop.Elements().FirstOrDefault()?.Elements().FirstOrDefault(e => !e.Name.LocalName.Contains('.'));
            if (templateRoot is not null && GetIdInfo(templateRoot).Origin == IdOrigin.DataBound) return true;
        }

        return false;
    }

    private static string? ResourceKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var m = ResourceReference().Match(value.Trim());
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? GetName(XElement el, string? effective, DocState st, string? xName)
    {
        var explicitName = el.Attribute("AutomationProperties.Name")?.Value;
        if (!string.IsNullOrWhiteSpace(explicitName)) return explicitName.Trim();

        var labeledBy = el.Attribute("AutomationProperties.LabeledBy")?.Value;
        if (!string.IsNullOrWhiteSpace(labeledBy))
        {
            var m = ElementReference().Match(labeledBy);
            var labelName = m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;
            return labelName is not null && st.NamedText.TryGetValue(labelName, out var text) ? text : "(from label)";
        }

        if (xName is not null && st.LabelTargets.TryGetValue(xName, out var labelText)) return labelText;

        if (effective is null || NeedsExternalLabel.Contains(effective)) return null;

        if (effective is "Window" or "Page" or "NavigationWindow")
        {
            var title = el.Attribute("Title")?.Value;
            return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        }

        // Only controls that display their content use it as their name (a panel or window does not).
        if (!NamedByContent.Contains(effective)) return null;

        foreach (var attr in new[] { "Content", "Header", "Text" })
        {
            var v = el.Attribute(attr)?.Value;
            if (!string.IsNullOrWhiteSpace(v)) return IdHeuristics.CleanLabel(v);
        }

        var found = FindText(el, 0);
        return found is null ? null : IdHeuristics.CleanLabel(found);
    }

    /// <summary>Controls whose readable name comes from what they display.</summary>
    private static readonly HashSet<string> NamedByContent = new(StringComparer.Ordinal)
    {
        "Button", "RepeatButton", "ToggleButton", "CheckBox", "RadioButton", "Hyperlink", "MenuItem",
        "TabItem", "Expander", "GroupBox", "Label", "TextBlock", "ListBoxItem", "ComboBoxItem",
        "ListViewItem", "TreeViewItem",
    };

    private static string? FindText(XElement el, int depth)
    {
        if (depth > 4) return null;

        var direct = string.Concat(el.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
        if (direct.Length > 0) return direct;

        foreach (var child in el.Elements())
        {
            var local = child.Name.LocalName;
            var dot = local.IndexOf('.');
            if (dot >= 0)
            {
                var prop = local[(dot + 1)..];
                if (prop is "Content" or "Header" or "Inlines")
                {
                    var t = FindText(child, depth + 1);
                    if (t is not null) return t;
                }
                continue;
            }
            if (local is "Image" or "Path" or "Rectangle" or "Ellipse") continue;

            foreach (var attr in new[] { "Text", "Content" })
            {
                var v = child.Attribute(attr)?.Value;
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
            var nested = FindText(child, depth + 1);
            if (nested is not null) return nested;
        }
        return null;
    }

    /// <summary>
    /// Best guess for third-party controls (Telerik, DevExpress, Syncfusion, ...) from their class name,
    /// e.g. "RadComboBox" → ComboBox, "GridControl" → DataGrid.
    /// </summary>
    internal static string? GuessThirdPartyType(string className)
    {
        if (className.EndsWith("DataGrid", StringComparison.Ordinal) ||
            className.EndsWith("GridView", StringComparison.Ordinal) ||
            className.EndsWith("GridControl", StringComparison.Ordinal))
            return "DataGrid";

        foreach (var tag in ControlCatalog.InteractiveTags.OrderByDescending(t => t.Length))
        {
            if (className.EndsWith(tag, StringComparison.Ordinal)) return tag;
        }
        return null;
    }
}
