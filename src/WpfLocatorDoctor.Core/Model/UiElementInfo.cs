namespace WpfLocatorDoctor.Core.Model;

/// <summary>Where an element's AutomationId value came from.</summary>
public enum IdOrigin
{
    /// <summary>No AutomationId at all.</summary>
    None,
    /// <summary>Set on purpose with AutomationProperties.AutomationId.</summary>
    Explicit,
    /// <summary>Set with AutomationProperties.AutomationId bound to data (for example one ID per list row).</summary>
    DataBound,
    /// <summary>Borrowed from x:Name / Name (WPF's last fallback).</summary>
    XName,
    /// <summary>Borrowed from a hand-written x:Uid (WPF's second fallback, used for translation).</summary>
    Uid,
    /// <summary>Looks machine-generated (for example "Button_12", a GUID, or only digits).</summary>
    GeneratedLooking,
    /// <summary>The ID exists but its origin cannot be told (live scan without source code).</summary>
    Unknown,
}

public sealed record SourceLocation(string File, int Line, int Column)
{
    public override string ToString() => $"{File}:{Line}";
}

/// <summary>One UI element, as seen either in a XAML file or in the running app.</summary>
public sealed class UiElementInfo
{
    /// <summary>Screen (window, page or user control) this element belongs to.</summary>
    public required string Screen { get; init; }

    /// <summary>The XAML tag or live class name, for example "Button" or "local:StarRating".</summary>
    public required string Kind { get; init; }

    /// <summary>UI Automation control type name, for example "Button", "Edit", "Custom".</summary>
    public required string ControlType { get; init; }

    public string? AutomationId { get; set; }
    public IdOrigin IdOrigin { get; set; }

    /// <summary>The readable name (label) if one could be determined.</summary>
    public string? Name { get; set; }
    public bool HasName { get; set; }

    /// <summary>Counts toward the "ID coverage" score: something a user clicks, types into, or selects.</summary>
    public bool IsInteractive { get; set; }

    /// <summary>Lives inside a list/grid row template, so it repeats once per row at runtime.</summary>
    public bool InRepeatedTemplate { get; set; }

    /// <summary>For repeated elements and lists: can each row be told apart by an ID? Null = not applicable.</summary>
    public bool? RowsIdentifiable { get; set; }

    /// <summary>True for lists, grids and trees that show rows of data.</summary>
    public bool IsItemsHost { get; set; }

    /// <summary>False when test tools cannot see this element at all (for example a custom control with no automation peer).</summary>
    public bool VisibleToAutomation { get; set; } = true;

    /// <summary>Live scan only: an empty "custom" or "pane" element with no ID, name or children. Possibly a drawn area tools can't look into.</summary>
    public bool IsOpaque { get; set; }

    /// <summary>Live scan only: UI Automation behaviours (patterns) the element supports, e.g. "Invoke", "Value".</summary>
    public List<string> Patterns { get; set; } = [];

    /// <summary>Human-readable position, e.g. "MainWindow > Grid > StackPanel > Button".</summary>
    public string? Path { get; set; }

    public SourceLocation? Location { get; set; }

    /// <summary>The x:Name, if any (source scan only). Useful when suggesting fixes.</summary>
    public string? XName { get; set; }

    /// <summary>Extra text that hints at the element's purpose (e.g. its tooltip). Only used to suggest IDs.</summary>
    public string? Hint { get; set; }

    /// <summary>Short description for reports, e.g. Button "Save".</summary>
    public string Describe()
    {
        var label = !string.IsNullOrWhiteSpace(Name) ? $" \"{Truncate(Name!, 40)}\""
                  : !string.IsNullOrWhiteSpace(AutomationId) ? $" [{AutomationId}]"
                  : "";
        return $"{Kind}{label}";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>Everything found on one screen.</summary>
public sealed class ScreenScan
{
    public required string Name { get; init; }
    /// <summary>XAML file (source scan) or window title (live scan).</summary>
    public string? Origin { get; init; }
    public List<UiElementInfo> Elements { get; } = [];
    /// <summary>Problems the scanner itself noticed (for example an unreadable file).</summary>
    public List<string> Notes { get; } = [];
}

public enum ScanMode { Source, Live }

/// <summary>Result of scanning a project folder or a running app.</summary>
public sealed class ScanResult
{
    public required ScanMode Mode { get; init; }
    /// <summary>Folder, exe or process that was scanned.</summary>
    public required string Target { get; init; }
    public List<ScreenScan> Screens { get; } = [];
    /// <summary>Custom-control problems found by reading C# code (source scan only).</summary>
    public List<CustomControlInfo> CustomControls { get; } = [];
    /// <summary>How many times each custom control class is used in XAML.</summary>
    public Dictionary<string, int> CustomControlUsage { get; } = new(StringComparer.Ordinal);
    public List<string> Notes { get; } = [];
}

/// <summary>A custom control class found in the project's C# code.</summary>
public sealed record CustomControlInfo(
    string ClassName,
    string? BaseClass,
    string? WpfBase,
    bool OverridesAutomationPeer,
    PeerStatus Status,
    SourceLocation? Location);

public enum PeerStatus
{
    /// <summary>Has its own automation peer or inherits a working one.</summary>
    Visible,
    /// <summary>Derives from a WPF class with no automation peer and doesn't add one: invisible to test tools.</summary>
    Invisible,
    /// <summary>Derives from a layout/base class with no peer. Only a problem if tests need the element itself.</summary>
    NoPeerLayout,
    /// <summary>Base class is outside this project (e.g. a third-party library); can't tell.</summary>
    Unknown,
}
