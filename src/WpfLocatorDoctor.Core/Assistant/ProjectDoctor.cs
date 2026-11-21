using System.Globalization;
using System.Text.Json;
using WpfLocatorDoctor.Core.Model;
using WpfLocatorDoctor.Core.Reporting;
using WpfLocatorDoctor.Core.Scoring;
using WpfLocatorDoctor.Core.Source;

namespace WpfLocatorDoctor.Core.Assistant;

/// <summary>
/// The questions an AI assistant (through the MCP server) can ask about a WPF project's source code.
/// Each method scans the project, checks the request, and returns a short, plain-English answer as data.
/// Mistakes in the request (a wrong path, an unknown screen) throw <see cref="DoctorInputException"/> with a helpful message.
/// </summary>
public static class ProjectDoctor
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;
    private const int TopProblemCount = 5;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] Grades = ["A", "B", "C", "D", "F"];

    // ------------------------------------------------------------------ score

    /// <summary>Scores the project and, if <paramref name="saveReportTo"/> is given, writes the Markdown and JSON reports there.</summary>
    public static ScoreReport Score(string path, string? baselineReport = null, string? saveReportTo = null)
    {
        var scan = ScanSource(path);
        Baseline? baseline = null;
        if (!string.IsNullOrWhiteSpace(baselineReport))
        {
            var (previous, file) = LoadBaseline(baselineReport);
            baseline = Baseline.FromScorecard(previous, file);
        }
        var card = Scorer.Evaluate(scan, baseline);

        SavedReportFiles? saved = null;
        if (!string.IsNullOrWhiteSpace(saveReportTo))
        {
            var dir = Path.GetFullPath(saveReportTo);
            Directory.CreateDirectory(dir);
            var json = Path.Combine(dir, JsonReport.DefaultFileName);
            var md = Path.Combine(dir, MarkdownReport.DefaultFileName);
            JsonReport.Save(card, json);
            File.WriteAllText(md, MarkdownReport.Render(card));
            saved = new SavedReportFiles(md, json);
        }

        var counts = Count(card.Findings);
        var screens = card.Screens
            .OrderBy(s => s.Summary.Score ?? 101)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => new ScreenLine(s.Name, s.Origin, s.Summary.Score, s.Summary.Grade, s.Summary.InteractiveCount, s.FindingCount))
            .ToList();

        var summary = card.Overall.Score is null
            ? $"Nothing to score in {card.Target}: no interactive controls were found."
            : string.Create(Inv,
                $"Grade {card.Overall.Grade}, score {Fmt(card.Overall.Score)}/100 across {screens.Count} screen(s) with {card.Overall.InteractiveCount} controls. " +
                $"{RuleBook.GradeMeaning(card.Overall.Grade)} {ProblemsSentence(counts)}") +
              (screens.Count > 1 ? string.Create(Inv, $" Worst screen: {screens[0].Name} ({Fmt(screens[0].Score)}).") : "") +
              (saved is null ? "" : $" Reports saved to {Path.GetDirectoryName(saved.JsonReport)}.");

        return new ScoreReport(
            summary,
            card.Target,
            card.Overall.Score,
            card.Overall.Grade,
            RuleBook.GradeMeaning(card.Overall.Grade),
            card.Overall.Checks.Select(c => new CheckLine(c.Title, c.Weight, c.Percent, c.Detail)).ToList(),
            screens,
            counts,
            card.Findings.Take(TopProblemCount).Select(ToLine).ToList(),
            card.Notes,
            saved);
    }

    // ------------------------------------------------------------------ problems

    /// <summary>Lists problems, optionally only for one screen, at or above a severity, or of one rule (e.g. "WT001").</summary>
    public static ProblemList ListProblems(string path, string? screen = null, string? minSeverity = null, string? rule = null, int limit = DefaultLimit)
    {
        var scan = ScanSource(path);
        var card = Scorer.Evaluate(scan);
        var screenName = screen is null ? null : ResolveScreen(scan, screen).Name;
        var severity = ParseSeverity(minSeverity);
        var ruleId = ParseRule(rule);
        limit = ClampLimit(limit);

        var matches = card.Findings
            .Where(f => screenName is null || f.Screen == screenName)
            .Where(f => f.Severity <= severity)
            .Where(f => ruleId is null || f.RuleId == ruleId)
            .ToList();
        var shown = matches.Take(limit).Select(ToLine).ToList();

        var filters = new List<string>();
        if (screenName is not null) filters.Add($"on {screenName}");
        if (minSeverity is not null) filters.Add($"of {severity.ToString().ToLowerInvariant()} severity or worse");
        if (ruleId is not null) filters.Add($"of type {ruleId} ({RuleBook.FindRule(ruleId)!.Title})");
        var scope = filters.Count == 0 ? "" : " " + string.Join(", ", filters);
        var summary = matches.Count == 0
            ? $"No problems found{scope}."
            : $"{ProblemsSentence(Count(matches)).TrimEnd('.')}{scope}." +
              (shown.Count < matches.Count ? $" Showing the first {shown.Count} (most severe first); raise the limit to see more." : "");
        return new ProblemList(summary, matches.Count, shown.Count, shown);
    }

    // ------------------------------------------------------------------ fixes

    /// <summary>
    /// Plans the XAML (and C#) changes that fix missing, borrowed, generated and duplicated IDs and unidentifiable list rows.
    /// Each fix names a unique AutomationId and, where possible, the exact line before and after.
    /// </summary>
    public static FixPlan SuggestFixes(string path, string? screen = null, int limit = DefaultLimit)
    {
        var scan = ScanSource(path);
        var root = ProjectRoot(scan);
        var screens = screen is null ? scan.Screens : [ResolveScreen(scan, screen)];
        limit = ClampLimit(limit);

        var cache = new Dictionary<string, string[]?>(StringComparer.Ordinal);
        string[]? ReadLines(string relative)
        {
            if (cache.TryGetValue(relative, out var lines)) return lines;
            try
            {
                var full = Path.GetFullPath(Path.Combine(root, relative));
                lines = File.Exists(full) ? File.ReadAllText(full).Split('\n').Select(l => l.TrimEnd('\r')).ToArray() : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lines = null;
            }
            cache[relative] = lines;
            return lines;
        }

        var fixes = screens.SelectMany(s => XamlFixPlanner.PlanScreen(s, ReadLines)).ToList();
        if (screen is null) fixes.AddRange(XamlFixPlanner.PlanCustomControls(scan));
        var shown = fixes.Take(limit).ToList();

        var byKind = fixes.GroupBy(f => f.Kind).Select(g => $"{g.Count()} {g.Key}");
        var scope = screen is null ? "" : $" on {screens[0].Name}";
        var summary = fixes.Count == 0
            ? $"Nothing to fix{scope}: every control has a unique AutomationId set on purpose."
            : $"{fixes.Count} fix(es){scope}: {string.Join(", ", byKind)}. " +
              "Apply each 'edit' by replacing the 'before' line with the 'after' line (file paths are relative to projectFolder), then score again." +
              (shown.Count < fixes.Count ? $" Showing the first {shown.Count}; raise the limit to see more." : "");
        return new FixPlan(summary, root, fixes.Count, shown.Count, shown);
    }

    // ------------------------------------------------------------------ baseline

    /// <summary>Compares the project now with an earlier JSON report: score change, IDs that disappeared and IDs that were added.</summary>
    public static BaselineComparison CompareToBaseline(string path, string baselineReport)
    {
        if (string.IsNullOrWhiteSpace(baselineReport))
            throw new DoctorInputException("Give the path of an earlier testability-report.json to compare with.");
        var scan = ScanSource(path);
        var (previous, file) = LoadBaseline(baselineReport);
        var baseline = Baseline.FromScorecard(previous, file);
        var card = Scorer.Evaluate(scan, baseline);

        // Compare like with like: if the baseline report was made without its own baseline, its score has no "Stability" check,
        // so score the project now without one too. (The IDs that disappeared are still found from the full check.)
        var baselineMeasuredStability = previous.Overall.Checks.Any(c => c.Id == RuleBook.Stability.Id && c.Measured);
        var comparable = baselineMeasuredStability ? card : Scorer.Evaluate(scan);
        var comparableScreens = comparable.Screens.GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var missingByScreen = card.Findings
            .Where(f => f.RuleId == RuleBook.ChangedSinceBaseline.Id)
            .GroupBy(f => f.Screen, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Element!).ToList(), StringComparer.Ordinal);

        var changes = new List<ScreenChange>();
        foreach (var now in card.Screens)
        {
            if (!baseline.IdsByScreen.TryGetValue(now.Name, out var before)) continue;
            var oldScore = previous.Screens.First(s => s.Name == now.Name).Summary.Score;
            var missing = missingByScreen.GetValueOrDefault(now.Name) ?? [];
            var added = now.AutomationIds.Where(id => !before.Contains(id)).ToList();
            changes.Add(new ScreenChange(now.Name, oldScore, comparableScreens[now.Name].Summary.Score, missing, added));
        }

        var currentNames = card.Screens.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var onlyBefore = baseline.IdsByScreen.Keys.Where(n => !currentNames.Contains(n)).Order(StringComparer.Ordinal).ToList();
        var onlyNow = card.Screens.Select(s => s.Name).Where(n => !baseline.IdsByScreen.ContainsKey(n)).ToList();

        var notes = new List<string>();
        if (previous.Mode != ScanMode.Source)
            notes.Add("The baseline came from a live scan, where screens are named after window titles, so screen names may not match this source scan.");
        if (changes.Count == 0)
            notes.Add("No screen in the baseline has the same name as a screen now, so IDs could not be compared.");

        double? change = comparable.Overall.Score is { } a && previous.Overall.Score is { } b ? Math.Round(a - b, 1) : null;
        var missingCount = changes.Sum(c => c.MissingIds.Count);
        var newCount = changes.Sum(c => c.NewIds.Count);

        var direction = change switch
        {
            null => "could not be compared",
            > 0 => string.Create(Inv, $"went up by {change}"),
            < 0 => string.Create(Inv, $"went down by {-change}"),
            _ => "did not change",
        };
        // A disappeared ID breaks existing tests, so it leads the summary whatever the score did.
        var missingText = missingCount == 0
            ? "No IDs from the baseline disappeared."
            : $"{missingCount} ID(s) from the baseline disappeared, so tests that use them will fail: " +
              string.Join("; ", changes.Where(c => c.MissingIds.Count > 0).Select(c => $"{c.Screen}: {string.Join(", ", c.MissingIds.Take(10))}")) + ".";
        var scoreText = string.Create(Inv,
            $"Score {direction}: {Fmt(previous.Overall.Score)} ({previous.Overall.Grade}) then, {Fmt(comparable.Overall.Score)} ({comparable.Overall.Grade}) now.");
        var summary = missingCount == 0 ? $"{scoreText} {missingText}" : $"{missingText} {scoreText}";
        summary += $" {newCount} new ID(s) were added.";
        if (onlyBefore.Count > 0) summary += $" Screens gone since the baseline: {string.Join(", ", onlyBefore)}.";

        return new BaselineComparison(summary, file, previous.Overall.Score, previous.Overall.Grade, comparable.Overall.Score, comparable.Overall.Grade,
            change, missingCount, newCount, changes, onlyBefore, onlyNow, notes);
    }

    // ------------------------------------------------------------------ rules

    public static RuleGuide Rules() => new(
        RuleBook.Checks,
        RuleBook.Rules,
        Grades.Select(g => $"{g}: {RuleBook.GradeMeaning(g)} ({GradeRange(g)})").ToList(),
        [
            "WPF reports the first of these as a control's AutomationId:",
            "1. AutomationProperties.AutomationId: set on purpose for tests. Best.",
            "2. x:Uid: meant for translation tools and often generated (Button_12), so it changes.",
            "3. x:Name: the developer's own name for the control, which may be renamed without knowing a test uses it.",
        ]);

    // ------------------------------------------------------------------ helpers

    /// <summary>Scans a folder, project file or single .xaml file, with friendly errors for wrong paths.</summary>
    public static ScanResult ScanSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new DoctorInputException("Give the path of a WPF project folder, .csproj or .xaml file.");
        var full = Path.GetFullPath(path.Trim());
        if (!File.Exists(full) && !Directory.Exists(full))
            throw new DoctorInputException(
                $"Not found: {full}. Use an absolute path; a relative path is read from the folder the server started in ({Environment.CurrentDirectory}).");
        var scan = new XamlSourceScanner().Scan(full);
        if (scan.Screens.Count == 0 && scan.Notes.Contains("No XAML files were found."))
            throw new DoctorInputException($"No .xaml files were found in {full}. Point to the folder of the WPF project (the one with the .csproj).");
        return scan;
    }

    /// <summary>Finds a screen by its name ("MainWindow") or file ("Views/MainWindow.xaml"), ignoring case.</summary>
    public static ScreenScan ResolveScreen(ScanResult scan, string screen)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(screen);
        var wanted = screen.Trim();
        static string Norm(string s) => s.Replace('\\', '/');
        var match =
            scan.Screens.FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase)) ??
            scan.Screens.FirstOrDefault(s => s.Origin is not null && string.Equals(Norm(s.Origin), Norm(wanted), StringComparison.OrdinalIgnoreCase)) ??
            scan.Screens.FirstOrDefault(s => s.Origin is not null &&
                (string.Equals(Path.GetFileName(Norm(s.Origin)), wanted, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Path.GetFileNameWithoutExtension(Norm(s.Origin)), wanted, StringComparison.OrdinalIgnoreCase)));
        return match ?? throw new DoctorInputException(
            $"No screen called '{wanted}'. Screens found: {string.Join(", ", scan.Screens.Select(s => s.Name))}.");
    }

    private static string ProjectRoot(ScanResult scan) =>
        Directory.Exists(scan.Target) ? scan.Target : Path.GetDirectoryName(scan.Target)!;

    private static (Scorecard Card, string File) LoadBaseline(string baselineReport)
    {
        var full = Path.GetFullPath(baselineReport.Trim());
        if (!File.Exists(full))
            throw new DoctorInputException($"Baseline report not found: {full}. Save one first with score_source's saveReportTo, or the CLI's --out.");
        try
        {
            return (JsonReport.Load(full), full);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            throw new DoctorInputException($"{full} isn't a wpf-locator-doctor JSON report: {ex.Message}", ex);
        }
    }

    private static Severity ParseSeverity(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "low" or "all" => Severity.Low,
        "medium" => Severity.Medium,
        "high" => Severity.High,
        _ => throw new DoctorInputException($"Unknown severity '{value}'. Use high, medium or low."),
    };

    private static string? ParseRule(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var id = value.Trim().ToUpperInvariant();
        return RuleBook.FindRule(id)?.Id ?? throw new DoctorInputException(
            $"Unknown problem type '{value}'. Use one of: {string.Join(", ", RuleBook.Rules.Select(r => r.Id))}.");
    }

    private static int ClampLimit(int limit) => limit < 1 ? DefaultLimit : Math.Min(limit, MaxLimit);

    private static ProblemCounts Count(List<Finding> findings) => new(
        findings.Count,
        findings.Count(f => f.Severity == Severity.High),
        findings.Count(f => f.Severity == Severity.Medium),
        findings.Count(f => f.Severity == Severity.Low));

    private static string ProblemsSentence(ProblemCounts c) => c.Total == 0
        ? "No problems found."
        : $"{c.Total} problem(s): {c.High} high, {c.Medium} medium, {c.Low} low.";

    internal static ProblemLine ToLine(Finding f) => new(
        f.RuleId,
        f.Severity.ToString().ToLowerInvariant(),
        f.Title,
        f.Message,
        f.Screen,
        f.Element,
        f.Location?.File,
        f.Location?.Line is > 0 ? f.Location.Line : null,
        f.Suggestion);

    private static string Fmt(double? score) => score?.ToString("0.#", Inv) ?? "n/a";

    private static string GradeRange(string grade) => grade switch
    {
        "A" => "90 and above",
        "B" => "75 to 89",
        "C" => "60 to 74",
        "D" => "40 to 59",
        _ => "below 40",
    };
}
