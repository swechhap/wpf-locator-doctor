using WpfTestability.Core.Model;
using WpfTestability.Core.Rules;

namespace WpfTestability.Core.Scoring;

/// <summary>IDs recorded by a previous run, used to check that IDs stay the same over time.</summary>
public sealed class Baseline
{
    public required string File { get; init; }
    public required Dictionary<string, HashSet<string>> IdsByScreen { get; init; }

    public static Baseline FromScorecard(Scorecard previous, string file) => new()
    {
        File = file,
        IdsByScreen = previous.Screens.ToDictionary(
            s => s.Name,
            s => new HashSet<string>(s.AutomationIds, StringComparer.Ordinal),
            StringComparer.Ordinal),
    };
}

/// <summary>Turns a scan into scores, grades and findings.</summary>
public static class Scorer
{
    /// <summary>Raw counts for one screen. Summed across screens for the overall score.</summary>
    private sealed class Stats
    {
        public int Elements, Interactive, InteractiveWithId;
        public double QualitySum; public int QualityCount;
        public int Explicit, XName, Uid, Generated, Unknown;
        public int UniqUnits, UniqOk, DuplicateIds, ListsTotal, ListsOk;
        public int StabPossible, StabOk;
        public int VisPossible, VisOk, Invisible, Opaque;
        public int NamePossible, NameOk;

        public void Add(Stats o)
        {
            Elements += o.Elements; Interactive += o.Interactive; InteractiveWithId += o.InteractiveWithId;
            QualitySum += o.QualitySum; QualityCount += o.QualityCount;
            Explicit += o.Explicit; XName += o.XName; Uid += o.Uid; Generated += o.Generated; Unknown += o.Unknown;
            UniqUnits += o.UniqUnits; UniqOk += o.UniqOk; DuplicateIds += o.DuplicateIds; ListsTotal += o.ListsTotal; ListsOk += o.ListsOk;
            StabPossible += o.StabPossible; StabOk += o.StabOk;
            VisPossible += o.VisPossible; VisOk += o.VisOk; Invisible += o.Invisible; Opaque += o.Opaque;
            NamePossible += o.NamePossible; NameOk += o.NameOk;
        }
    }

    public static Scorecard Evaluate(ScanResult scan, Baseline? baseline = null)
    {
        var card = new Scorecard
        {
            Mode = scan.Mode,
            Target = scan.Target,
            BaselineFile = baseline?.File,
        };
        card.Notes.AddRange(scan.Notes);
        foreach (var screen in scan.Screens) card.Notes.AddRange(screen.Notes.Select(n => $"{screen.Name}: {n}"));

        var total = new Stats();
        var baselineScreensMatched = 0;

        foreach (var screen in scan.Screens)
        {
            var findings = new List<Finding>();
            HashSet<string>? baselineIds = null;
            if (baseline is not null && baseline.IdsByScreen.TryGetValue(screen.Name, out var b))
            {
                baselineIds = b;
                baselineScreensMatched++;
            }

            var stats = Analyze(screen, scan.Mode, baselineIds, findings, out var stableIds);
            total.Add(stats);

            var summary = Summarize(stats, baselineMeasured: baselineIds is not null);
            card.Screens.Add(new ScreenScore
            {
                Name = screen.Name,
                Origin = screen.Origin,
                Summary = summary,
                AutomationIds = stableIds,
                FindingCount = findings.Count,
            });
            card.Findings.AddRange(findings);
        }

        card.Findings.AddRange(CustomControlFindings(scan));

        if (baseline is not null && baselineScreensMatched == 0)
            card.Notes.Add("The baseline report has no screens with the same names as this scan, so stability could not be measured.");

        card.Overall = Summarize(total, baselineMeasured: baselineScreensMatched > 0);
        card.Findings = card.Findings
            .OrderBy(f => f.Severity)
            .ThenBy(f => f.RuleId, StringComparer.Ordinal)
            .ThenBy(f => f.Screen, StringComparer.Ordinal)
            .ThenBy(f => f.Location?.Line ?? 0)
            .ToList();
        return card;
    }

    private static Stats Analyze(ScreenScan screen, ScanMode mode, HashSet<string>? baselineIds, List<Finding> findings, out List<string> stableIds)
    {
        var s = new Stats();
        var elements = screen.Elements.Where(e => !IdHeuristics.IsTemplatePartId(e.AutomationId)).ToList();
        s.Elements = elements.Count;

        foreach (var e in elements)
        {
            var hasId = !string.IsNullOrWhiteSpace(e.AutomationId);

            // ---- coverage, quality, names (interactive controls only)
            if (e.IsInteractive)
            {
                s.Interactive++;
                if (hasId && e.VisibleToAutomation)
                {
                    s.InteractiveWithId++;
                    s.QualitySum += IdHeuristics.QualityWeight(e.IdOrigin);
                    s.QualityCount++;
                    switch (e.IdOrigin)
                    {
                        case IdOrigin.Explicit or IdOrigin.DataBound: s.Explicit++; break;
                        case IdOrigin.XName: s.XName++; break;
                        case IdOrigin.Uid: s.Uid++; break;
                        case IdOrigin.GeneratedLooking: s.Generated++; break;
                        case IdOrigin.Unknown: s.Unknown++; break;
                    }
                }

                if (e.VisibleToAutomation && NameExpected(e))
                {
                    s.NamePossible++;
                    if (e.HasName) s.NameOk++;
                }
            }

            // ---- visibility
            var invisibleMatters = !e.VisibleToAutomation && (e.IsInteractive || hasId);
            if (e.IsInteractive || invisibleMatters || e.IsOpaque)
            {
                s.VisPossible++;
                if (invisibleMatters) s.Invisible++;
                else if (e.IsOpaque) s.Opaque++;
                else s.VisOk++;
            }

            // ---- rows
            if (e.IsItemsHost && e.RowsIdentifiable is not null)
            {
                s.ListsTotal++;
                s.UniqUnits++;
                if (e.RowsIdentifiable == true) { s.ListsOk++; s.UniqOk++; }
            }

            AddElementFindings(e, mode, findings);
        }

        // ---- uniqueness of IDs that appear once per screen (not inside row templates)
        var single = elements
            .Where(e => e.VisibleToAutomation && !e.InRepeatedTemplate && !string.IsNullOrWhiteSpace(e.AutomationId))
            .GroupBy(e => e.AutomationId!, StringComparer.Ordinal)
            .ToList();
        foreach (var g in single)
        {
            var count = g.Count();
            s.UniqUnits += count;
            if (count == 1)
            {
                s.UniqOk++;
                continue;
            }

            s.DuplicateIds++;
            var first = g.First();
            var where = string.Join(", ", g.Select(x => x.Location?.Line > 0 ? $"line {x.Location.Line}" : x.Path ?? x.Kind).Distinct().Take(8));
            findings.Add(new Finding(
                RuleBook.Duplicate.Id, Severity.High, RuleBook.Duplicate.Title,
                $"\"{g.Key}\" is used {count} times on this screen ({where}).",
                screen.Name, first.Describe(), first.Location,
                "Give each element its own AutomationId. If they are in a repeated section, give that section a unique ID too."));
        }

        // ---- IDs inside row templates: fine only if rows can be told apart
        foreach (var e in elements.Where(e => e.VisibleToAutomation && e.InRepeatedTemplate && !string.IsNullOrWhiteSpace(e.AutomationId)))
        {
            if (e.RowsIdentifiable is null) continue;
            s.UniqUnits++;
            if (e.RowsIdentifiable == true) s.UniqOk++;
        }

        // ---- stability
        stableIds = elements
            .Where(e => e.VisibleToAutomation && !e.InRepeatedTemplate && !string.IsNullOrWhiteSpace(e.AutomationId) &&
                        e.IdOrigin is not IdOrigin.DataBound and not IdOrigin.GeneratedLooking)
            .Select(e => e.AutomationId!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        if (baselineIds is not null)
        {
            var current = new HashSet<string>(elements.Where(e => e.AutomationId is not null).Select(e => e.AutomationId!), StringComparer.Ordinal);
            foreach (var id in baselineIds.OrderBy(x => x, StringComparer.Ordinal))
            {
                s.StabPossible++;
                if (current.Contains(id))
                {
                    s.StabOk++;
                    continue;
                }
                findings.Add(new Finding(
                    RuleBook.ChangedSinceBaseline.Id, Severity.High, RuleBook.ChangedSinceBaseline.Title,
                    $"\"{id}\" was on this screen in the baseline but is gone now. It may have been renamed or removed.",
                    screen.Name, id, null,
                    "If the control still exists, restore its old AutomationId (or update the tests that use it)."));
            }
        }

        return s;
    }

    private static void AddElementFindings(UiElementInfo e, ScanMode mode, List<Finding> findings)
    {
        var hasId = !string.IsNullOrWhiteSpace(e.AutomationId);
        var where = e.Describe();

        if (e.IsInteractive && e.VisibleToAutomation && !hasId)
        {
            var suggested = IdHeuristics.SuggestId(e);
            var message = e.InRepeatedTemplate
                ? $"{where} (inside a list row template) has no AutomationId."
                : $"{where} has no AutomationId.";
            var suggestion = mode == ScanMode.Source
                ? $"Add AutomationProperties.AutomationId=\"{suggested}\" to this element."
                : $"Ask the developers to add AutomationProperties.AutomationId=\"{suggested}\" to this control.";
            findings.Add(new Finding(RuleBook.MissingId.Id, e.InRepeatedTemplate ? Severity.Medium : Severity.High,
                RuleBook.MissingId.Title, message, e.Screen, where, e.Location, suggestion));
        }

        if (e.IsInteractive && e.VisibleToAutomation && hasId)
        {
            switch (e.IdOrigin)
            {
                case IdOrigin.XName:
                    findings.Add(new Finding(RuleBook.BorrowedFromXName.Id, Severity.Low, RuleBook.BorrowedFromXName.Title,
                        $"{where} gets its ID \"{e.AutomationId}\" from x:Name.", e.Screen, where, e.Location,
                        $"Add AutomationProperties.AutomationId=\"{e.AutomationId}\" so renaming x:Name won't break tests."));
                    break;
                case IdOrigin.Uid:
                    findings.Add(new Finding(RuleBook.BorrowedFromUid.Id, Severity.Low, RuleBook.BorrowedFromUid.Title,
                        $"{where} gets its ID \"{e.AutomationId}\" from x:Uid.", e.Screen, where, e.Location,
                        $"Add AutomationProperties.AutomationId=\"{IdHeuristics.SuggestId(e)}\"."));
                    break;
                case IdOrigin.GeneratedLooking:
                    findings.Add(new Finding(RuleBook.GeneratedLooking.Id, Severity.Medium, RuleBook.GeneratedLooking.Title,
                        $"{where} has the ID \"{e.AutomationId}\", which looks machine-generated.", e.Screen, where, e.Location,
                        $"Replace it with a meaningful ID, e.g. AutomationProperties.AutomationId=\"{IdHeuristics.SuggestId(e)}\"."));
                    break;
            }
        }

        if (!e.VisibleToAutomation && hasId && !e.IsInteractive)
        {
            findings.Add(new Finding(RuleBook.IdOnInvisibleElement.Id, Severity.Medium, RuleBook.IdOnInvisibleElement.Title,
                $"{where} has the ID \"{e.AutomationId}\", but this element type isn't visible to UI Automation.",
                e.Screen, where, e.Location,
                "Put the ID on a child control tests actually use, or give the class an automation peer (override OnCreateAutomationPeer)."));
        }

        if (e.IsInteractive && e.VisibleToAutomation && NameExpected(e) && !e.HasName)
        {
            findings.Add(new Finding(RuleBook.MissingName.Id, Severity.Low, RuleBook.MissingName.Title,
                $"{where} has no readable name.", e.Screen, where, e.Location,
                e.ControlType switch
                {
                    "Edit" or "ComboBox" or "List" or "DataGrid" or "Tree" or "Slider" or "Document" =>
                        "Connect its label with AutomationProperties.LabeledBy=\"{Binding ElementName=TheLabel}\" (or Target on the Label), or add AutomationProperties.Name=\"…\".",
                    "Button" or "MenuItem" or "Hyperlink" =>
                        $"Add AutomationProperties.Name=\"{(e.Hint is null ? "…" : e.Hint)}\" (icon-only controls have no text for tools to read).",
                    _ => "Add AutomationProperties.Name=\"…\".",
                }));
        }

        if (e.IsItemsHost && e.RowsIdentifiable == false)
        {
            findings.Add(new Finding(RuleBook.RowsNotIdentifiable.Id, Severity.Medium, RuleBook.RowsNotIdentifiable.Title,
                $"Rows in {where} have no unique ID.", e.Screen, where, e.Location,
                mode == ScanMode.Source ? RowStyleSnippet(e.Kind) : "Ask the developers to bind each row's AutomationId to a unique field of its data (for example an order number)."));
        }

        if (e.IsOpaque)
        {
            findings.Add(new Finding(RuleBook.OpaqueElement.Id, Severity.Medium, RuleBook.OpaqueElement.Title,
                $"{where} ({e.ControlType}) shows nothing inside it to test tools.", e.Screen, where, e.Location,
                "If users interact with it, the developers need to add automation support (an automation peer) for its parts."));
        }

        if (mode == ScanMode.Live && e.IsInteractive && e.VisibleToAutomation && e.ControlType == "Button" &&
            !e.Patterns.Any(p => p is "Invoke" or "Toggle" or "ExpandCollapse"))
        {
            findings.Add(new Finding(RuleBook.MissingBehaviour.Id, Severity.Low, RuleBook.MissingBehaviour.Title,
                $"{where} is a button but doesn't support click (Invoke), toggle or expand.", e.Screen, where, e.Location,
                "Tests will have to use mouse clicks. Developers can fix this in the control's automation peer."));
        }
    }

    /// <summary>A tab strip's container rarely needs its own name; its tabs carry the labels.</summary>
    private static bool NameExpected(UiElementInfo e) => e.ControlType is not "Tab";

    private static IEnumerable<Finding> CustomControlFindings(ScanResult scan)
    {
        foreach (var c in scan.CustomControls.Where(c => c.Status == PeerStatus.Invisible))
        {
            var uses = scan.CustomControlUsage.GetValueOrDefault(c.ClassName);
            var usedText = uses > 0 ? $"It is used {uses} time(s) in XAML." : "It isn't used in any XAML file (it may be created in code).";
            yield return new Finding(
                RuleBook.InvisibleCustomControl.Id, uses > 0 ? Severity.High : Severity.Medium, RuleBook.InvisibleCustomControl.Title,
                $"{c.ClassName} derives from {c.WpfBase} and doesn't override OnCreateAutomationPeer, so test tools can't see it. {usedText}",
                "(code)", c.ClassName, c.Location,
                $"In {c.ClassName}, add: protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this); " +
                "(or a custom peer that reports the right control type and actions).");
        }
    }

    private static string RowStyleSnippet(string hostKind)
    {
        var (property, rowType) = hostKind switch
        {
            "DataGrid" => ("DataGrid.RowStyle", "DataGridRow"),
            "ListView" => ("ListView.ItemContainerStyle", "ListViewItem"),
            "TreeView" => ("TreeView.ItemContainerStyle", "TreeViewItem"),
            "ComboBox" => ("ComboBox.ItemContainerStyle", "ComboBoxItem"),
            "ItemsControl" => ("", ""),
            _ => ("ListBox.ItemContainerStyle", "ListBoxItem"),
        };
        if (property.Length == 0)
            return "Bind an ID on the root element of the ItemTemplate, e.g. AutomationProperties.AutomationId=\"{Binding Id}\" (use any field that is unique per row).";
        return $"Add <{property}><Style TargetType=\"{rowType}\"><Setter Property=\"AutomationProperties.AutomationId\" Value=\"{{Binding Id}}\"/></Style></{property}> " +
               "(use any field that is unique per row instead of Id).";
    }

    private static ScoreSummary Summarize(Stats s, bool baselineMeasured)
    {
        CheckResult Make(CheckInfo info, double earned, double possible, string detail) => new()
        {
            Id = info.Id, Title = info.Title, Weight = info.Weight, Earned = earned, Possible = possible, Detail = detail,
        };

        var qualityParts = new List<string>();
        if (s.Explicit > 0) qualityParts.Add($"{s.Explicit} set on purpose");
        if (s.XName > 0) qualityParts.Add($"{s.XName} borrowed from x:Name");
        if (s.Uid > 0) qualityParts.Add($"{s.Uid} borrowed from x:Uid");
        if (s.Generated > 0) qualityParts.Add($"{s.Generated} look generated");
        if (s.Unknown > 0) qualityParts.Add($"{s.Unknown} of unknown origin");

        var checks = new List<CheckResult>
        {
            Make(RuleBook.Coverage, s.InteractiveWithId, s.Interactive,
                s.Interactive == 0 ? "No interactive controls found." : $"{s.InteractiveWithId} of {s.Interactive} controls have an AutomationId."),
            Make(RuleBook.Quality, s.QualitySum, s.QualityCount,
                s.QualityCount == 0 ? "No IDs to judge." : string.Join(", ", qualityParts) + "."),
            Make(RuleBook.Uniqueness, s.UniqOk, s.UniqUnits,
                s.UniqUnits == 0 ? "No IDs or lists to check."
                    : $"{s.DuplicateIds} ID(s) used more than once; {s.ListsOk} of {s.ListsTotal} data lists have identifiable rows."),
            Make(RuleBook.Stability, s.StabOk, baselineMeasured ? s.StabPossible : 0,
                !baselineMeasured ? "Not measured. Run again with --baseline <previous report.json> to compare."
                    : s.StabPossible == 0 ? "The baseline had no IDs for this screen."
                    : $"{s.StabOk} of {s.StabPossible} IDs from the baseline are still present."),
            Make(RuleBook.Visibility, s.VisOk, s.VisPossible,
                s.VisPossible == 0 ? "Nothing to check." : $"{s.Invisible} invisible control(s), {s.Opaque} opaque area(s)."),
            Make(RuleBook.Names, s.NameOk, s.NamePossible,
                s.NamePossible == 0 ? "No interactive controls found." : $"{s.NameOk} of {s.NamePossible} controls have a readable name."),
        };

        var measured = checks.Where(c => c.Measured).ToList();
        double? score = measured.Count == 0
            ? null
            : Math.Round(measured.Sum(c => c.Weight * c.Earned / c.Possible) / measured.Sum(c => c.Weight) * 100, 1);

        return new ScoreSummary
        {
            Score = score,
            Grade = RuleBook.GradeFor(score),
            Checks = checks,
            ElementCount = s.Elements,
            InteractiveCount = s.Interactive,
        };
    }
}
