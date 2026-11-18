using System.Globalization;
using System.Text;
using WpfLocatorDoctor.Core.Scoring;

namespace WpfLocatorDoctor.Core.Reporting;

/// <summary>Short summary for the terminal.</summary>
public static class TextSummary
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Render(Scorecard card, int maxFindings = 10)
    {
        ArgumentNullException.ThrowIfNull(card);
        var sb = new StringBuilder();
        var o = card.Overall;
        sb.AppendLine(Inv, $"Overall: grade {o.Grade}, score {Fmt(o.Score)}/100. {RuleBook.GradeMeaning(o.Grade)}");
        sb.AppendLine();

        foreach (var c in o.Checks)
            sb.AppendLine(Inv, $"  {c.Title,-24} {(c.Measured ? Fmt(c.Percent) + "%" : "n/a"),8}   {c.Detail}");
        sb.AppendLine();

        if (card.Screens.Count > 0)
        {
            sb.AppendLine("Screens (worst first):");
            foreach (var s in card.Screens.OrderBy(s => s.Summary.Score ?? 101).ThenBy(s => s.Name, StringComparer.Ordinal))
                sb.AppendLine(Inv, $"  {s.Summary.Grade,-3} {Fmt(s.Summary.Score),6}  {s.Name}  ({s.Summary.InteractiveCount} controls, {s.FindingCount} problems)");
            sb.AppendLine();
        }

        var high = card.Findings.Count(f => f.Severity == Severity.High);
        var medium = card.Findings.Count(f => f.Severity == Severity.Medium);
        var low = card.Findings.Count(f => f.Severity == Severity.Low);
        sb.AppendLine(Inv, $"Problems: {card.Findings.Count} ({high} high, {medium} medium, {low} low)");
        foreach (var f in card.Findings.Take(maxFindings))
        {
            var loc = f.Location is null || f.Location.Line == 0 ? "" : $"  [{f.Location}]";
            sb.AppendLine(Inv, $"  {f.Severity.ToString().ToUpperInvariant(),-6} {f.RuleId} {f.Message}{loc}");
        }
        if (card.Findings.Count > maxFindings)
            sb.AppendLine(Inv, $"  … and {card.Findings.Count - maxFindings} more in the full report.");

        foreach (var n in card.Notes)
            sb.AppendLine(Inv, $"Note: {n}");
        return sb.ToString();
    }

    private static string Fmt(double? v) => v is null ? "–" : v.Value.ToString("0.#", CultureInfo.InvariantCulture);
}
