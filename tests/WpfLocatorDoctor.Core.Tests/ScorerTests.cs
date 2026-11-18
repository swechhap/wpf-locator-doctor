using WpfLocatorDoctor.Core.Model;
using WpfLocatorDoctor.Core.Reporting;
using WpfLocatorDoctor.Core.Scoring;
using WpfLocatorDoctor.Core.Source;

namespace WpfLocatorDoctor.Core.Tests;

public class ScorerTests
{
    private static ScanResult Scan(string body, string? csharp = null) =>
        new XamlSourceScanner().ScanText($"""
            <Window x:Class="Demo.Screen"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="clr-namespace:Demo">
            {body}
            </Window>
            """, "Screen.xaml", csharp);

    private static double? Check(Scorecard card, string id) => card.Overall.Checks.Single(c => c.Id == id).Percent;

    [Fact]
    public void A_well_built_screen_scores_100_and_grade_A()
    {
        var card = Scorer.Evaluate(Scan("""
            <StackPanel>
              <Button Content="Save" AutomationProperties.AutomationId="SaveButton"/>
              <CheckBox Content="Remember me" AutomationProperties.AutomationId="RememberCheckBox"/>
            </StackPanel>
            """));

        Assert.Equal(100, card.Overall.Score);
        Assert.Equal("A", card.Overall.Grade);
        Assert.Empty(card.Findings);
    }

    [Fact]
    public void A_screen_with_nothing_to_measure_has_no_score()
    {
        var card = Scorer.Evaluate(Scan("<Grid/>"));
        Assert.Null(card.Overall.Score);
        Assert.Equal("N/A", card.Overall.Grade);
    }

    [Fact]
    public void Missing_ids_lower_coverage_and_get_a_suggested_fix()
    {
        var card = Scorer.Evaluate(Scan("""
            <StackPanel>
              <Button Content="Save" AutomationProperties.AutomationId="SaveButton"/>
              <Button Content="Save &amp; close"/>
            </StackPanel>
            """));

        Assert.Equal(50, Check(card, "coverage"));
        var f = Assert.Single(card.Findings, x => x.RuleId == "WT001");
        Assert.Equal(Severity.High, f.Severity);
        Assert.Contains("AutomationProperties.AutomationId=\"SaveCloseButton\"", f.Suggestion);
        Assert.Equal(7, f.Location!.Line);
    }

    [Fact]
    public void Duplicate_ids_are_reported_once_per_id()
    {
        var card = Scorer.Evaluate(Scan("""
            <StackPanel>
              <Button Content="A" AutomationProperties.AutomationId="Go"/>
              <Button Content="B" AutomationProperties.AutomationId="Go"/>
              <Button Content="C" AutomationProperties.AutomationId="Go"/>
              <Button Content="D" AutomationProperties.AutomationId="Unique"/>
            </StackPanel>
            """));

        var f = Assert.Single(card.Findings, x => x.RuleId == "WT004");
        Assert.Contains("3 times", f.Message);
        Assert.Equal(25, Check(card, "uniqueness"));
    }

    [Fact]
    public void Repeated_ids_are_fine_when_rows_are_identifiable()
    {
        var withRowIds = Scorer.Evaluate(Scan("""
            <ListBox ItemsSource="{Binding Orders}" AutomationProperties.AutomationId="Orders" AutomationProperties.Name="Orders">
              <ListBox.ItemContainerStyle>
                <Style TargetType="ListBoxItem"><Setter Property="AutomationProperties.AutomationId" Value="{Binding Id}"/></Style>
              </ListBox.ItemContainerStyle>
              <ListBox.ItemTemplate>
                <DataTemplate><Button Content="Remove" AutomationProperties.AutomationId="RemoveButton"/></DataTemplate>
              </ListBox.ItemTemplate>
            </ListBox>
            """));
        Assert.DoesNotContain(withRowIds.Findings, f => f.RuleId is "WT004" or "WT005");
        Assert.Equal(100, Check(withRowIds, "uniqueness"));

        var withoutRowIds = Scorer.Evaluate(Scan("""
            <ListBox ItemsSource="{Binding Orders}" AutomationProperties.AutomationId="Orders" AutomationProperties.Name="Orders">
              <ListBox.ItemTemplate>
                <DataTemplate><Button Content="Remove" AutomationProperties.AutomationId="RemoveButton"/></DataTemplate>
              </ListBox.ItemTemplate>
            </ListBox>
            """));
        var rows = Assert.Single(withoutRowIds.Findings, f => f.RuleId == "WT005");
        Assert.Contains("ListBox.ItemContainerStyle", rows.Suggestion);
        Assert.True(Check(withoutRowIds, "uniqueness") < 100);
    }

    [Fact]
    public void Borrowed_and_generated_ids_lower_quality()
    {
        var card = Scorer.Evaluate(Scan("""
            <StackPanel>
              <Button Content="One" AutomationProperties.AutomationId="OneButton"/>
              <Button Content="Two" x:Name="TwoButton"/>
              <Button Content="Three" x:Uid="Button_3"/>
            </StackPanel>
            """));

        Assert.Equal(100, Check(card, "coverage"));
        Assert.Equal(Math.Round(100 * (1.0 + 0.7 + 0.1) / 3, 1), Check(card, "quality"));
        Assert.Contains(card.Findings, f => f.RuleId == "WT002" && f.Severity == Severity.Low);
        Assert.Contains(card.Findings, f => f.RuleId == "WT003" && f.Suggestion!.Contains("ThreeButton"));
    }

    [Fact]
    public void Invisible_custom_controls_are_reported_against_the_class()
    {
        var card = Scorer.Evaluate(Scan(
            """<local:Rating AutomationProperties.AutomationId="Stars"/>""",
            "namespace Demo; public class Rating : System.Windows.Controls.Control { }"));

        var f = Assert.Single(card.Findings, x => x.RuleId == "WT006");
        Assert.Equal(Severity.High, f.Severity);
        Assert.Contains("OnCreateAutomationPeer", f.Suggestion);
        Assert.Equal(0, Check(card, "coverage"));
        Assert.Equal(0, Check(card, "visibility"));
        Assert.DoesNotContain(card.Findings, x => x.RuleId == "WT001");
    }

    [Fact]
    public void Stability_compares_with_a_baseline()
    {
        var before = Scorer.Evaluate(Scan("""
            <StackPanel>
              <Button Content="Save" AutomationProperties.AutomationId="SaveButton"/>
              <Button Content="Cancel" AutomationProperties.AutomationId="CancelButton"/>
            </StackPanel>
            """));
        Assert.False(before.Overall.Checks.Single(c => c.Id == "stability").Measured);

        var baseline = Baseline.FromScorecard(JsonReport.Deserialize(JsonReport.Serialize(before)), "before.json");
        var after = Scorer.Evaluate(Scan("""
            <StackPanel>
              <Button Content="Save" AutomationProperties.AutomationId="SaveButton"/>
              <Button Content="Cancel" AutomationProperties.AutomationId="CloseButton"/>
            </StackPanel>
            """), baseline);

        Assert.Equal(50, Check(after, "stability"));
        var f = Assert.Single(after.Findings, x => x.RuleId == "WT010");
        Assert.Contains("CancelButton", f.Message);
        Assert.Equal("before.json", after.BaselineFile);
    }

    [Fact]
    public void Score_is_the_weighted_average_of_measured_checks()
    {
        var card = Scorer.Evaluate(Scan("""
            <StackPanel>
              <Button Content="A" AutomationProperties.AutomationId="AButton"/>
              <Button/>
            </StackPanel>
            """));

        var measured = card.Overall.Checks.Where(c => c.Measured).ToList();
        var expected = Math.Round(measured.Sum(c => c.Weight * c.Earned / c.Possible) / measured.Sum(c => c.Weight) * 100, 1);
        Assert.Equal(expected, card.Overall.Score);
        Assert.DoesNotContain(measured, c => c.Id == "stability");
    }

    [Theory]
    [InlineData(95, "A")]
    [InlineData(90, "A")]
    [InlineData(89.9, "B")]
    [InlineData(75, "B")]
    [InlineData(60, "C")]
    [InlineData(40, "D")]
    [InlineData(39.9, "F")]
    public void Grades_follow_the_published_bands(double score, string grade) =>
        Assert.Equal(grade, RuleBook.GradeFor(score));

    [Fact]
    public void Live_scans_get_ids_origin_from_source_when_available()
    {
        var live = new ScanResult { Mode = ScanMode.Live, Target = "app.exe" };
        var screen = new ScreenScan { Name = "MainWindow" };
        screen.Elements.Add(new UiElementInfo { Screen = "MainWindow", Kind = "Button", ControlType = "Button", AutomationId = "SaveButton", IdOrigin = IdOrigin.Unknown, IsInteractive = true });
        screen.Elements.Add(new UiElementInfo { Screen = "MainWindow", Kind = "Edit", ControlType = "Edit", AutomationId = "SearchBox", IdOrigin = IdOrigin.Unknown, IsInteractive = true });
        screen.Elements.Add(new UiElementInfo { Screen = "MainWindow", Kind = "Edit", ControlType = "Edit", AutomationId = "Elsewhere", IdOrigin = IdOrigin.Unknown, IsInteractive = true });
        live.Screens.Add(screen);

        var source = Scan("""
            <StackPanel>
              <Button Content="Save" AutomationProperties.AutomationId="SaveButton"/>
              <TextBox x:Name="SearchBox"/>
            </StackPanel>
            """);

        Assert.Equal(2, SourceEnricher.Enrich(live, source));
        Assert.Equal(IdOrigin.Explicit, screen.Elements[0].IdOrigin);
        Assert.Equal(IdOrigin.XName, screen.Elements[1].IdOrigin);
        Assert.Equal(IdOrigin.Unknown, screen.Elements[2].IdOrigin);
    }

    [Fact]
    public void Live_buttons_without_click_support_are_flagged_and_template_parts_ignored()
    {
        var live = new ScanResult { Mode = ScanMode.Live, Target = "app.exe" };
        var screen = new ScreenScan { Name = "Main" };
        screen.Elements.Add(new UiElementInfo { Screen = "Main", Kind = "Button", ControlType = "Button", AutomationId = "Go", IdOrigin = IdOrigin.Unknown, IsInteractive = true, HasName = true, Patterns = [] });
        screen.Elements.Add(new UiElementInfo { Screen = "Main", Kind = "Edit", ControlType = "Edit", AutomationId = "PART_EditableTextBox", IdOrigin = IdOrigin.Unknown, IsInteractive = true });
        screen.Elements.Add(new UiElementInfo { Screen = "Main", Kind = "Custom", ControlType = "Custom", IsOpaque = true });
        live.Screens.Add(screen);

        var card = Scorer.Evaluate(live);

        Assert.Contains(card.Findings, f => f.RuleId == "WT012");
        Assert.Contains(card.Findings, f => f.RuleId == "WT011");
        Assert.DoesNotContain(card.Findings, f => f.Message.Contains("PART_"));
        Assert.Equal(1, card.Overall.InteractiveCount);
    }
}
