using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FamilyStudio.Revit.UI;

/// <summary>
/// Tracked capitals. WPF has no letter-spacing, so Caps upper-cases its label and sets a hair
/// space between letters, which reads as roughly 8 percent tracking at label sizes.
/// </summary>
public sealed class Caps : TextBlock
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(Caps), new PropertyMetadata("", (d, e) => ((Caps)d).Update()));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    private void Update()
    {
        var text = (Label ?? "").ToUpperInvariant();
        Text = Track(text);
        System.Windows.Automation.AutomationProperties.SetName(this, text);
    }

    public static string Track(string text)
    {
        var builder = new StringBuilder(text.Length * 2);
        for (var i = 0; i < text.Length; i++)
        {
            builder.Append(text[i]);
            if (i < text.Length - 1) builder.Append(' ');
        }
        return builder.ToString();
    }
}

/// <summary>Attached state shared by several templates.</summary>
public static class Studio
{
    /// <summary>On a dimension: the value is checked (ink) rather than proposed (construction blue).</summary>
    public static readonly DependencyProperty CheckedProperty = DependencyProperty.RegisterAttached(
        "Checked", typeof(bool), typeof(Studio), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetChecked(DependencyObject element) => (bool)element.GetValue(CheckedProperty);
    public static void SetChecked(DependencyObject element, bool value) => element.SetValue(CheckedProperty, value);
}

/// <summary>
/// The drafting line: a hairline across the title block while Family Studio works, with a short
/// ink stroke travelling along it, like a pen being drawn along a straightedge.
/// </summary>
public sealed class DraftingLine : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(DraftingLine), new PropertyMetadata(false, (d, e) => ((DraftingLine)d).Toggle((bool)e.NewValue)));

    public static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        nameof(Phase), typeof(double), typeof(DraftingLine), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(DraftingLine), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeBrushProperty = DependencyProperty.Register(
        nameof(StrokeBrush), typeof(Brush), typeof(DraftingLine), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public double Phase { get => (double)GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush StrokeBrush { get => (Brush)GetValue(StrokeBrushProperty); set => SetValue(StrokeBrushProperty, value); }

    public DraftingLine()
    {
        Height = 2;
        SnapsToDevicePixels = true;
    }

    private void Toggle(bool active)
    {
        if (!active || !SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(PhaseProperty, null);
            InvalidateVisual();
            return;
        }
        BeginAnimation(PhaseProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromSeconds(1.8)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        dc.DrawRectangle(LineBrush, null, new Rect(0, ActualHeight - 1, width, 1));
        if (!IsActive) return;
        var length = Math.Max(40, width * 0.18);
        var x = -length + Phase * (width + length);
        dc.DrawRectangle(StrokeBrush, null, new Rect(Math.Max(0, x), 0, Math.Max(0, Math.Min(width, x + length) - Math.Max(0, x)), ActualHeight));
    }
}

public sealed class VisibleWhen : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            bool b => b,
            string s => s.Length > 0,
            int n => n > 0,
            null => false,
            _ => true
        };
        if (parameter is string expected) visible = string.Equals(value?.ToString(), expected, StringComparison.Ordinal);
        return visible ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Binds a radio button to an enum or string value: IsChecked="{Binding Mode, Converter={StaticResource Is}, ConverterParameter=Build}".</summary>
public sealed class IsValue : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null) return Binding.DoNothing;
        return targetType.IsEnum ? Enum.Parse(targetType, parameter.ToString()!) : parameter.ToString()!;
    }
}
