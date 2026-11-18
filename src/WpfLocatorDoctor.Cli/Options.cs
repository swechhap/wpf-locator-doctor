using System.Globalization;

namespace WpfLocatorDoctor.Cli;

/// <summary>A mistake in how the tool was called (exit code 2).</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>Command-line options shared by the scan commands.</summary>
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

    public static Options Parse(IReadOnlyList<string> args)
    {
        var o = new Options();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Count ? args[++i] : throw new UsageException($"{a} needs a value.");

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
                case "--wait": o.WaitSeconds = ParseWholeNumber(a, Next(), 1, 600); break;
                case "--max-elements": o.MaxElements = ParseWholeNumber(a, Next(), 1, 1_000_000); break;
                case "--max-rows": o.MaxRows = ParseWholeNumber(a, Next(), 1, 100_000); break;
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
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || double.IsNaN(n) || n < min || n > max)
            throw new UsageException($"{option} must be a number between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}.");
        return n;
    }

    private static int ParseWholeNumber(string option, string value, int min, int max)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < min || n > max)
            throw new UsageException($"{option} must be a whole number between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}.");
        return n;
    }
}
