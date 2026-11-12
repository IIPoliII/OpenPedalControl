using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenPedalControl.Model;

public sealed class CurvePreset
{
    public CurvePreset(string name, params double[] points)
    {
        Name = name;
        Points = points;
    }

    public string Name { get; }
    public IReadOnlyList<double> Points { get; }
    public override string ToString() => Name;
}

/// <summary>
/// Multi-point output curve used in virtual mode: output values at evenly spaced points of the travel,
/// joined by straight lines.
/// </summary>
public static class Curve
{
    public const int PointCount = 6;

    public static readonly IReadOnlyList<CurvePreset> Presets = new[]
    {
        new CurvePreset("Linear", 0, 20, 40, 60, 80, 100),
        new CurvePreset("Progressive", 0, 9, 25, 46, 72, 100),
        new CurvePreset("Strong progressive", 0, 4, 16, 36, 64, 100),
        new CurvePreset("Degressive", 0, 34, 54, 71, 86, 100),
        new CurvePreset("S-curve", 0, 10, 35, 65, 90, 100),
    };

    public static List<double> Linear() => Presets[0].Points.ToList();

    /// <summary>Input (travel) percentage of each curve point.</summary>
    public static double InputOf(int index) => 100.0 * index / (PointCount - 1);

    /// <summary>Output (0..1) for a travel (0..1).</summary>
    public static double Evaluate(IReadOnlyList<double> points, double travel)
    {
        if (points.Count < 2) return Clamp01(travel);
        travel = Clamp01(travel);
        double position = travel * (points.Count - 1);
        int segment = Math.Min((int)position, points.Count - 2);
        double fraction = position - segment;
        double output = points[segment] + (points[segment + 1] - points[segment]) * fraction;
        return Clamp01(output / 100.0);
    }

    /// <summary>The preset whose points equal the given curve, or null for a custom curve.</summary>
    public static CurvePreset? MatchPreset(IReadOnlyList<double> points) =>
        Presets.FirstOrDefault(p => p.Points.Count == points.Count &&
                                    p.Points.Zip(points, (a, b) => Math.Abs(a - b) < 0.5).All(equal => equal));

    /// <summary>Repairs a curve read from a file: exactly <see cref="PointCount"/> values within 0..100.</summary>
    public static List<double> Normalize(List<double>? points)
    {
        if (points == null || points.Count != PointCount) return Linear();
        return points.Select(p => double.IsNaN(p) ? 0 : Math.Max(0, Math.Min(100, p))).ToList();
    }

    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
}
