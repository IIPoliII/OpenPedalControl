using System;
using System.Collections.Generic;
using System.Linq;
using OpenPedalControl.Hid;

namespace OpenPedalControl.Model;

/// <summary>Starting layouts for devices that have no saved profile yet.</summary>
public static class DeviceTemplates
{
    public const double DefaultDeadzone = 2;

    // "HE SIM PEDALS": controller of the first-generation Heusinkveld Sim Pedals (Pro / Ultimate).
    private const int LegacyHeusinkveldVendorId = 0x10C4;
    private const int LegacyHeusinkveldPedalsProductId = 0x8B02;

    private const int LeoBodnarVendorId = 0x16C0;
    private const int LeoBodnarBu0836LcProductId = 0x05B7;

    private const int GenericDesktopPage = 0x01;
    private const int UsageX = 0x30, UsageY = 0x31, UsageZ = 0x32;

    public static List<ControlSettings> CreateDefault(string deviceName, int vendorId, int productId, IReadOnlyList<HidAxis> axes)
    {
        bool isLegacyPedals = vendorId == LegacyHeusinkveldVendorId && productId == LegacyHeusinkveldPedalsProductId;
        // Other first-generation Heusinkveld controllers ("HE ...") with axes are single-lever devices: the handbrake.
        // The first Heusinkveld handbrake shipped with a Leo Bodnar BU0836-LC load cell interface with one axis.
        bool isLoadCellInterface = (vendorId == LeoBodnarVendorId && productId == LeoBodnarBu0836LcProductId) ||
                                   Contains(deviceName, "BU0836-LC");
        bool isHandbrake = Contains(deviceName, "HANDBRAKE") || Contains(deviceName, "HAND BRAKE") ||
                           (isLoadCellInterface && axes.Count == 1) ||
                           (!isLegacyPedals && !Contains(deviceName, "PEDAL") &&
                            deviceName.StartsWith("HE ", StringComparison.OrdinalIgnoreCase));
        bool isPedals = !isHandbrake && (isLegacyPedals || Contains(deviceName, "PEDAL"));

        if (isPedals && axes.Count >= 3)
        {
            // First guess for the wiring; the calibration wizard detects the real axis of each pedal.
            HidAxis throttle = Find(axes, UsageX) ?? axes[0];
            HidAxis brake = Find(axes, UsageY) ?? axes[1];
            HidAxis clutch = Find(axes, UsageZ) ?? axes[2];
            return new List<ControlSettings>
            {
                Create("Clutch", ControlKind.Clutch, clutch, DefaultDeadzone, DefaultDeadzone),
                Create("Brake", ControlKind.Brake, brake, DefaultDeadzone, 0),
                Create("Throttle", ControlKind.Throttle, throttle, DefaultDeadzone, DefaultDeadzone),
            };
        }

        if (isHandbrake && axes.Count >= 1)
        {
            return new List<ControlSettings> { Create("Handbrake", ControlKind.Handbrake, axes[0], DefaultDeadzone, DefaultDeadzone) };
        }

        // Unknown device: one neutral entry per axis. Untouched, these reproduce Windows' default behaviour exactly.
        return axes.Take(8).Select(axis => Create("Axis " + axis.Name, ControlKind.Generic, axis, 0, 0)).ToList();
    }

    public static void ResetTuning(ControlSettings settings)
    {
        ControlKind kind = settings.GetKind();
        settings.BottomDeadzone = kind == ControlKind.Generic ? 0 : DefaultDeadzone;
        settings.TopDeadzone = kind == ControlKind.Generic || kind == ControlKind.Brake ? 0 : DefaultDeadzone;
        settings.CurveMidpoint = 50;
        settings.CurvePoints = Curve.Linear();
    }

    /// <summary>Default vJoy axis per role, chosen so pedals and a handbrake on separate USB devices never collide.</summary>
    public static int DefaultVirtualAxis(ControlKind kind) => kind switch
    {
        ControlKind.Throttle => 0x30, // X
        ControlKind.Brake => 0x31,    // Y
        ControlKind.Clutch => 0x32,   // Z
        ControlKind.Handbrake => 0x33, // Rx
        _ => 0,
    };

    private static ControlSettings Create(string name, ControlKind kind, HidAxis axis, double bottom, double top) => new ControlSettings
    {
        VirtualAxis = DefaultVirtualAxis(kind),
        Name = name,
        Kind = kind.ToString(),
        UsagePage = axis.UsagePage,
        Usage = axis.Usage,
        RawRest = axis.LogicalMin,
        RawFull = axis.LogicalMax,
        BottomDeadzone = bottom,
        TopDeadzone = top,
        CurveMidpoint = 50,
    };

    private static HidAxis? Find(IReadOnlyList<HidAxis> axes, int usage) =>
        axes.FirstOrDefault(a => a.UsagePage == GenericDesktopPage && a.Usage == usage);

    private static bool Contains(string text, string value) =>
        text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
}
