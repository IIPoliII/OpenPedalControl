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

    /// <summary>Percentage of pedal travel at which the output reaches 50 %. 50 is linear.</summary>
    [DataMember(Order = 8)] public double CurveMidpoint { get; set; } = 50;

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
    }
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

    [OnDeserializing]
    private void OnDeserializing(StreamingContext context)
    {
        Version = 1;
        DeviceName = "";
        Serial = "";
        Controls = new List<ControlSettings>();
    }
}
