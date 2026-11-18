using System.Windows.Controls;

namespace PracticeApp.Controls;

/// <summary>
/// A layout panel. Panels have no automation peer, so test tools skip the panel itself
/// and only see its children. PROBLEM (WT007): an AutomationId set on it is lost.
/// </summary>
public class ColorSwatchPanel : WrapPanel
{
}
