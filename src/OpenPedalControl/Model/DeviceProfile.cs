using System.Collections.Generic;
using System.Runtime.Serialization;

namespace OpenPedalControl.Model;

public enum ControlKind
{
    Generic,
    Clutch,
    Brake,
    Throttle,
    Handbrake,
}

/// <summary>Where the result of a device's settings goes.</summary>
public enum OutputMode
{
    /// <summary>Stored as the Windows calibration of the real device. Works with the app closed; one-bend curve.</summary>
    Windows,

    /// <summary>Sent to a vJoy virtual controller by the running app. Full multi-point curve.</summary>
    Virtual,
}

/// <summary>Settings of one pedal (or handbrake lever), as stored in the profile file.</summary>
[DataContract]
public sealed class ControlSettings
{
    [DataMember(Order = 0)] public string Name { get; set; } = "";
    [DataMember(Order = 1)] public string Kind { get; set; } = nameof(ControlKind.Generic);

    /// <summary>HID usage page of the assigned axis; 0 when no axis is assigned.</summary>
    [DataMember(Order = 2)] public int UsagePage { get; set; }
    [DataMember(Order = 3)] public int Usage { get; set; }

    /// <summary>Raw sensor value with the pedal released.</summary>
    [DataMember(Order = 4)] public int RawRest { get; set; }

    /// <summary>Raw sensor value with the pedal fully pressed.</summary>
    [DataMember(Order = 5)] public int RawFull { get; set; }

    [DataMember(Order = 6)] public double BottomDeadzone { get; set; }
    [DataMember(Order = 7)] public double TopDeadzone { get; set; }

    /// <summary>Windows mode: percentage of pedal travel at which the output reaches 50 %. 50 is linear.</summary>
    [DataMember(Order = 8)] public double CurveMidpoint { get; set; } = 50;

    /// <summary>Virtual mode: output in percent at 0, 20, 40, 60, 80 and 100 % of the travel.</summary>
    [DataMember(Order = 9)] public List<double> CurvePoints { get; set; } = Curve.Linear();

    /// <summary>Virtual mode: HID usage of the vJoy axis this control drives (0x30 = X ... 0x37 = Dial); 0 = off.</summary>
    [DataMember(Order = 10)] public int VirtualAxis { get; set; }

    public bool IsAssigned => UsagePage != 0;

    public ControlKind GetKind() =>
        System.Enum.TryParse(Kind, true, out ControlKind kind) ? kind : ControlKind.Generic;

    // The JSON serializer skips constructors, so defaults for fields missing in older files are set here.
    [OnDeserializing]
    private void OnDeserializing(StreamingContext context)
    {
        Name = "";
        Kind = nameof(ControlKind.Generic);
        CurveMidpoint = 50;
        CurvePoints = Curve.Linear();
    }

    [OnDeserialized]
    private void OnDeserialized(StreamingContext context) => CurvePoints = Curve.Normalize(CurvePoints);
}

[DataContract]
public sealed class DeviceProfile
{
    [DataMember(Order = 0)] public int Version { get; set; } = 1;
    [DataMember(Order = 1)] public string DeviceName { get; set; } = "";
    [DataMember(Order = 2)] public int VendorId { get; set; }
    [DataMember(Order = 3)] public int ProductId { get; set; }
    [DataMember(Order = 4)] public string Serial { get; set; } = "";
    [DataMember(Order = 5)] public List<ControlSettings> Controls { get; set; } = new List<ControlSettings>();
    [DataMember(Order = 6)] public string Output { get; set; } = nameof(OutputMode.Windows);

    public OutputMode GetOutputMode() =>
        System.Enum.TryParse(Output, true, out OutputMode mode) ? mode : OutputMode.Windows;

    [OnDeserializing]
    private void OnDeserializing(StreamingContext context)
    {
        Version = 1;
        DeviceName = "";
        Serial = "";
        Controls = new List<ControlSettings>();
        Output = nameof(OutputMode.Windows);
    }
}
