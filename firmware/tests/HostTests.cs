using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using OpenPedalControl.Model;

internal static class HostTests
{
    private const int Max = 4095, Off = 255;
    private static readonly int[] Linear = { 0, 20, 40, 60, 80, 100 };
    private static int profileChecks, comparedSamples, largestDifference;

    [DllImport("opc", CallingConvention = CallingConvention.Cdecl)]
    private static extern void opc_defaults([Out] byte[] profile);
    [DllImport("opc", CallingConvention = CallingConvention.Cdecl)]
    private static extern byte opc_valid([In] byte[] profile);
    [DllImport("opc", CallingConvention = CallingConvention.Cdecl)]
    private static extern void opc_input([In] byte[] profile, [In] ushort[] raw, [Out] byte[] report);

    private static void Require(bool condition, string context)
    {
        if (!condition) throw new InvalidOperationException(context);
    }

    private static ushort Read(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));

    private static void Write(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), (ushort)value);

    private static byte[] Defaults()
    {
        var profile = new byte[45];
        opc_defaults(profile);
        return profile;
    }

    private static void SetAxis(byte[] profile, int slot, int source, int rest, int full,
                                int bottom, int top, IReadOnlyList<int> points)
    {
        int offset = 6 + 13 * slot;
        profile[offset] = (byte)source;
        Write(profile, offset + 1, rest);
        Write(profile, offset + 3, full);
        profile[offset + 5] = (byte)bottom;
        profile[offset + 6] = (byte)top;
        for (int i = 0; i < 6; i++) profile[offset + 7 + i] = (byte)points[i];
    }

    private static void ExpectValid(byte[] profile, bool valid, string context)
    {
        Require((opc_valid(profile) != 0) == valid, context);
        profileChecks++;
    }

    private static void CheckProfileFormat()
    {
        var expected = new byte[45];
        new byte[] { 2, (byte)'O', (byte)'P', (byte)'C', 1, 3 }.CopyTo(expected, 0);
        for (int axis = 0; axis < 3; axis++)
            SetAxis(expected, axis, axis, 0, 4092, 0, 0, Linear);
        Require(Defaults().SequenceEqual(expected), "Unexpected default profile bytes");
        ExpectValid(expected, true, "Default profile rejected");

        for (int offset = 0; offset < 6; offset++)
        {
            var invalid = Defaults();
            invalid[offset] ^= 1;
            ExpectValid(invalid, false, $"Accepted corrupt header byte {offset}");
        }

        for (int slot = 0; slot < 3; slot++)
        {
            int offset = 6 + 13 * slot;
            foreach (var (field, value) in new[] {
                (0, 3), (0, 254), (0, (slot + 1) % 3), (2, 16), (4, 16), (5, 91), (6, 91),
                (7, 101), (8, 101), (9, 101), (10, 101), (11, 101), (12, 101) })
            {
                var invalid = Defaults();
                invalid[offset + field] = (byte)value;
                ExpectValid(invalid, false, $"Accepted slot {slot}, field {field}, value {value}");
            }
            var profile = Defaults();
            SetAxis(profile, slot, slot, Max, Max, 0, 0, Linear);
            ExpectValid(profile, false, "Accepted zero travel on enabled axis");
            SetAxis(profile, slot, Off, Max, Max, 0, 0, Linear);
            ExpectValid(profile, true, "Rejected disabled axis with no travel");
            SetAxis(profile, slot, slot, 0, Max, 90, 10, Linear);
            ExpectValid(profile, false, "Accepted combined deadzones of 100%");
            SetAxis(profile, slot, slot, 0, Max, 10, 90, Linear);
            ExpectValid(profile, false, "Accepted reversed combined deadzones of 100%");
            SetAxis(profile, slot, slot, Max, 0, 90, 90, Linear);
            ExpectValid(profile, false, "Accepted overlapping deadzones");
            SetAxis(profile, slot, slot, Max, 0, 9, 90, Linear);
            ExpectValid(profile, true, "Rejected 1% active range with inverted travel");
        }
    }

    private static void CheckMapping()
    {
        ushort[] raw = { 103, 1998, 3826 };
        for (int first = 0; first < 3; first++)
        for (int second = 0; second < 3; second++)
        {
            if (first == second) continue;
            int[] sources = { first, second, 3 - first - second };
            for (int disableMask = 0; disableMask < 8; disableMask++)
            {
                var profile = Defaults();
                for (int slot = 0; slot < 3; slot++)
                    profile[6 + 13 * slot] = (byte)((disableMask & (1 << slot)) == 0 ? sources[slot] : Off);
                ExpectValid(profile, true, "Rejected permuted or disabled axis");
                var report = new byte[13];
                opc_input(profile, raw, report);
                Require(report[0] == 1, "Wrong input report ID");
                for (int slot = 0; slot < 3; slot++)
                {
                    int cooked = (disableMask & (1 << slot)) == 0
                        ? (int)Math.Floor(raw[sources[slot]] * Max / 4092.0 + 0.5) : 0;
                    Require(Read(report, 1 + 2 * slot) == cooked, "Unexpected mapped output");
                    Require(Read(report, 7 + 2 * slot) == raw[slot], "Raw channels did not retain connector order");
                }
            }
        }
    }

    private static void CheckMath()
    {
        var curves = Curve.Presets.Select(p => (p.Name, p.Points))
            .Append(("Alternating endpoints", new double[] { 100, 0, 100, 0, 100, 0 }))
            .Append(("Flat", new double[] { 37, 37, 37, 37, 37, 37 }));
        var ranges = new[] {
            (0, Max, 0, 0), (Max, 0, 0, 0), (0, Max, 90, 9), (Max, 0, 9, 90),
            (390, 3750, 7, 13), (3750, 390, 7, 13), (0, 5, 11, 80), (5, 0, 11, 80),
            (20, 22, 0, 50), (22, 20, 50, 0), (2048, 2049, 0, 0), (2049, 2048, 0, 0)
        };
        var raw = new ushort[3];
        var report = new byte[13];
        foreach (var (name, points) in curves)
        foreach (var (rest, full, bottom, top) in ranges)
        {
            var profile = Defaults();
            SetAxis(profile, 0, 0, rest, full, bottom, top, points.Select(p => (int)p).ToArray());
            ExpectValid(profile, true, "Rejected curve or calibration used in parity test");
            // Rounding within a segment can move a rounded 12-bit output by one count.
            const int tolerance = 1;
            for (int value = 0; value <= Max; value++)
            {
                raw[0] = (ushort)value;
                opc_input(profile, raw, report);
                int actual = Read(report, 1);
                double travel = CalibrationMath.Travel(value, rest, full, bottom, top);
                double expected = Max * Curve.Evaluate(points, travel);
                int rounded = (int)Math.Floor(expected + 0.5 + 1e-9);
                int difference = Math.Abs(actual - rounded);
                bool exact = travel == 0 || travel == 1 || (bottom == 0 && top == 0 && Math.Abs(full - rest) == Max);
                if (difference > (exact ? 0 : tolerance))
                    throw new InvalidOperationException($"{name} {rest}->{full}, deadzones {bottom}/{top}, " +
                        $"raw {value}: expected {rounded}, actual {actual}, tolerance {(exact ? 0 : tolerance)}");
                comparedSamples++;
                largestDifference = Math.Max(largestDifference, difference);
            }
        }
    }

    public static void Main()
    {
        CheckProfileFormat();
        CheckMapping();
        CheckMath();
        Console.WriteLine($"Passed {profileChecks} profile checks, 48 input mappings and {comparedSamples} " +
                          $"desktop/firmware comparisons. Largest difference: {largestDifference} of {Max} counts.");
    }
}
