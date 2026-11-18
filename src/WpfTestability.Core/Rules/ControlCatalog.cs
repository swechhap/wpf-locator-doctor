namespace WpfTestability.Core.Rules;

/// <summary>
/// What we know about built-in WPF element types: how UI Automation reports them,
/// whether a tester interacts with them, and whether they come with an automation peer.
/// </summary>
public static class ControlCatalog
{
    /// <summary>WPF XAML tag → UI Automation control type.</summary>
    private static readonly Dictionary<string, string> UiaTypes = new(StringComparer.Ordinal)
    {
        ["Button"] = "Button", ["RepeatButton"] = "Button", ["ToggleButton"] = "Button",
        ["CheckBox"] = "CheckBox", ["RadioButton"] = "RadioButton",
        ["TextBox"] = "Edit", ["PasswordBox"] = "Edit", ["RichTextBox"] = "Document",
        ["ComboBox"] = "ComboBox", ["ListBox"] = "List", ["ListView"] = "List",
        ["DataGrid"] = "DataGrid", ["TreeView"] = "Tree", ["ItemsControl"] = "List",
        ["TabControl"] = "Tab", ["TabItem"] = "TabItem",
        ["Menu"] = "Menu", ["ContextMenu"] = "Menu", ["MenuItem"] = "MenuItem",
        ["Slider"] = "Slider", ["Hyperlink"] = "Hyperlink",
        ["DatePicker"] = "Custom", ["Calendar"] = "Calendar",
        ["Expander"] = "Group", ["GroupBox"] = "Group",
        ["ProgressBar"] = "ProgressBar", ["ScrollBar"] = "ScrollBar", ["ScrollViewer"] = "Pane",
        ["Label"] = "Text", ["TextBlock"] = "Text", ["Image"] = "Image",
        ["ListBoxItem"] = "ListItem", ["ComboBoxItem"] = "ListItem", ["ListViewItem"] = "ListItem",
        ["TreeViewItem"] = "TreeItem",
        ["StatusBar"] = "StatusBar", ["ToolBar"] = "ToolBar",
        ["Window"] = "Window", ["UserControl"] = "Custom", ["Page"] = "Pane", ["Frame"] = "Pane",
        ["Separator"] = "Separator", ["ToolTip"] = "ToolTip", ["Thumb"] = "Thumb", ["GridSplitter"] = "Thumb",
    };

    /// <summary>Things a tester clicks, types into or selects. Each one should have an AutomationId.</summary>
    public static readonly HashSet<string> InteractiveTags = new(StringComparer.Ordinal)
    {
        "Button", "RepeatButton", "ToggleButton", "CheckBox", "RadioButton",
        "TextBox", "PasswordBox", "RichTextBox", "ComboBox",
        "ListBox", "ListView", "DataGrid", "TreeView", "TabControl", "TabItem",
        "MenuItem", "Slider", "Hyperlink", "DatePicker", "Calendar", "Expander",
    };

    /// <summary>Controls that show rows of data.</summary>
    public static readonly HashSet<string> ItemsHostTags = new(StringComparer.Ordinal)
    {
        "ListBox", "ListView", "DataGrid", "TreeView", "ComboBox", "ItemsControl",
    };

    /// <summary>Row containers. Usually found by their text, so they don't count toward ID coverage.</summary>
    public static readonly HashSet<string> RowItemTags = new(StringComparer.Ordinal)
    {
        "ListBoxItem", "ComboBoxItem", "ListViewItem", "TreeViewItem",
    };

    /// <summary>Tags that start a new screen when they are the root of a XAML file.</summary>
    public static readonly HashSet<string> ScreenRootTags = new(StringComparer.Ordinal)
    {
        "Window", "UserControl", "Page", "NavigationWindow",
    };

    /// <summary>
    /// WPF control base classes that do NOT create an automation peer.
    /// A custom control deriving from one of these (without overriding OnCreateAutomationPeer)
    /// is invisible to UI Automation, so any AutomationId set on it goes nowhere.
    /// Verified against the dotnet/wpf source code.
    /// </summary>
    public static readonly HashSet<string> NoPeerControlBases = new(StringComparer.Ordinal)
    {
        "Control", "ContentControl", "ButtonBase", "RangeBase", "HeaderedContentControl", "TextBoxBase",
    };

    /// <summary>Layout/base classes with no automation peer. Their children still show up; the element itself doesn't.</summary>
    public static readonly HashSet<string> NoPeerLayoutBases = new(StringComparer.Ordinal)
    {
        "FrameworkElement", "UIElement", "Panel", "Decorator", "Border", "Canvas", "Grid",
        "StackPanel", "DockPanel", "WrapPanel", "UniformGrid", "VirtualizingPanel",
        "VirtualizingStackPanel", "Shape", "Popup", "Adorner",
    };

    /// <summary>Base classes known to come with a working automation peer.</summary>
    public static readonly HashSet<string> PeerBases = new(StringComparer.Ordinal)
    {
        "Button", "RepeatButton", "ToggleButton", "CheckBox", "RadioButton",
        "TextBox", "PasswordBox", "RichTextBox", "ComboBox", "ListBox", "ListView", "DataGrid",
        "TreeView", "TabControl", "TabItem", "Menu", "ContextMenu", "MenuItem", "MenuBase", "Slider",
        "DatePicker", "Calendar", "Expander", "GroupBox", "ProgressBar", "ScrollBar", "ScrollViewer",
        "Label", "TextBlock", "Image", "ListBoxItem", "ComboBoxItem", "ListViewItem", "TreeViewItem",
        "StatusBar", "ToolBar", "Window", "NavigationWindow", "UserControl", "Page", "Frame",
        "ItemsControl", "Selector", "MultiSelector", "HeaderedItemsControl", "Thumb", "ToolTip", "Separator",
        "DocumentViewer", "GridSplitter", "MediaElement", "InkCanvas",
    };

    public static string UiaControlType(string wpfTag) =>
        UiaTypes.TryGetValue(wpfTag, out var t) ? t : "Custom";

    public static bool IsKnownWpfType(string name) =>
        UiaTypes.ContainsKey(name) || PeerBases.Contains(name) ||
        NoPeerControlBases.Contains(name) || NoPeerLayoutBases.Contains(name);

    // ---- Live scan (UI Automation control type names) ----

    /// <summary>UIA control types that count as interactive in a live scan.</summary>
    public static readonly HashSet<string> LiveInteractiveTypes = new(StringComparer.Ordinal)
    {
        "Button", "Edit", "CheckBox", "RadioButton", "ComboBox", "List", "DataGrid", "Tree",
        "Tab", "TabItem", "MenuItem", "Slider", "Hyperlink", "Spinner", "SplitButton", "Document", "Calendar",
    };

    /// <summary>UIA row types. Rows are judged per list ("can rows be told apart?"), not per row.</summary>
    public static readonly HashSet<string> LiveRowTypes = new(StringComparer.Ordinal)
    {
        "ListItem", "DataItem", "TreeItem",
    };

    /// <summary>UIA list-like types whose children are rows.</summary>
    public static readonly HashSet<string> LiveItemsHostTypes = new(StringComparer.Ordinal)
    {
        "List", "DataGrid", "Tree", "ComboBox", "Table",
    };

    /// <summary>
    /// Self-contained controls. Anything inside them is an internal part of the control
    /// (e.g. a slider's thumb, a date picker's button). Tests address the outer control, so parts are not scored.
    /// </summary>
    public static readonly HashSet<string> LiveLeafTypes = new(StringComparer.Ordinal)
    {
        "Button", "Edit", "CheckBox", "RadioButton", "ComboBox", "Slider", "Spinner", "Hyperlink",
        "SplitButton", "Calendar", "ScrollBar", "ProgressBar", "Document",
    };

    /// <summary>Window furniture we never score.</summary>
    public static readonly HashSet<string> LiveSkippedTypes = new(StringComparer.Ordinal)
    {
        "TitleBar", "ScrollBar", "Thumb",
    };
}
