using WpfLocatorDoctor.Core.Scoring;

namespace WpfLocatorDoctor.Core.Assistant;

// Answers returned by ProjectDoctor. They are plain data (text, numbers and lists) so they turn into tidy JSON
// for an AI assistant, and every one starts with a one-paragraph plain-English Summary.

/// <summary>Something the caller asked for can't be done as asked (wrong path, unknown screen…). The message says why, in plain English.</summary>
public sealed class DoctorInputException : Exception
{
    public DoctorInputException() { }
    public DoctorInputException(string message) : base(message) { }
    public DoctorInputException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Answer to "how testable is this project?".</summary>
public sealed record ScoreReport(
    string Summary,
    string Target,
    double? Score,
    string Grade,
    string GradeMeaning,
    IReadOnlyList<CheckLine> Checks,
    IReadOnlyList<ScreenLine> Screens,
    ProblemCounts Problems,
    IReadOnlyList<ProblemLine> TopProblems,
    IReadOnlyList<string> Notes,
    SavedReportFiles? SavedReports);

/// <summary>One check of the score, e.g. "ID coverage 80.6%: 25 of 31 controls have an AutomationId."</summary>
public sealed record CheckLine(string Check, double Weight, double? Percent, string Detail);

/// <summary>One screen's score. Screens are listed worst first.</summary>
public sealed record ScreenLine(string Name, string? File, double? Score, string Grade, int Controls, int Problems);

public sealed record ProblemCounts(int Total, int High, int Medium, int Low);

/// <summary>One problem with its fix. Severity is "high", "medium" or "low".</summary>
public sealed record ProblemLine(
    string Rule,
    string Severity,
    string Title,
    string Message,
    string Screen,
    string? Element,
    string? File,
    int? Line,
    string? Fix);

public sealed record ProblemList(string Summary, int Total, int Shown, IReadOnlyList<ProblemLine> Problems);

/// <summary>Where the full reports were written (the JSON one can be used as a baseline next time).</summary>
public sealed record SavedReportFiles(string MarkdownReport, string JsonReport);

/// <summary>Ready-to-apply changes to the app's XAML (and C#) that would fix its locator problems.</summary>
public sealed record FixPlan(string Summary, string ProjectFolder, int Total, int Shown, IReadOnlyList<XamlFix> Fixes);

/// <summary>
/// One fix. Kind is one of:
/// "add-id" (no AutomationId), "pin-id" (ID borrowed from x:Name or x:Uid: write the same value explicitly so tests keep working),
/// "replace-generated-id" (machine-made ID), "fix-duplicate-id" (same ID used twice), "row-ids" (list rows can't be told apart),
/// "automation-peer" (custom control invisible to test tools; C# change).
/// </summary>
public sealed record XamlFix(
    string Kind,
    string Rule,
    string Screen,
    string Element,
    string? File,
    int? Line,
    string? CurrentId,
    string? SuggestedId,
    string Instruction,
    LineEdit? Edit);

/// <summary>Replace line <see cref="Line"/> (1-based) of the file, which currently reads <see cref="Before"/>, with <see cref="After"/>.</summary>
public sealed record LineEdit(int Line, string Before, string After);

/// <summary>Answer to "what changed since the last report?".</summary>
public sealed record BaselineComparison(
    string Summary,
    string BaselineFile,
    double? BaselineScore,
    string BaselineGrade,
    double? CurrentScore,
    string CurrentGrade,
    double? Change,
    int MissingIdCount,
    int NewIdCount,
    IReadOnlyList<ScreenChange> Screens,
    IReadOnlyList<string> ScreensOnlyInBaseline,
    IReadOnlyList<string> NewScreens,
    IReadOnlyList<string> Notes);

/// <summary>
/// How one screen changed. MissingIds were in the baseline but are gone now (tests using them will fail).
/// NewIds were added since the baseline.
/// </summary>
public sealed record ScreenChange(string Screen, double? BaselineScore, double? CurrentScore, IReadOnlyList<string> MissingIds, IReadOnlyList<string> NewIds);

/// <summary>Every check and problem type, in plain English.</summary>
public sealed record RuleGuide(IReadOnlyList<CheckInfo> Checks, IReadOnlyList<RuleInfo> Problems, IReadOnlyList<string> Grades, IReadOnlyList<string> IdOrigins);
