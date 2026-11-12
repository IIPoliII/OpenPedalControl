using System;
using System.Collections.Generic;
using System.Linq;
using OpenPedalControl.DirectInput;
using OpenPedalControl.Hid;
using OpenPedalControl.Model;

namespace OpenPedalControl.VJoy;

public enum VirtualOutputState
{
    /// <summary>No connected device is set to virtual output.</summary>
    Idle,
    Active,
    Unavailable,
}

/// <summary>
/// Feeds vJoy from every connected device whose profile is in virtual mode. Runs independently of the
/// window, so it keeps working while the app sits in the tray. Each input report is converted and sent on
/// the device's own reader thread, so latency is that of the hardware.
/// </summary>
public sealed class VirtualOutput : IDisposable
{
    private sealed class Binding
    {
        public Binding(ControlSettings settings, HidAxis axis, int virtualAxis)
        {
            Settings = settings;
            Axis = axis;
            VirtualAxis = virtualAxis;
        }

        public ControlSettings Settings { get; }
        public HidAxis Axis { get; }
        public int VirtualAxis { get; }
    }

    private readonly object _lock = new object();
    private readonly Dictionary<string, HidDeviceReader> _readers = new Dictionary<string, HidDeviceReader>(StringComparer.OrdinalIgnoreCase);
    private Dictionary<HidDeviceReader, Binding[]> _bindings = new Dictionary<HidDeviceReader, Binding[]>();
    private VJoyDevice? _vjoy;
    private HashSet<int> _usedAxes = new HashSet<int>();
    private uint _requestedDevice;

    /// <summary>vJoy device to use (1..16), or 0 for the first free one so devices of other programs are never taken.</summary>
    public uint RequestedDevice
    {
        get => _requestedDevice;
        set
        {
            lock (_lock)
            {
                if (_requestedDevice == value) return;
                _requestedDevice = value;
                ReleaseVJoy();
            }
            Rebuild();
        }
    }

    /// <summary>The vJoy device currently fed, or 0.</summary>
    public uint CurrentDeviceId => _vjoy?.Id ?? 0;

    public VirtualOutputState State { get; private set; } = VirtualOutputState.Idle;
    public string Problem { get; private set; } = "";

    /// <summary>Number of connected devices currently sent to vJoy.</summary>
    public int ActiveDeviceCount { get; private set; }

    /// <summary>The vJoy axis usages that exist on the virtual device (empty while it is not taken).</summary>
    public bool HasVirtualAxis(int usage) => _vjoy?.HasAxis(usage) ?? false;

    /// <summary>Opens readers for the connected controllers and closes those of unplugged ones.</summary>
    public void Sync(IEnumerable<GameController> controllers)
    {
        lock (_lock)
        {
            var paths = new HashSet<string>(controllers.Select(c => c.HidPath), StringComparer.OrdinalIgnoreCase);
            foreach (string gone in _readers.Keys.Where(p => !paths.Contains(p) || _readers[p].IsClosed).ToList())
            {
                _readers[gone].ReportReceived -= OnReport;
                _readers[gone].Dispose();
                _readers.Remove(gone);
            }

            foreach (string path in paths)
            {
                if (_readers.ContainsKey(path)) continue;
                HidDeviceReader? reader = HidDeviceReader.Open(path);
                if (reader == null) continue;
                reader.ReportReceived += OnReport;
                _readers[path] = reader;
            }
        }
        Rebuild();
    }

    /// <summary>Re-reads which controls go where. Call after any change to output mode or axis assignment.</summary>
    public void Rebuild()
    {
        lock (_lock)
        {
            var bindings = new Dictionary<HidDeviceReader, Binding[]>();
            foreach (HidDeviceReader reader in _readers.Values.Where(r => !r.IsClosed))
            {
                DeviceProfile? profile = ProfileStore.Load(reader.VendorId, reader.ProductId, reader.Serial);
                if (profile == null || profile.GetOutputMode() != OutputMode.Virtual) continue;

                Binding[] list = profile.Controls
                    .Where(c => c.IsAssigned && c.VirtualAxis != 0)
                    .Select(c => (settings: c, axis: reader.FindAxis(c.UsagePage, c.Usage)))
                    .Where(x => x.axis != null)
                    .Select(x => new Binding(x.settings, x.axis!, x.settings.VirtualAxis))
                    .ToArray();
                if (list.Length > 0) bindings[reader] = list;
            }

            var usedAxes = new HashSet<int>(bindings.Values.SelectMany(b => b).Select(b => b.VirtualAxis));
            ActiveDeviceCount = bindings.Count;

            if (bindings.Count == 0)
            {
                _bindings = bindings;
                ReleaseVJoy();
                State = VirtualOutputState.Idle;
                Problem = "";
                return;
            }

            if (_vjoy == null)
            {
                _vjoy = AcquireVJoy(out string problem);
                if (_vjoy == null)
                {
                    _bindings = new Dictionary<HidDeviceReader, Binding[]>();
                    State = VirtualOutputState.Unavailable;
                    Problem = problem;
                    return;
                }
            }

            // Axes that are no longer driven go back to rest.
            foreach (int usage in _usedAxes.Where(u => !usedAxes.Contains(u))) _vjoy.SetAxis(usage, 0);
            _usedAxes = usedAxes;
            _bindings = bindings;

            List<string> missing = usedAxes.Where(u => !_vjoy.HasAxis(u)).Select(VJoyDevice.AxisName).ToList();
            State = VirtualOutputState.Active;
            Problem = missing.Count == 0 ? "" : $"vJoy device {_vjoy.Id} has no {string.Join(", ", missing)} axis. Add it in “Configure vJoy”.";

            foreach (HidDeviceReader reader in bindings.Keys) Send(reader, bindings[reader]);
        }
    }

    private void OnReport(HidDeviceReader reader)
    {
        // Reads a snapshot of the dictionary reference; Rebuild replaces it rather than changing it.
        if (_bindings.TryGetValue(reader, out Binding[]? list)) Send(reader, list);
    }

    private void Send(HidDeviceReader reader, Binding[] list)
    {
        VJoyDevice? vjoy = _vjoy;
        if (vjoy == null) return;
        foreach (Binding binding in list)
        {
            if (!reader.TryGetValue(binding.Axis.Index, out int raw)) continue;
            vjoy.SetAxis(binding.VirtualAxis, Compute(raw, binding.Settings));
        }
    }

    /// <summary>The value sent to vJoy (0..1) for a raw reading.</summary>
    public static double Compute(int raw, ControlSettings settings)
    {
        double travel = CalibrationMath.Travel(raw, settings.RawRest, settings.RawFull, settings.BottomDeadzone, settings.TopDeadzone);
        return Curve.Evaluate(settings.CurvePoints, travel);
    }

    private VJoyDevice? AcquireVJoy(out string problem)
    {
        if (_requestedDevice != 0) return VJoyDevice.Acquire(_requestedDevice, out problem);

        problem = "vJoy is not installed.";
        if (!VJoyDevice.IsDriverAvailable) return null;
        foreach ((uint id, VJoyStatus status) in VJoyDevice.ListDevices())
        {
            if (status != VJoyStatus.Free && status != VJoyStatus.Own) continue;
            VJoyDevice? device = VJoyDevice.Acquire(id, out problem);
            if (device != null) return device;
        }
        problem = "Every vJoy device is in use by another program. Add a device in “Configure vJoy” (Settings).";
        return null;
    }

    private void ReleaseVJoy()
    {
        _vjoy?.Dispose();
        _vjoy = null;
        _usedAxes = new HashSet<int>();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _bindings = new Dictionary<HidDeviceReader, Binding[]>();
            foreach (HidDeviceReader reader in _readers.Values)
            {
                reader.ReportReceived -= OnReport;
                reader.Dispose();
            }
            _readers.Clear();
            ReleaseVJoy();
        }
    }
}
