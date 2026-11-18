using WpfLocatorDoctor.Core.Live;
using WpfLocatorDoctor.Core.Model;
using WpfLocatorDoctor.Core.Scoring;

namespace WpfLocatorDoctor.Core.Tests;

/// <summary>
/// The live scan's decisions, tested with a fake UI Automation tree (no Windows or running app needed).
/// </summary>
public class LiveTreeWalkerTests
{
    private sealed class Node(string controlType, string? id = null, string? name = null) : ILiveNode
    {
        public string ControlType { get; } = controlType;
        public string? AutomationId { get; } = id;
        public string? Name { get; } = name;
        public string? ClassName { get; init; }
        public bool IsKeyboardFocusable { get; init; }
        public bool IsOffscreen { get; init; }
        public double Width { get; init; } = 100;
        public double Height { get; init; } = 30;
        public IReadOnlyList<string> Patterns { get; init; } = [];
        public IReadOnlyList<ILiveNode> Children { get; init; } = [];
    }

    private static Node Window(params ILiveNode[] children) =>
        new("Window", "MainWindow", "Main") { ClassName = "Window", Children = children };

    private static ScreenScan Walk(ILiveNode window, LiveWalkLimits? limits = null) =>
        LiveTreeWalker.WalkWindow(window, limits ?? new LiveWalkLimits(), new HashSet<string>(StringComparer.Ordinal));

    private static UiElementInfo Find(ScreenScan s, string kindOrId) =>
        Assert.Single(s.Elements, e => e.AutomationId == kindOrId || e.Kind == kindOrId);

    [Fact]
    public void Interactive_controls_are_recognised_and_the_window_itself_is_not_interactive()
    {
        var screen = Walk(Window(
            new Node("Button", "SaveButton", "Save") { Patterns = ["Invoke"] },
            new Node("Edit", "SearchBox"),
            new Node("Text", null, "Hello")));

        Assert.Equal("MainWindow", screen.Name);
        Assert.False(Find(screen, "MainWindow").IsInteractive);
        Assert.True(Find(screen, "SaveButton").IsInteractive);
        Assert.True(Find(screen, "SearchBox").IsInteractive);
        Assert.False(Find(screen, "Text").IsInteractive);
        Assert.Equal(["Invoke"], Find(screen, "SaveButton").Patterns);
    }

    [Fact]
    public void Ids_from_a_running_app_have_unknown_origin_unless_they_look_generated()
    {
        var screen = Walk(Window(new Node("Button", "SaveButton"), new Node("Button", "Button_3"), new Node("Button")));

        Assert.Equal(IdOrigin.Unknown, Find(screen, "SaveButton").IdOrigin);
        Assert.Equal(IdOrigin.GeneratedLooking, Find(screen, "Button_3").IdOrigin);
        Assert.Contains(screen.Elements, e => e.AutomationId is null && e.IdOrigin == IdOrigin.None && e.Kind == "Button");
    }

    [Fact]
    public void Custom_elements_count_as_interactive_only_when_focusable_and_actionable()
    {
        var screen = Walk(Window(
            new Node("Custom", "Rating") { IsKeyboardFocusable = true, Patterns = ["RangeValue"] },
            new Node("Custom", "Decoration") { IsKeyboardFocusable = true },
            new Node("Custom", "NotFocusable") { Patterns = ["Invoke"] },
            new Node("Pane", "Picker") { ClassName = "DatePicker" }));

        Assert.True(Find(screen, "Rating").IsInteractive);
        Assert.False(Find(screen, "Decoration").IsInteractive);
        Assert.False(Find(screen, "NotFocusable").IsInteractive);
        Assert.True(Find(screen, "Picker").IsInteractive);
    }

    [Fact]
    public void Lists_with_unique_row_ids_have_identifiable_rows()
    {
        var screen = Walk(Window(
            new Node("List", "Good") { Children = [new Node("ListItem", "Order1"), new Node("ListItem", "Order2")] },
            new Node("List", "Missing") { Children = [new Node("ListItem", "Order1"), new Node("ListItem")] },
            new Node("List", "Repeated") { Children = [new Node("ListItem", "Row"), new Node("ListItem", "Row")] },
            new Node("List", "Empty")));

        Assert.True(Find(screen, "Good").RowsIdentifiable);
        Assert.False(Find(screen, "Missing").RowsIdentifiable);
        Assert.False(Find(screen, "Repeated").RowsIdentifiable);
        Assert.Null(Find(screen, "Empty").RowsIdentifiable);
    }

    [Fact]
    public void Controls_inside_rows_are_marked_repeated_and_rows_are_not_interactive()
    {
        var screen = Walk(Window(
            new Node("DataGrid", "Orders")
            {
                Children =
                [
                    new Node("Header", "Header") { Children = [new Node("HeaderItem", "IdColumn")] },
                    new Node("DataItem", "Order1") { Children = [new Node("Button", "DeleteButton")] },
                ],
            }));

        var row = Find(screen, "Order1");
        Assert.False(row.IsInteractive);
        Assert.True(row.InRepeatedTemplate); // a row is itself one of the repeated items

        var delete = Find(screen, "DeleteButton");
        Assert.True(delete.InRepeatedTemplate);
        Assert.True(delete.RowsIdentifiable);

        Assert.False(Find(screen, "IdColumn").InRepeatedTemplate); // headers are not data rows
    }

    [Fact]
    public void Parts_inside_self_contained_controls_and_window_furniture_are_skipped()
    {
        var screen = Walk(Window(
            new Node("Slider", "Volume") { Children = [new Node("Thumb", "Thumb"), new Node("Button", "PART_IncreaseButton")] },
            new Node("TitleBar", "TitleBar") { Children = [new Node("Button", "Close")] },
            new Node("MenuBar", "SystemMenuBar") { Children = [new Node("MenuItem", "System")] }));

        Assert.Equal(["MainWindow", "Volume"], screen.Elements.Select(e => e.AutomationId ?? "").ToArray());
    }

    [Fact]
    public void Empty_custom_areas_are_opaque_but_small_offscreen_or_labelled_ones_are_not()
    {
        var screen = Walk(Window(
            new Node("Custom") { ClassName = "Chart", Width = 300, Height = 200 },
            new Node("Custom") { ClassName = "Tiny", Width = 10, Height = 10 },
            new Node("Custom") { ClassName = "Hidden", IsOffscreen = true },
            new Node("Pane", null, "Has a name") { ClassName = "Named" },
            new Node("Pane") { ClassName = "HasChildren", Children = [new Node("Text", null, "x")] }));

        Assert.True(Find(screen, "Chart").IsOpaque);
        Assert.False(Find(screen, "Tiny").IsOpaque);
        Assert.False(Find(screen, "Hidden").IsOpaque);
        Assert.False(Find(screen, "Named").IsOpaque);
        Assert.False(Find(screen, "HasChildren").IsOpaque);
    }

    [Fact]
    public void Stops_at_the_element_limit_and_says_so()
    {
        var buttons = Enumerable.Range(1, 20).Select(i => (ILiveNode)new Node("Button", $"B{i}")).ToArray();
        var screen = Walk(Window(buttons), new LiveWalkLimits(MaxElementsPerWindow: 5));

        Assert.Equal(5, screen.Elements.Count);
        Assert.Contains(screen.Notes, n => n.Contains("Stopped after 5 elements", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_the_first_rows_of_long_lists_are_checked()
    {
        var rows = Enumerable.Range(1, 10).Select(i => (ILiveNode)new Node("ListItem", i <= 3 ? $"Row{i}" : null)).ToArray();
        var screen = Walk(Window(new Node("List", "Long") { Children = rows }), new LiveWalkLimits(MaxRowsPerList: 3));

        Assert.True(Find(screen, "Long").RowsIdentifiable); // rows 4-10 (no IDs) were not looked at
        Assert.Equal(3, screen.Elements.Count(e => e.ControlType == "ListItem"));
        Assert.Contains(screen.Notes, n => n.Contains("1 list(s) had more than 3 rows", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("MainWindow", "Window", "Title", "MainWindow")]
    [InlineData(null, "SettingsDialog", "Title", "SettingsDialog")]
    [InlineData(null, "Window", "Orders", "Orders")]
    [InlineData(null, null, null, "Window")]
    public void Screens_are_named_from_id_then_class_then_title(string? id, string? cls, string? title, string expected)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        Assert.Equal(expected, LiveTreeWalker.UniqueScreenName(new Node("Window", id, title) { ClassName = cls }, used));
    }

    [Fact]
    public void Windows_with_the_same_name_get_numbered()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        Assert.Equal("Dialog", LiveTreeWalker.UniqueScreenName(new Node("Window", "Dialog"), used));
        Assert.Equal("Dialog (2)", LiveTreeWalker.UniqueScreenName(new Node("Window", "Dialog"), used));
        Assert.Equal("Dialog (3)", LiveTreeWalker.UniqueScreenName(new Node("Window", "Dialog"), used));
    }

    [Fact]
    public void A_walked_window_can_be_scored_end_to_end()
    {
        var screen = Walk(Window(
            new Node("Button", "SaveButton", "Save") { Patterns = ["Invoke"] },
            new Node("Button", null, "Cancel") { Patterns = ["Invoke"] }));
        var scan = new ScanResult { Mode = ScanMode.Live, Target = "app.exe" };
        scan.Screens.Add(screen);

        var card = Scorer.Evaluate(scan);

        Assert.Equal(50, card.Overall.Checks.Single(c => c.Id == "coverage").Percent);
        var missing = Assert.Single(card.Findings, f => f.RuleId == "WT001");
        Assert.Contains("CancelButton", missing.Suggestion, StringComparison.Ordinal);
    }
}
