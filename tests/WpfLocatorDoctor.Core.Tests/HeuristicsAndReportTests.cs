using WpfLocatorDoctor.Core.Model;
using WpfLocatorDoctor.Core.Reporting;
using WpfLocatorDoctor.Core.Rules;
using WpfLocatorDoctor.Core.Scoring;
using WpfLocatorDoctor.Core.Source;

namespace WpfLocatorDoctor.Core.Tests;

public class HeuristicsAndReportTests
{
    [Theory]
    [InlineData("Button_12", true)]
    [InlineData("TextBlock_3", true)]
    [InlineData("1001", true)]
    [InlineData("{3f2504e0-4f89-11d3-9a0c-0305e82c3301}", true)]
    [InlineData("a1b2c3d4e5f60718", true)]
    [InlineData("SaveButton", false)]
    [InlineData("Order_Total", false)]
    [InlineData("", false)]
    public void Recognises_machine_generated_ids(string id, bool expected) =>
        Assert.Equal(expected, IdHeuristics.LooksGenerated(id));

    [Theory]
    [InlineData("E_xit", "Exit")]
    [InlineData("_Search:", "Search")]
    [InlineData("Save__As", "Save_As")]
    [InlineData("{Binding Title}", "{Binding Title}")]
    public void Cleans_access_keys_and_colons_from_labels(string input, string expected) =>
        Assert.Equal(expected, IdHeuristics.CleanLabel(input));

    [Theory]
    [InlineData("Button", "Save & close…", null, null, "SaveCloseButton")]
    [InlineData("TextBox", "City", null, null, "CityInput")]
    [InlineData("Button", null, "refreshBtn", null, "RefreshBtnButton")]
    [InlineData("Button", null, null, "Refresh", "RefreshButton")]
    [InlineData("ComboBox", null, null, null, "OrdersComboBox")]
    [InlineData("MenuItem", "E_xit", null, null, "ExitMenuItem")]
    [InlineData("Button", "OK Button", null, null, "OKButton")]
    public void Suggests_readable_ids(string kind, string? name, string? xName, string? hint, string expected)
    {
        var e = new UiElementInfo
        {
            Screen = "Orders", Kind = kind, ControlType = "Button",
            Name = name, HasName = name is not null, XName = xName, Hint = hint,
        };
        Assert.Equal(expected, IdHeuristics.SuggestId(e));
    }

    [Fact]
    public void Json_report_round_trips()
    {
        var scan = new XamlSourceScanner().ScanText("""
            <Window x:Class="Demo.Main" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Button Content="Go"/>
            </Window>
            """, "Main.xaml");
        var card = Scorer.Evaluate(scan);

        var json = JsonReport.Serialize(card);
        var back = JsonReport.Deserialize(json);

        Assert.Contains("\"mode\": \"source\"", json);
        Assert.Equal(card.Overall.Score, back.Overall.Score);
        Assert.Equal(card.Findings.Count, back.Findings.Count);
        Assert.Equal(card.Findings[0].Location, back.Findings[0].Location);
        Assert.Equal(card.Screens[0].Name, back.Screens[0].Name);
    }

    [Fact]
    public void Markdown_report_explains_grade_checks_and_fixes()
    {
        var scan = new XamlSourceScanner().ScanText("""
            <Window x:Class="Demo.Main" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <StackPanel>
                <Button Content="Go | Run"/>
                <Button Content="Stop" AutomationProperties.AutomationId="StopButton"/>
              </StackPanel>
            </Window>
            """, "Main.xaml");
        var md = MarkdownReport.Render(Scorer.Evaluate(scan));

        Assert.Contains("# WPF testability report", md);
        Assert.Contains("**Overall grade:", md);
        Assert.Contains("| ID coverage |", md);
        Assert.Contains("`AutomationProperties.AutomationId=\"GoRunButton\"`", md);
        Assert.Contains("`Main.xaml:3`", md);
    }

    [Fact]
    public void Text_summary_lists_worst_screens_first()
    {
        var card = new Scorecard
        {
            Screens =
            [
                new ScreenScore { Name = "Good", Summary = new ScoreSummary { Score = 95, Grade = "A" } },
                new ScreenScore { Name = "Bad", Summary = new ScoreSummary { Score = 30, Grade = "F" } },
            ],
        };
        var text = TextSummary.Render(card);
        Assert.True(text.IndexOf("Bad", StringComparison.Ordinal) < text.IndexOf("Good", StringComparison.Ordinal));
    }
}
