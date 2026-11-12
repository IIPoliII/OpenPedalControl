using System.Collections.Generic;
using System.Linq;
using OpenPedalControl.Hid;
using OpenPedalControl.Model;
using OpenPedalControl.ViewModels;

namespace OpenPedalControl.Tests;

/// <summary>Stand-in for a 12-bit pedal controller whose axis values the test sets by hand.</summary>
internal sealed class FakeDevice : IAxisSource
{
    private static readonly (ushort usage, string name)[] Layout =
    {
        (0x30, "X"), (0x31, "Y"), (0x32, "Z"), (0x33, "Rx"), (0x34, "Ry"), (0x35, "Rz"), (0x36, "Slider"),
    };

    private readonly int?[] _values;

    public FakeDevice(int axisCount = 7)
    {
        Axes = Layout.Take(axisCount)
            .Select((a, index) => new HidAxis(index, 0x01, a.usage, 0, 16, 0, 4095, a.name))
            .ToList();
        _values = new int?[axisCount];
    }

    public IReadOnlyList<HidAxis> Axes { get; }

    public HidAxis this[string name] => Axes.First(a => a.Name == name);

    public void Set(string axis, int value) => _values[this[axis].Index] = value;

    /// <summary>A full report: every axis gets a value, as on the real controller.</summary>
    public void Report(int x, int y, int z)
    {
        for (int i = 0; i < _values.Length; i++) _values[i] = 0;
        Set("X", x);
        Set("Y", y);
        Set("Z", z);
    }

    public bool TryGetValue(int axisIndex, out int value)
    {
        value = _values[axisIndex] ?? 0;
        return _values[axisIndex].HasValue;
    }

    /// <summary>Builds the pedal cards the app would show for this device.</summary>
    public List<ControlViewModel> CreateControls(string deviceName = "HE SIM PEDALS", int vendorId = 0x10C4, int productId = 0x8B02)
    {
        var options = new List<AxisOption> { new AxisOption(null) };
        options.AddRange(Axes.Select(a => new AxisOption(a)));
        return DeviceTemplates.CreateDefault(deviceName, vendorId, productId, Axes)
            .Select(settings => new ControlViewModel(settings, this, options))
            .ToList();
    }
}
