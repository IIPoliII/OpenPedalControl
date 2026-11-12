using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenPedalControl.Hid;

internal static class HidNative
{
    public const int HIDP_STATUS_SUCCESS = 0x00110000;
    public const int HidP_Input = 0;

    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    /// <summary>Size of the native HIDP_VALUE_CAPS structure.</summary>
    public const int ValueCapsSize = 72;

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetAttributes(SafeFileHandle device, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetProductString(SafeFileHandle device, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetManufacturerString(SafeFileHandle device, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetSerialNumberString(SafeFileHandle device, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetInputReport(SafeFileHandle device, byte[] buffer, int bufferLength);

    /// <summary>HIDP_CAPS is 32 consecutive USHORTs; see <see cref="Caps"/> for the indices used.</summary>
    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(IntPtr preparsedData, [Out] ushort[] caps);

    [DllImport("hid.dll")]
    public static extern int HidP_GetValueCaps(int reportType, IntPtr valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint usageValue, IntPtr preparsedData, byte[] report, uint reportLength);

    public static class Caps
    {
        public const int Usage = 0;
        public const int UsagePage = 1;
        public const int InputReportByteLength = 2;
        public const int NumberInputValueCaps = 24;
    }
}
