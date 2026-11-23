using WpfLocatorDoctor.Core.Model;
using WpfLocatorDoctor.Core.Reporting;
using WpfLocatorDoctor.Core.Rules;
using WpfLocatorDoctor.Core.Scoring;
using WpfLocatorDoctor.Core.Source;

namespace WpfLocatorDoctor.Core.Tests;

/// <summary>Folder scanning, unusual input, and report edge cases.</summary>
public sealed class EdgeCaseTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wld-core-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Window(string xClass, string body = "<Grid/>") => $"""
        <Window x:Class="{xClass}"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
        {body}
        </Window>
        """;

    // ------------------------------------------------------------------ folder scanning

    [Fact]
    public void Two_screens_with_the_same_class_name_get_distinct_names()
    {
        Write("Admin/MainView.xaml", Window("Shop.Admin.MainView", """<Button Content="A" AutomationProperties.AutomationId="AdminButton"/>"""));
        Write("Customer/MainView.xaml", Window("Shop.Customer.MainView", """<Button Content="C" AutomationProperties.AutomationId="CustomerButton"/>"""));

        var scan = new XamlSourceScanner().Scan(_root);

        var names = scan.Screens.Select(s => s.Name).ToList();
        Assert.Equal(2, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("MainView", names);
        Assert.Contains("Shop.Customer.MainView", names);
    }

    [Fact]
    public void Same_class_name_and_no_namespace_falls_back_to_the_file_path()
    {
        Write("A/Dialog.xaml", Window("Dialog"));
        Write("B/Dialog.xaml", Window("Dialog"));

        var names = new XamlSourceScanner().Scan(_root).Screens.Select(s => s.Name).ToList();

        Assert.Equal(["Dialog", Path.Combine("B", "Dialog.xaml")], names);
    }

    [Fact]
    public void A_baseline_with_duplicate_screen_names_still_loads()
    {
        var old = new Scorecard
        {
            Screens =
            [
                new ScreenScore { Name = "Main", Summary = new ScoreSummary(), AutomationIds = ["A"] },
                new ScreenScore { Name = "Main", Summary = new ScoreSummary(), AutomationIds = ["B"] },
            ],
        };

        var baseline = Baseline.FromScorecard(old, "old.json");

        Assert.Equal(["A", "B"], baseline.IdsByScreen["Main"].Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Build_output_and_tool_folders_are_skipped()
    {
        Write("MainWindow.xaml", Window("Demo.MainWindow"));
        Write("bin/Debug/Copy.xaml", Window("Demo.Copy"));
        Write("obj/Generated.xaml", Window("Demo.Generated"));
        Write("node_modules/x/Other.xaml", Window("Demo.Other"));

        var scan = new XamlSourceScanner().Scan(_root);

        Assert.Equal(["MainWindow"], scan.Screens.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void Broken_xaml_is_noted_and_the_other_files_are_still_scanned()
    {
        Write("Good.xaml", Window("Demo.Good"));
        Write("Broken.xaml", "<Window><Button></Window>");

        var scan = new XamlSourceScanner().Scan(_root);

        Assert.Equal(["Good"], scan.Screens.Select(s => s.Name).ToArray());
        Assert.Contains(scan.Notes, n => n.StartsWith("Could not read Broken.xaml", StringComparison.Ordinal));
    }

    [Fact]
    public void Xaml_saved_in_a_windows_code_page_is_read()
    {
        // Found by the benchmark: three files in microsoft/WPF-Samples declare encoding="Windows-1252".
        var xaml = "<?xml version=\"1.0\" encoding=\"Windows-1252\"?>\n" +
                   Window("Demo.Legacy", "<Button Content=\"Café\" AutomationProperties.AutomationId=\"CafeButton\"/>");
        var path = Path.Combine(_root, "Legacy.xaml");
        File.WriteAllBytes(path, [.. xaml.Select(c => (byte)c)]); // every character here is one byte in Windows-1252

        var scan = new XamlSourceScanner().Scan(_root);

        Assert.Empty(scan.Notes);
        var button = Assert.Single(scan.Screens.Single().Elements, e => e.Kind == "Button");
        Assert.Equal("CafeButton", button.AutomationId);
        Assert.Equal("Café", button.Name);
    }

    [Fact]
    public void A_folder_without_xaml_says_so()
    {
        Write("readme.txt", "hello");
        var scan = new XamlSourceScanner().Scan(_root);
        Assert.Empty(scan.Screens);
        Assert.Contains("No XAML files were found.", scan.Notes);
    }

    [Fact]
    public void A_project_file_path_scans_its_folder()
    {
        Write("MainWindow.xaml", Window("Demo.MainWindow"));
        Write("App.csproj", "<Project/>");

        var scan = new XamlSourceScanner().Scan(Path.Combine(_root, "App.csproj"));

        Assert.Single(scan.Screens);
    }

    [Fact]
    public void A_missing_folder_throws_a_clear_error()
    {
        var ex = Assert.Throws<DirectoryNotFoundException>(() => new XamlSourceScanner().Scan(Path.Combine(_root, "nope")));
        Assert.Contains("Folder not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_controls_in_cs_files_are_found_but_generated_files_are_ignored()
    {
        Write("MainWindow.xaml", Window("Demo.MainWindow", """<local:Gauge xmlns:local="clr-namespace:Demo"/>"""));
        Write("Gauge.cs", "namespace Demo; public class Gauge : System.Windows.Controls.Control { }");
        Write("obj/Ghost.cs", "public class Ghost : Control { }");
        Write("Other.g.cs", "public class Generated : Control { }");

        var scan = new XamlSourceScanner().Scan(_root);

        Assert.Equal(["Gauge"], scan.CustomControls.Select(c => c.ClassName).ToArray());
        Assert.Equal(1, scan.CustomControlUsage["Gauge"]);
    }

    // ------------------------------------------------------------------ scorer edge cases

    [Fact]
    public void An_id_on_an_element_test_tools_cannot_see_is_reported()
    {
        var scan = new XamlSourceScanner().ScanText(Window("Demo.Main", """<local:Box xmlns:local="clr-namespace:Demo" AutomationProperties.AutomationId="BoxPanel"/>"""),
            "Main.xaml", "public class Box : Panel { }");

        var card = Scorer.Evaluate(scan);

        var f = Assert.Single(card.Findings, x => x.RuleId == "WT007");
        Assert.Contains("BoxPanel", f.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_baseline_with_no_matching_screens_leaves_stability_unmeasured_and_says_why()
    {
        var scan = new XamlSourceScanner().ScanText(Window("Demo.Main", """<Button Content="Go" AutomationProperties.AutomationId="GoButton"/>"""), "Main.xaml");
        var baseline = new Baseline { File = "old.json", IdsByScreen = new() { ["SomethingElse"] = ["X"] } };

        var card = Scorer.Evaluate(scan, baseline);

        Assert.False(card.Overall.Checks.Single(c => c.Id == "stability").Measured);
        Assert.Contains(card.Notes, n => n.Contains("no screens with the same names", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ heuristics

    [Theory]
    [InlineData(IdOrigin.Explicit, 1.0)]
    [InlineData(IdOrigin.DataBound, 1.0)]
    [InlineData(IdOrigin.Unknown, 0.85)]
    [InlineData(IdOrigin.XName, 0.7)]
    [InlineData(IdOrigin.Uid, 0.5)]
    [InlineData(IdOrigin.GeneratedLooking, 0.1)]
    [InlineData(IdOrigin.None, 0.0)]
    public void Id_quality_ranks_ids_set_on_purpose_highest(IdOrigin origin, double expected) =>
        Assert.Equal(expected, IdHeuristics.QualityWeight(origin));

    [Theory]
    [InlineData("PART_EditableTextBox", true)]
    [InlineData("part_lowercase", false)]
    [InlineData("SaveButton", false)]
    [InlineData(null, false)]
    public void Template_parts_are_recognised(string? id, bool expected) =>
        Assert.Equal(expected, IdHeuristics.IsTemplatePartId(id));

    [Theory]
    [InlineData("2nd step", "Id2ndStep")]
    [InlineData("!!!", "")]
    [InlineData("_Save as", "SaveAs")]
    public void Labels_become_safe_identifiers(string text, string expected) =>
        Assert.Equal(expected, IdHeuristics.ToPascalIdentifier(text));

    [Fact]
    public void Underscores_in_an_x_name_separate_words()
    {
        var e = new UiElementInfo { Screen = "Orders", Kind = "Button", ControlType = "Button", XName = "save_button" };
        Assert.Equal("SaveButton", IdHeuristics.SuggestId(e));
    }

    [Fact]
    public void A_generated_looking_x_name_is_not_used_to_suggest_an_id()
    {
        var e = new UiElementInfo { Screen = "Orders", Kind = "Button", ControlType = "Button", XName = "Button_7", Name = "Print", HasName = true };
        Assert.Equal("PrintButton", IdHeuristics.SuggestId(e));
    }

    [Fact]
    public void A_label_with_no_usable_letters_falls_back_to_the_screen_name()
    {
        var e = new UiElementInfo { Screen = "local:Orders", Kind = "local:Gauge", ControlType = "Custom", Name = "???", HasName = true };
        Assert.Equal("OrdersGauge", IdHeuristics.SuggestId(e));
    }

    // ------------------------------------------------------------------ reports

    private static Scorecard CardWithFindings(int count, ScanMode mode = ScanMode.Source)
    {
        var card = new Scorecard { Mode = mode, Target = "app" };
        card.Screens.Add(new ScreenScore { Name = "Main", Summary = new ScoreSummary { Score = 50, Grade = "D" } });
        for (var i = 1; i <= count; i++)
            card.Findings.Add(new Finding("WT001", Severity.High, "Control has no AutomationId", $"Problem {i}.", "Main", null, null, null));
        return card;
    }

    [Fact]
    public void Markdown_lists_up_to_the_limit_without_a_truncation_note()
    {
        var md = MarkdownReport.Render(CardWithFindings(MarkdownReport.MaxDetailedFindings));

        Assert.Contains($"Problem {MarkdownReport.MaxDetailedFindings}.", md, StringComparison.Ordinal);
        Assert.DoesNotContain("Only the first", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_says_when_it_stops_listing_problems()
    {
        var md = MarkdownReport.Render(CardWithFindings(MarkdownReport.MaxDetailedFindings + 1));

        Assert.Contains($"Problem {MarkdownReport.MaxDetailedFindings}.", md, StringComparison.Ordinal);
        Assert.DoesNotContain($"Problem {MarkdownReport.MaxDetailedFindings + 1}.", md, StringComparison.Ordinal);
        Assert.Contains($"Only the first {MarkdownReport.MaxDetailedFindings} problems", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_does_not_print_an_empty_heading_after_the_limit()
    {
        var card = CardWithFindings(MarkdownReport.MaxDetailedFindings);
        card.Findings.Add(new Finding("WT001", Severity.High, "t", "Late problem.", "Zeta", null, null, null));

        var md = MarkdownReport.Render(card);

        Assert.DoesNotContain("### Zeta", md, StringComparison.Ordinal);
        Assert.Contains("Only the first", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_for_a_clean_live_scan_has_notes_and_live_limits()
    {
        var card = CardWithFindings(0, ScanMode.Live);
        card.Notes.Add("Matched 3 live IDs.");

        var md = MarkdownReport.Render(card);

        Assert.Contains("No problems found.", md, StringComparison.Ordinal);
        Assert.Contains("- Matched 3 live IDs.", md, StringComparison.Ordinal);
        Assert.Contains("A live scan only sees the windows that were open", md, StringComparison.Ordinal);
        Assert.Contains("the running app", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_puts_custom_control_problems_first_with_copyable_code()
    {
        var card = CardWithFindings(1);
        card.Findings.Add(new Finding("WT006", Severity.High, "t", "Gauge is invisible.", "(code)", "Gauge", null,
            "In Gauge, add: protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this); (or a custom peer)."));

        var md = MarkdownReport.Render(card);

        Assert.True(md.IndexOf("### Custom controls (C# code)", StringComparison.Ordinal) < md.IndexOf("### Main", StringComparison.Ordinal));
        Assert.Contains("`protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);`", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_summary_shortens_long_problem_lists_and_shows_notes()
    {
        var card = CardWithFindings(15);
        card.Notes.Add("Something to know.");

        var text = TextSummary.Render(card, maxFindings: 10);

        Assert.Contains("… and 5 more in the full report.", text, StringComparison.Ordinal);
        Assert.Contains("Note: Something to know.", text, StringComparison.Ordinal);
        Assert.Contains("Problems: 15 (15 high, 0 medium, 0 low)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_use_the_same_number_format_on_every_computer()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var card = CardWithFindings(0);
            card.Overall = new ScoreSummary { Score = 87.5, Grade = "B" };

            Assert.Contains("87.5", TextSummary.Render(card), StringComparison.Ordinal);
            Assert.Contains("87.5 / 100", MarkdownReport.Render(card), StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
