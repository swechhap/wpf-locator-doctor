using WpfLocatorDoctor.Core.Scoring;
using WpfLocatorDoctor.Core.Source;

namespace WpfLocatorDoctor.Core.Tests;

/// <summary>End-to-end: scans the sample WPF app in samples/PracticeApp, which has one example of each problem.</summary>
public class PracticeAppTests
{
    private static string PracticeAppFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WpfLocatorDoctor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "samples", "PracticeApp");
    }

    private static readonly Lazy<Scorecard> Card = new(() =>
        Scorer.Evaluate(new XamlSourceScanner().Scan(PracticeAppFolder())));

    [Fact]
    public void Finds_all_three_screens()
    {
        Assert.Equal(["AddressPanel", "MainWindow", "SettingsWindow"], Card.Value.Screens.Select(s => s.Name).Order().ToArray());
    }

    [Fact]
    public void The_well_built_settings_screen_scores_an_A_with_no_problems()
    {
        var settings = Card.Value.Screens.Single(s => s.Name == "SettingsWindow");
        Assert.Equal(100, settings.Summary.Score);
        Assert.Equal(0, settings.FindingCount);
    }

    [Theory]
    [InlineData("WT001", "MainWindow.xaml", 16)] // Exit menu item
    [InlineData("WT001", "MainWindow.xaml", 29)] // icon-only Refresh button
    [InlineData("WT001", "MainWindow.xaml", 33)] // ComboBox
    [InlineData("WT002", "MainWindow.xaml", 23)] // SearchBox borrows x:Name
    [InlineData("WT003", "MainWindow.xaml", 27)] // x:Uid="Button_1"
    [InlineData("WT004", "MainWindow.xaml", 44)] // ActionButton twice
    [InlineData("WT005", "MainWindow.xaml", 57)] // ListBox rows
    [InlineData("WT006", "Controls/StarRating.cs", 13)]
    [InlineData("WT007", "MainWindow.xaml", 101)] // ID on a panel
    [InlineData("WT008", "Views/AddressPanel.xaml", 27)] // unlabelled postcode box
    public void Reports_each_planted_problem_at_the_right_place(string rule, string file, int line)
    {
        Assert.Contains(Card.Value.Findings, f =>
            f.RuleId == rule &&
            f.Location is not null &&
            f.Location.File.Replace('\\', '/') == file &&
            f.Location.Line == line);
    }

    [Fact]
    public void Good_examples_are_not_reported()
    {
        var findings = Card.Value.Findings;
        Assert.DoesNotContain(findings, f => f.Element?.Contains("AccessibleRating", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(findings, f => f.Element?.Contains("DataGrid", StringComparison.Ordinal) == true && f.RuleId == "WT005");
        Assert.DoesNotContain(findings, f => f.Message.Contains("SaveButton", StringComparison.Ordinal));
    }
}
