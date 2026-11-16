using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using OpenPedalControl.DirectInput;
using OpenPedalControl.Hid;
using OpenPedalControl.Model;
using OpenPedalControl.Updates;

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

    // Screen readers and UI automation read list items through ToString.
    public override string ToString() => Label;
}

public sealed class MainViewModel : ObservableObject, IDisposable
{

    private readonly DirectInputService _directInput = new DirectInputService();
    private HidDeviceReader? _reader;
    private GameController? _controller;
    private DeviceProfile? _profile;
    private string _savedSnapshot = "";
    private DeviceEntry? _selectedDevice;
    private bool _updatingDeviceList;
    private bool _isDirty;
    private string _status = "";
    private string _deviceSummary = "";
    private readonly AppSettings _settings = AppSettings.Load();
    private UpdateInfo? _update;
    private string _updateStatus = "";
    private bool _updateBusy;

    public MainViewModel()
    {
        SaveCommand = new RelayCommand(Save, () => HasDevice && IsDirty);
        ResetCommand = new RelayCommand(ResetTuning, () => HasDevice);
        RefreshCommand = new RelayCommand(() => RefreshDevices(true));
        OpenGameControllersCommand = new RelayCommand(() => Open("control.exe", "joy.cpl"));
        CheckUpdatesCommand = new RelayCommand(() => _ = CheckForUpdatesAsync(true), () => !_updateBusy);
        InstallUpdateCommand = new RelayCommand(() => _ = InstallUpdateAsync(), () => _update != null && !_updateBusy);
    }

    // --- Settings window ----------------------------------------------------------------------------

    public RelayCommand CheckUpdatesCommand { get; }
    public RelayCommand InstallUpdateCommand { get; }

    public bool CheckForUpdatesOnStart
    {
        get => _settings.CheckForUpdates;
        set
        {
            if (value == _settings.CheckForUpdates) return;
            _settings.CheckForUpdates = value;
            SaveSettings();
            Raise();
        }
    }

    public string CurrentVersionText => "Installed: " + Version + (Updater.IsInstalled ? "" : " (portable)");
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }
    public bool UpdateAvailable => _update != null;
    public string UpdateBannerText => _update == null ? "" : $"OpenPedalControl {_update.Version} is available.";

    private void SaveSettings()
    {
        try { _settings.Save(); }
        catch (Exception ex) { Status = "Could not save the settings: " + ex.Message; }
    }

    /// <summary>
    /// Records the running version. Returns the previous version when the app was updated since it last ran,
    /// or null (first run, or same version).
    /// </summary>
    public string? TakeUpdatedFromVersion()
    {
        string current = Version;
        string previous = _settings.LastVersion;
        if (previous == current) return null;
        _settings.LastVersion = current;
        SaveSettings();
        return previous.Length == 0 ? null : previous;
    }

    /// <summary>Called once at startup; respects the "check at startup" setting.</summary>
    public void CheckForUpdatesAtStartup()
    {
        if (_settings.CheckForUpdates) _ = CheckForUpdatesAsync(false);
    }

    private async System.Threading.Tasks.Task CheckForUpdatesAsync(bool userAsked)
    {
        SetUpdateBusy(true);
        UpdateStatus = "Checking for updates…";
        try
        {
            _update = await Updater.CheckAsync();
            UpdateStatus = _update == null ? $"You have the latest version ({Version})." : $"Version {_update.Version} is available.";
        }
        catch (Exception ex)
        {
            _update = null;
            UpdateStatus = userAsked ? "Could not check for updates: " + ex.Message : "";
        }
        finally
        {
            SetUpdateBusy(false);
            Raise(nameof(UpdateAvailable));
            Raise(nameof(UpdateBannerText));
        }
    }

    /// <summary>Raised when an update was downloaded and the app must exit so it can be applied.</summary>
    public event EventHandler? ExitForUpdate;

    private async System.Threading.Tasks.Task InstallUpdateAsync()
    {
        if (_update == null) return;
        SetUpdateBusy(true);
        UpdateStatus = $"Downloading version {_update.Version}…";
        try
        {
            string file = await Updater.DownloadAsync(_update);
            if (HasDevice && IsDirty)
            {
                Save();
                if (IsDirty)
                {
                    UpdateStatus = "Update deferred. " + Status;
                    return;
                }
            }
            Updater.ApplyAfterExit(file);
            UpdateStatus = "Installing the update. OpenPedalControl restarts by itself.";
            ExitForUpdate?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            UpdateStatus = "Update failed: " + ex.Message;
            Open(_update.PageUrl, null);
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    private void SetUpdateBusy(bool busy)
    {
        _updateBusy = busy;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
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
    public bool HasDevice => _reader != null && _profile != null;

    /// <summary>Some setting is not saved yet.</summary>
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

    // --- Output mode --------------------------------------------------------------------------------

    public bool IsOnboardMode => _reader?.IsOnboard == true;
    public bool IsWindowsMode => !IsOnboardMode;

    public bool StartWithWindows
    {
        get => AutoStart.IsEnabled;
        set
        {
            try
            {
                AutoStart.SetEnabled(value);
            }
            catch (Exception ex)
            {
                Status = "Could not change the Windows startup setting: " + ex.Message;
            }
            Raise();
        }
    }

    private void RaiseModeChanged()
    {
        Raise(nameof(IsWindowsMode));
        Raise(nameof(IsOnboardMode));
        Raise(nameof(SaveText));
        Raise(nameof(SaveHint));
        Raise(nameof(RemoveCalibrationHint));
    }

    public string SaveText => IsOnboardMode ? "Save to controller" : "Save to Windows";
    public string SaveHint => IsOnboardMode ? "Store the calibration and curves in the controller's flash memory."
        : "Write the calibration to Windows for every game.";
    public string RemoveCalibrationHint => IsOnboardMode ? "Restore the controller's default input mapping, calibration and curves."
        : "Delete this device's calibration from Windows and start over.";

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
        if (name.Contains("HANDBRAKE") || name.Contains("BU0836")) return 1;
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

        DeviceProfile? saved;
        if (reader.IsOnboard)
        {
            try
            {
                saved = reader.ReadOnboardProfile();
            }
            catch (Exception ex)
            {
                Status = "Could not read the controller profile. Use Refresh to retry. " + ex.Message;
                RaiseModeChanged();
                NotifyDeviceChanged();
                return;
            }
        }
        else saved = ProfileStore.Load(reader.VendorId, reader.ProductId, reader.Serial);
        bool imported = false;
        if (saved == null)
        {
            _profile = CreateProfile(entry.Controller, reader, out imported);
            ProfileStore.Adopt(_profile);
        }
        else
        {
            _profile = saved;
        }
        // Profiles from the removed virtual output mode now use the real device.
        _profile.Output = (IsOnboardMode ? OutputMode.Onboard : OutputMode.Windows).ToString();
        _profile.DeviceName = entry.Controller.Name;
        _savedSnapshot = ProfileStore.Snapshot(_profile);
        OutputMode mode = IsOnboardMode ? OutputMode.Onboard : OutputMode.Windows;

        var options = new List<AxisOption> { new AxisOption(null) };
        options.AddRange(reader.Axes.Select(axis => new AxisOption(axis, IsOnboardMode ? $"LC {axis.Index + 1}" : null)));

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
            control.SetOutputMode(mode);
            control.SettingsChanged += (_, _) => UpdateDirty();
            control.AxisChanged += OnControlAxisChanged;
            Controls.Add(control);
        }

        RefreshApplied();

        RaiseModeChanged();

        string maker = string.IsNullOrEmpty(reader.Manufacturer) ? "" : reader.Manufacturer + "  ·  ";
        string serial = string.IsNullOrEmpty(reader.Serial) ? "" : "  ·  S/N " + reader.Serial;
        DeviceSummary = $"{maker}USB {reader.VendorId:X4}:{reader.ProductId:X4}{serial}";

        Status = IsOnboardMode ? "Profile read from the controller."
            : saved != null ? (IsDirty ? "Profile loaded. It differs from what Windows has stored." : "Profile loaded.")
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
        _savedSnapshot = "";
        Controls.Clear();
        DeviceSummary = "";
        IsDirty = false;
        RaiseModeChanged();
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
        if (IsOnboardMode)
        {
            UpdateDirty();
            return;
        }
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

    private void UpdateDirty()
    {
        if (_profile == null)
        {
            IsDirty = false;
            return;
        }
        bool profileChanged = ProfileStore.Snapshot(_profile) != _savedSnapshot;
        bool calibrationDiffers = IsWindowsMode && Controls.Any(c => c.IsAssigned && !c.IsApplied);
        IsDirty = profileChanged || calibrationDiffers;
        if (IsOnboardMode)
            foreach (ControlViewModel control in Controls) control.SetOnboardSaved(!profileChanged);
    }

    /// <summary>
    /// Saves to the storage used by the selected device and output mode.
    /// </summary>
    public void Save()
    {
        if (_controller == null || _profile == null) return;
        try
        {
            if (IsOnboardMode)
            {
                _reader!.SaveOnboardProfile(_profile);
                _savedSnapshot = ProfileStore.Snapshot(_profile);
                UpdateDirty();
                Status = $"Saved to the controller at {DateTime.Now:T}.";
                return;
            }
            if (IsWindowsMode)
            {
                foreach (ControlViewModel control in Controls)
                {
                    if (control.Axis is HidAxis axis)
                        _directInput.SetCalibration(_controller.InstanceGuid, axis.UsagePage, axis.Usage, control.Computed);
                }
            }
            ProfileStore.Save(_profile);
            _savedSnapshot = ProfileStore.Snapshot(_profile);
            RefreshApplied();

            Status = $"Saved to Windows at {DateTime.Now:T}. Restart your sim if it is already running.";
        }
        catch (Exception ex)
        {
            RefreshApplied();
            Status = "Could not save: " + ex.Message;
        }
    }

    /// <summary>Throws away unsaved edits of the open device and reloads it from its saved profile.</summary>
    public void RevertUnsaved()
    {
        if (_reader == null) return;
        if (!IsOnboardMode) ProfileStore.Forget(_reader.VendorId, _reader.ProductId, _reader.Serial);
        LoadDevice(_selectedDevice);
    }

    private void ResetTuning()
    {
        foreach (ControlViewModel control in Controls) control.ResetTuning();
        Status = IsDirty ? "Deadzones and curves reset. Save to make it permanent." : "Deadzones and curves are at their defaults.";
    }

    public void ApplyWizardResults(IReadOnlyList<WizardResult> results)
    {
        if (results.Count == 0) return;
        foreach (WizardResult result in results) result.Control.ApplyMeasurement(result.Axis, result.Rest, result.Full);
        Save();
    }

    /// <summary>Restores the device's default onboard profile or removes its Windows calibration and local profile.</summary>
    public void RemoveCalibration()
    {
        if (_controller == null || _reader == null || _profile == null) return;
        DeviceEntry? entry = _selectedDevice;
        try
        {
            if (IsOnboardMode)
            {
                _reader.ResetOnboardProfile();
                LoadDevice(entry);
                if (HasDevice) Status = "Default profile restored on the controller.";
                return;
            }
            foreach (ControlViewModel control in Controls)
            {
                if (control.Axis is HidAxis axis)
                    _directInput.SetCalibration(_controller.InstanceGuid, axis.UsagePage, axis.Usage, DefaultCalibration(axis));
            }
            ProfileStore.Delete(_reader.VendorId, _reader.ProductId, _reader.Serial);
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

    private void Open(string target, string? arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target, arguments ?? "") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = "Could not open " + target + ": " + ex.Message;
        }
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _reader = null;

        _directInput.Dispose();
    }
}
