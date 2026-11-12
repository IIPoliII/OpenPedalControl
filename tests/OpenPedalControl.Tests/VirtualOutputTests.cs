using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenPedalControl.Model;
using OpenPedalControl.ViewModels;
using OpenPedalControl.VJoy;
using Xunit;

namespace OpenPedalControl.Tests;

public class VirtualOutputTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.1, 0.1)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    public void Linear_curve_passes_the_travel_through(double travel, double expected) =>
        Assert.Equal(expected, Curve.Evaluate(Curve.Linear(), travel), 6);

    [Fact]
    public void Curve_interpolates_between_points()
    {
        var points = new List<double> { 0, 4, 16, 36, 64, 100 };
        Assert.Equal(0.04, Curve.Evaluate(points, 0.2), 6);
        Assert.Equal(0.10, Curve.Evaluate(points, 0.3), 6); // halfway between 4 % and 16 %
        Assert.Equal(0.82, Curve.Evaluate(points, 0.9), 6);
    }

    [Fact]
    public void Curve_output_can_start_above_zero_and_end_below_full()
    {
        var points = new List<double> { 10, 30, 50, 70, 85, 90 };
        Assert.Equal(0.10, Curve.Evaluate(points, 0), 6);
        Assert.Equal(0.90, Curve.Evaluate(points, 1), 6);
        Assert.Equal(0.10, Curve.Evaluate(points, -0.5), 6);
    }

    [Fact]
    public void Presets_are_recognised_and_custom_curves_are_not()
    {
        Assert.Equal("S-curve", Curve.MatchPreset(new List<double> { 0, 10, 35, 65, 90, 100 })?.Name);
        Assert.Null(Curve.MatchPreset(new List<double> { 0, 11, 35, 65, 90, 100 }));
    }

    [Fact]
    public void Travel_removes_deadzones_and_handles_inverted_sensors()
    {
        // 2 % deadzones on a 1000..3000 travel: active from 1040 to 2960.
        Assert.Equal(0, CalibrationMath.Travel(1040, 1000, 3000, 2, 2), 6);
        Assert.Equal(0.5, CalibrationMath.Travel(2000, 1000, 3000, 2, 2), 6);
        Assert.Equal(1, CalibrationMath.Travel(2990, 1000, 3000, 2, 2), 6);
        Assert.Equal(0.25, CalibrationMath.Travel(2500, 3000, 1000, 0, 0), 6);
    }

    [Fact]
    public void Value_sent_to_vjoy_combines_calibration_deadzones_and_curve()
    {
        var settings = new ControlSettings
        {
            RawRest = 76, RawFull = 2850, BottomDeadzone = 0, TopDeadzone = 10,
            CurvePoints = new List<double> { 0, 4, 16, 36, 64, 100 },
        };
        // Full output at 90 % of the measured force; half force gives 0.5/0.9 of the travel.
        Assert.Equal(1, VirtualOutput.Compute(2600, settings), 6);
        double fullEdge = 2850 - (2850 - 76) * 0.10; // 2572.6
        double travel = (1463 - 76) / (fullEdge - 76);
        Assert.Equal(Curve.Evaluate(settings.CurvePoints, travel), VirtualOutput.Compute(1463, settings), 6);
    }

    [Fact]
    public void Old_profiles_without_a_curve_load_as_linear_windows_mode()
    {
        string path = Path.Combine(Path.GetTempPath(), "opc-old-" + System.Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{ \"Controls\": [ { \"Name\": \"Brake\", \"Kind\": \"Brake\", \"UsagePage\": 1, \"Usage\": 49, \"RawRest\": 76, \"RawFull\": 2850 } ] }", new UTF8Encoding(false));
        try
        {
            DeviceProfile profile = ProfileStore.Read(path)!;
            Assert.Equal(OutputMode.Windows, profile.GetOutputMode());
            Assert.Equal(Curve.Linear(), profile.Controls[0].CurvePoints);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Card_switches_curve_editor_and_live_output_with_the_mode()
    {
        var device = new FakeDevice();
        ControlViewModel throttle = device.CreateControls().Single(c => c.Kind == ControlKind.Throttle);
        throttle.ApplyMeasurement(device["X"], 1000, 3000);
        throttle.BottomDeadzone = 0;
        throttle.TopValue = 0;
        Assert.Null(throttle.GraphPoints);
        Assert.Equal("X", throttle.SelectedVirtualAxis.Label);

        throttle.SetOutputMode(true);
        throttle.SelectedPreset = Curve.Presets.Single(p => p.Name == "Strong progressive");
        Assert.Equal(6, throttle.GraphPoints!.Count);

        device.Set("X", 1600); // 30 % travel
        throttle.UpdateLive();
        Assert.Equal(0.10, throttle.OutputLevel, 3);

        throttle.CurvePoints[1].Value = 20;
        Assert.Equal("Custom", throttle.SelectedPreset.Name);
        throttle.UpdateLive();
        Assert.Equal(0.18, throttle.OutputLevel, 3);
    }
}
