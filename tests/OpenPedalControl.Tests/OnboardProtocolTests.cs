using System;
using System.IO;
using System.Linq;
using OpenPedalControl.Hid;
using OpenPedalControl.Model;
using Xunit;

namespace OpenPedalControl.Tests;

public sealed class OnboardProtocolTests
{
    // Three different sources, inverted brake, inactive clutch, and explicit non-linear curves.
    private static byte[] Report() => new byte[]
    {
        2, 79, 80, 67, 1, 3,
        2,   0x34, 0x02, 0xA0, 0x0F, 5, 2,  0, 20, 40, 60, 80, 100,
        0,   0xFF, 0x0F, 0x10, 0x00, 0, 30, 1, 4, 16, 36, 64, 99,
        255, 0,    0,    0,    0,    0, 0,  0, 20, 40, 60, 80, 100
    };

    [Fact]
    public void Board_profile_is_authoritative_and_serializes_in_output_order()
    {
        DeviceProfile profile = OnboardProtocol.Decode(Report(), 42, 17, "unit");
        Assert.Equal(OutputMode.Onboard, profile.GetOutputMode());
        ControlSettings brake = profile.Controls.Single(c => c.GetKind() == ControlKind.Brake);
        Assert.Equal(0x30, brake.Usage);
        Assert.Equal(4095, brake.RawRest);
        Assert.Equal(16, brake.RawFull);
        Assert.Equal(30, brake.TopDeadzone);
        Assert.False(profile.Controls.Single(c => c.GetKind() == ControlKind.Clutch).IsAssigned);
        profile.Controls.Reverse();
        Assert.Equal(Report(), OnboardProtocol.Encode(profile));
    }

    [Theory]
    [InlineData(0, 1)]   // Wrong report ID
    [InlineData(4, 2)]   // Unknown protocol version
    [InlineData(5, 4)]   // Wrong number of axes
    [InlineData(19, 2)]  // Conflicting source assignment
    [InlineData(19, 3)]  // Invalid source
    [InlineData(21, 16)] // Raw value above 12 bits
    [InlineData(24, 75)] // Deadzones overlap
    [InlineData(13, 101)]
    public void Malformed_reports_are_rejected(int index, byte value)
    {
        var report = Report();
        report[index] = value;
        Assert.Throws<InvalidDataException>(() => OnboardProtocol.Decode(report, 0, 0, ""));
        Assert.Throws<InvalidDataException>(() => OnboardProtocol.Decode(report.Take(44).ToArray(), 0, 0, ""));
    }

    [Fact]
    public void Bad_input_cannot_wrap_into_a_valid_profile()
    {
        var profile = OnboardProtocol.Decode(Report(), 0, 0, "");
        var throttle = profile.Controls[0];
        throttle.RawRest = -1;
        Assert.Throws<InvalidDataException>(() => OnboardProtocol.Encode(profile));
        throttle.RawRest = throttle.RawFull;
        Assert.Throws<InvalidDataException>(() => OnboardProtocol.Encode(profile));
        throttle.RawRest = 100;
        throttle.CurvePoints[1] = double.NaN;
        Assert.Throws<InvalidDataException>(() => OnboardProtocol.Encode(profile));
    }

    [Fact]
    public void Snapshot_transport_keeps_live_values_separate_from_saved_settings()
    {
        DeviceProfile profile = OnboardProtocol.Decode(Report(), 0x1209, 2, "OPC-TEST0001");
        byte[] wire = OnboardProtocol.EncodeTransport(profile, true);
        Assert.Equal(57, wire.Length);
        Assert.Equal(0, wire[0]);
        wire[45] = 0x34; wire[46] = 0x02; // Cooked X is independent of raw LC1.
        wire[51] = 0xAB; wire[52] = 0x0E;
        wire[53] = 0x12; wire[54] = 0x03;
        wire[55] = 0xFC; wire[56] = 0x0F;
        Assert.Equal(new[] { 0xEAB, 0x312, 4092 }, OnboardProtocol.ReadSnapshot(wire));
        Assert.Equal(Report(), OnboardProtocol.Encode(OnboardProtocol.DecodeTransport(wire, 0x1209, 2, "unit")));
        byte[] saved = OnboardProtocol.EncodeTransport(OnboardProtocol.DecodeTransport(wire, 0, 0, ""), true);
        Assert.All(saved.Skip(45), value => Assert.Equal(0, value));
        wire[56] = 0x10;
        Assert.Throws<InvalidDataException>(() => OnboardProtocol.ReadSnapshot(wire));
        wire[0] = 1;
        Assert.Throws<InvalidDataException>(() => OnboardProtocol.DecodeTransport(wire, 0, 0, ""));
        Assert.Equal(Report(), OnboardProtocol.EncodeTransport(profile, false));
    }
}
