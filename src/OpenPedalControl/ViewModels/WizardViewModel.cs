using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using OpenPedalControl.Hid;
using OpenPedalControl.Model;

namespace OpenPedalControl.ViewModels;

public enum WizardItemState
{
    Pending,
    Active,
    Done,
    Skipped,
}

public sealed class WizardResult
{
    public WizardResult(ControlViewModel control, HidAxis axis, int rest, int full)
    {
        Control = control;
        Axis = axis;
        Rest = rest;
        Full = full;
    }

    public ControlViewModel Control { get; }
    public HidAxis Axis { get; }
    public int Rest { get; }
    public int Full { get; }
}

/// <summary>One pedal in the wizard's overview row.</summary>
public sealed class WizardItem : ObservableObject
{
    private WizardItemState _state;
    private double _level = double.NaN;
    private string _detail = "";

    public WizardItem(ControlViewModel control) => Control = control;

    public ControlViewModel Control { get; }
    public string Name => Control.Name;
    public Brush Accent => Control.Accent;

    public WizardItemState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(IsActive));
            Raise(nameof(IsDone));
        }
    }

    public bool IsActive => State == WizardItemState.Active;
    public bool IsDone => State == WizardItemState.Done;
    public double Level { get => _level; set => Set(ref _level, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    internal HidAxis? Axis { get; set; }
    internal int Rest { get; set; }
    internal int Full { get; set; }
}

/// <summary>
/// Guided calibration: first the resting position of everything, then each pedal is pressed in turn.
/// Whichever axis moves during a step is taken to be that pedal, so the wiring never has to be known,
/// and the furthest position reached is recorded, so the pedal does not have to be held while clicking.
/// </summary>
public sealed class WizardViewModel : ObservableObject
{
    private const int ReleaseStep = -1;

    private readonly IAxisSource _reader;
    private readonly int[] _rest;
    private readonly int[] _peakValue;
    private readonly int[] _peakDeviation;
    private readonly bool[] _taken;
    private int _step = ReleaseStep;
    private string _title = "";
    private string _instruction = "";
    private string _hint = "";
    private bool _canGoNext;

    public WizardViewModel(IAxisSource reader, IEnumerable<ControlViewModel> controls)
    {
        _reader = reader;
        int axes = reader.Axes.Count;
        _rest = new int[axes];
        _peakValue = new int[axes];
        _peakDeviation = new int[axes];
        _taken = new bool[axes];

        foreach (ControlViewModel control in controls.OrderBy(c => Order(c.Kind)))
            Items.Add(new WizardItem(control) { Detail = "Waiting" });
        UpdateTexts();
    }

    public ObservableCollection<WizardItem> Items { get; } = new ObservableCollection<WizardItem>();

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Instruction { get => _instruction; private set => Set(ref _instruction, value); }
    public string Hint { get => _hint; private set => Set(ref _hint, value); }
    public bool CanGoNext { get => _canGoNext; private set => Set(ref _canGoNext, value); }

    public bool IsPressStep => _step >= 0 && _step < Items.Count;
    public bool IsFinished => _step >= Items.Count;
    public string NextText => IsFinished ? "Save" : "Next";

    public IReadOnlyList<WizardResult> Results => Items
        .Where(i => i.State == WizardItemState.Done && i.Axis != null)
        .Select(i => new WizardResult(i.Control, i.Axis!, i.Rest, i.Full))
        .ToList();

    private static int Order(ControlKind kind) => kind switch
    {
        ControlKind.Throttle => 0,
        ControlKind.Brake => 1,
        ControlKind.Clutch => 2,
        ControlKind.Handbrake => 3,
        _ => 4,
    };

    /// <summary>Called on every frame while the wizard is open.</summary>
    public void Tick()
    {
        if (_step == ReleaseStep)
        {
            bool hasData = _reader.Axes.Any(a => _reader.TryGetValue(a.Index, out _));
            CanGoNext = hasData;
            Hint = hasData ? "" : "Waiting for the device to report. This can take a few seconds; touching a pedal speeds it up.";
            return;
        }

        if (!IsPressStep) return;

        HidAxis? candidate = null;
        foreach (HidAxis axis in _reader.Axes)
        {
            if (_taken[axis.Index] || !_reader.TryGetValue(axis.Index, out int value)) continue;
            int deviation = Math.Abs(value - _rest[axis.Index]);
            if (deviation > _peakDeviation[axis.Index])
            {
                _peakDeviation[axis.Index] = deviation;
                _peakValue[axis.Index] = value;
            }
            if (candidate == null || _peakDeviation[axis.Index] > _peakDeviation[candidate.Index]) candidate = axis;
        }

        WizardItem item = Items[_step];
        bool detected = candidate != null && _peakDeviation[candidate.Index] >= Threshold(candidate);
        if (detected && candidate != null && _reader.TryGetValue(candidate.Index, out int current))
        {
            item.Level = (current - candidate.LogicalMin) / (double)Math.Max(1, candidate.LogicalMax - candidate.LogicalMin);
            item.Detail = $"Axis {candidate.Name}  ·  now {current}  ·  furthest {_peakValue[candidate.Index]}";
        }
        else
        {
            item.Level = double.NaN;
            item.Detail = "Waiting for a firm press";
        }
        CanGoNext = detected;
    }

    /// <summary>Movement below this is treated as sensor noise rather than a pedal being pressed.</summary>
    private static int Threshold(HidAxis axis) => Math.Max(16, (int)((axis.LogicalMax - (long)axis.LogicalMin) * 0.04));

    public void Next()
    {
        if (!CanGoNext && !IsFinished) return;

        if (_step == ReleaseStep)
        {
            foreach (HidAxis axis in _reader.Axes)
                _rest[axis.Index] = _reader.TryGetValue(axis.Index, out int value) ? value : axis.LogicalMin;
        }
        else if (IsPressStep)
        {
            HidAxis axis = _reader.Axes
                .Where(a => !_taken[a.Index])
                .OrderByDescending(a => _peakDeviation[a.Index])
                .First();
            WizardItem item = Items[_step];
            item.Axis = axis;
            item.Rest = _rest[axis.Index];
            item.Full = _peakValue[axis.Index];
            item.State = WizardItemState.Done;
            item.Level = double.NaN;
            item.Detail = $"Axis {axis.Name}  ·  {item.Rest} → {item.Full}";
            _taken[axis.Index] = true;
        }
        BeginStep(_step + 1);
    }

    /// <summary>Leaves the current pedal as it was.</summary>
    public void Skip()
    {
        if (!IsPressStep) return;
        WizardItem item = Items[_step];
        item.State = WizardItemState.Skipped;
        item.Level = double.NaN;
        item.Detail = "Skipped";
        BeginStep(_step + 1);
    }

    private void BeginStep(int step)
    {
        _step = step;
        for (int i = 0; i < _peakDeviation.Length; i++)
        {
            _peakDeviation[i] = 0;
            _peakValue[i] = _rest[i];
        }
        if (IsPressStep) Items[_step].State = WizardItemState.Active;
        CanGoNext = IsFinished && Results.Count > 0;
        Hint = "";
        UpdateTexts();
        Raise(nameof(IsPressStep));
        Raise(nameof(IsFinished));
        Raise(nameof(NextText));
    }

    private void UpdateTexts()
    {
        int total = Items.Count + 1;
        if (_step == ReleaseStep)
        {
            Title = $"Step 1 of {total}";
            Instruction = Items.Any(i => i.Control.Kind == ControlKind.Handbrake) && Items.Count == 1
                ? "Let go of the handbrake, then click Next."
                : "Take your feet off all pedals, then click Next.";
        }
        else if (IsPressStep)
        {
            WizardItem item = Items[_step];
            Title = $"Step {_step + 2} of {total}";
            Instruction = item.Control.Kind switch
            {
                ControlKind.Brake => "Press the brake as hard as you want 100 % braking to be, let go, then click Next.",
                ControlKind.Handbrake => "Pull the handbrake all the way, let go, then click Next.",
                ControlKind.Generic => $"Move “{item.Name}” through its full travel, let go, then click Next.",
                _ => $"Press the {item.Name.ToLowerInvariant()} all the way down, let go, then click Next.",
            };
        }
        else
        {
            int done = Results.Count;
            Title = "Finished";
            Instruction = done > 0
                ? "Click Save to store the new calibration in Windows."
                : "Nothing was calibrated. Close this window to keep everything as it was.";
        }
    }
}
