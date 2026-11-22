using WpfLocatorDoctor.Core.Assistant;
using WpfLocatorDoctor.Core.Source;

namespace WpfLocatorDoctor.Core.Tests;

/// <summary>Which AutomationId each fix suggests, and the exact line edits that apply it.</summary>
public class XamlFixPlannerTests
{
    private static string[] Lines(string text) => text.Replace("\r", "", StringComparison.Ordinal).Split('\n');

    // ------------------------------------------------------------------ line edits

    [Fact]
    public void Adds_the_attribute_right_after_the_tag_name()
    {
        var edit = XamlFixPlanner.BuildEdit(["    <Button Content=\"Save\" />"], line: 1, column: 6, "SaveButton");
        Assert.NotNull(edit);
        Assert.Equal(1, edit!.Line);
        Assert.Equal("    <Button Content=\"Save\" />", edit.Before);
        Assert.Equal("    <Button AutomationProperties.AutomationId=\"SaveButton\" Content=\"Save\" />", edit.After);
    }

    [Fact]
    public void Adds_the_attribute_to_a_tag_with_no_attributes()
    {
        Assert.Equal("<Button AutomationProperties.AutomationId=\"OkButton\"/>", XamlFixPlanner.BuildEdit(["<Button/>"], 1, 2, "OkButton")!.After);
        Assert.Equal("<Button AutomationProperties.AutomationId=\"OkButton\">OK</Button>", XamlFixPlanner.BuildEdit(["<Button>OK</Button>"], 1, 2, "OkButton")!.After);
    }

    [Fact]
    public void Replaces_an_existing_value_on_the_same_line()
    {
        var edit = XamlFixPlanner.BuildEdit(["<Button AutomationProperties.AutomationId=\"Button_1\" Content=\"Clear\" />"], 1, 2, "ClearButton");
        Assert.Equal("<Button AutomationProperties.AutomationId=\"ClearButton\" Content=\"Clear\" />", edit!.After);
    }

    [Fact]
    public void Replaces_an_existing_value_on_a_later_line_of_the_tag()
    {
        string[] lines =
        [
            "<Button Content=\"Clear\"",
            "        AutomationProperties.AutomationId = 'Button_1'",
            "        Margin=\"4\" />",
        ];
        var edit = XamlFixPlanner.BuildEdit(lines, 1, 2, "ClearButton");
        Assert.Equal(2, edit!.Line);
        Assert.Equal("        AutomationProperties.AutomationId = 'ClearButton'", edit.After);
    }

    [Fact]
    public void Does_not_touch_an_id_that_belongs_to_a_child_element()
    {
        string[] lines =
        [
            "<StackPanel>",
            "  <Button AutomationProperties.AutomationId=\"Inner\" />",
            "</StackPanel>",
        ];
        var edit = XamlFixPlanner.BuildEdit(lines, 1, 2, "Panel");
        Assert.Equal(1, edit!.Line);
        Assert.Equal("<StackPanel AutomationProperties.AutomationId=\"Panel\">", edit.After);
    }

    [Fact]
    public void Ignores_the_attribute_name_when_it_is_only_text_inside_another_value()
    {
        var edit = XamlFixPlanner.BuildEdit(["<TextBlock Text=\"Set AutomationProperties.AutomationId=x > here\" />"], 1, 2, "HelpText");
        Assert.Equal("<TextBlock AutomationProperties.AutomationId=\"HelpText\" Text=\"Set AutomationProperties.AutomationId=x > here\" />", edit!.After);
    }

    [Fact]
    public void Escapes_characters_that_are_special_in_xml()
    {
        var edit = XamlFixPlanner.BuildEdit(["<Button />"], 1, 2, "A\"B&C");
        Assert.Equal("<Button AutomationProperties.AutomationId=\"A&quot;B&amp;C\" />", edit!.After);
    }

    [Theory]
    [InlineData(0, 2)]   // no such line
    [InlineData(2, 2)]   // past the end
    [InlineData(1, 1)]   // column points at '<' rather than the tag name
    [InlineData(1, 5)]   // column in the middle of the tag name
    public void Gives_no_edit_when_the_position_does_not_point_at_a_tag(int line, int column)
    {
        Assert.Null(XamlFixPlanner.BuildEdit(["<Button />"], line, column, "X"));
    }

    [Fact]
    public void Positions_reported_by_the_scanner_produce_valid_edits()
    {
        const string xaml = """
            <Window x:Class="Demo.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <StackPanel>
            	<Button Content="Save" />
                <TextBox
                    Width="100" />
              </StackPanel>
            </Window>
            """;
        var screen = new XamlSourceScanner().ScanText(xaml, "MainWindow.xaml").Screens.Single();
        var fixes = XamlFixPlanner.PlanScreen(screen, _ => Lines(xaml));

        var save = fixes.Single(f => f.SuggestedId == "SaveButton");
        Assert.Equal("\t<Button AutomationProperties.AutomationId=\"SaveButton\" Content=\"Save\" />", save.Edit!.After);
        var input = fixes.Single(f => f.Element.StartsWith("TextBox", StringComparison.Ordinal));
        Assert.Equal("    <TextBox AutomationProperties.AutomationId=\"" + input.SuggestedId + "\"", input.Edit!.After);
    }

    // ------------------------------------------------------------------ which fix, which ID

    private static List<XamlFix> Plan(string body)
    {
        var xaml = $"""
            <Window x:Class="Demo.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <StackPanel>
            {body}
              </StackPanel>
            </Window>
            """;
        var screen = new XamlSourceScanner().ScanText(xaml, "MainWindow.xaml").Screens.Single();
        return XamlFixPlanner.PlanScreen(screen, _ => Lines(xaml));
    }

    [Fact]
    public void Suggested_ids_never_clash_with_each_other_or_with_existing_ids()
    {
        var fixes = Plan("""
                <Button Content="Save" AutomationProperties.AutomationId="SaveButton" />
                <Button Content="Save" />
                <Button Content="Save" />
            """);
        Assert.Equal(["SaveButton2", "SaveButton3"], fixes.Select(f => f.SuggestedId!).ToArray());
        Assert.All(fixes, f => Assert.Equal("add-id", f.Kind));
    }

    [Fact]
    public void A_duplicated_id_stays_on_the_first_element_and_the_others_get_new_ones()
    {
        var fixes = Plan("""
                <Button Content="Delete" AutomationProperties.AutomationId="ActionButton" />
                <Button Content="Export" AutomationProperties.AutomationId="ActionButton" />
            """);
        var fix = Assert.Single(fixes);
        Assert.Equal("fix-duplicate-id", fix.Kind);
        Assert.Equal("WT004", fix.Rule);
        Assert.Equal("ActionButton", fix.CurrentId);
        Assert.Equal("ExportButton", fix.SuggestedId);
        Assert.Equal("    <Button Content=\"Export\" AutomationProperties.AutomationId=\"ExportButton\" />", fix.Edit!.After);
    }

    [Fact]
    public void Ids_borrowed_from_x_name_or_x_uid_are_pinned_with_the_same_value()
    {
        var fixes = Plan("""
                <TextBox x:Name="SearchBox" />
                <Button x:Uid="SaveCommand" Content="Save" />
            """);
        Assert.Equal(2, fixes.Count);
        Assert.All(fixes, f => Assert.Equal("pin-id", f.Kind));
        Assert.Equal(["WT002", "WT009"], fixes.Select(f => f.Rule).ToArray());
        Assert.Equal(["SearchBox", "SaveCommand"], fixes.Select(f => f.SuggestedId!).ToArray());
        Assert.Equal(fixes.Select(f => f.CurrentId), fixes.Select(f => f.SuggestedId));
    }

    [Fact]
    public void A_generated_looking_id_is_replaced_with_a_meaningful_one()
    {
        var fix = Assert.Single(Plan("""    <Button x:Uid="Button_7" Content="Clear" />"""));
        Assert.Equal("replace-generated-id", fix.Kind);
        Assert.Equal("ClearButton", fix.SuggestedId);
        Assert.Contains("update any test that used \"Button_7\"", fix.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void Lists_whose_rows_cannot_be_told_apart_get_a_row_style_fix()
    {
        var fixes = Plan("""
                <ListBox AutomationProperties.AutomationId="OrdersList" ItemsSource="{Binding Orders}">
                  <ListBox.ItemTemplate>
                    <DataTemplate><TextBlock Text="{Binding Name}" /></DataTemplate>
                  </ListBox.ItemTemplate>
                </ListBox>
            """);
        var fix = Assert.Single(fixes);
        Assert.Equal("row-ids", fix.Kind);
        Assert.Contains("ListBox.ItemContainerStyle", fix.Instruction, StringComparison.Ordinal);
        Assert.Null(fix.Edit);
    }

    [Fact]
    public void Controls_with_good_ids_need_no_fix()
    {
        Assert.Empty(Plan("""    <Button Content="OK" AutomationProperties.AutomationId="OkButton" />"""));
    }

    [Fact]
    public void No_edit_is_offered_when_the_file_cannot_be_read()
    {
        const string xaml = """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Button Content="Go" /></Window>
            """;
        var screen = new XamlSourceScanner().ScanText(xaml).Screens.Single();
        var fix = Assert.Single(XamlFixPlanner.PlanScreen(screen, _ => null));
        Assert.Equal("GoButton", fix.SuggestedId);
        Assert.Null(fix.Edit);
    }
}
