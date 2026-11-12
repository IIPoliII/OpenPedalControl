using System.Collections.Generic;
using System.Linq;
using OpenPedalControl.Model;
using OpenPedalControl.ViewModels;
using Xunit;

namespace OpenPedalControl.Tests;

public class ControlViewModelTests
{
    private static ControlViewModel Get(List<ControlViewModel> controls, ControlKind kind) => controls.Single(c => c.Kind == kind);

    [Fact]
    public void Legacy_pedals_get_clutch_brake_throttle_in_that_order()
    {
        List<ControlViewModel> controls = new FakeDevice().CreateControls();
        Assert.Equal(new[] { ControlKind.Clutch, ControlKind.Brake, ControlKind.Throttle }, controls.Select(c => c.Kind));
        Assert.All(controls, c => Assert.True(c.IsAssigned));
        Assert.Equal(3, controls.Select(c => c.Axis).Distinct().Count());
    }

    [Fact]
    public void Unknown_device_gets_one_neutral_entry_per_axis_matching_the_windows_default()
    {
        List<ControlViewModel> controls = new FakeDevice(4).CreateControls("Some Wheel", 0x1234, 0x5678);
        Assert.Equal(4, controls.Count);
        Assert.All(controls, c => Assert.Equal(new AxisCalibration(0, 2048, 4095), c.Computed));
    }

    [Fact]
    public void Measurement_moves_the_control_to_the_detected_axis()
    {
        var device = new FakeDevice();
        ControlViewModel throttle = Get(device.CreateControls(), ControlKind.Throttle);
        int axisChanges = 0;
        throttle.AxisChanged += (_, _) => axisChanges++;

        throttle.ApplyMeasurement(device["Z"], 384, 3100);

        Assert.Same(device["Z"], throttle.Axis);
        Assert.Equal(1, axisChanges);
        Assert.Equal(384, throttle.RawRest);
        Assert.Equal(3100, throttle.RawFull);
        // 2 % deadzone at both ends of the 2716-count travel.
        Assert.Equal(new AxisCalibration(438, 1742, 3046), throttle.Computed);
    }

    [Fact]
    public void Brake_max_force_is_the_complement_of_the_top_deadzone()
    {
        var device = new FakeDevice();
        ControlViewModel brake = Get(device.CreateControls(), ControlKind.Brake);
        brake.ApplyMeasurement(device["Y"], 0, 3000);
        brake.BottomDeadzone = 0;

        Assert.Equal("Max force", brake.TopLabel);
        Assert.Equal(100, brake.TopValue);
        Assert.Equal(3000, brake.Computed.Max);

        brake.TopValue = 80;
        Assert.Equal(20, brake.Settings.TopDeadzone);
        Assert.Equal(2400, brake.Computed.Max);
    }

    [Fact]
    public void Live_readout_follows_the_device()
    {
        var device = new FakeDevice();
        ControlViewModel throttle = Get(device.CreateControls(), ControlKind.Throttle);
        throttle.ApplyMeasurement(device["X"], 1000, 3000);
        throttle.BottomDeadzone = 0;
        throttle.TopValue = 0;

        throttle.UpdateLive();
        Assert.Equal("–", throttle.RawText);
        Assert.True(double.IsNaN(throttle.OutputLevel));

        device.Set("X", 1500);
        throttle.UpdateLive();
        Assert.Equal("1500", throttle.RawText);
        Assert.Equal(0.25, throttle.OutputLevel, 3);
        Assert.Equal("25 %", throttle.OutputText);

        // A midpoint of 25 % means half output is reached at a quarter of the travel.
        throttle.Midpoint = 25;
        throttle.UpdateLive();
        Assert.Equal(0.5, throttle.OutputLevel, 3);
    }

    [Fact]
    public void Inverted_pedal_reads_zero_at_rest_and_full_when_pressed()
    {
        var device = new FakeDevice();
        ControlViewModel clutch = Get(device.CreateControls(), ControlKind.Clutch);
        clutch.ApplyMeasurement(device["Z"], 3000, 1000);
        clutch.BottomDeadzone = 0;
        clutch.TopValue = 0;
        Assert.True(clutch.IsInverted);

        device.Set("Z", 3000);
        clutch.UpdateLive();
        Assert.Equal(0, clutch.OutputLevel, 3);

        device.Set("Z", 1500);
        clutch.UpdateLive();
        Assert.Equal(0.75, clutch.OutputLevel, 3);
        Assert.Equal(0.75, clutch.Travel, 3);
    }

    [Fact]
    public void Applied_state_tracks_what_windows_has_stored()
    {
        var device = new FakeDevice();
        ControlViewModel throttle = Get(device.CreateControls(), ControlKind.Throttle);
        Assert.False(throttle.IsApplied);

        throttle.SetApplied(throttle.Computed);
        Assert.True(throttle.IsApplied);

        throttle.BottomDeadzone = 7;
        Assert.False(throttle.IsApplied);
    }

    [Fact]
    public void Reset_restores_default_tuning_but_keeps_the_measurement()
    {
        var device = new FakeDevice();
        ControlViewModel throttle = Get(device.CreateControls(), ControlKind.Throttle);
        throttle.ApplyMeasurement(device["X"], 300, 3000);
        throttle.BottomDeadzone = 12;
        throttle.Midpoint = 70;

        throttle.ResetTuning();

        Assert.Equal(DeviceTemplates.DefaultDeadzone, throttle.BottomDeadzone);
        Assert.Equal(50, throttle.Midpoint);
        Assert.Equal(300, throttle.RawRest);
        Assert.Equal(3000, throttle.RawFull);
    }
}
