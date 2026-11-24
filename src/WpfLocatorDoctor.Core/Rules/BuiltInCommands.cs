using System.Text.RegularExpressions;

namespace WpfLocatorDoctor.Core.Rules;

/// <summary>
/// The text WPF shows for its built-in commands, which becomes the name of a menu item that has no header.
/// The English texts come from WPF's own source (the RoutedUICommand classes and their resource strings).
/// </summary>
internal static partial class BuiltInCommands
{
    /// <summary>In the order WPF's CommandConverter searches them for a bare name such as Command="Stop".</summary>
    private static readonly (string Class, HashSet<string> Members)[] Classes =
    [
        ("NavigationCommands", new HashSet<string>(StringComparer.Ordinal)
        {
            "BrowseBack", "BrowseForward", "BrowseHome", "BrowseStop", "DecreaseZoom", "Favorites", "FirstPage",
            "GoToPage", "IncreaseZoom", "LastPage", "NavigateJournal", "NextPage", "PreviousPage", "Refresh",
            "Search", "Zoom",
        }),
        ("ApplicationCommands", new HashSet<string>(StringComparer.Ordinal)
        {
            "CancelPrint", "Close", "ContextMenu", "Copy", "CorrectionList", "Cut", "Delete", "Find", "Help", "New",
            "NotACommand", "Open", "Paste", "Print", "PrintPreview", "Properties", "Redo", "Replace", "Save",
            "SaveAs", "SelectAll", "Stop", "Undo",
        }),
        ("ComponentCommands", new HashSet<string>(StringComparer.Ordinal)
        {
            "ExtendSelectionDown", "ExtendSelectionLeft", "ExtendSelectionRight", "ExtendSelectionUp", "MoveDown",
            "MoveFocusBack", "MoveFocusDown", "MoveFocusForward", "MoveFocusPageDown", "MoveFocusPageUp",
            "MoveFocusUp", "MoveLeft", "MoveRight", "MoveToEnd", "MoveToHome", "MoveToPageDown", "MoveToPageUp",
            "MoveUp", "ScrollByLine", "ScrollPageDown", "ScrollPageLeft", "ScrollPageRight", "ScrollPageUp",
            "SelectToEnd", "SelectToHome", "SelectToPageDown", "SelectToPageUp",
        }),
        ("EditingCommands", new HashSet<string>(StringComparer.Ordinal)
        {
            "AlignCenter", "AlignJustify", "AlignLeft", "AlignRight", "ApplyBackground", "ApplyFontFamily",
            "ApplyFontSize", "ApplyForeground", "Backspace", "CorrectSpellingError", "DecreaseFontSize",
            "DecreaseIndentation", "Delete", "DeleteNextWord", "DeletePreviousWord", "EnterLineBreak",
            "EnterParagraphBreak", "IgnoreSpellingError", "IncreaseFontSize", "IncreaseIndentation",
            "MoveDownByLine", "MoveDownByPage", "MoveDownByParagraph", "MoveLeftByCharacter", "MoveLeftByWord",
            "MoveRightByCharacter", "MoveRightByWord", "MoveToDocumentEnd", "MoveToDocumentStart", "MoveToLineEnd",
            "MoveToLineStart", "MoveUpByLine", "MoveUpByPage", "MoveUpByParagraph", "SelectDownByLine",
            "SelectDownByPage", "SelectDownByParagraph", "SelectLeftByCharacter", "SelectLeftByWord",
            "SelectRightByCharacter", "SelectRightByWord", "SelectToDocumentEnd", "SelectToDocumentStart",
            "SelectToLineEnd", "SelectToLineStart", "SelectUpByLine", "SelectUpByPage", "SelectUpByParagraph",
            "TabBackward", "TabForward", "ToggleBold", "ToggleBullets", "ToggleInsert", "ToggleItalic",
            "ToggleNumbering", "ToggleSubscript", "ToggleSuperscript", "ToggleUnderline",
        }),
        ("MediaCommands", new HashSet<string>(StringComparer.Ordinal)
        {
            "BoostBass", "ChannelDown", "ChannelUp", "DecreaseBass", "DecreaseMicrophoneVolume", "DecreaseTreble",
            "DecreaseVolume", "FastForward", "IncreaseBass", "IncreaseMicrophoneVolume", "IncreaseTreble",
            "IncreaseVolume", "MuteMicrophoneVolume", "MuteVolume", "NextTrack", "Pause", "Play", "PreviousTrack",
            "Record", "Rewind", "Select", "Stop", "ToggleMicrophoneOnOff", "TogglePlayPause",
        }),
    ];

    /// <summary>Texts that aren't just the command's name split into words.</summary>
    private static readonly Dictionary<string, string> SpecialTexts = new(StringComparer.Ordinal)
    {
        ["NavigationCommands.BrowseBack"] = "Back",
        ["NavigationCommands.BrowseForward"] = "Forward",
        ["NavigationCommands.BrowseHome"] = "Home",
        ["NavigationCommands.BrowseStop"] = "Stop",
        ["ApplicationCommands.NotACommand"] = "Not a Command",
        ["MediaCommands.ToggleMicrophoneOnOff"] = "Toggle Microphone OnOff",
        ["ComponentCommands.SelectToPageDown"] = "Select To PageDown",
        ["ComponentCommands.SelectToPageUp"] = "Select To PageUp",
    };

    [GeneratedRegex(@"^\{\s*x:Static\s+(?:Member\s*=\s*)?([^\s}]+)\s*\}$")]
    private static partial Regex XStaticReference();

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
    private static partial Regex WordBoundary();

    /// <summary>
    /// The text of Command="Copy", "ApplicationCommands.Copy" or "{x:Static ApplicationCommands.Copy}". Null for any
    /// other command, such as a binding or the app's own command, whose text can't be known from the XAML.
    /// </summary>
    public static string? TextOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var value = command.Trim();
        var xStatic = XStaticReference().Match(value);
        if (xStatic.Success) value = xStatic.Groups[1].Value;

        var dot = value.LastIndexOf('.');
        if (dot < 0 && xStatic.Success) return null; // only a plain Command="Copy" is looked up by bare name
        var owner = dot < 0 ? null : value[(value.IndexOf(':') + 1)..dot];
        var member = value[(dot + 1)..];

        foreach (var (name, members) in Classes)
        {
            if ((owner is null || owner == name) && members.Contains(member)) return TextOf(name, member);
        }
        return null;
    }

    private static string TextOf(string commandClass, string member)
    {
        if (SpecialTexts.TryGetValue($"{commandClass}.{member}", out var text)) return text;
        return commandClass == "EditingCommands" ? member : WordBoundary().Replace(member, " ");
    }
}
