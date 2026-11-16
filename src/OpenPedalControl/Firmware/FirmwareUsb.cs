using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using OpenPedalControl.Hid;

namespace OpenPedalControl.Firmware;

internal sealed class FirmwareDevice
{
    public string Path = "", Product = "";
    public ushort Vid, Pid, Usage, Page, Input, Output, Feature, VersionBcd;
    public Version? InstalledVersion
    {
        get
        {
            if (!IsCustom) return null;
            int a = VersionBcd >> 12, b = (VersionBcd >> 8) & 15, c = (VersionBcd >> 4) & 15, d = VersionBcd & 15;
            return a > 9 || b > 9 || c > 9 || d > 9 ? null : new Version(a * 10 + b, c * 10 + d, 0);
        }
    }
    public bool IsBootloader => Vid == 0x10C4 && Pid == 0x8B02 && Page == 0xFF00 && Usage == 1 && Input == 5 && Output == 65 && Feature == 0;
    public bool IsCustom => Product == OnboardProtocol.Product &&
        ((Vid == 0x1209 && (Pid == 1 || Pid == 2)) || (Vid == 0x10C4 && Pid == 0x8B02)) &&
        Page == 1 && ((Input == 9 && Feature == 57) || (Input == 13 && Feature == 45));
}

/// <summary>Metadata enumeration and bounded overlapped HID I/O. Never selects the PIC handbrake.</summary>
internal sealed class FirmwareUsb : IFirmwareTransport
{
    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceData { public int Size; public Guid Guid; public int Flags; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct OverlappedData { public UIntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr Event; }
    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_FlushQueue(SafeFileHandle handle);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devices, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devices, ref InterfaceData data, IntPtr detail, uint size, out uint needed, IntPtr device);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, IntPtr buffer, uint size, out uint done, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle handle, IntPtr buffer, uint size, out uint done, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out uint done, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
    private readonly SafeFileHandle _handle;

    private static SafeFileHandle Open(string path, uint access = 0, uint flags = 0)
    {
        SafeFileHandle h = HidNative.CreateFile(path, access, 3, IntPtr.Zero, 3, flags, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        int error = Marshal.GetLastWin32Error();
        h.Dispose(); throw new Win32Exception(error);
    }
    private static FirmwareDevice Inspect(SafeFileHandle h, string path)
    {
        var a = new HidNative.HIDD_ATTRIBUTES { Size = Marshal.SizeOf(typeof(HidNative.HIDD_ATTRIBUTES)) };
        if (!HidNative.HidD_GetAttributes(h, ref a) || !HidNative.HidD_GetPreparsedData(h, out IntPtr p))
            throw new IOException("Could not inspect the firmware USB interface.");
        try
        {
            var caps = new ushort[32];
            if (HidNative.HidP_GetCaps(p, caps) != HidNative.HIDP_STATUS_SUCCESS) throw new IOException("Invalid HID capabilities.");
            var text = new byte[512];
            string product = HidNative.HidD_GetProductString(h, text, text.Length) ? Encoding.Unicode.GetString(text).TrimEnd('\0') : "";
            return new FirmwareDevice { Path = path, Product = product, Vid = a.VendorID, Pid = a.ProductID,
                Usage = caps[0], Page = caps[1], Input = caps[2], Output = caps[3], Feature = caps[4], VersionBcd = a.VersionNumber };
        }
        finally { HidNative.HidD_FreePreparsedData(p); }
    }
    internal static List<FirmwareDevice> Enumerate()
    {
        HidD_GetHidGuid(out Guid guid);
        IntPtr devices = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (devices == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var found = new List<FirmwareDevice>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var info = new InterfaceData { Size = Marshal.SizeOf(typeof(InterfaceData)) };
                if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref guid, index, ref info))
                {
                    if (Marshal.GetLastWin32Error() != 259) throw new Win32Exception(Marshal.GetLastWin32Error());
                    break;
                }
                // A device that is just leaving or arriving (the pedals reset into update mode) can fail any of
                // these queries. Skip it for this pass rather than failing the whole scan; callers poll again.
                SetupDiGetDeviceInterfaceDetail(devices, ref info, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
                if (needed < 8 || needed > 65536) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)needed);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(devices, ref info, detail, needed, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    string lower = path.ToLowerInvariant();
                    if (!lower.Contains("vid_10c4&pid_8b02") && !lower.Contains("vid_1209&pid_0001") && !lower.Contains("vid_1209&pid_0002")) continue;
                    try
                    {
                        using var h = Open(path);
                        found.Add(Inspect(h, path));
                    }
                    catch (Exception ex) when (ex is IOException || ex is Win32Exception) { }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
        return found;
    }
    internal static FirmwareUsb OpenBootloader()
    {
        List<FirmwareDevice> devices = Enumerate();
        List<FirmwareDevice> matches = devices.Where(d => d.IsBootloader).ToList();
        if (matches.Count != 1 || devices.Count != 1) throw new IOException("Expected one pedal bootloader and no other pedal board. Hold FW/reset while reconnecting the pedal USB, then check again.");
        return new FirmwareUsb(matches[0].Path);
    }
    private FirmwareUsb(string path)
    {
        _handle = Open(path, 0xC0000000, HidNative.FILE_FLAG_OVERLAPPED);
        try
        {
            if (!Inspect(_handle, path).IsBootloader) throw new IOException("USB device is not the expected EFM8 bootloader.");
            if (!HidD_FlushQueue(_handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
            for (int i = 0; i < 8; i++)
            {
                if (!Transfer(null, new byte[5], 100, true)) return;
            }
            throw new IOException("Bootloader reply queue did not become idle.");
        }
        catch { _handle.Dispose(); throw; }
    }
    private bool Transfer(byte[]? write, byte[] read, int timeout = 3000, bool quietTimeout = false)
    {
        byte[] data = write ?? read;
        using var signal = new EventWaitHandle(false, EventResetMode.ManualReset);
        var ov = new OverlappedData { Event = signal.SafeWaitHandle.DangerousGetHandle() };
        IntPtr native = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(OverlappedData)));
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        bool pending = false;
        try
        {
            Marshal.StructureToPtr(ov, native, false);
            bool ok = write == null ? ReadFile(_handle, pinned.AddrOfPinnedObject(), (uint)data.Length, out _, native)
                : WriteFile(_handle, pinned.AddrOfPinnedObject(), (uint)data.Length, out _, native);
            if (!ok)
            {
                if (Marshal.GetLastWin32Error() != 997) throw new Win32Exception(Marshal.GetLastWin32Error());
                pending = true;
                if (!signal.WaitOne(timeout))
                {
                    CancelIoEx(_handle, native);
                    GetOverlappedResult(_handle, native, out _, true); // Join before freeing OVERLAPPED/buffer.
                    pending = false;
                    if (quietTimeout) return false;
                    throw new TimeoutException("USB operation timed out. No automatic retry was attempted.");
                }
            }
            if (!GetOverlappedResult(_handle, native, out uint done, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
            pending = false;
            if (done != data.Length) throw new IOException("Incomplete bootloader USB report.");
            return true;
        }
        finally
        {
            if (pending) { CancelIoEx(_handle, native); GetOverlappedResult(_handle, native, out _, true); }
            pinned.Free(); Marshal.FreeHGlobal(native);
        }
    }
    public byte[] Exchange(byte[] report)
    {
        if (report.Length != 65 || report[0] != 0) throw new InvalidDataException("Invalid firmware USB packet.");
        Transfer(report, Array.Empty<byte>());
        var reply = new byte[5]; Transfer(null, reply); return reply;
    }
    internal static void EnterBootloader()
    {
        var devices = Enumerate();
        if (devices.Count == 1 && devices[0].IsBootloader) return;
        List<FirmwareDevice> matches = devices.Where(d => d.IsCustom).ToList();
        if (matches.Count != 1 || devices.Count != 1)
            throw new IOException("Connect only one supported pedal board. For original firmware, hold FW/reset while reconnecting its USB.");
        FirmwareDevice device = matches[0];
        using (var h = Open(device.Path, 0xC0000000))
        {
            if (!Inspect(h, device.Path).IsCustom) throw new IOException("Controller identity changed.");
            var report = new byte[device.Feature]; report[0] = device.Feature == 57 ? (byte)0 : (byte)2;
            if (!HidNative.HidD_GetFeature(h, report, report.Length)) throw new IOException("Could not check controller profile before boot entry.");
            OnboardProtocol.DecodeTransport(report, device.Vid, device.Pid, "");
            var command = new byte[report.Length]; command[0] = report[0];
            Buffer.BlockCopy(new byte[] { 66, 79, 79, 84, 1 }, 0, command, 1, 5);
            if (!HidNative.HidD_SetFeature(h, command, command.Length)) throw new IOException("Controller did not accept update mode.");
        }
        for (int i = 0; i < 25; i++)
        {
            Thread.Sleep(200);
            if (Enumerate().Count(d => d.IsBootloader) == 1) return;
        }
        throw new IOException("Bootloader did not appear. Hold FW/reset while reconnecting USB.");
    }
    public void Dispose() => _handle.Dispose();
}
