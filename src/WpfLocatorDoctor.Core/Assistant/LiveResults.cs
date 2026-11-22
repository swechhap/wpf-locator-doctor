using System.Text.Json.Serialization;
using WpfLocatorDoctor.Core.Live;

namespace WpfLocatorDoctor.Core.Assistant;

// Answers returned by LiveSession (the AI assistant's tools for a running app). Like ProjectDoctor's answers,
// each starts with a plain-English Summary.

/// <summary>The app the tools are working with. Windows are shown as <c>"Title" #AutomationId</c>.</summary>
public sealed record AppStatus(string Summary, string App, int ProcessId, bool StartedByTool, IReadOnlyList<string> Windows);

/// <summary>A short answer with nothing else to report.</summary>
public sealed record LiveNote(string Summary);

/// <summary>
/// What is on screen now. Each Outline line is one element, indented under its parent:
/// <c>ref Type "Name" #AutomationId = "value" [states] ⚠ problems</c>. Refs (e1, e2…) are valid until the next snapshot.
/// </summary>
public sealed record ScreenSnapshot(string Summary, IReadOnlyList<string> Windows, int Shown, int Total, IReadOnlyList<string> Outline, IReadOnlyList<string> Notes);

/// <summary>Ranked ways to find one element in a test, best first, and the AutomationId to add to the app if none is strong.</summary>
public sealed record LocatorAdvice(string Summary, string Element, string Window, IReadOnlyList<LocatorOption> Locators, string? SuggestedAutomationId, string? AppFix);

/// <summary>Does a locator find exactly one element right now? Matches and CloseMatches are outline lines with refs.</summary>
public sealed record LocatorCheck(string Summary, bool Unique, int Count, IReadOnlyList<string> Matches, IReadOnlyList<string> CloseMatches);

/// <summary>What an action did, and which windows opened or closed because of it.</summary>
public sealed record ActionResult(string Summary, string Element, string Method, IReadOnlyList<string> WindowsNow, IReadOnlyList<string> NewWindows, IReadOnlyList<string> ClosedWindows);

/// <summary>Whether the awaited condition was met, and how long it took. Element is the matching element (with a ref) when there is one.</summary>
public sealed record WaitResult(string Summary, bool Met, double Seconds, string? Element, IReadOnlyList<string> CloseMatches);

/// <summary>A picture of a window or element. The PNG goes to the assistant as an image, not as JSON.</summary>
public sealed record ScreenshotResult(string Summary, [property: JsonIgnore] byte[] Png, string? SavedTo);
