using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using WpfLocatorDoctor.Core.Live;
using WpfLocatorDoctor.Core.Model;
using WpfLocatorDoctor.Core.Rules;
using WpfLocatorDoctor.Core.Reporting;
using WpfLocatorDoctor.Core.Scoring;
using WpfLocatorDoctor.Core.Source;
using WpfLocatorDoctor.Live;

// LiveBench --pid <id> --out <folder> [--source <folder>] [--max-targets <n>]
//
// Writes, for the app that is already running:
//   live-elements.json    every element the live scan (scan-live) recorded
//   source-elements.json  every element the source scan (scan-source) recorded, if --source is given
//   live-report.json      the live scan's report (with --source, as scan-live --source makes it)
//   locators.json         for each element, every locator recommend_locators would give, for UiaDump to check

var o = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i + 1 < args.Length; i += 2) o[args[i].TrimStart('-')] = args[i + 1];
if (!o.TryGetValue("pid", out var pid) || !o.TryGetValue("out", out var outDir))
{
    Console.Error.WriteLine("Usage: LiveBench --pid <id> --out <folder> [--source <folder>] [--max-targets <n>]");
    return 2;
}
var maxTargets = o.TryGetValue("max-targets", out var mt) ? int.Parse(mt, CultureInfo.InvariantCulture) : 600;
Directory.CreateDirectory(outDir);
var json = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
};

// 1. The live scan, exactly as scan-live does it (attaching to the running process).
var live = new LiveAppScanner().Scan(new LiveScanOptions { Process = pid, WaitForWindow = TimeSpan.FromSeconds(60) });
ScanResult? source = null;
if (o.TryGetValue("source", out var sourcePath))
{
    source = new XamlSourceScanner().Scan(sourcePath);
    File.WriteAllText(Path.Combine(outDir, "source-elements.json"), JsonSerializer.Serialize(Elements(source), json));
    SourceEnricher.Enrich(live, source);
}
File.WriteAllText(Path.Combine(outDir, "live-elements.json"), JsonSerializer.Serialize(Elements(live), json));
JsonReport.Save(Scorer.Evaluate(live), Path.Combine(outDir, "live-report.json"));
Console.WriteLine($"Live scan: {live.Screens.Count} window(s), {live.Screens.Sum(s => s.Elements.Count)} elements.");

// 2. Locators, the way recommend_locators makes them (same connector and advisor the MCP server uses).
var connector = new FlaUiAppConnector();
using var app = connector.Open(new LiveOpenRequest(null, null, pid, false, TimeSpan.FromSeconds(60)));
var windows = app.ReadWindows(includeChildren: true);
var candidates = LocatorMatcher.Distinct(windows.SelectMany(w => LiveTree.Descendants(w, w)))
    .Where(l => IsTarget(l.Node))
    .ToList();
// Spread the checked elements evenly over the app when there are more than maxTargets.
var step = Math.Max(1.0, candidates.Count / (double)maxTargets);
var chosen = Enumerable.Range(0, Math.Min(maxTargets, candidates.Count)).Select(i => candidates[(int)(i * step)]).ToList();
var targets = chosen.Select(l => new
{
    Element = LiveTree.Describe(l.Node),
    l.Node.RuntimeId,
    Window = LiveTree.DescribeWindow(l.Window),
    Locators = LocatorAdvisor.Recommend(l).Select(x => new
    {
        x.Rank,
        x.Confidence,
        x.Strength,
        x.Locator.AutomationId,
        x.Locator.Name,
        x.Locator.ControlType,
        x.Locator.ClassName,
        x.Locator.Within,
        x.Locator.Index,
        x.Locator.Window,
    }).ToList(),
}).ToList();
File.WriteAllText(Path.Combine(outDir, "locators.json"), JsonSerializer.Serialize(targets, json));
Console.WriteLine($"Locators: {targets.Sum(t => t.Locators.Count)} for {targets.Count} of {candidates.Count} elements.");
return 0;

// Elements a tester would ask recommend_locators about: controls, rows, and anything with an AutomationId.
static bool IsTarget(ILiveNode n) =>
    n.ControlType is not ("TitleBar" or "ScrollBar" or "Thumb") &&
    (ControlCatalog.LiveInteractiveTypes.Contains(n.ControlType) || ControlCatalog.LiveRowTypes.Contains(n.ControlType) ||
     !string.IsNullOrWhiteSpace(n.AutomationId));

static List<object> Elements(ScanResult scan) => scan.Screens.SelectMany(s => s.Elements).Select(e => (object)new
{
    e.Screen,
    e.Kind,
    e.ControlType,
    e.AutomationId,
    e.IdOrigin,
    e.Name,
    e.IsInteractive,
    e.IsItemsHost,
    e.InRepeatedTemplate,
    e.VisibleToAutomation,
    e.Path,
    Location = e.Location?.ToString().Replace('\\', '/'),
}).ToList();
