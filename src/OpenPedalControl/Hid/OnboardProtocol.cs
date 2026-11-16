using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenPedalControl.Model;

namespace OpenPedalControl.Hid;

/// <summary>Wire format shared with firmware/core/opc.h. Offsets include the HID report ID.</summary>
public static class OnboardProtocol
{
    public const string Product = "OpenPedalControl Pedals";
    public const int InputLength = 13;
    public const int FeatureLength = 45;
    public const int SnapshotFeatureLength = 57;
    public const int SnapshotInputLength = 9;
    internal const byte InputId = 1;
    internal const byte FeatureId = 2;
    private const int DefaultSensorFull = 4092; // EFM8UB1 12-bit burst-mode ADC maximum.
    private const byte Off = 255;
    private static readonly ControlKind[] Roles = { ControlKind.Throttle, ControlKind.Brake, ControlKind.Clutch };

    internal static bool Matches(string product, int inputLength, int featureLength, IReadOnlyList<HidAxis> axes) =>
        product == Product && ((inputLength == InputLength && featureLength == FeatureLength) ||
            (inputLength == SnapshotInputLength && featureLength == SnapshotFeatureLength)) &&
        axes.Count == Roles.Length && axes.Select((axis, i) => axis.UsagePage == 1 && axis.Usage == 0x30 + i &&
            axis.LogicalMin == 0 && axis.LogicalMax == 4095 && axis.BitSize == 16).All(matches => matches);

    internal static int ReadWord(byte[] report, int offset) => report[offset] | (report[offset + 1] << 8);

    public static DeviceProfile DecodeTransport(byte[] report, int vendorId, int productId, string serial)
    {
        if (report.Length != SnapshotFeatureLength) return Decode(report, vendorId, productId, serial);
        if (report[0] != 0) throw new InvalidDataException("Unexpected snapshot report ID.");
        byte[] profile = report.Take(FeatureLength).ToArray();
        profile[0] = FeatureId;
        return Decode(profile, vendorId, productId, serial);
    }

    public static byte[] EncodeTransport(DeviceProfile profile, bool snapshotFormat)
    {
        byte[] encoded = Encode(profile);
        if (!snapshotFormat) return encoded;
        var report = new byte[SnapshotFeatureLength];
        Buffer.BlockCopy(encoded, 1, report, 1, FeatureLength - 1);
        return report;
    }

    public static int[] ReadSnapshot(byte[] report)
    {
        if (report.Length != SnapshotFeatureLength || report[0] != 0 || report[1] != 'O' || report[2] != 'P' ||
            report[3] != 'C' || report[4] != 1 || report[5] != Roles.Length)
            throw new InvalidDataException("Unsupported controller snapshot.");
        var raw = new int[Roles.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = ReadWord(report, FeatureLength + 6 + 2 * i);
            if (raw[i] > 4095) throw new InvalidDataException("Controller sensor exceeds the 12-bit range.");
        }
        return raw;
    }

    public static DeviceProfile Decode(byte[] report, int vendorId, int productId, string serial)
    {
        if (report.Length != FeatureLength || report[0] != FeatureId || report[1] != 'O' || report[2] != 'P' ||
            report[3] != 'C' || report[4] != 1 || report[5] != Roles.Length)
            throw new InvalidDataException("Unsupported controller settings format.");

        var profile = new DeviceProfile
        {
            DeviceName = Product, VendorId = vendorId, ProductId = productId, Serial = serial,
            Output = nameof(OutputMode.Onboard)
        };
        var assigned = new HashSet<int>();
        for (int i = 0; i < Roles.Length; i++)
        {
            int offset = 6 + 13 * i;
            int source = report[offset];
            int rest = ReadWord(report, offset + 1);
            int full = ReadWord(report, offset + 3);
            int bottom = report[offset + 5];
            int top = report[offset + 6];
            var points = report.Skip(offset + 7).Take(Curve.PointCount).Select(p => (double)p).ToList();
            if ((source != Off && (source >= Roles.Length || !assigned.Add(source) || rest == full)) ||
                rest > 4095 || full > 4095 || bottom > 90 || top > 90 || bottom + top >= 100 || points.Any(p => p > 100))
                throw new InvalidDataException($"Invalid calibration for {Roles[i]}.");

            profile.Controls.Add(new ControlSettings
            {
                Kind = Roles[i].ToString(), Name = Roles[i].ToString(), UsagePage = source == Off ? 0 : 1,
                Usage = source == Off ? 0 : 0x30 + source, RawRest = rest, RawFull = full,
                BottomDeadzone = bottom, TopDeadzone = top, CurvePoints = points
            });
        }
        return profile;
    }

    public static byte[] Encode(DeviceProfile profile)
    {
        if (profile.Controls.Count != Roles.Length)
            throw new InvalidDataException("The controller requires throttle, brake and clutch settings.");
        var report = new byte[FeatureLength];
        report[0] = FeatureId;
        report[1] = (byte)'O';
        report[2] = (byte)'P';
        report[3] = (byte)'C';
        report[4] = 1;
        report[5] = (byte)Roles.Length;
        for (int i = 0; i < Roles.Length; i++)
        {
            var control = profile.Controls.SingleOrDefault(c => c.GetKind() == Roles[i])
                          ?? throw new InvalidDataException($"Missing settings for {Roles[i]}.");
            int offset = 6 + 13 * i;
            if (control.IsAssigned && (control.UsagePage != 1 || control.Usage < 0x30 || control.Usage > 0x32))
                throw new InvalidDataException($"Unknown physical input for {Roles[i]}.");
            report[offset] = control.IsAssigned ? (byte)(control.Usage - 0x30) : Off;
            WriteWord(report, offset + 1, control.RawRest);
            WriteWord(report, offset + 3, control.RawFull);
            report[offset + 5] = Percent(control.BottomDeadzone);
            report[offset + 6] = Percent(control.TopDeadzone);
            if (control.CurvePoints.Count != Curve.PointCount)
                throw new InvalidDataException("A controller curve needs six points.");
            for (int point = 0; point < Curve.PointCount; point++)
                report[offset + 7 + point] = Percent(control.CurvePoints[point]);
        }
        // Apply the same channel/range invariants to edited values and to reports from flash.
        Decode(report, profile.VendorId, profile.ProductId, profile.Serial);
        return report;
    }

    internal static byte[] Defaults()
    {
        var profile = new DeviceProfile();
        for (int i = 0; i < Roles.Length; i++)
            profile.Controls.Add(new ControlSettings { Kind = Roles[i].ToString(), UsagePage = 1, Usage = 0x30 + i, RawFull = DefaultSensorFull });
        return Encode(profile);
    }

    private static void WriteWord(byte[] report, int offset, int value)
    {
        if (value < 0 || value > 4095) throw new InvalidDataException("Sensor limits must be between 0 and 4095.");
        report[offset] = (byte)value;
        report[offset + 1] = (byte)(value >> 8);
    }

    private static byte Percent(double value)
    {
        if (!(value >= 0 && value <= 100) || value != Math.Round(value))
            throw new InvalidDataException("The controller expects whole percentages from 0 to 100.");
        return (byte)value;
    }
}
