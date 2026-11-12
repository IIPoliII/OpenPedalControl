using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using OpenPedalControl.DirectInput;
using OpenPedalControl.Hid;
using OpenPedalControl.Model;

namespace OpenPedalControl.ViewModels;

public sealed class DeviceEntry
{
    public DeviceEntry(GameController controller, string label)
    {
        Controller = controller;
        Label = label;
    }

    public GameController Controller { get; }
    public string Label { get; }
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly DirectInputService _directInput = new DirectInputService();
    private HidDeviceReader? _reader;
    private GameController? _controller;
    private DeviceProfile? _profile;
    private DeviceEntry? _selectedDevice;
    private bool _updatingDeviceList;
    private bool _isDirty;
    private string _status = "";
    private string _deviceSummary = "";

    public MainViewModel()
    {
        SaveCommand = new RelayCommand(Save, () => HasDevice && IsDirty);
        ResetCommand = new RelayCommand(ResetTuning, () => HasDevice);
        RefreshCommand = new RelayCommand(() => RefreshDevices(true));
        OpenGameControllersCommand = new RelayCommand(OpenGameControllers);
    }

    public ObservableCollection<DeviceEntry> Devices { get; } = new ObservableCollection<DeviceEntry>();
    public ObservableCollection<ControlViewModel> Controls { get; } = new ObservableCollection<ControlViewModel>();

    public RelayCommand SaveCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenGameControllersCommand { get; }

    public DeviceEntry? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (ReferenceEquals(value, _selectedDevice)) return;
            _selectedDevice = value;
            Raise();
            if (!_updatingDeviceList) LoadDevice(value);
        }
    }

    /// <summary>Version shown in the header, e.g. "v0.1.0".</summary>
    public string Version { get; } = ReadVersion();

    public HidDeviceReader? Reader => _reader;
    public bool HasDevice => _reader != null;

    /// <summary>Some setting differs from what Windows has stored.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (Set(ref _isDirty, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string DeviceSummary { get => _deviceSummary; private set => Set(ref _deviceSummary, value); }

    // --- Devices ------------------------------------------------------------------------------------

    /// <summary>Re-reads the list of connected controllers. The open device is kept unless it vanished.</summary>
    public void RefreshDevices(bool reload = false)
    {
        List<GameController> controllers;
        try
        {
            controllers = _directInput.EnumerateControllers();
        }
        catch (Exception ex)
        {
            Status = "Could not list game controllers: " + ex.Message;
            return;
        }

        string? openPath = !reload && _reader != null && !_reader.IsClosed ? _controller?.HidPath : null;
        string? wantedPath = _selectedDevice?.Controller.HidPath;

        _updatingDeviceList = true;
        Devices.Clear();
        foreach (GameController controller in controllers)
        {
            int sameName = controllers.Count(c => c.Name == controller.Name);
            int index = controllers.Where(c => c.Name == controller.Name).ToList().IndexOf(controller) + 1;
            Devices.Add(new DeviceEntry(controller, sameName > 1 ? $"{controller.Name} ({index})" : controller.Name));
        }

        DeviceEntry? stillOpen = Find(openPath);
        if (stillOpen != null)
        {
            _controller = stillOpen.Controller;
            _selectedDevice = stillOpen;
            Raise(nameof(SelectedDevice));
            _updatingDeviceList = false;
            return;
        }

        DeviceEntry? next = Find(wantedPath) ?? Devices.OrderByDescending(Preference).FirstOrDefault();
        _selectedDevice = next;
        Raise(nameof(SelectedDevice));
        _updatingDeviceList = false;
        LoadDevice(next);
    }

    private DeviceEntry? Find(string? hidPath) =>
        hidPath == null ? null : Devices.FirstOrDefault(d => string.Equals(d.Controller.HidPath, hidPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>Pedals and handbrakes are picked over wheels and gamepads when the app starts.</summary>
    private static int Preference(DeviceEntry entry)
    {
        string name = entry.Controller.Name.ToUpperInvariant();
        if (name.StartsWith("HE ", StringComparison.Ordinal)) return 3;
        if (name.Contains("PEDAL")) return 2;
        if (name.Contains("HANDBRAKE")) return 1;
        return 0;
    }

    private void LoadDevice(DeviceEntry? entry)
    {
        CloseDevice();

        if (entry == null)
        {
            Status = "No game controller found. Plug in your pedals or handbrake.";
            NotifyDeviceChanged();
            return;
        }

        HidDeviceReader? reader = HidDeviceReader.Open(entry.Controller.HidPath);
        if (reader == null)
        {
            Status = $"Could not open {entry.Label}. It may have been unplugged.";
            NotifyDeviceChanged();
            return;
        }

        _reader = reader;
        _controller = entry.Controller;

        DeviceProfile? saved = ProfileStore.Load(reader.VendorId, reader.ProductId, reader.Serial);
        bool imported = false;
        _profile = saved ?? CreateProfile(entry.Controller, reader, out imported);
        _profile.DeviceName = entry.Controller.Name;

        var options = new List<AxisOption> { new AxisOption(null) };
        options.AddRange(reader.Axes.Select(axis => new AxisOption(axis)));

        var seen = new HashSet<HidAxis>();
        foreach (ControlSettings settings in _profile.Controls)
        {
            // A profile may refer to an axis this device does not have, or (hand-edited) use one twice.
            HidAxis? axis = settings.IsAssigned ? reader.FindAxis(settings.UsagePage, settings.Usage) : null;
            if (axis == null || !seen.Add(axis))
            {
                settings.UsagePage = 0;
                settings.Usage = 0;
            }

            var control = new ControlViewModel(settings, reader, options);
            control.SettingsChanged += (_, _) => UpdateDirty();
            control.AxisChanged += OnControlAxisChanged;
            Controls.Add(control);
        }

        RefreshApplied();

        string maker = string.IsNullOrEmpty(reader.Manufacturer) ? "" : reader.Manufacturer + "  ·  ";
        string serial = string.IsNullOrEmpty(reader.Serial) ? "" : "  ·  S/N " + reader.Serial;
        DeviceSummary = $"{maker}USB {reader.VendorId:X4}:{reader.ProductId:X4}{serial}";

        Status = saved != null ? (IsDirty ? "Profile loaded. It differs from what Windows has stored." : "Profile loaded.")
            : imported ? "Existing Windows calibration loaded."
            : "New device: run the calibration to get started.";
        NotifyDeviceChanged();
    }

    private DeviceProfile CreateProfile(GameController controller, HidDeviceReader reader, out bool imported)
    {
        var profile = new DeviceProfile
        {
            DeviceName = controller.Name,
            VendorId = reader.VendorId,
            ProductId = reader.ProductId,
            Serial = reader.Serial,
            Controls = DeviceTemplates.CreateDefault(controller.Name, reader.VendorId, reader.ProductId, reader.Axes),
        };

        // Take over a calibration that already exists in Windows (made with DIView or the control panel).
        imported = false;
        foreach (ControlSettings settings in profile.Controls)
        {
            HidAxis? axis = reader.FindAxis(settings.UsagePage, settings.Usage);
            if (axis == null) continue;
            if (!_directInput.TryGetCalibration(controller.InstanceGuid, axis.UsagePage, axis.Usage, out AxisCalibration existing)) continue;
            if (existing == DefaultCalibration(axis) || existing.Max <= existing.Min) continue;

            settings.RawRest = existing.Min;
            settings.RawFull = existing.Max;
            settings.BottomDeadzone = 0;
            settings.TopDeadzone = 0;
            settings.CurveMidpoint = Math.Round(CalibrationMath.MidpointOf(existing, false));
            imported = true;
        }
        return profile;
    }

    /// <summary>What Windows uses for an axis that was never calibrated: the full range with the center in the middle.</summary>
    private static AxisCalibration DefaultCalibration(HidAxis axis) =>
        new AxisCalibration(axis.LogicalMin, (int)Math.Floor((axis.LogicalMin + axis.LogicalMax + 1) / 2.0), axis.LogicalMax);

    private void CloseDevice()
    {
        _reader?.Dispose();
        _reader = null;
        _controller = null;
        _profile = null;
        Controls.Clear();
        DeviceSummary = "";
        IsDirty = false;
    }

    private void NotifyDeviceChanged()
    {
        Raise(nameof(HasDevice));
        Raise(nameof(Reader));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    // --- Live data ----------------------------------------------------------------------------------

    public void Tick()
    {
        if (_reader == null) return;
        if (_reader.IsClosed)
        {
            RefreshDevices(true);
            if (_reader == null) Status = "Device disconnected.";
            return;
        }
        foreach (ControlViewModel control in Controls) control.UpdateLive();
    }

    // --- Settings -----------------------------------------------------------------------------------

    private void OnControlAxisChanged(object? sender, EventArgs e)
    {
        if (sender is not ControlViewModel changed) return;

        // An axis can only drive one control.
        if (changed.Axis != null)
        {
            foreach (ControlViewModel other in Controls)
            {
                if (!ReferenceEquals(other, changed) && ReferenceEquals(other.Axis, changed.Axis)) other.Unassign();
            }
        }
        RefreshApplied();
    }

    private void RefreshApplied()
    {
        foreach (ControlViewModel control in Controls)
        {
            if (_controller != null && control.Axis is HidAxis axis &&
                _directInput.TryGetCalibration(_controller.InstanceGuid, axis.UsagePage, axis.Usage, out AxisCalibration applied))
            {
                control.SetApplied(applied);
            }
            else
            {
                control.SetApplied(null);
            }
        }
        UpdateDirty();
    }

    private void UpdateDirty() => IsDirty = Controls.Any(c => c.IsAssigned && !c.IsApplied);

    /// <summary>Writes the calibration of every pedal to Windows and stores the profile.</summary>
    public void Save()
    {
        if (_controller == null || _profile == null) return;
        try
        {
            foreach (ControlViewModel control in Controls)
            {
                if (control.Axis is HidAxis axis)
                    _directInput.SetCalibration(_controller.InstanceGuid, axis.UsagePage, axis.Usage, control.Computed);
            }
            ProfileStore.Save(_profile);
            RefreshApplied();
            Status = $"Saved to Windows at {DateTime.Now:T}. Restart your sim if it is already running.";
        }
        catch (Exception ex)
        {
            RefreshApplied();
            Status = "Could not save: " + ex.Message;
        }
    }

    private void ResetTuning()
    {
        foreach (ControlViewModel control in Controls) control.ResetTuning();
        Status = IsDirty ? "Deadzones and curves reset. Save to Windows to make it permanent." : "Deadzones and curves are at their defaults.";
    }

    public void ApplyWizardResults(IReadOnlyList<WizardResult> results)
    {
        if (results.Count == 0) return;
        foreach (WizardResult result in results) result.Control.ApplyMeasurement(result.Axis, result.Rest, result.Full);
        Save();
    }

    /// <summary>Puts every assigned axis back to Windows' uncalibrated default and forgets the profile.</summary>
    public void RemoveCalibration()
    {
        if (_controller == null || _reader == null) return;
        DeviceEntry? entry = _selectedDevice;
        try
        {
            foreach (ControlViewModel control in Controls)
            {
                if (control.Axis is HidAxis axis)
                    _directInput.SetCalibration(_controller.InstanceGuid, axis.UsagePage, axis.Usage, DefaultCalibration(axis));
            }
            string path = ProfileStore.GetPath(_reader.VendorId, _reader.ProductId, _reader.Serial);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            RefreshApplied();
            Status = "Could not remove the calibration: " + ex.Message;
            return;
        }

        LoadDevice(entry);
        if (HasDevice) Status = "Calibration removed. Windows uses the raw sensor range again.";
    }

    private static string ReadVersion()
    {
        Assembly assembly = typeof(MainViewModel).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                         ?? assembly.GetName().Version?.ToString(3) ?? "";
        int build = version.IndexOf('+'); // Drop the commit hash the SDK appends.
        return "v" + (build >= 0 ? version.Substring(0, build) : version);
    }

    private void OpenGameControllers()
    {
        try
        {
            Process.Start(new ProcessStartInfo("control.exe", "joy.cpl") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = "Could not open the Windows game controller panel: " + ex.Message;
        }
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _reader = null;
        _directInput.Dispose();
    }
}
