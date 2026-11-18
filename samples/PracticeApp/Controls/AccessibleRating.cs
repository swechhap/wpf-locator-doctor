using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;

namespace PracticeApp.Controls;

/// <summary>
/// GOOD: the same rating control, but it creates an automation peer, so test tools can see it,
/// read its ID, and set its value through the RangeValue behaviour.
/// </summary>
public class AccessibleRating : StarRating
{
    protected override AutomationPeer OnCreateAutomationPeer() => new RatingAutomationPeer(this);
}

public sealed class RatingAutomationPeer(AccessibleRating owner)
    : FrameworkElementAutomationPeer(owner), IRangeValueProvider
{
    private AccessibleRating Rating => (AccessibleRating)Owner;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

    protected override string GetClassNameCore() => nameof(AccessibleRating);

    protected override bool IsKeyboardFocusableCore() => true;

    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);

    public double Value => Rating.Value;
    public double Minimum => 0;
    public double Maximum => Rating.Maximum;
    public double SmallChange => 1;
    public double LargeChange => 1;
    public bool IsReadOnly => !Rating.IsEnabled;

    public void SetValue(double value)
    {
        if (!Rating.IsEnabled) throw new ElementNotEnabledException();
        if (value < Minimum || value > Maximum) throw new ArgumentOutOfRangeException(nameof(value));
        Rating.Value = (int)Math.Round(value);
    }
}
