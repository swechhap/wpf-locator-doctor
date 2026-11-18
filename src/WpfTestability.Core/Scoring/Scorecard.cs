using WpfTestability.Core.Model;

namespace WpfTestability.Core.Scoring;

public enum Severity { High, Medium, Low }

/// <summary>One problem, explained in plain English with a suggested fix.</summary>
public sealed record Finding(
    string RuleId,
    Severity Severity,
    string Title,
    string Message,
    string Screen,
    string? Element,
    SourceLocation? Location,
    string? Suggestion);

/// <summary>Result of one check (for example "ID coverage") on one screen or on the whole app.</summary>
public sealed class CheckResult
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required double Weight { get; init; }
    public double Earned { get; set; }
    public double Possible { get; set; }
    public bool Measured => Possible > 0;
    /// <summary>0–100, or null when there was nothing to measure.</summary>
    public double? Percent => Measured ? Math.Round(100.0 * Earned / Possible, 1) : null;
    /// <summary>Short plain-English description of what was found, e.g. "34 of 40 controls have an ID".</summary>
    public string Detail { get; set; } = "";
}

public sealed class ScoreSummary
{
    /// <summary>0–100, or null if nothing could be measured.</summary>
    public double? Score { get; set; }
    public string Grade { get; set; } = "N/A";
    public List<CheckResult> Checks { get; set; } = [];
    public int ElementCount { get; set; }
    public int InteractiveCount { get; set; }
}

public sealed class ScreenScore
{
    public required string Name { get; init; }
    public string? Origin { get; init; }
    public required ScoreSummary Summary { get; init; }
    /// <summary>IDs a test could rely on (used as the baseline for the next run's stability check).</summary>
    public List<string> AutomationIds { get; set; } = [];
    public int FindingCount { get; set; }
}

/// <summary>The full testability report.</summary>
public sealed class Scorecard
{
    public string Tool { get; set; } = "wpf-testability";
    public string ToolVersion { get; set; } = typeof(Scorecard).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public ScanMode Mode { get; set; }
    public string Target { get; set; } = "";
    public string? BaselineFile { get; set; }
    public ScoreSummary Overall { get; set; } = new();
    public List<ScreenScore> Screens { get; set; } = [];
    public List<Finding> Findings { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}
