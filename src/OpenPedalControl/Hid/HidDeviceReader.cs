using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace OpenPedalControl.Hid;

/// <summary>One analog axis found in a device's HID report descriptor.</summary>
public sealed class HidAxis
{
    public HidAxis(int index, ushort usagePage, ushort usage, ushort linkCollection, int bitSize, int logicalMin, int logicalMax, string name)
    {
        Index = index;
        UsagePage = usagePage;
        Usage = usage;
        LinkCollection = linkCollection;
        BitSize = bitSize;
        LogicalMin = logicalMin;
        LogicalMax = logicalMax;
        Name = name;
    }

    public int Index { get; }
    public ushort UsagePage { get; }
    public ushort Usage { get; }
    public ushort LinkCollection { get; }
    public int BitSize { get; }
    public int LogicalMin { get; }
    public int LogicalMax { get; }
    public string Name { get; }
}

/// <summary>
/// Reads the raw, uncalibrated axis values of a HID game controller straight from its input reports,
/// so the numbers are unaffected by whatever calibration Windows currently applies.
/// </summary>
public sealed class HidDeviceReader : IAxisSource, IDisposable
{
    private const int NoData = int.MinValue;

    private readonly FileStream _stream;
    private readonly IntPtr _preparsed;
    private readonly int _reportLength;
    private readonly int[] _values;
    private int _closed;

    private HidDeviceReader(FileStream stream, IntPtr preparsed, int reportLength, List<HidAxis> axes)
    {
        _stream = stream;
        _preparsed = preparsed;
        _reportLength = reportLength;
        Axes = axes;
        _values = Enumerable.Repeat(NoData, axes.Count).ToArray();
    }

    public string Path { get; private set; } = "";
    public string Product { get; private set; } = "";
    public string Manufacturer { get; private set; } = "";
    public string Serial { get; private set; } = "";
    public ushort VendorId { get; private set; }
    public ushort ProductId { get; private set; }
    public IReadOnlyList<HidAxis> Axes { get; }

    /// <summary>True once the device has gone away or the reader was disposed.</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Latest raw value of an axis, or false while the device has not reported it yet.</summary>
    public bool TryGetValue(int axisIndex, out int value)
    {
        value = Volatile.Read(ref _values[axisIndex]);
        return value != NoData;
    }

    public HidAxis? FindAxis(int usagePage, int usage) =>
        Axes.FirstOrDefault(a => a.UsagePage == usagePage && a.Usage == usage);

    /// <summary>Opens a HID device by its interface path. Returns null if it is not a readable device with axes.</summary>
    public static HidDeviceReader? Open(string path)
    {
        SafeFileHandle handle = HidNative.CreateFile(path, HidNative.GENERIC_READ,
            HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE, IntPtr.Zero, HidNative.OPEN_EXISTING,
            HidNative.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid) return null;

        IntPtr preparsed = IntPtr.Zero;
        try
        {
            if (!HidNative.HidD_GetPreparsedData(handle, out preparsed)) { handle.Dispose(); return null; }

            var caps = new ushort[32];
            if (HidNative.HidP_GetCaps(preparsed, caps) != HidNative.HIDP_STATUS_SUCCESS) throw new IOException("HidP_GetCaps failed");
            int reportLength = caps[HidNative.Caps.InputReportByteLength];
            List<HidAxis> axes = ReadAxes(preparsed, caps[HidNative.Caps.NumberInputValueCaps]);
            if (reportLength == 0 || axes.Count == 0) throw new IOException("Device has no input axes");

            var attributes = new HidNative.HIDD_ATTRIBUTES { Size = Marshal.SizeOf(typeof(HidNative.HIDD_ATTRIBUTES)) };
            HidNative.HidD_GetAttributes(handle, ref attributes);

            var reader = new HidDeviceReader(new FileStream(handle, FileAccess.Read, reportLength, true), preparsed, reportLength, axes)
            {
                Path = path,
                VendorId = attributes.VendorID,
                ProductId = attributes.ProductID,
                Product = ReadString(handle, HidNative.HidD_GetProductString),
                Manufacturer = ReadString(handle, HidNative.HidD_GetManufacturerString),
                Serial = ReadString(handle, HidNative.HidD_GetSerialNumberString),
            };
            reader.RequestInitialReport(path);
            Task.Run(reader.ReadLoopAsync);
            return reader;
        }
        catch (Exception)
        {
            if (preparsed != IntPtr.Zero) HidNative.HidD_FreePreparsedData(preparsed);
            handle.Dispose();
            return null;
        }
    }

    private delegate bool StringGetter(SafeFileHandle device, byte[] buffer, int length);

    private static string ReadString(SafeFileHandle handle, StringGetter getter)
    {
        var buffer = new byte[512];
        if (!getter(handle, buffer, buffer.Length)) return "";
        string text = Encoding.Unicode.GetString(buffer);
        int end = text.IndexOf('\0');
        return (end >= 0 ? text.Substring(0, end) : text).Trim();
    }

    private static List<HidAxis> ReadAxes(IntPtr preparsed, ushort count)
    {
        var found = new List<(ushort page, ushort usage, ushort link, int bits, int min, int max, string name)>();
        if (count == 0) return new List<HidAxis>();

        IntPtr buffer = Marshal.AllocHGlobal(count * HidNative.ValueCapsSize);
        try
        {
            if (HidNative.HidP_GetValueCaps(HidNative.HidP_Input, buffer, ref count, preparsed) != HidNative.HIDP_STATUS_SUCCESS)
                return new List<HidAxis>();

            for (int i = 0; i < count; i++)
            {
                // Field offsets of HIDP_VALUE_CAPS.
                IntPtr cap = buffer + i * HidNative.ValueCapsSize;
                ushort page = (ushort)Marshal.ReadInt16(cap, 0);
                ushort link = (ushort)Marshal.ReadInt16(cap, 6);
                bool isRange = Marshal.ReadByte(cap, 12) != 0;
                int bits = (ushort)Marshal.ReadInt16(cap, 18);
                int logicalMin = Marshal.ReadInt32(cap, 40);
                int logicalMax = Marshal.ReadInt32(cap, 44);
                ushort usageMin = (ushort)Marshal.ReadInt16(cap, 56);
                ushort usageMax = isRange ? (ushort)Marshal.ReadInt16(cap, 58) : usageMin;

                // Some descriptors declare an unsigned range through a "negative" maximum (e.g. 0..-1 for 16 bits).
                if (logicalMax < logicalMin && bits is > 0 and < 32) logicalMax = (1 << bits) - 1;
                if (bits < 2 || logicalMax <= logicalMin) continue;

                for (int usage = usageMin; usage <= usageMax; usage++)
                {
                    string? name = AxisName(page, (ushort)usage);
                    if (name == null) continue;
                    // DirectInput addresses axes by usage, so a second axis with the same usage cannot be calibrated separately.
                    if (found.Any(f => f.page == page && f.usage == usage)) continue;
                    found.Add((page, (ushort)usage, link, bits, logicalMin, logicalMax, name));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return found
            .OrderBy(f => f.page).ThenBy(f => f.usage)
            .Select((f, index) => new HidAxis(index, f.page, f.usage, f.link, f.bits, f.min, f.max, f.name))
            .ToList();
    }

    private static string? AxisName(ushort usagePage, ushort usage)
    {
        switch (usagePage)
        {
            case 0x01: // Generic Desktop
                switch (usage)
                {
                    case 0x30: return "X";
                    case 0x31: return "Y";
                    case 0x32: return "Z";
                    case 0x33: return "Rx";
                    case 0x34: return "Ry";
                    case 0x35: return "Rz";
                    case 0x36: return "Slider";
                    case 0x37: return "Dial";
                    case 0x38: return "Wheel";
                }
                break;
            case 0x02: // Simulation Controls
                switch (usage)
                {
                    case 0xBA: return "Rudder";
                    case 0xBB: return "Throttle";
                    case 0xC4: return "Accelerator";
                    case 0xC5: return "Brake";
                    case 0xC6: return "Clutch";
                    case 0xC8: return "Steering";
                }
                break;
        }
        return null;
    }

    /// <summary>
    /// Many controllers only send a report when something moves. Ask for the current state once so the
    /// bars are populated before the first pedal is touched. Devices that do not support it are simply ignored.
    /// </summary>
    private void RequestInitialReport(string path)
    {
        // A separate synchronous handle: hid.dll issues this request without an OVERLAPPED structure.
        using SafeFileHandle handle = HidNative.CreateFile(path, HidNative.GENERIC_READ,
            HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE, IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) return;

        var report = new byte[_reportLength];
        if (HidNative.HidD_GetInputReport(handle, report, report.Length)) ParseReport(report);
    }

    private async Task ReadLoopAsync()
    {
        var report = new byte[_reportLength];
        try
        {
            while (!IsClosed)
            {
                int read = await _stream.ReadAsync(report, 0, report.Length).ConfigureAwait(false);
                if (read == 0) break;
                if (read == report.Length) ParseReport(report);
            }
        }
        catch (Exception)
        {
            // Unplugged device or reader disposed: either way the stream is finished.
        }
        finally
        {
            Volatile.Write(ref _closed, 1);
            _stream.Dispose();
            HidNative.HidD_FreePreparsedData(_preparsed);
        }
    }

    private void ParseReport(byte[] report)
    {
        foreach (HidAxis axis in Axes)
        {
            int status = HidNative.HidP_GetUsageValue(HidNative.HidP_Input, axis.UsagePage, axis.LinkCollection, axis.Usage,
                out uint raw, _preparsed, report, (uint)report.Length);
            if (status != HidNative.HIDP_STATUS_SUCCESS) continue; // Axis lives in a different report ID.

            int value = (int)raw;
            if (axis.LogicalMin < 0 && axis.BitSize < 32)
            {
                int shift = 32 - axis.BitSize;
                value = (value << shift) >> shift; // sign-extend
            }
            Volatile.Write(ref _values[axis.Index], value);
        }
    }

    public void Dispose()
    {
        // Closing the stream aborts the pending read; the read loop then releases the native resources.
        if (Interlocked.Exchange(ref _closed, 1) == 0) _stream.Dispose();
    }
}
