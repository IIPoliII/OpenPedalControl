using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using OpenPedalControl.Hid;
using OpenPedalControl.Model;

namespace OpenPedalControl.ViewModels;

public sealed class AxisOption
{
    public AxisOption(HidAxis? axis) => Axis = axis;

    public HidAxis? Axis { get; }
    public string Label => Axis?.Name ?? "None";
}

/// <summary>One pedal card: its settings, the calibration they produce and the live readout.</summary>
public sealed class ControlViewModel : ObservableObject
{
    private readonly IAxisSource _reader;
    private AxisOption _selectedAxis;
    private AxisCalibration _computed;
    private AxisCalibration? _applied;
    private int? _raw;
    private bool _liveStale = true;

    private double _rawLevel = double.NaN;
    private double _outputLevel = double.NaN;
    private double _travel = double.NaN;
    private string _rawText = "–";
    private string _outputText = "–";

    public ControlViewModel(ControlSettings settings, IAxisSource reader, IReadOnlyList<AxisOption> axisOptions)
    {
        Settings = settings;
        _reader = reader;
        AxisOptions = axisOptions;
        Kind = settings.GetKind();
        Accent = AccentFor(Kind);
        _selectedAxis = axisOptions.FirstOrDefault(o => o.Axis != null && o.Axis.UsagePage == settings.UsagePage && o.Axis.Usage == settings.Usage)
                        ?? axisOptions[0];
        Recompute();
    }

    /// <summary>Raised whenever a setting changes the resulting calibration.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Raised when the control is moved to a different axis.</summary>
    public event EventHandler? AxisChanged;

    public ControlSettings Settings { get; }
    public ControlKind Kind { get; }
    public string Name => Settings.Name;
    public Brush Accent { get; }
    public IReadOnlyList<AxisOption> AxisOptions { get; }

    public AxisOption SelectedAxis
    {
        get => _selectedAxis;
        set
        {
            if (value == null || ReferenceEquals(value, _selectedAxis)) return;
            _selectedAxis = value;
            Settings.UsagePage = value.Axis?.UsagePage ?? 0;
            Settings.Usage = value.Axis?.Usage ?? 0;
            if (value.Axis is HidAxis axis)
            {
                Settings.RawRest = CalibrationMath.Clamp(Settings.RawRest, axis.LogicalMin, axis.LogicalMax);
                Settings.RawFull = CalibrationMath.Clamp(Settings.RawFull, axis.LogicalMin, axis.LogicalMax);
            }
            _raw = null;
            Raise();
            Raise(nameof(IsAssigned));
            Raise(nameof(AxisMinimum));
            Raise(nameof(AxisMaximum));
            Raise(nameof(RawRest));
            Raise(nameof(RawFull));
            OnSettingsChanged();
            AxisChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public HidAxis? Axis => _selectedAxis.Axis;
    public bool IsAssigned => Axis != null;
    public double AxisMinimum => Axis?.LogicalMin ?? 0;
    public double AxisMaximum => Axis?.LogicalMax ?? 0;

    public double RawRest
    {
        get => Settings.RawRest;
        set
        {
            int rounded = (int)Math.Round(value);
            if (rounded == Settings.RawRest) return;
            Settings.RawRest = rounded;
            Raise();
            OnSettingsChanged();
        }
    }

    public double RawFull
    {
        get => Settings.RawFull;
        set
        {
            int rounded = (int)Math.Round(value);
            if (rounded == Settings.RawFull) return;
            Settings.RawFull = rounded;
            Raise();
            OnSettingsChanged();
        }
    }

    public double BottomDeadzone
    {
        get => Settings.BottomDeadzone;
        set
        {
            if (value.Equals(Settings.BottomDeadzone)) return;
            Settings.BottomDeadzone = value;
            Raise();
            OnSettingsChanged();
        }
    }

    // A load cell brake has no end stop, so its upper limit is expressed as a share of the calibrated force
    // ("Max force") rather than as a deadzone. Both map onto the same stored setting.
    private bool UsesMaxForce => Kind == ControlKind.Brake;
    public string TopLabel => UsesMaxForce ? "Max force" : "Top deadzone";
    public string TopToolTip => UsesMaxForce
        ? "Share of the calibrated press that gives 100 % braking. Lower it to reach full braking with less force."
        : "Share of the travel ignored at the fully pressed end, so 100 % is always reached.";
    public double TopMinimum => UsesMaxForce ? 100 - TopLimit : 0;
    public double TopMaximum => UsesMaxForce ? 100 : TopLimit;
    private const double TopLimit = 80;
    public double BottomMaximum => 50;

    public double TopValue
    {
        get => UsesMaxForce ? 100 - Settings.TopDeadzone : Settings.TopDeadzone;
        set
        {
            double deadzone = UsesMaxForce ? 100 - value : value;
            if (deadzone.Equals(Settings.TopDeadzone)) return;
            Settings.TopDeadzone = deadzone;
            Raise();
            OnSettingsChanged();
        }
    }

    public double Midpoint
    {
        get => Settings.CurveMidpoint;
        set
        {
            double rounded = Math.Round(value);
            if (rounded.Equals(Settings.CurveMidpoint)) return;
            Settings.CurveMidpoint = rounded;
            Raise();
            Raise(nameof(CurveName));
            OnSettingsChanged();
        }
    }

    public double MidpointMinimum => CalibrationMath.MinMidpointPercent;
    public double MidpointMaximum => CalibrationMath.MaxMidpointPercent;

    public string CurveName =>
        Settings.CurveMidpoint > 50 ? "Progressive – gentle at first" :
        Settings.CurveMidpoint < 50 ? "Degressive – strong at first" :
        "Linear";

    /// <summary>True when the raw value falls as the pedal is pressed.</summary>
    public bool IsInverted => Settings.RawFull < Settings.RawRest;

    public AxisCalibration Computed => _computed;

    /// <summary>Calibration matches what Windows currently has stored for this axis.</summary>
    public bool IsApplied => IsAssigned && _applied.HasValue && _applied.Value == _computed;

    public string StateText => !IsAssigned ? "No axis assigned" : IsApplied ? "Saved in Windows" : "Not saved yet";

    public string ActiveRangeText
    {
        get
        {
            if (!IsAssigned) return "Pick an axis or run the calibration.";
            int start = IsInverted ? _computed.Max : _computed.Min;
            int end = IsInverted ? _computed.Min : _computed.Max;
            return $"0 % at raw {start}, 100 % at raw {end}" + (IsInverted ? " (inverted axis)" : "");
        }
    }

    // --- Live readout -------------------------------------------------------------------------------

    /// <summary>Raw position within the sensor's full range, 0..1 (NaN while unknown).</summary>
    public double RawLevel { get => _rawLevel; private set => Set(ref _rawLevel, value); }

    /// <summary>What a game receives, 0..1 in pedal direction (NaN while unknown).</summary>
    public double OutputLevel { get => _outputLevel; private set => Set(ref _outputLevel, value); }

    /// <summary>Position within the active range, 0..1 in pedal direction (NaN while unknown).</summary>
    public double Travel { get => _travel; private set => Set(ref _travel, value); }

    public string RawText { get => _rawText; private set => Set(ref _rawText, value); }
    public string OutputText { get => _outputText; private set => Set(ref _outputText, value); }

    /// <summary>Lower edge of the active range as a share of the sensor range (for the raw bar).</summary>
    public double ZoneLow { get; private set; }
    public double ZoneHigh { get; private set; } = 1;

    /// <summary>Share of the active travel at which the output reaches 50 %.</summary>
    public double Knee { get; private set; } = 0.5;

    public void UpdateLive()
    {
        int? raw = Axis is HidAxis axis && _reader.TryGetValue(axis.Index, out int value) ? value : (int?)null;
        if (raw == _raw && !_liveStale) return;
        _raw = raw;
        _liveStale = false;

        if (raw is int r && Axis is HidAxis a)
        {
            double cooked = CalibrationMath.CookedOutput(r, _computed);
            double output = IsInverted ? 1 - cooked : cooked;
            double span = Math.Max(1, _computed.Max - _computed.Min);
            double travel = IsInverted ? (_computed.Max - r) / span : (r - _computed.Min) / span;

            RawLevel = (r - a.LogicalMin) / (double)Math.Max(1, a.LogicalMax - a.LogicalMin);
            OutputLevel = output;
            Travel = CalibrationMath.Clamp(travel, 0, 1);
            RawText = r.ToString();
            OutputText = Math.Round(output * 100) + " %";
        }
        else
        {
            RawLevel = double.NaN;
            OutputLevel = double.NaN;
            Travel = double.NaN;
            RawText = "–";
            OutputText = "–";
        }
    }

    // --- Changes coming from outside the card -------------------------------------------------------

    public void SetApplied(AxisCalibration? applied)
    {
        _applied = applied;
        Raise(nameof(IsApplied));
        Raise(nameof(StateText));
    }

    /// <summary>Takes over the result of the calibration wizard.</summary>
    public void ApplyMeasurement(HidAxis axis, int rest, int full)
    {
        AxisOption option = AxisOptions.First(o => ReferenceEquals(o.Axis, axis));
        Settings.RawRest = rest;
        Settings.RawFull = full;
        if (ReferenceEquals(option, _selectedAxis))
        {
            Raise(nameof(RawRest));
            Raise(nameof(RawFull));
            OnSettingsChanged();
        }
        else
        {
            SelectedAxis = option; // Also announces the new raw values.
        }
    }

    public void Unassign() => SelectedAxis = AxisOptions[0];

    public void ResetTuning()
    {
        DeviceTemplates.ResetTuning(Settings);
        Raise(nameof(BottomDeadzone));
        Raise(nameof(TopValue));
        Raise(nameof(Midpoint));
        Raise(nameof(CurveName));
        OnSettingsChanged();
    }

    private void OnSettingsChanged()
    {
        Recompute();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Recompute()
    {
        if (Axis is HidAxis axis)
        {
            _computed = CalibrationMath.Compute(Settings.RawRest, Settings.RawFull, Settings.BottomDeadzone,
                Settings.TopDeadzone, Settings.CurveMidpoint, axis.LogicalMin, axis.LogicalMax);

            double range = Math.Max(1, axis.LogicalMax - axis.LogicalMin);
            double span = Math.Max(1, _computed.Max - _computed.Min);
            ZoneLow = (_computed.Min - axis.LogicalMin) / range;
            ZoneHigh = (_computed.Max - axis.LogicalMin) / range;
            Knee = IsInverted ? (_computed.Max - _computed.Center) / span : (_computed.Center - _computed.Min) / span;
        }
        else
        {
            _computed = default;
            ZoneLow = 0;
            ZoneHigh = 1;
            Knee = 0.5;
        }

        _liveStale = true;
        Raise(nameof(ZoneLow));
        Raise(nameof(ZoneHigh));
        Raise(nameof(Knee));
        Raise(nameof(IsInverted));
        Raise(nameof(ActiveRangeText));
        Raise(nameof(IsApplied));
        Raise(nameof(StateText));
    }

    private static Brush AccentFor(ControlKind kind)
    {
        Color color = kind switch
        {
            ControlKind.Clutch => Color.FromRgb(0x3B, 0x82, 0xF6),
            ControlKind.Brake => Color.FromRgb(0xE5, 0x48, 0x4D),
            ControlKind.Throttle => Color.FromRgb(0x2F, 0xA3, 0x6B),
            ControlKind.Handbrake => Color.FromRgb(0xE0, 0x8A, 0x1B),
            _ => Color.FromRgb(0x64, 0x74, 0x8B),
        };
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
