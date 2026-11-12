using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpenPedalControl.VJoy;

public enum VJoyStatus
{
    /// <summary>Taken by this app.</summary>
    Own = 0,
    Free = 1,
    /// <summary>Taken by another program.</summary>
    Busy = 2,
    /// <summary>Not set up in “Configure vJoy”.</summary>
    Missing = 3,
    Unknown = 4,
}

/// <summary>
/// Minimal binding to vJoy (github.com/BrunnerInnovation/vJoy), a signed virtual joystick driver.
/// vJoyInterface.dll is loaded from the vJoy installation, so nothing of vJoy ships with this app.
/// </summary>
public sealed class VJoyDevice : IDisposable
{
    /// <summary>vJoy supports up to 16 virtual devices.</summary>
    public const uint MaxDevices = 16;

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8E31F76F-74C3-47F1-9550-E041EEDC5FBB}_is1";

    private static readonly object LoadLock = new object();
    private static bool? _loaded;

    private readonly object _writeLock = new object();
    private readonly Dictionary<int, (int min, int max)> _ranges = new Dictionary<int, (int min, int max)>();
    private bool _owned;

    private VJoyDevice(uint id) => Id = id;

    public uint Id { get; }

    /// <summary>Virtual axes and their HID usages, in the order vJoy lists them.</summary>
    public static readonly IReadOnlyList<(int usage, string name)> AxisNames = new[]
    {
        (0x30, "X"), (0x31, "Y"), (0x32, "Z"), (0x33, "Rx"), (0x34, "Ry"), (0x35, "Rz"), (0x36, "Slider"), (0x37, "Dial"),
    };

    public static string AxisName(int usage) => AxisNames.FirstOrDefault(a => a.usage == usage).name ?? "Off";

    /// <summary>True when the vJoy driver is installed and enabled.</summary>
    public static bool IsDriverAvailable
    {
        get
        {
            if (!EnsureLoaded()) return false;
            try { return Native.vJoyEnabled(); }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Takes ownership of a vJoy device. Returns null with a reason when that is not possible.</summary>
    public static VJoyDevice? Acquire(uint id, out string problem)
    {
        problem = "";
        if (!IsDriverAvailable)
        {
            problem = "vJoy is not installed.";
            return null;
        }

        switch (GetStatus(id))
        {
            case VJoyStatus.Own:
            case VJoyStatus.Free:
                break;
            case VJoyStatus.Busy:
                problem = $"vJoy device {id} is in use by another program.";
                return null;
            default:
                problem = $"vJoy device {id} is not set up. Enable it in “Configure vJoy”.";
                return null;
        }

        if (!Native.AcquireVJD(id))
        {
            problem = $"Could not take vJoy device {id}.";
            return null;
        }

        var device = new VJoyDevice(id) { _owned = true };
        Native.ResetVJD(id);
        foreach ((int usage, _) in AxisNames)
        {
            if (!Native.GetVJDAxisExist(id, (uint)usage)) continue;
            int min = 0, max = 0x8000;
            Native.GetVJDAxisMin(id, (uint)usage, ref min);
            Native.GetVJDAxisMax(id, (uint)usage, ref max);
            if (max > min) device._ranges[usage] = (min, max);
        }
        return device;
    }

    public static VJoyStatus GetStatus(uint id)
    {
        if (!EnsureLoaded()) return VJoyStatus.Missing;
        try { return (VJoyStatus)Native.GetVJDStatus(id); }
        catch (Exception) { return VJoyStatus.Unknown; }
    }

    /// <summary>The vJoy devices that are set up, with who is using them.</summary>
    public static IReadOnlyList<(uint id, VJoyStatus status)> ListDevices()
    {
        var list = new List<(uint id, VJoyStatus status)>();
        if (!IsDriverAvailable) return list;
        for (uint id = 1; id <= MaxDevices; id++)
        {
            VJoyStatus status = GetStatus(id);
            if (status != VJoyStatus.Missing && status != VJoyStatus.Unknown) list.Add((id, status));
        }
        return list;
    }

    /// <summary>vJoy's own configuration tool, used to add devices or axes; null when not found.</summary>
    public static string? ConfigToolPath => InstallFolders()
        .Select(folder => Path.Combine(folder, Environment.Is64BitOperatingSystem ? "x64" : "x86", "vJoyConf.exe"))
        .FirstOrDefault(File.Exists);

    public bool HasAxis(int usage) => _ranges.ContainsKey(usage);

    /// <summary>Sets an axis from a 0..1 position. Safe to call from any thread.</summary>
    public void SetAxis(int usage, double position)
    {
        if (!_ranges.TryGetValue(usage, out (int min, int max) range)) return;
        position = position < 0 ? 0 : position > 1 ? 1 : position;
        int value = range.min + (int)Math.Round((range.max - range.min) * position);
        lock (_writeLock)
        {
            if (_owned) Native.SetAxis(value, Id, (uint)usage);
        }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (!_owned) return;
            _owned = false;
            Native.ResetVJD(Id);
            Native.RelinquishVJD(Id);
        }
    }

    private static bool EnsureLoaded()
    {
        lock (LoadLock)
        {
            if (_loaded.HasValue) return _loaded.Value;
            _loaded = false;
            foreach (string folder in InstallFolders())
            {
                string dll = Path.Combine(folder, Environment.Is64BitProcess ? "x64" : "x86", "vJoyInterface.dll");
                if (File.Exists(dll) && LoadLibrary(dll) != IntPtr.Zero)
                {
                    _loaded = true;
                    break;
                }
            }
            return _loaded.Value;
        }
    }

    private static IEnumerable<string> InstallFolders()
    {
        using (RegistryKey? key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(UninstallKey))
        {
            if (key?.GetValue("InstallLocation") is string location && location.Length > 0) yield return location;
        }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string fileName);


    // Resolved against the vJoyInterface.dll already loaded by EnsureLoaded.
    private static class Native
    {
        private const string Dll = "vJoyInterface.dll";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool vJoyEnabled();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetVJDStatus(uint id);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AcquireVJD(uint id);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void RelinquishVJD(uint id);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ResetVJD(uint id);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVJDAxisExist(uint id, uint axis);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVJDAxisMin(uint id, uint axis, ref int min);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVJDAxisMax(uint id, uint axis, ref int max);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetAxis(int value, uint id, uint axis);
    }
}
