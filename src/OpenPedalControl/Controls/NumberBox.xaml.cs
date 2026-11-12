using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OpenPedalControl.Controls;

/// <summary>Numeric text field with spin buttons, keyboard arrows and mouse wheel support.</summary>
public partial class NumberBox : UserControl
{
    private static readonly Brush FocusBorder = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly Brush NormalBorder = new SolidColorBrush(Color.FromRgb(0xC4, 0xCA, 0xD3));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(NumberBox),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((NumberBox)d).ShowValue(), (d, value) => ((NumberBox)d).Limit((double)value)));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(NumberBox),
        new PropertyMetadata(0.0, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(NumberBox),
        new PropertyMetadata(100.0, OnRangeChanged));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(double), typeof(NumberBox),
        new PropertyMetadata(1.0));

    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(nameof(Suffix), typeof(string), typeof(NumberBox),
        new PropertyMetadata(""));

    public NumberBox()
    {
        InitializeComponent();
        Input.LostKeyboardFocus += (_, _) =>
        {
            Commit();
            Frame.BorderBrush = NormalBorder;
        };
        Input.GotKeyboardFocus += (_, _) =>
        {
            Frame.BorderBrush = FocusBorder;
            Input.SelectAll();
        };
        Input.PreviewKeyDown += OnKeyDown;
        Input.PreviewMouseWheel += OnMouseWheel;
        ShowValue();
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        d.CoerceValue(ValueProperty);
        ((NumberBox)d).ShowValue();
    }

    private double Limit(double value)
    {
        if (double.IsNaN(value)) return Minimum;
        if (Maximum < Minimum) return value;
        return value < Minimum ? Minimum : value > Maximum ? Maximum : value;
    }

    private void ShowValue()
    {
        if (Input == null) return;
        Input.Text = Math.Round(Value).ToString(CultureInfo.CurrentCulture);
    }

    private void Commit()
    {
        string text = Input.Text.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsed) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
        {
            SetCurrentValue(ValueProperty, Limit(Math.Round(parsed)));
        }
        ShowValue();
    }

    private void StepBy(int direction)
    {
        Commit();
        SetCurrentValue(ValueProperty, Limit(Value + direction * Step));
    }

    private void OnUpClick(object sender, RoutedEventArgs e) => StepBy(1);
    private void OnDownClick(object sender, RoutedEventArgs e) => StepBy(-1);

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Commit();
                Input.SelectAll();
                e.Handled = true;
                break;
            case Key.Escape:
                ShowValue();
                Input.SelectAll();
                e.Handled = true;
                break;
            case Key.Up:
                StepBy(1);
                e.Handled = true;
                break;
            case Key.Down:
                StepBy(-1);
                e.Handled = true;
                break;
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Only while editing, so scrolling the window never changes a value by accident.
        if (!Input.IsKeyboardFocused) return;
        StepBy(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }
}
