using System.Globalization;
using WpfLocatorDoctor.Core.Model;
using WpfLocatorDoctor.Core.Reporting;
using WpfLocatorDoctor.Core.Scoring;
using WpfLocatorDoctor.Core.Source;
#if LIVE_SCAN
using WpfLocatorDoctor.Live;
#endif

namespace WpfLocatorDoctor.Cli;

/// <summary>
/// The wpf-locator-doctor command. Output goes to the writers it is given (the console in real use,
/// string writers in tests), so every command and exit code can be tested without starting a process.
/// </summary>
internal sealed class CliApp(TextWriter stdout, TextWriter stderr)
{
    public const int ExitOk = 0;
    public const int ExitBelowMinScore = 1;
    public const int ExitUsage = 2;
    public const int ExitScanFailed = 3;

    public const string JsonReportName = "testability-report.json";
    public const string MarkdownReportName = "testability-report.md";

    public int Run(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is "-h" or "--help" or "help")
        {
            stdout.WriteLine(HelpText);
            return args.Count == 0 ? ExitUsage : ExitOk;
        }
        if (args[0] is "--version" or "version")
        {
            stdout.WriteLine(typeof(Scorecard).Assembly.GetName().Version?.ToString(3));
            return ExitOk;
        }

        Func<Options, int>? command = args[0] switch
        {
            "scan-source" => ScanSource,
            "scan-live" => ScanLive,
            "rules" => _ => PrintRules(),
            _ => null,
        };
        if (command is null) return Usage($"Unknown command '{args[0]}'.");

        try
        {
            return command(Options.Parse(args.Skip(1).ToList()));
        }
        catch (UsageException ex)
        {
            return Usage(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            stderr.WriteLine($"Scan failed: {ex.Message}");
            return ExitScanFailed;
        }
    }

    private int Usage(string message)
    {
        stderr.WriteLine(message);
        stderr.WriteLine("Run 'wpf-locator-doctor --help' for usage.");
        return ExitUsage;
    }

    private int ScanSource(Options o)
    {
        var path = o.Positional ?? throw new UsageException("scan-source needs a folder or file to scan, e.g. 'wpf-locator-doctor scan-source ./src/MyApp'.");
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new UsageException($"Not found: {path}");

        var scan = new XamlSourceScanner().Scan(path);
        return Finish(scan, o);
    }

    private int ScanLive(Options o)
    {
#if LIVE_SCAN
        if (o.Exe is null && o.Process is null)
            throw new UsageException("scan-live needs --exe <path to .exe> or --process <name or id>.");
        if (o.SourcePath is not null && !File.Exists(o.SourcePath) && !Directory.Exists(o.SourcePath))
            throw new UsageException($"Source folder not found: {o.SourcePath}");

        ScanResult scan;
        try
        {
            scan = new LiveAppScanner().Scan(new LiveScanOptions
            {
                ExePath = o.Exe,
                Arguments = o.AppArgs,
                Process = o.Process,
                UseUia2 = o.Uia2,
                WaitForWindow = TimeSpan.FromSeconds(o.WaitSeconds),
                MaxElementsPerWindow = o.MaxElements,
                MaxRowsPerList = o.MaxRows,
                KeepOpen = o.KeepOpen,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            stderr.WriteLine($"Could not scan the app: {ex.Message}");
            return ExitScanFailed;
        }

        if (o.SourcePath is not null)
            SourceEnricher.Enrich(scan, new XamlSourceScanner().Scan(o.SourcePath));
        return Finish(scan, o);
#else
        _ = o;
        stderr.WriteLine("Live scanning uses Windows UI Automation, so it only works in the Windows build of this tool.");
        stderr.WriteLine("On Windows run: dotnet run --project src/WpfLocatorDoctor.Cli -f net10.0-windows -- scan-live --exe <path>");
        return ExitUsage;
#endif
    }

    private int Finish(ScanResult scan, Options o)
    {
        Baseline? baseline = null;
        if (o.BaselinePath is not null)
        {
            if (!File.Exists(o.BaselinePath)) throw new UsageException($"Baseline file not found: {o.BaselinePath}");
            baseline = Baseline.FromScorecard(JsonReport.Load(o.BaselinePath), o.BaselinePath);
        }

        var card = Scorer.Evaluate(scan, baseline);

        Directory.CreateDirectory(o.OutDir);
        var jsonPath = Path.Combine(o.OutDir, JsonReportName);
        var mdPath = Path.Combine(o.OutDir, MarkdownReportName);
        JsonReport.Save(card, jsonPath);
        File.WriteAllText(mdPath, MarkdownReport.Render(card));

        if (!o.Quiet) stdout.WriteLine(TextSummary.Render(card));
        stdout.WriteLine($"Full report: {Path.GetFullPath(mdPath)}");
        stdout.WriteLine($"Data (use as --baseline next time): {Path.GetFullPath(jsonPath)}");

        if (o.MinScore is { } min && (card.Overall.Score ?? 0) < min)
        {
            var score = card.Overall.Score?.ToString("0.#", CultureInfo.InvariantCulture) ?? "n/a";
            stderr.WriteLine($"Score {score} is below the required minimum of {min.ToString(CultureInfo.InvariantCulture)}.");
            return ExitBelowMinScore;
        }
        return ExitOk;
    }

    private int PrintRules()
    {
        stdout.WriteLine("Checks that make up the score:");
        foreach (var c in RuleBook.Checks)
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {c.Title,-24} weight {c.Weight,3:0}   {c.Question}"));
        stdout.WriteLine();
        stdout.WriteLine("Problems the tool reports:");
        foreach (var r in RuleBook.Rules)
        {
            stdout.WriteLine($"  {r.Id}  {r.Title}");
            stdout.WriteLine($"         {r.WhyItMatters}");
        }
        return ExitOk;
    }

    public const string HelpText = """
        wpf-locator-doctor: rates how easy a WPF app is to automate, and says exactly what to fix.

        Usage:
          wpf-locator-doctor scan-source <folder or .xaml file> [options]
              Reads the project's XAML and C# files. Works on Windows, macOS and Linux.

          wpf-locator-doctor scan-live --exe <path> [--args "<app arguments>"] [options]
          wpf-locator-doctor scan-live --process <name or id> [options]
              Starts or attaches to the app and reads what test tools see. Windows only.

          wpf-locator-doctor rules
              Lists every check and problem type, in plain English.

        Options for both scans:
          --out <folder>          Where to write the reports (default: ./testability-report)
          --baseline <file.json>  A previous report; checks that its IDs are still there
          --min-score <0-100>     Exit with code 1 if the overall score is lower (for build pipelines)
          --quiet                 Print only where the reports were written

        Options for scan-live:
          --source <folder>       Project source, to learn where each ID comes from
          --uia2                  Use the older UIA2 engine (some WPF controls report better with it)
          --wait <seconds>        How long to wait for the main window (default 30)
          --max-elements <n>      Stop after this many elements per window (default 5000)
          --max-rows <n>          Rows to check per list or grid (default 50)
          --keep-open             Leave the app running if this tool started it

        Exit codes: 0 OK, 1 below --min-score, 2 usage error, 3 scan failed.
        """;
}
