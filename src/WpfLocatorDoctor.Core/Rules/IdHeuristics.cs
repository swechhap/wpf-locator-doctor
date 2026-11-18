using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WpfLocatorDoctor.Core.Model;

namespace WpfLocatorDoctor.Core.Rules;

/// <summary>Rules of thumb for judging and suggesting AutomationIds.</summary>
public static partial class IdHeuristics
{
    // "Button_12", "TextBlock_3": the pattern `msbuild /t:updateuid` produces. Positional, so it shifts when the screen changes.
    [GeneratedRegex(@"^[A-Za-z]+_\d+$")]
    private static partial Regex UpdateUidPattern();

    [GeneratedRegex(@"^\{?[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}\}?$")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex DigitsOnly();

    [GeneratedRegex(@"^[0-9a-fA-F]{16,}$")]
    private static partial Regex LongHex();

    /// <summary>True when an ID looks machine-made and is unlikely to stay the same between builds or runs.</summary>
    public static bool LooksGenerated(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        id = id.Trim();
        return UpdateUidPattern().IsMatch(id) || GuidPattern().IsMatch(id) || DigitsOnly().IsMatch(id) || LongHex().IsMatch(id);
    }

    /// <summary>Internal parts of WPF control templates, e.g. "PART_EditableTextBox". Tests should address the outer control instead.</summary>
    public static bool IsTemplatePartId(string? id) =>
        id is not null && id.StartsWith("PART_", StringComparison.Ordinal);

    /// <summary>How trustworthy an ID is, from 0 (useless) to 1 (ideal), based on where it came from.</summary>
    public static double QualityWeight(IdOrigin origin) => origin switch
    {
        IdOrigin.Explicit => 1.0,
        IdOrigin.DataBound => 1.0,
        IdOrigin.Unknown => 0.85,
        IdOrigin.XName => 0.7,
        IdOrigin.Uid => 0.5,
        IdOrigin.GeneratedLooking => 0.1,
        _ => 0.0,
    };

    public static string DescribeOrigin(IdOrigin origin) => origin switch
    {
        IdOrigin.Explicit => "set on purpose (AutomationProperties.AutomationId)",
        IdOrigin.DataBound => "set from data, one per row",
        IdOrigin.XName => "borrowed from x:Name",
        IdOrigin.Uid => "borrowed from x:Uid",
        IdOrigin.GeneratedLooking => "looks machine-generated",
        IdOrigin.Unknown => "present (origin unknown)",
        _ => "missing",
    };

    /// <summary>Suggests an AutomationId for an element that has none, e.g. "SaveButton" for a Button labelled "Save".</summary>
    public static string SuggestId(UiElementInfo element)
    {
        var typeSuffix = element.Kind switch
        {
            "TextBox" or "PasswordBox" or "RichTextBox" => "Input",
            "ComboBox" => "ComboBox",
            "ListBox" or "ListView" => "List",
            "DataGrid" => "Grid",
            "TreeView" => "Tree",
            "TabControl" => "Tabs",
            "TabItem" => "Tab",
            "MenuItem" => "MenuItem",
            "CheckBox" => "CheckBox",
            "RadioButton" => "Option",
            "Hyperlink" => "Link",
            "Slider" => "Slider",
            "DatePicker" => "DatePicker",
            "ToggleButton" or "RepeatButton" or "Button" => "Button",
            _ => StripPrefix(element.Kind),
        };

        var basis = !string.IsNullOrWhiteSpace(element.XName) && !LooksGenerated(element.XName) ? element.XName
                  : element.HasName && IsUsableText(element.Name) ? element.Name
                  : IsUsableText(element.Hint) ? element.Hint
                  : null;

        if (basis is null)
            return $"{StripPrefix(element.Screen)}{typeSuffix}";

        var core = ToPascalIdentifier(basis);
        if (core.Length == 0) return $"{StripPrefix(element.Screen)}{typeSuffix}";
        return core.EndsWith(typeSuffix, StringComparison.OrdinalIgnoreCase) ? core : core + typeSuffix;
    }

    private static bool IsUsableText(string? s) =>
        !string.IsNullOrWhiteSpace(s) && !s.StartsWith('{') && !s.StartsWith('(');

    /// <summary>
    /// Cleans a WPF label for display: removes access-key underscores ("E_xit" → "Exit", "__" stays "_")
    /// and a trailing colon ("Search:" → "Search").
    /// </summary>
    public static string CleanLabel(string text)
    {
        var s = text.Trim();
        if (s.StartsWith('{')) return s;
        s = s.Replace("__", "\u0001", StringComparison.Ordinal)
             .Replace("_", "", StringComparison.Ordinal)
             .Replace("\u0001", "_", StringComparison.Ordinal)
             .Trim();
        return s.TrimEnd(':', '：').Trim();
    }

    private static string StripPrefix(string kind)
    {
        var i = kind.IndexOf(':');
        return i >= 0 ? kind[(i + 1)..] : kind;
    }

    /// <summary>"Save & close…" → "SaveClose". Keeps letters and digits only.</summary>
    public static string ToPascalIdentifier(string text)
    {
        var sb = new StringBuilder();
        var upperNext = true;
        foreach (var ch in CleanLabel(text).Replace("_", " "))
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(upperNext ? char.ToUpper(ch, CultureInfo.InvariantCulture) : ch);
                upperNext = false;
            }
            else
            {
                upperNext = true;
            }
            if (sb.Length >= 40) break;
        }
        var s = sb.ToString();
        if (s.Length > 0 && char.IsDigit(s[0])) s = "Id" + s;
        return s;
    }
}
