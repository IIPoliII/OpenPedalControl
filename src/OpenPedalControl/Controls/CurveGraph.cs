using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace OpenPedalControl.Controls;

/// <summary>
/// Plots output against pedal travel. Windows' calibration is two straight segments that meet at 50 %
/// output, so the curve is fully described by <see cref="Knee"/>: the travel at which 50 % is reached.
/// </summary>
public sealed class CurveGraph : FrameworkElement
{
    private const double LeftMargin = 30, BottomMargin = 18, TopMargin = 6, RightMargin = 10;

    private static readonly Typeface LabelTypeface = new Typeface("Segoe UI");
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x66, 0x70, 0x85)));
    private static readonly Brush PlotBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)));
    private static readonly Brush DefaultStroke = Frozen(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)));
    private static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xDD, 0xE1, 0xE7)), 1));
    private static readonly Pen LinearPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xC5, 0xCB, 0xD5)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen CrosshairPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0x1B, 0x22, 0x30)), 1));

    public static readonly DependencyProperty KneeProperty = Register(nameof(Knee), 0.5);
    public static readonly DependencyProperty PointsProperty = Register<IReadOnlyList<double>?>(nameof(Points), null);

    /// <summary>Evenly spaced output values (0..100). When set, drawn instead of the one-bend curve.</summary>
    public IReadOnlyList<double>? Points { get => (IReadOnlyList<double>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public static readonly DependencyProperty InputProperty = Register(nameof(Input), double.NaN);
    public static readonly DependencyProperty OutputProperty = Register(nameof(Output), double.NaN);
    public static readonly DependencyProperty StrokeProperty = Register(nameof(Stroke), DefaultStroke);

    /// <summary>Share of the travel (0..1) at which the output reaches 50 %.</summary>
    public double Knee { get => (double)GetValue(KneeProperty); set => SetValue(KneeProperty, value); }

    /// <summary>Current pedal travel, 0..1. NaN hides the live marker.</summary>
    public double Input { get => (double)GetValue(InputProperty); set => SetValue(InputProperty, value); }

    /// <summary>Current output, 0..1.</summary>
    public double Output { get => (double)GetValue(OutputProperty); set => SetValue(OutputProperty, value); }

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    private static DependencyProperty Register<T>(string name, T defaultValue) =>
        DependencyProperty.Register(name, typeof(T), typeof(CurveGraph),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth - LeftMargin - RightMargin;
        double height = ActualHeight - TopMargin - BottomMargin;
        if (width < 20 || height < 20) return;

        var plot = new Rect(LeftMargin, TopMargin, width, height);
        Point At(double x, double y) => new Point(plot.Left + plot.Width * x, plot.Bottom - plot.Height * y);

        dc.DrawRectangle(PlotBrush, null, plot);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int i = 0; i <= 2; i++)
        {
            double share = i / 2.0;
            Point onX = At(share, 0), onY = At(0, share);
            dc.DrawLine(GridPen, new Point(Snap(onX.X), plot.Top), new Point(Snap(onX.X), plot.Bottom));
            dc.DrawLine(GridPen, new Point(plot.Left, Snap(onY.Y)), new Point(plot.Right, Snap(onY.Y)));

            var label = new FormattedText((i * 50).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, LabelTypeface, 10, LabelBrush, pixelsPerDip);
            dc.DrawText(label, new Point(onX.X - label.Width / 2, plot.Bottom + 3));
            dc.DrawText(label, new Point(plot.Left - label.Width - 5, onY.Y - label.Height / 2));
        }

        dc.DrawLine(LinearPen, At(0, 0), At(1, 1));

        Brush stroke = Stroke ?? DefaultStroke;
        // Either the multi-point curve of the virtual output, or Windows' two segments meeting at 50 %.
        IReadOnlyList<double>? points = Points;
        Point[] vertices;
        if (points != null && points.Count >= 2)
        {
            vertices = new Point[points.Count];
            for (int i = 0; i < points.Count; i++) vertices[i] = At(i / (double)(points.Count - 1), Clamp01(points[i] / 100.0));
        }
        else
        {
            double knee = Knee < 0.01 ? 0.01 : Knee > 0.99 ? 0.99 : Knee;
            vertices = new[] { At(0, 0), At(knee, 0.5), At(1, 1) };
        }

        var curve = new StreamGeometry();
        using (StreamGeometryContext context = curve.Open())
        {
            context.BeginFigure(vertices[0], false, false);
            for (int i = 1; i < vertices.Length; i++) context.LineTo(vertices[i], true, true);
        }
        curve.Freeze();
        dc.DrawGeometry(null, new Pen(stroke, 2) { LineJoin = PenLineJoin.Round }, curve);
        for (int i = points != null ? 0 : 1; i < (points != null ? vertices.Length : 2); i++)
            dc.DrawEllipse(stroke, null, vertices[i], 3.5, 3.5);

        if (!double.IsNaN(Input) && !double.IsNaN(Output))
        {
            Point live = At(Clamp01(Input), Clamp01(Output));
            dc.DrawLine(CrosshairPen, new Point(Snap(live.X), plot.Top), new Point(Snap(live.X), plot.Bottom));
            dc.DrawLine(CrosshairPen, new Point(plot.Left, Snap(live.Y)), new Point(plot.Right, Snap(live.Y)));
            dc.DrawEllipse(Brushes.White, new Pen(stroke, 2), live, 4.5, 4.5);
        }
    }

    private static double Snap(double value) => System.Math.Round(value) + 0.5;
    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
}
