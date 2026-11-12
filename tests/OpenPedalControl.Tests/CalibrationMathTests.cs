using System;
using OpenPedalControl.Model;
using Xunit;

namespace OpenPedalControl.Tests;

public class CalibrationMathTests
{
    [Fact]
    public void Without_deadzones_the_calibration_spans_rest_to_full()
    {
        AxisCalibration c = CalibrationMath.Compute(324, 3000, 0, 0, 50, 0, 4095);
        Assert.Equal(new AxisCalibration(324, 1662, 3000), c);
    }

    [Fact]
    public void Deadzones_shrink_the_active_range_from_both_ends()
    {
        AxisCalibration c = CalibrationMath.Compute(1000, 3000, 5, 10, 50, 0, 4095);
        Assert.Equal(new AxisCalibration(1100, 1950, 2800), c);
    }

    [Fact]
    public void Midpoint_moves_the_center()
    {
        Assert.Equal(1000, CalibrationMath.Compute(0, 4000, 0, 0, 25, 0, 4095).Center);
        Assert.Equal(3000, CalibrationMath.Compute(0, 4000, 0, 0, 75, 0, 4095).Center);
    }

    [Fact]
    public void Untouched_full_range_equals_the_windows_default()
    {
        // Windows reports 0 / 2048 / 4095 for a 12-bit axis that was never calibrated.
        Assert.Equal(new AxisCalibration(0, 2048, 4095), CalibrationMath.Compute(0, 4095, 0, 0, 50, 0, 4095));
    }

    [Fact]
    public void Inverted_axis_keeps_min_below_max_and_measures_travel_from_the_rest_end()
    {
        // Rest at 3000, fully pressed at 1000: bottom deadzone trims the high end.
        AxisCalibration c = CalibrationMath.Compute(3000, 1000, 10, 0, 25, 0, 4095);
        Assert.Equal(new AxisCalibration(1000, 2350, 2800), c);
    }

    [Theory]
    [InlineData(500, 500)]
    [InlineData(0, 1)]
    [InlineData(4095, 4095)]
    public void Degenerate_range_still_gives_min_below_center_below_max(int rest, int full)
    {
        AxisCalibration c = CalibrationMath.Compute(rest, full, 0, 0, 50, 0, 4095);
        Assert.True(c.Min < c.Center && c.Center < c.Max, c.ToString());
        Assert.InRange(c.Min, 0, 4095);
        Assert.InRange(c.Max, 0, 4095);
    }

    [Fact]
    public void Excessive_deadzones_cannot_cross()
    {
        AxisCalibration c = CalibrationMath.Compute(0, 4000, 90, 90, 50, 0, 4095);
        Assert.True(c.Min < c.Center && c.Center < c.Max, c.ToString());
    }

    // Values Windows actually reported (winmm, 0..65535) for raw 324 on a real "HE SIM PEDALS" controller.
    [Theory]
    [InlineData(200, 1000, 4095, 5079)]
    [InlineData(0, 200, 4095, 33811)]
    [InlineData(100, 2000, 3000, 3863)]
    public void Cooked_output_matches_what_windows_reports(int min, int center, int max, int reported)
    {
        double predicted = CalibrationMath.CookedOutput(324, new AxisCalibration(min, center, max)) * 65535;
        Assert.True(Math.Abs(predicted - reported) <= 1, $"predicted {predicted}, Windows reported {reported}");
    }

    [Fact]
    public void Cooked_output_is_clamped_outside_the_range()
    {
        var c = new AxisCalibration(100, 500, 900);
        Assert.Equal(0, CalibrationMath.CookedOutput(50, c));
        Assert.Equal(0.5, CalibrationMath.CookedOutput(500, c));
        Assert.Equal(1, CalibrationMath.CookedOutput(950, c));
    }

    [Theory]
    [InlineData(300, 1200, 3000)]
    [InlineData(0, 2048, 4095)]
    [InlineData(82, 3000, 4013)]
    public void Existing_calibration_survives_a_round_trip_through_the_settings(int min, int center, int max)
    {
        var existing = new AxisCalibration(min, center, max);
        double midpoint = Math.Round(CalibrationMath.MidpointOf(existing, false));
        AxisCalibration again = CalibrationMath.Compute(min, max, 0, 0, midpoint, 0, 4095);
        Assert.Equal(min, again.Min);
        Assert.Equal(max, again.Max);
        Assert.InRange(again.Center, center - (max - min) / 100, center + (max - min) / 100);
    }
}
