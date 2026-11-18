using System.Globalization;
using System.Text;
using WpfTestability.Core.Model;
using WpfTestability.Core.Scoring;

namespace WpfTestability.Core.Reporting;

/// <summary>Plain-English report for people (readable on GitHub, in a pull request, or in any Markdown viewer).</summary>
public static partial class MarkdownReport
{
    private const int MaxDetailedFindings = 500;

    public static string Render(Scorecard card)
    {
        var sb = new StringBuilder();
        var o = card.Overall;
        var high = card.Findings.Count(f => f.Severity == Severity.High);
        var medium = card.Findings.Count(f => f.Severity == Severity.Medium);
        var low = card.Findings.Count(f => f.Severity == Severity.Low);

        sb.AppendLine("# WPF testability report");
        sb.AppendLine();
        sb.AppendLine($"**Overall grade: {o.Grade} ({Fmt(o.Score)} / 100).** {RuleBook.GradeMeaning(o.Grade)}");
        sb.AppendLine();
        sb.AppendLine($"- **Scanned:** `{card.Target}` ({(card.Mode == ScanMode.Source ? "XAML and C# source files" : "the running app")})");
        sb.AppendLine($"- **Date:** {card.GeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC");
        sb.AppendLine($"- **Screens:** {card.Screens.Count} · **Controls a user interacts with:** {o.InteractiveCount}");
        sb.AppendLine($"- **Problems found:** {card.Findings.Count} ({high} high, {medium} medium, {low} low)");
        if (card.BaselineFile is not null) sb.AppendLine($"- **Compared with baseline:** `{card.BaselineFile}`");
        sb.AppendLine();

        sb.AppendLine("## How the score is made");
        sb.AppendLine();
        sb.AppendLine("Each check gives a percentage. The overall score is the weighted average of the checks that could be measured.");
        sb.AppendLine();
        sb.AppendLine("| Check | What it asks | Weight | Result | Details |");
        sb.AppendLine("|---|---|---:|---:|---|");
        foreach (var c in o.Checks)
        {
            var info = RuleBook.Checks.First(x => x.Id == c.Id);
            sb.AppendLine($"| {c.Title} | {Cell(info.Question)} | {c.Weight:0} | {(c.Measured ? Fmt(c.Percent) + "%" : "not measured")} | {Cell(c.Detail)} |");
        }
        sb.AppendLine();
        sb.AppendLine("Grades: **A** 90+ · **B** 75–89 · **C** 60–74 · **D** 40–59 · **F** below 40.");
        sb.AppendLine();

        if (card.Screens.Count > 0)
        {
            sb.AppendLine("## Screens (worst first)");
            sb.AppendLine();
            sb.AppendLine("| Screen | Grade | Score | Interactive controls | Problems | Where |");
            sb.AppendLine("|---|:---:|---:|---:|---:|---|");
            foreach (var s in card.Screens.OrderBy(s => s.Summary.Score ?? 101).ThenBy(s => s.Name, StringComparer.Ordinal))
            {
                sb.AppendLine($"| {Cell(s.Name)} | {s.Summary.Grade} | {Fmt(s.Summary.Score)} | {s.Summary.InteractiveCount} | {s.FindingCount} | {Cell(s.Origin ?? "")} |");
            }
            sb.AppendLine();
        }

        if (card.Findings.Count > 0)
        {
            sb.AppendLine("## Most common problems");
            sb.AppendLine();
            sb.AppendLine("| Problem | How many | Why it matters |");
            sb.AppendLine("|---|---:|---|");
            foreach (var g in card.Findings.GroupBy(f => f.RuleId).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                var rule = RuleBook.FindRule(g.Key);
                sb.AppendLine($"| {g.Key}: {Cell(rule?.Title ?? g.First().Title)} | {g.Count()} | {Cell(rule?.WhyItMatters ?? "")} |");
            }
            sb.AppendLine();

            sb.AppendLine("## Problems and fixes, by screen");
            sb.AppendLine();
            var shown = 0;
            foreach (var group in card.Findings.GroupBy(f => f.Screen).OrderBy(g => g.Key == "(code)" ? 0 : 1).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                var screen = card.Screens.FirstOrDefault(s => s.Name == group.Key);
                sb.AppendLine(group.Key == "(code)"
                    ? "### Custom controls (C# code)"
                    : $"### {group.Key}{(screen is null ? "" : $" (grade {screen.Summary.Grade}, {Fmt(screen.Summary.Score)})")}");
                sb.AppendLine();
                foreach (var f in group)
                {
                    if (shown++ >= MaxDetailedFindings) break;
                    var loc = f.Location is null || f.Location.Line == 0 ? "" : $" (`{f.Location}`)";
                    sb.AppendLine($"- **{f.Severity}: {f.Title}.** {f.Message}{loc}");
                    if (!string.IsNullOrWhiteSpace(f.Suggestion))
                        sb.AppendLine($"  - Fix: {FormatSuggestion(f.Suggestion!)}");
                }
                sb.AppendLine();
                if (shown >= MaxDetailedFindings)
                {
                    sb.AppendLine($"_Only the first {MaxDetailedFindings} problems are listed here. The JSON report has all of them._");
                    sb.AppendLine();
                    break;
                }
            }
        }
        else
        {
            sb.AppendLine("No problems found.");
            sb.AppendLine();
        }

        if (card.Notes.Count > 0)
        {
            sb.AppendLine("## Notes");
            sb.AppendLine();
            foreach (var n in card.Notes) sb.AppendLine($"- {n}");
            sb.AppendLine();
        }

        sb.AppendLine("## Limits of this report");
        sb.AppendLine();
        if (card.Mode == ScanMode.Source)
        {
            sb.AppendLine("- A source scan reads XAML and C# files. It can't see controls created only in code, and it can only guess at third-party controls from their names.");
            sb.AppendLine("- Run a live scan on Windows (`wpf-testability scan-live`) to see exactly what test tools see.");
        }
        else
        {
            sb.AppendLine("- A live scan only sees the windows that were open, and only the list rows that were on screen. Open other screens and scan again to cover them.");
            sb.AppendLine("- Add `--source <project folder>` to learn where each ID comes from (set on purpose or borrowed from x:Name).");
        }
        return sb.ToString();
    }

    [System.Text.RegularExpressions.GeneratedRegex("""(?:AutomationProperties\.\w+|Target)="[^"]*"|protected override AutomationPeer OnCreateAutomationPeer\(\)[^;]*;""")]
    private static partial System.Text.RegularExpressions.Regex CodeFragment();

    /// <summary>Shows XAML and C# snippets as code so they can be copied.</summary>
    private static string FormatSuggestion(string suggestion)
    {
        var start = suggestion.IndexOf('<');
        var end = suggestion.LastIndexOf('>');
        if (start >= 0 && end > start)
            return $"{suggestion[..start]}`{suggestion[start..(end + 1)]}`{suggestion[(end + 1)..]}";

        return CodeFragment().Replace(suggestion, m => $"`{m.Value}`");
    }

    private static string Fmt(double? v) => v is null ? "–" : v.Value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Cell(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
