using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenPedalControl.Model;
using static OpenPedalControl.DirectInput.DirectInputNative;

namespace OpenPedalControl.DirectInput;

/// <summary>A game controller as Windows lists it, with the HID path needed to read its raw data.</summary>
public sealed class GameController
{
    public GameController(Guid instanceGuid, string name, ushort vendorId, ushort productId, string hidPath)
    {
        InstanceGuid = instanceGuid;
        Name = name;
        VendorId = vendorId;
        ProductId = productId;
        HidPath = hidPath;
    }

    public Guid InstanceGuid { get; }
    public string Name { get; }
    public ushort VendorId { get; }
    public ushort ProductId { get; }
    public string HidPath { get; }
}

/// <summary>
/// Reads and writes the per-axis calibration Windows keeps for game controllers. This is the same
/// DirectInput property DIView and the "Game Controllers" control panel use; Windows stores it per user
/// and applies it for every DirectInput game. Must be used from a single (UI) thread.
/// </summary>
public sealed class DirectInputService : IDisposable
{
    private IDirectInput8W? _directInput;

    public DirectInputService()
    {
        Guid iid = typeof(IDirectInput8W).GUID;
        int hr = DirectInput8Create(GetModuleHandle(null), DIRECTINPUT_VERSION, ref iid, out _directInput, IntPtr.Zero);
        if (hr < 0 || _directInput == null) throw new InvalidOperationException($"DirectInput is not available (0x{hr:X8}).");
    }

    public List<GameController> EnumerateControllers()
    {
        var found = new List<(Guid instance, Guid product, string name)>();
        EnumDevicesCallback callback = (deviceInstance, _) =>
        {
            Guid instance = Marshal.PtrToStructure<Guid>(deviceInstance + InstanceGuidOffset);
            Guid product = Marshal.PtrToStructure<Guid>(deviceInstance + ProductGuidOffset);
            string name = Marshal.PtrToStringUni(deviceInstance + ProductNameOffset) ?? "";
            if (name.Length == 0) name = Marshal.PtrToStringUni(deviceInstance + InstanceNameOffset) ?? "";
            found.Add((instance, product, name.Trim()));
            return DIENUM_CONTINUE;
        };
        Api.EnumDevices(DI8DEVCLASS_GAMECTRL, callback, IntPtr.Zero, DIEDFL_ATTACHEDONLY);
        GC.KeepAlive(callback);

        var controllers = new List<GameController>();
        foreach ((Guid instance, Guid product, string name) in found)
        {
            string? path = GetHidPath(instance);
            if (path == null) continue; // Not a HID device: nothing to read raw data from.

            // For HID devices the product GUID starts with MAKELONG(vendorId, productId).
            byte[] bytes = product.ToByteArray();
            ushort vendorId = BitConverter.ToUInt16(bytes, 0);
            ushort productId = BitConverter.ToUInt16(bytes, 2);
            controllers.Add(new GameController(instance, name, vendorId, productId, path));
        }
        return controllers;
    }

    public bool TryGetCalibration(Guid instanceGuid, int usagePage, int usage, out AxisCalibration calibration)
    {
        calibration = default;
        IDirectInputDevice8W? device = OpenDevice(instanceGuid);
        if (device == null) return false;

        IntPtr buffer = AllocateProperty(PropCalSize, MakeUsage(usagePage, usage), DIPH_BYUSAGE);
        try
        {
            if (device.GetProperty(DIPROP_CALIBRATION, buffer) < 0) return false;
            calibration = new AxisCalibration(
                Marshal.ReadInt32(buffer, PropHeaderSize),
                Marshal.ReadInt32(buffer, PropHeaderSize + 4),
                Marshal.ReadInt32(buffer, PropHeaderSize + 8));
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.ReleaseComObject(device);
        }
    }

    /// <summary>Stores a calibration for one axis. Windows persists it for the current user.</summary>
    public void SetCalibration(Guid instanceGuid, int usagePage, int usage, AxisCalibration calibration)
    {
        IDirectInputDevice8W device = OpenDevice(instanceGuid)
            ?? throw new InvalidOperationException("The device is no longer connected.");

        IntPtr buffer = AllocateProperty(PropCalSize, MakeUsage(usagePage, usage), DIPH_BYUSAGE);
        try
        {
            Marshal.WriteInt32(buffer, PropHeaderSize, calibration.Min);
            Marshal.WriteInt32(buffer, PropHeaderSize + 4, calibration.Center);
            Marshal.WriteInt32(buffer, PropHeaderSize + 8, calibration.Max);
            int hr = device.SetProperty(DIPROP_CALIBRATION, buffer);
            if (hr < 0) throw new InvalidOperationException($"Windows rejected the calibration (0x{hr:X8}).");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.ReleaseComObject(device);
        }
    }

    private string? GetHidPath(Guid instanceGuid)
    {
        IDirectInputDevice8W? device = OpenDevice(instanceGuid);
        if (device == null) return null;

        IntPtr buffer = AllocateProperty(PropGuidAndPathSize, 0, DIPH_DEVICE);
        try
        {
            if (device.GetProperty(DIPROP_GUIDANDPATH, buffer) < 0) return null;
            string? path = Marshal.PtrToStringUni(buffer + PropHeaderSize + 16);
            return string.IsNullOrEmpty(path) ? null : path;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.ReleaseComObject(device);
        }
    }

    private IDirectInputDevice8W? OpenDevice(Guid instanceGuid)
    {
        int hr = Api.CreateDevice(ref instanceGuid, out IDirectInputDevice8W? device, IntPtr.Zero);
        return hr < 0 ? null : device;
    }

    private IDirectInput8W Api => _directInput ?? throw new ObjectDisposedException(nameof(DirectInputService));

    private static uint MakeUsage(int usagePage, int usage) => ((uint)usagePage << 16) | (uint)usage;

    private static IntPtr AllocateProperty(int size, uint obj, uint how)
    {
        IntPtr buffer = Marshal.AllocHGlobal(size);
        for (int offset = 0; offset < size; offset += 4) Marshal.WriteInt32(buffer, offset, 0);
        Marshal.WriteInt32(buffer, 0, size);
        Marshal.WriteInt32(buffer, 4, PropHeaderSize);
        Marshal.WriteInt32(buffer, 8, unchecked((int)obj));
        Marshal.WriteInt32(buffer, 12, unchecked((int)how));
        return buffer;
    }

    public void Dispose()
    {
        if (_directInput != null)
        {
            Marshal.ReleaseComObject(_directInput);
            _directInput = null;
        }
    }
}
