using System;
using System.Windows;
using System.Windows.Media;

namespace OpenPedalControl.Controls;

/// <summary>
/// Vertical level meter. The areas below <see cref="ZoneLow"/> and above <see cref="ZoneHigh"/> are shaded
/// to show the part of the range that is ignored (deadzones).
/// </summary>
public sealed class LevelBar : FrameworkElement
{
    private const double CornerRadius = 4;

    private static readonly Brush DefaultFill = Frozen(new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)));
    private static readonly Brush TrackBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xEB, 0xF0)));
    private static readonly Brush ZoneBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x60, 0x1B, 0x22, 0x30)));
    private static readonly Pen ZoneEdgePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x1B, 0x22, 0x30)), 1));

    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), double.NaN);
    public static readonly DependencyProperty ZoneLowProperty = Register(nameof(ZoneLow), 0.0);
    public static readonly DependencyProperty ZoneHighProperty = Register(nameof(ZoneHigh), 1.0);
    public static readonly DependencyProperty FillProperty = Register(nameof(Fill), DefaultFill);

    /// <summary>Fill level from 0 to 1. NaN draws an empty bar.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double ZoneLow { get => (double)GetValue(ZoneLowProperty); set => SetValue(ZoneLowProperty, value); }
    public double ZoneHigh { get => (double)GetValue(ZoneHighProperty); set => SetValue(ZoneHighProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    private static DependencyProperty Register<T>(string name, T defaultValue) =>
        DependencyProperty.Register(name, typeof(T), typeof(LevelBar),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var bounds = new Rect(0, 0, width, height);
        dc.PushClip(new RectangleGeometry(bounds, CornerRadius, CornerRadius));
        dc.DrawRectangle(TrackBrush, null, bounds);

        if (!double.IsNaN(Value))
        {
            double filled = height * Clamp01(Value);
            dc.DrawRectangle(Fill ?? DefaultFill, null, new Rect(0, height - filled, width, filled));
        }

        double low = Clamp01(ZoneLow), high = Clamp01(ZoneHigh);
        if (low > 0)
        {
            double y = height - height * low;
            dc.DrawRectangle(ZoneBrush, null, new Rect(0, y, width, height - y));
            dc.DrawLine(ZoneEdgePen, new Point(0, Math.Round(y) + 0.5), new Point(width, Math.Round(y) + 0.5));
        }
        if (high < 1)
        {
            double y = height - height * high;
            dc.DrawRectangle(ZoneBrush, null, new Rect(0, 0, width, y));
            dc.DrawLine(ZoneEdgePen, new Point(0, Math.Round(y) - 0.5), new Point(width, Math.Round(y) - 0.5));
        }
        dc.Pop();
    }

    private static double Clamp01(double value) => double.IsNaN(value) ? 0 : value < 0 ? 0 : value > 1 ? 1 : value;
}
