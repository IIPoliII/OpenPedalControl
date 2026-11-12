using System;

namespace OpenPedalControl.Model;

/// <summary>
/// The three raw values Windows (DirectInput) stores per axis. Raw input at <see cref="Min"/> is reported
/// to games as 0 %, at <see cref="Center"/> as 50 % and at <see cref="Max"/> as 100 %, linear in between.
/// </summary>
public readonly struct AxisCalibration : IEquatable<AxisCalibration>
{
    public AxisCalibration(int min, int center, int max)
    {
        Min = min;
        Center = center;
        Max = max;
    }

    public int Min { get; }
    public int Center { get; }
    public int Max { get; }

    public bool Equals(AxisCalibration other) => Min == other.Min && Center == other.Center && Max == other.Max;
    public override bool Equals(object? obj) => obj is AxisCalibration other && Equals(other);
    public override int GetHashCode() => (Min * 397 ^ Center) * 397 ^ Max;
    public static bool operator ==(AxisCalibration a, AxisCalibration b) => a.Equals(b);
    public static bool operator !=(AxisCalibration a, AxisCalibration b) => !a.Equals(b);
    public override string ToString() => $"{Min} / {Center} / {Max}";
}

public static class CalibrationMath
{
    public const double MaxDeadzonePercent = 90;
    public const double MinMidpointPercent = 5;
    public const double MaxMidpointPercent = 95;

    /// <summary>
    /// Turns pedal settings into a Windows axis calibration.
    /// </summary>
    /// <param name="rawRest">Raw value with the pedal released.</param>
    /// <param name="rawFull">Raw value with the pedal fully pressed. Smaller than <paramref name="rawRest"/> on inverted axes.</param>
    /// <param name="bottomPercent">Share of the travel ignored at the released end.</param>
    /// <param name="topPercent">Share of the travel ignored at the pressed end.</param>
    /// <param name="midpointPercent">Share of the remaining travel at which the output reaches 50 %.</param>
    public static AxisCalibration Compute(int rawRest, int rawFull, double bottomPercent, double topPercent,
        double midpointPercent, int logicalMin, int logicalMax)
    {
        double travel = rawFull - rawRest;
        double restEdge = rawRest + travel * Clamp(bottomPercent, 0, MaxDeadzonePercent) / 100.0;
        double fullEdge = rawFull - travel * Clamp(topPercent, 0, MaxDeadzonePercent) / 100.0;
        double mid = restEdge + (fullEdge - restEdge) * Clamp(midpointPercent, MinMidpointPercent, MaxMidpointPercent) / 100.0;

        // DirectInput needs min < center < max, whatever the direction of the axis.
        int min = RoundHalfUp(Math.Min(restEdge, fullEdge));
        int max = RoundHalfUp(Math.Max(restEdge, fullEdge));
        if (logicalMax - logicalMin >= 2)
        {
            min = Clamp(min, logicalMin, logicalMax - 2);
            max = Clamp(max, min + 2, logicalMax);
        }
        else if (max < min + 2)
        {
            max = min + 2;
        }

        int center = Clamp(RoundHalfUp(mid), min + 1, max - 1);
        return new AxisCalibration(min, center, max);
    }

    /// <summary>
    /// Position within the active range in pedal direction: 0 at the released edge, 1 at the pressed edge
    /// (deadzones removed). Used by the virtual output, which applies its own curve afterwards.
    /// </summary>
    public static double Travel(int raw, int rawRest, int rawFull, double bottomPercent, double topPercent)
    {
        double travel = rawFull - rawRest;
        double restEdge = rawRest + travel * Clamp(bottomPercent, 0, MaxDeadzonePercent) / 100.0;
        double fullEdge = rawFull - travel * Clamp(topPercent, 0, MaxDeadzonePercent) / 100.0;
        double span = fullEdge - restEdge;
        if (Math.Abs(span) < 1) return (raw - restEdge) * (travel < 0 ? -1 : 1) > 0 ? 1 : 0;
        return Clamp((raw - restEdge) / span, 0, 1);
    }

    /// <summary>
    /// The position DirectInput hands to a game for a raw value: 0 at Min, 0.5 at Center, 1 at Max.
    /// </summary>
    public static double CookedOutput(int raw, AxisCalibration c)
    {
        if (raw <= c.Min) return 0;
        if (raw >= c.Max) return 1;
        if (raw < c.Center) return 0.5 * (raw - c.Min) / (c.Center - c.Min);
        if (raw > c.Center) return 0.5 + 0.5 * (raw - c.Center) / (c.Max - c.Center);
        return 0.5;
    }

    /// <summary>The midpoint setting that reproduces an existing calibration's center.</summary>
    public static double MidpointOf(AxisCalibration c, bool inverted)
    {
        if (c.Max <= c.Min) return 50;
        double fromMin = 100.0 * (c.Center - c.Min) / (c.Max - c.Min);
        return inverted ? 100 - fromMin : fromMin;
    }

    /// <summary>Rounds .5 upwards, which is how Windows places the default center (0..4095 gives 2048).</summary>
    private static int RoundHalfUp(double value) => (int)Math.Floor(value + 0.5);

    public static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;
    public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
}
