using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PracticeApp.Controls;

/// <summary>
/// A 1–5 star rating drawn by hand.
/// PROBLEM (WT006): it derives from Control and doesn't override OnCreateAutomationPeer,
/// so UI Automation can't see it. The AutomationId set on it in XAML goes nowhere.
/// </summary>
public class StarRating : Control
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(StarRating),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, Math.Clamp(value, 0, Maximum));
    }

    public int Maximum => 5;

    public StarRating()
    {
        Focusable = true;
        MinWidth = 130;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Max(8, ActualHeight - 4);
        for (var i = 0; i < Maximum; i++)
        {
            var brush = i < Value ? Brushes.Gold : Brushes.LightGray;
            dc.DrawEllipse(brush, new Pen(Brushes.DarkGoldenrod, 1),
                new Point(2 + size / 2 + i * (size + 4), ActualHeight / 2), size / 2, size / 2);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var size = Math.Max(8, ActualHeight - 4);
        Value = (int)(e.GetPosition(this).X / (size + 4)) + 1;
        Focus();
        base.OnMouseLeftButtonDown(e);
    }
}
