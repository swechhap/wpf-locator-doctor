using WpfLocatorDoctor.Core.Assistant;
using WpfLocatorDoctor.Core.Reporting;
using WpfLocatorDoctor.Core.Scoring;
using WpfLocatorDoctor.Core.Source;

namespace WpfLocatorDoctor.Core.Tests;

/// <summary>The answers the MCP server gives about a project's source code.</summary>
public sealed class ProjectDoctorTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("wld-doctor-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private static string PracticeAppFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WpfLocatorDoctor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "samples", "PracticeApp");
    }

    private string CopyOfPracticeApp()
    {
        var source = PracticeAppFolder();
        var target = Path.Combine(_temp, "PracticeApp");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal)))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith("bin", StringComparison.Ordinal) || relative.StartsWith("obj", StringComparison.Ordinal)) continue;
            var to = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        return target;
    }

    private string WriteXaml(string body, string file = "MainWindow.xaml")
    {
        var path = Path.Combine(_temp, "app", file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"""
            <Window x:Class="Demo.{Path.GetFileNameWithoutExtension(file)}"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <StackPanel>
            {body}
              </StackPanel>
            </Window>
            """);
        return Path.Combine(_temp, "app");
    }

    // ------------------------------------------------------------------ score

    [Fact]
    public void Score_gives_the_same_numbers_as_the_command_line()
    {
        var report = ProjectDoctor.Score(PracticeAppFolder());

        Assert.Equal(86.5, report.Score);
        Assert.Equal("B", report.Grade);
        Assert.Equal(["MainWindow", "AddressPanel", "SettingsWindow"], report.Screens.Select(s => s.Name).ToArray()); // worst first
        Assert.Equal(new ProblemCounts(18, 6, 4, 8), report.Problems);
        Assert.Equal(6, report.Checks.Count);
        Assert.Equal(5, report.TopProblems.Count);
        Assert.All(report.TopProblems, p => Assert.Equal("high", p.Severity));
        Assert.StartsWith("Grade B, score 86.5/100 across 3 screen(s) with 31 controls.", report.Summary, StringComparison.Ordinal);
        Assert.Contains("Worst screen: MainWindow (80.5).", report.Summary, StringComparison.Ordinal);
        Assert.Null(report.SavedReports);
    }

    [Fact]
    public void Score_can_save_reports_that_work_as_a_baseline()
    {
        var outDir = Path.Combine(_temp, "out");
        var report = ProjectDoctor.Score(PracticeAppFolder(), saveReportTo: outDir);

        Assert.NotNull(report.SavedReports);
        Assert.True(File.Exists(Path.Combine(outDir, "testability-report.md")));
        Assert.Equal(Path.Combine(outDir, "testability-report.json"), report.SavedReports!.JsonReport);
        Assert.Equal(86.5, JsonReport.Load(report.SavedReports.JsonReport).Overall.Score);

        var again = ProjectDoctor.Score(PracticeAppFolder(), baselineReport: report.SavedReports.JsonReport);
        var stability = again.Checks.Single(c => c.Check == "Stability");
        Assert.Equal(100, stability.Percent);
    }

    [Fact]
    public void Score_explains_when_there_is_nothing_to_score()
    {
        var report = ProjectDoctor.Score(WriteXaml("""    <TextBlock Text="Hello" />"""));
        Assert.Null(report.Score);
        Assert.Equal("N/A", report.Grade);
        Assert.StartsWith("Nothing to score", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Score_accepts_a_project_file_or_a_single_xaml_file()
    {
        var folder = WriteXaml("""    <Button Content="OK" AutomationProperties.AutomationId="OkButton" />""");
        File.WriteAllText(Path.Combine(folder, "App.csproj"), "<Project />");
        Assert.Equal(100, ProjectDoctor.Score(Path.Combine(folder, "App.csproj")).Score);
        Assert.Equal(100, ProjectDoctor.Score(Path.Combine(folder, "MainWindow.xaml")).Score);
    }

    // ------------------------------------------------------------------ friendly errors

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_path_is_explained(string path)
    {
        var ex = Assert.Throws<DoctorInputException>(() => ProjectDoctor.Score(path));
        Assert.Contains("Give the path", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrong_path_is_explained_with_the_folder_relative_paths_start_from()
    {
        var ex = Assert.Throws<DoctorInputException>(() => ProjectDoctor.Score(Path.Combine(_temp, "nope")));
        Assert.Contains("Not found", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Environment.CurrentDirectory, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_without_xaml_is_explained()
    {
        var ex = Assert.Throws<DoctorInputException>(() => ProjectDoctor.ListProblems(_temp));
        Assert.Contains("No .xaml files", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_or_broken_baseline_is_explained()
    {
        var folder = WriteXaml("""    <Button Content="OK" />""");
        var missing = Assert.Throws<DoctorInputException>(() => ProjectDoctor.Score(folder, Path.Combine(_temp, "none.json")));
        Assert.Contains("Baseline report not found", missing.Message, StringComparison.Ordinal);

        var broken = Path.Combine(_temp, "broken.json");
        File.WriteAllText(broken, "this is not json");
        var bad = Assert.Throws<DoctorInputException>(() => ProjectDoctor.CompareToBaseline(folder, broken));
        Assert.Contains("isn't a wpf-locator-doctor JSON report", bad.Message, StringComparison.Ordinal);

        Assert.Throws<DoctorInputException>(() => ProjectDoctor.CompareToBaseline(folder, " "));
    }

    // ------------------------------------------------------------------ problems

    [Theory]
    [InlineData("AddressPanel")]
    [InlineData("addresspanel")]
    [InlineData("Views/AddressPanel.xaml")]
    [InlineData("Views\\AddressPanel.xaml")]
    [InlineData("AddressPanel.xaml")]
    public void Problems_can_be_filtered_by_screen_name_or_file(string screen)
    {
        var list = ProjectDoctor.ListProblems(PracticeAppFolder(), screen: screen);
        Assert.Equal(3, list.Total);
        Assert.All(list.Problems, p => Assert.Equal("AddressPanel", p.Screen));
        Assert.EndsWith("on AddressPanel.", list.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_screen_lists_the_screens_that_exist()
    {
        var ex = Assert.Throws<DoctorInputException>(() => ProjectDoctor.ListProblems(PracticeAppFolder(), screen: "Nope"));
        Assert.Equal("No screen called 'Nope'. Screens found: MainWindow, SettingsWindow, AddressPanel.", ex.Message);
    }

    [Fact]
    public void Problems_can_be_filtered_by_severity_and_type()
    {
        var high = ProjectDoctor.ListProblems(PracticeAppFolder(), minSeverity: "HIGH");
        Assert.Equal(6, high.Total);
        Assert.All(high.Problems, p => Assert.Equal("high", p.Severity));

        var medium = ProjectDoctor.ListProblems(PracticeAppFolder(), minSeverity: "medium");
        Assert.Equal(10, medium.Total);

        var missing = ProjectDoctor.ListProblems(PracticeAppFolder(), rule: "wt001");
        Assert.All(missing.Problems, p => Assert.Equal("WT001", p.Rule));
        Assert.Contains("of type WT001 (Control has no AutomationId)", missing.Summary, StringComparison.Ordinal);
        Assert.All(missing.Problems, p => Assert.NotNull(p.Fix));
    }

    [Theory]
    [InlineData("urgent", null, "Unknown severity")]
    [InlineData(null, "WT999", "Unknown problem type")]
    public void Unknown_filters_are_explained(string? severity, string? rule, string expected)
    {
        var ex = Assert.Throws<DoctorInputException>(() => ProjectDoctor.ListProblems(PracticeAppFolder(), minSeverity: severity, rule: rule));
        Assert.StartsWith(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_lists_are_cut_to_the_limit_and_say_so()
    {
        var list = ProjectDoctor.ListProblems(PracticeAppFolder(), limit: 2);
        Assert.Equal(18, list.Total);
        Assert.Equal(2, list.Shown);
        Assert.Contains("Showing the first 2", list.Summary, StringComparison.Ordinal);

        Assert.Equal(18, ProjectDoctor.ListProblems(PracticeAppFolder(), limit: 0).Shown);       // 0 means the default (50)
        Assert.Equal(18, ProjectDoctor.ListProblems(PracticeAppFolder(), limit: 100_000).Shown); // capped, not an error
    }

    [Fact]
    public void A_screen_with_no_problems_says_so()
    {
        var list = ProjectDoctor.ListProblems(PracticeAppFolder(), screen: "SettingsWindow");
        Assert.Equal(0, list.Total);
        Assert.Equal("No problems found on SettingsWindow.", list.Summary);
    }

    // ------------------------------------------------------------------ fixes

    [Fact]
    public void Fixes_cover_every_planted_id_problem_in_the_practice_app()
    {
        var plan = ProjectDoctor.SuggestFixes(PracticeAppFolder());
        Assert.Equal(PracticeAppFolder(), plan.ProjectFolder);
        var kinds = plan.Fixes.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(5, kinds["add-id"]);
        Assert.Equal(5, kinds["pin-id"]);
        Assert.Equal(1, kinds["replace-generated-id"]);
        Assert.Equal(1, kinds["fix-duplicate-id"]);
        Assert.Equal(1, kinds["row-ids"]);
        Assert.Equal(1, kinds["automation-peer"]);
        Assert.Contains(plan.Fixes, f => f is { Kind: "replace-generated-id", SuggestedId: "ClearButton", Line: 27 });
        Assert.Contains(plan.Fixes, f => f is { Kind: "fix-duplicate-id", SuggestedId: "ExportButton", Line: 45 });
        Assert.Contains(plan.Fixes, f => f is { Kind: "automation-peer", Element: "StarRating" });
    }

    [Fact]
    public void Suggested_ids_are_unique_on_each_screen()
    {
        var scan = new XamlSourceScanner().Scan(PracticeAppFolder());
        foreach (var screen in scan.Screens)
        {
            var existing = screen.Elements.Where(e => e.AutomationId is not null).Select(e => e.AutomationId!).ToHashSet();
            var fixes = ProjectDoctor.SuggestFixes(PracticeAppFolder(), screen.Name).Fixes;
            var newIds = fixes.Where(f => f.Kind is "add-id" or "replace-generated-id" or "fix-duplicate-id").Select(f => f.SuggestedId!).ToList();
            Assert.Equal(newIds.Count, newIds.Distinct().Count());
            Assert.DoesNotContain(newIds, existing.Contains);
        }
    }

    [Fact]
    public void Applying_every_edit_removes_the_id_problems_and_raises_the_score()
    {
        var app = CopyOfPracticeApp();
        var before = ProjectDoctor.Score(app);
        var plan = ProjectDoctor.SuggestFixes(app);
        var edits = plan.Fixes.Where(f => f.Edit is not null).ToList();
        Assert.Equal(12, edits.Count); // every add/pin/replace/duplicate fix comes with an exact edit

        foreach (var group in edits.GroupBy(f => f.File!))
        {
            var path = Path.Combine(plan.ProjectFolder, group.Key);
            var lines = File.ReadAllText(path).Split('\n');
            foreach (var fix in group)
            {
                var i = fix.Edit!.Line - 1;
                Assert.Equal(fix.Edit.Before, lines[i].TrimEnd('\r'));
                lines[i] = fix.Edit.After;
            }
            File.WriteAllText(path, string.Join('\n', lines));
        }

        var after = ProjectDoctor.ListProblems(app);
        foreach (var rule in new[] { "WT001", "WT002", "WT003", "WT004", "WT009" })
            Assert.DoesNotContain(after.Problems, p => p.Rule == rule);
        Assert.True(ProjectDoctor.Score(app).Score > before.Score);
    }

    [Fact]
    public void Fixes_for_one_screen_leave_out_the_c_sharp_fixes()
    {
        var plan = ProjectDoctor.SuggestFixes(PracticeAppFolder(), screen: "MainWindow");
        Assert.All(plan.Fixes, f => Assert.Equal("MainWindow", f.Screen));
        Assert.Contains(" on MainWindow:", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_screen_with_nothing_to_fix_says_so()
    {
        var plan = ProjectDoctor.SuggestFixes(PracticeAppFolder(), screen: "SettingsWindow");
        Assert.Equal(0, plan.Total);
        Assert.StartsWith("Nothing to fix on SettingsWindow", plan.Summary, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ baseline

    [Fact]
    public void Comparing_with_a_baseline_lists_ids_that_disappeared_and_ids_that_were_added()
    {
        var folder = WriteXaml("""
                <Button Content="Save" AutomationProperties.AutomationId="SaveButton" />
                <Button Content="Cancel" AutomationProperties.AutomationId="CancelButton" />
            """);
        var saved = ProjectDoctor.Score(folder, saveReportTo: Path.Combine(_temp, "baseline")).SavedReports!.JsonReport;

        WriteXaml("""
                <Button Content="Save" AutomationProperties.AutomationId="SaveChangesButton" />
                <Button Content="Cancel" AutomationProperties.AutomationId="CancelButton" />
                <Button Content="Help" AutomationProperties.AutomationId="HelpButton" />
            """);
        var diff = ProjectDoctor.CompareToBaseline(folder, saved);

        var screen = Assert.Single(diff.Screens);
        Assert.Equal("MainWindow", screen.Screen);
        Assert.Equal(["SaveButton"], screen.MissingIds);
        Assert.Equal(["HelpButton", "SaveChangesButton"], screen.NewIds);
        Assert.Equal(1, diff.MissingIdCount);
        Assert.Equal(2, diff.NewIdCount);
        Assert.Equal(100, diff.BaselineScore);
        Assert.Equal(0, diff.Change); // every control still has a good ID; the renamed one is reported above instead
        Assert.StartsWith("1 ID(s) from the baseline disappeared, so tests that use them will fail: MainWindow: SaveButton. Score did not change",
            diff.Summary, StringComparison.Ordinal);
        Assert.Empty(diff.Notes);
    }

    [Fact]
    public void Comparing_with_an_unchanged_project_reports_no_change()
    {
        var folder = WriteXaml("""    <Button Content="Save" AutomationProperties.AutomationId="SaveButton" />""");
        var saved = ProjectDoctor.Score(folder, saveReportTo: Path.Combine(_temp, "baseline")).SavedReports!.JsonReport;

        var diff = ProjectDoctor.CompareToBaseline(folder, saved);
        Assert.Equal(0, diff.Change);
        Assert.StartsWith("Score did not change", diff.Summary, StringComparison.Ordinal);
        Assert.Contains("No IDs from the baseline disappeared.", diff.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Scores_are_compared_like_with_like()
    {
        // The saved report has no Stability check (it had no baseline of its own). Scoring the project now with one would
        // add a 100% check and make an unchanged project look better.
        var saved = ProjectDoctor.Score(PracticeAppFolder(), saveReportTo: Path.Combine(_temp, "first")).SavedReports!.JsonReport;
        var diff = ProjectDoctor.CompareToBaseline(PracticeAppFolder(), saved);
        Assert.Equal(0, diff.Change);
        Assert.Equal(86.5, diff.CurrentScore);
        Assert.All(diff.Screens, s => Assert.Equal(s.BaselineScore, s.CurrentScore));

        // A report that did measure Stability is compared with a score that measures it too.
        var second = ProjectDoctor.Score(PracticeAppFolder(), baselineReport: saved, saveReportTo: Path.Combine(_temp, "second")).SavedReports!.JsonReport;
        var diff2 = ProjectDoctor.CompareToBaseline(PracticeAppFolder(), second);
        Assert.Equal(0, diff2.Change);
        Assert.Equal(88.5, diff2.CurrentScore);
    }

    [Fact]
    public void Screens_that_were_removed_or_added_are_listed()
    {
        var folder = WriteXaml("""    <Button Content="Save" AutomationProperties.AutomationId="SaveButton" />""", "OldWindow.xaml");
        var saved = ProjectDoctor.Score(folder, saveReportTo: Path.Combine(_temp, "baseline")).SavedReports!.JsonReport;
        File.Delete(Path.Combine(folder, "OldWindow.xaml"));
        WriteXaml("""    <Button Content="Save" AutomationProperties.AutomationId="SaveButton" />""", "NewWindow.xaml");

        var diff = ProjectDoctor.CompareToBaseline(folder, saved);
        Assert.Equal(["OldWindow"], diff.ScreensOnlyInBaseline);
        Assert.Equal(["NewWindow"], diff.NewScreens);
        Assert.Contains("could not be compared", diff.Notes.Single(), StringComparison.Ordinal);
        Assert.Contains("Screens gone since the baseline: OldWindow.", diff.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_baseline_from_a_live_scan_comes_with_a_warning()
    {
        var folder = WriteXaml("""    <Button Content="Save" AutomationProperties.AutomationId="SaveButton" />""");
        var live = new Scorecard { Mode = Model.ScanMode.Live, Target = "App.exe" };
        var file = Path.Combine(_temp, "live.json");
        JsonReport.Save(live, file);

        var diff = ProjectDoctor.CompareToBaseline(folder, file);
        Assert.Contains(diff.Notes, n => n.Contains("live scan", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ rules

    [Fact]
    public void Rules_explain_every_check_problem_and_grade()
    {
        var guide = ProjectDoctor.Rules();
        Assert.Equal(6, guide.Checks.Count);
        Assert.Equal(12, guide.Problems.Count);
        Assert.Equal(5, guide.Grades.Count);
        Assert.StartsWith("A: Easy to automate.", guide.Grades[0], StringComparison.Ordinal);
        Assert.Contains(guide.IdOrigins, l => l.Contains("x:Name", StringComparison.Ordinal));
    }
}
