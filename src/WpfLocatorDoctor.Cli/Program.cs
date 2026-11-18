using System.Globalization;
using WpfLocatorDoctor.Core.Model;
using WpfLocatorDoctor.Core.Reporting;
using WpfLocatorDoctor.Core.Scoring;
using WpfLocatorDoctor.Core.Source;
#if LIVE_SCAN
using WpfLocatorDoctor.Live;
#endif

namespace WpfLocatorDoctor.Cli;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitBelowMinScore = 1;
    private const int ExitUsage = 2;
    private const int ExitScanFailed = 3;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(HelpText);
            return args.Length == 0 ? ExitUsage : ExitOk;
        }
        if (args[0] is "--version" or "version")
        {
            Console.WriteLine(typeof(Scorecard).Assembly.GetName().Version?.ToString(3));
            return ExitOk;
        }

        Options opts;
        try
        {
            opts = Options.Parse(args.Skip(1).ToArray());
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Run 'wpf-locator-doctor --help' for usage.");
            return ExitUsage;
        }

        try
        {
            return args[0] switch
            {
                "scan-source" => ScanSource(opts),
                "scan-live" => ScanLive(opts),
                "rules" => PrintRules(),
                _ => Usage($"Unknown command '{args[0]}'."),
            };
        }
        catch (UsageException ex)
        {
            return Usage(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"Scan failed: {ex.Message}");
            return ExitScanFailed;
        }
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine("Run 'wpf-locator-doctor --help' for usage.");
        return ExitUsage;
    }

    private static int ScanSource(Options o)
    {
        var path = o.Positional ?? throw new UsageException("scan-source needs a folder or file to scan, e.g. 'wpf-locator-doctor scan-source ./src/MyApp'.");
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new UsageException($"Not found: {path}");

        var scan = new XamlSourceScanner().Scan(path);
        return Finish(scan, o);
    }

    private static int ScanLive(Options o)
    {
#if LIVE_SCAN
        if (o.Exe is null && o.Process is null)
            throw new UsageException("scan-live needs --exe <path to .exe> or --process <name or id>.");

        var scanner = new LiveAppScanner();
        ScanResult scan;
        try
        {
            scan = scanner.Scan(new LiveScanOptions
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
            Console.Error.WriteLine($"Could not scan the app: {ex.Message}");
            return ExitScanFailed;
        }

        if (o.SourcePath is not null)
        {
            var source = new XamlSourceScanner().Scan(o.SourcePath);
            SourceEnricher.Enrich(scan, source);
        }
        return Finish(scan, o);
#else
        Console.Error.WriteLine("Live scanning uses Windows UI Automation, so it only works in the Windows build of this tool.");
        Console.Error.WriteLine("On Windows run: dotnet run --project src/WpfLocatorDoctor.Cli -f net10.0-windows -- scan-live --exe <path>");
        return ExitUsage;
#endif
    }

    private static int Finish(ScanResult scan, Options o)
    {
        Baseline? baseline = null;
        if (o.BaselinePath is not null)
        {
            if (!File.Exists(o.BaselinePath)) throw new UsageException($"Baseline file not found: {o.BaselinePath}");
            baseline = Baseline.FromScorecard(JsonReport.Load(o.BaselinePath), o.BaselinePath);
        }

        var card = Scorer.Evaluate(scan, baseline);

        Directory.CreateDirectory(o.OutDir);
        var jsonPath = Path.Combine(o.OutDir, "testability-report.json");
        var mdPath = Path.Combine(o.OutDir, "testability-report.md");
        JsonReport.Save(card, jsonPath);
        File.WriteAllText(mdPath, MarkdownReport.Render(card));

        if (!o.Quiet) Console.WriteLine(TextSummary.Render(card));
        Console.WriteLine($"Full report: {Path.GetFullPath(mdPath)}");
        Console.WriteLine($"Data (use as --baseline next time): {Path.GetFullPath(jsonPath)}");

        if (o.MinScore is { } min && (card.Overall.Score ?? 0) < min)
        {
            Console.Error.WriteLine($"Score {card.Overall.Score?.ToString("0.#", CultureInfo.InvariantCulture) ?? "n/a"} is below the required minimum of {min.ToString(CultureInfo.InvariantCulture)}.");
            return ExitBelowMinScore;
        }
        return ExitOk;
    }

    private static int PrintRules()
    {
        Console.WriteLine("Checks that make up the score:");
        foreach (var c in RuleBook.Checks)
            Console.WriteLine($"  {c.Title,-24} weight {c.Weight,3:0}   {c.Question}");
        Console.WriteLine();
        Console.WriteLine("Problems the tool reports:");
        foreach (var r in RuleBook.Rules)
            Console.WriteLine($"  {r.Id}  {r.Title}\n         {r.WhyItMatters}");
        return ExitOk;
    }

    private const string HelpText = """
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

internal sealed class UsageException(string message) : Exception(message);

internal sealed class Options
{
    public string? Positional { get; private set; }
    public string OutDir { get; private set; } = "testability-report";
    public string? BaselinePath { get; private set; }
    public double? MinScore { get; private set; }
    public bool Quiet { get; private set; }
    public string? Exe { get; private set; }
    public string? AppArgs { get; private set; }
    public string? Process { get; private set; }
    public string? SourcePath { get; private set; }
    public bool Uia2 { get; private set; }
    public int WaitSeconds { get; private set; } = 30;
    public int MaxElements { get; private set; } = 5000;
    public int MaxRows { get; private set; } = 50;
    public bool KeepOpen { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new UsageException($"{a} needs a value.");

            switch (a)
            {
                case "--out": o.OutDir = Next(); break;
                case "--baseline": o.BaselinePath = Next(); break;
                case "--min-score": o.MinScore = ParseNumber(a, Next(), 0, 100); break;
                case "--quiet": o.Quiet = true; break;
                case "--exe": o.Exe = Next(); break;
                case "--args": o.AppArgs = Next(); break;
                case "--process": o.Process = Next(); break;
                case "--source": o.SourcePath = Next(); break;
                case "--uia2": o.Uia2 = true; break;
                case "--wait": o.WaitSeconds = (int)ParseNumber(a, Next(), 1, 600); break;
                case "--max-elements": o.MaxElements = (int)ParseNumber(a, Next(), 1, 1_000_000); break;
                case "--max-rows": o.MaxRows = (int)ParseNumber(a, Next(), 1, 100_000); break;
                case "--keep-open": o.KeepOpen = true; break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"Unknown option '{a}'.");
                    if (o.Positional is not null) throw new UsageException($"Unexpected extra argument '{a}'.");
                    o.Positional = a;
                    break;
            }
        }
        return o;
    }

    private static double ParseNumber(string option, string value, double min, double max)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < min || n > max)
            throw new UsageException($"{option} must be a number between {min} and {max}.");
        return n;
    }
}
