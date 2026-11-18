using WpfLocatorDoctor.Core.Reporting;

namespace WpfLocatorDoctor.Cli.Tests;

/// <summary>Runs the wpf-locator-doctor command in-process and checks its output, files and exit codes.</summary>
public sealed class CliAppTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("wld-cli-").FullName;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        Directory.Delete(_temp, recursive: true);
    }

    private int Run(params string[] args) => new CliApp(_out, _err).Run(args);

    private string OutDir => Path.Combine(_temp, "report");

    private string WriteXaml(string body, string file = "MainWindow.xaml")
    {
        var path = Path.Combine(_temp, "app", file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"""
            <Window x:Class="Demo.{Path.GetFileNameWithoutExtension(file)}"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            {body}
            </Window>
            """);
        return Path.Combine(_temp, "app");
    }

    private static string PracticeAppFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WpfLocatorDoctor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "samples", "PracticeApp");
    }

    // ------------------------------------------------------------------ help, version, rules

    [Fact]
    public void No_arguments_prints_help_and_signals_wrong_usage()
    {
        Assert.Equal(CliApp.ExitUsage, Run());
        Assert.Contains("Usage:", _out.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public void Help_prints_usage_and_succeeds(string flag)
    {
        Assert.Equal(CliApp.ExitOk, Run(flag));
        Assert.Contains("scan-source", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Version_prints_the_version()
    {
        Assert.Equal(CliApp.ExitOk, Run("--version"));
        Assert.Matches(@"^\d+\.\d+\.\d+\s*$", _out.ToString());
    }

    [Fact]
    public void Rules_lists_every_check_and_problem_type()
    {
        Assert.Equal(CliApp.ExitOk, Run("rules"));
        var text = _out.ToString();
        foreach (var id in Enumerable.Range(1, 12).Select(i => $"WT{i:000}"))
            Assert.Contains(id, text, StringComparison.Ordinal);
        Assert.Contains("ID coverage", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ wrong usage (exit 2)

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("frobnicate", "--bad-option")]
    public void Unknown_commands_are_reported_before_options(params string[] args)
    {
        Assert.Equal(CliApp.ExitUsage, Run(args));
        Assert.Contains("Unknown command 'frobnicate'", _err.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Unknown option '--colour'", "scan-source", ".", "--colour")]
    [InlineData("--out needs a value", "scan-source", ".", "--out")]
    [InlineData("Unexpected extra argument 'b'", "scan-source", "a", "b")]
    [InlineData("--min-score must be a number between 0 and 100", "scan-source", ".", "--min-score", "150")]
    [InlineData("--min-score must be a number between 0 and 100", "scan-source", ".", "--min-score", "lots")]
    [InlineData("--wait must be a whole number", "scan-live", "--exe", "a.exe", "--wait", "2.5")]
    [InlineData("--max-rows must be a whole number", "scan-live", "--exe", "a.exe", "--max-rows", "0")]
    [InlineData("scan-source needs a folder", "scan-source")]
    [InlineData("Not found:", "scan-source", "/definitely/not/here")]
    public void Bad_arguments_explain_the_problem(string expectedMessage, params string[] args)
    {
        Assert.Equal(CliApp.ExitUsage, Run(args));
        Assert.Contains(expectedMessage, _err.ToString(), StringComparison.Ordinal);
        Assert.Contains("--help", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Live_scan_explains_it_needs_the_Windows_build()
    {
        // The test project uses the cross-platform build of the tool, which has no live scanner.
        Assert.Equal(CliApp.ExitUsage, Run("scan-live", "--exe", "app.exe"));
        Assert.Contains("only works in the Windows build", _err.ToString(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ scan-source

    [Fact]
    public void Scan_source_writes_both_reports_and_a_summary()
    {
        var app = WriteXaml("""<Button Content="Save" AutomationProperties.AutomationId="SaveButton"/>""");

        Assert.Equal(CliApp.ExitOk, Run("scan-source", app, "--out", OutDir));

        var json = Path.Combine(OutDir, CliApp.JsonReportName);
        var md = Path.Combine(OutDir, CliApp.MarkdownReportName);
        Assert.True(File.Exists(json));
        Assert.True(File.Exists(md));
        Assert.Equal(100, JsonReport.Load(json).Overall.Score);
        Assert.Contains("# WPF testability report", File.ReadAllText(md), StringComparison.Ordinal);
        Assert.Contains("Overall: grade A", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("Full report:", _out.ToString(), StringComparison.Ordinal);
        Assert.Empty(_err.ToString());
    }

    [Fact]
    public void Quiet_prints_only_where_the_reports_are()
    {
        var app = WriteXaml("""<Button Content="Save"/>""");

        Assert.Equal(CliApp.ExitOk, Run("scan-source", app, "--out", OutDir, "--quiet"));

        Assert.DoesNotContain("Overall:", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("Full report:", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_xaml_file_can_be_scanned()
    {
        var app = WriteXaml("""<Button Content="Save" AutomationProperties.AutomationId="SaveButton"/>""");

        Assert.Equal(CliApp.ExitOk, Run("scan-source", Path.Combine(app, "MainWindow.xaml"), "--out", OutDir));
        Assert.Single(JsonReport.Load(Path.Combine(OutDir, CliApp.JsonReportName)).Screens);
    }

    [Fact]
    public void Score_below_min_score_fails_the_build()
    {
        var app = WriteXaml("""<StackPanel><Button Content="A"/><Button Content="B"/></StackPanel>""");

        Assert.Equal(CliApp.ExitBelowMinScore, Run("scan-source", app, "--out", OutDir, "--min-score", "90"));
        Assert.Contains("is below the required minimum of 90", _err.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(OutDir, CliApp.JsonReportName)), "reports are still written when the score is too low");
    }

    [Fact]
    public void Score_at_or_above_min_score_passes()
    {
        var app = WriteXaml("""<Button Content="Save" AutomationProperties.AutomationId="SaveButton"/>""");
        Assert.Equal(CliApp.ExitOk, Run("scan-source", app, "--out", OutDir, "--min-score", "100"));
    }

    [Fact]
    public void Nothing_to_measure_counts_as_below_any_min_score()
    {
        var app = WriteXaml("<Grid/>");
        Assert.Equal(CliApp.ExitBelowMinScore, Run("scan-source", app, "--out", OutDir, "--min-score", "1"));
        Assert.Contains("Score n/a", _err.ToString(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ baseline

    [Fact]
    public void Baseline_from_a_previous_run_catches_a_removed_id()
    {
        var app = WriteXaml("""
            <StackPanel>
              <Button Content="Save" AutomationProperties.AutomationId="SaveButton"/>
              <Button Content="Undo" AutomationProperties.AutomationId="UndoButton"/>
            </StackPanel>
            """);
        Assert.Equal(CliApp.ExitOk, Run("scan-source", app, "--out", OutDir));
        var baseline = Path.Combine(_temp, "baseline.json");
        File.Copy(Path.Combine(OutDir, CliApp.JsonReportName), baseline);

        WriteXaml("""<Button Content="Save" AutomationProperties.AutomationId="SaveButton"/>""");
        Assert.Equal(CliApp.ExitOk, Run("scan-source", app, "--out", OutDir, "--baseline", baseline));

        var card = JsonReport.Load(Path.Combine(OutDir, CliApp.JsonReportName));
        Assert.Equal(baseline, card.BaselineFile);
        var gone = Assert.Single(card.Findings, f => f.RuleId == "WT010");
        Assert.Contains("UndoButton", gone.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_baseline_file_is_a_usage_error()
    {
        var app = WriteXaml("<Grid/>");
        Assert.Equal(CliApp.ExitUsage, Run("scan-source", app, "--out", OutDir, "--baseline", Path.Combine(_temp, "nope.json")));
        Assert.Contains("Baseline file not found", _err.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("null")]
    public void Unreadable_baseline_file_fails_the_scan(string content)
    {
        var app = WriteXaml("<Grid/>");
        var baseline = Path.Combine(_temp, "broken.json");
        File.WriteAllText(baseline, content);

        Assert.Equal(CliApp.ExitScanFailed, Run("scan-source", app, "--out", OutDir, "--baseline", baseline));
        Assert.Contains("Scan failed:", _err.ToString(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ end to end on the sample app

    [Fact]
    public void Practice_app_scan_finds_every_planted_problem_type()
    {
        Assert.Equal(CliApp.ExitOk, Run("scan-source", PracticeAppFolder(), "--out", OutDir));

        var card = JsonReport.Load(Path.Combine(OutDir, CliApp.JsonReportName));
        var rules = card.Findings.Select(f => f.RuleId).Distinct().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["WT001", "WT002", "WT003", "WT004", "WT005", "WT006", "WT007", "WT008"], rules);
        Assert.Equal(3, card.Screens.Count);
    }
}
