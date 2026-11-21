namespace WpfLocatorDoctor.Core.Live;

/// <summary>What to start or attach to. Exactly one of <see cref="ExePath"/> and <see cref="Process"/> is set.</summary>
public sealed record LiveOpenRequest(string? ExePath, string? Arguments, string? Process, bool UseUia2, TimeSpan WaitForWindow);

/// <summary>
/// Starts or attaches to a running app. The Windows-only live library implements it with FlaUI; tests use fakes.
/// Problems the user can fix (no such process, the window never appeared…) are thrown as
/// <see cref="Assistant.DoctorInputException"/> with a plain-English message.
/// </summary>
public interface ILiveAppConnector
{
    ILiveApp Open(LiveOpenRequest request);
}

/// <summary>
/// One running app, as the AI assistant's live tools need it. The adapter only reads and acts; every decision
/// (what to show, which locator is best, how to click something) is made in Core.
/// </summary>
public interface ILiveApp : IDisposable
{
    /// <summary>Process name, e.g. "PracticeApp".</summary>
    string Name { get; }
    int ProcessId { get; }
    /// <summary>True when the tool started the app (so it may close it), false when it attached to one already running.</summary>
    bool StartedByTool { get; }
    bool HasExited { get; }

    /// <summary>
    /// A fresh read of every top-level window of the app. With <paramref name="includeChildren"/> false, only the windows
    /// themselves (cheap; for titles). The nodes keep a link to the real element, so they can be passed back to
    /// <see cref="Perform"/> and <see cref="CapturePng"/>.
    /// </summary>
    IReadOnlyList<ILiveNode> ReadWindows(bool includeChildren);

    /// <summary>Does one thing to an element. Throws <see cref="Assistant.DoctorInputException"/> if the app refuses.</summary>
    void Perform(ILiveNode element, LiveActionKind action, string? text);

    /// <summary>A PNG picture of the element, after bringing <paramref name="window"/> to the front.</summary>
    byte[] CapturePng(ILiveNode element, ILiveNode window);

    /// <summary>Closes the app (only called when the tool started it).</summary>
    void Close();
}

/// <summary>The ways the adapter can act on an element. Core picks one from what the element supports.</summary>
public enum LiveActionKind
{
    /// <summary>The element's own "click" behaviour (buttons, menu items, links).</summary>
    Invoke,
    /// <summary>Check or uncheck (check boxes, toggle buttons).</summary>
    Toggle,
    /// <summary>Select this item (list rows, tabs, radio buttons, combo box items).</summary>
    Select,
    Expand,
    Collapse,
    /// <summary>Replace the text in one step (text boxes).</summary>
    SetValue,
    /// <summary>Focus the element and type the text with the keyboard.</summary>
    TypeText,
    Focus,
    /// <summary>A real mouse click in the middle of the element. Last resort.</summary>
    MouseClick,
    /// <summary>In a list, combo box or tab control: select the item whose name is the text.</summary>
    SelectChildByName,
}
