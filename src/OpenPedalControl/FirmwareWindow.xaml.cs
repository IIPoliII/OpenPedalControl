using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using OpenPedalControl.Firmware;

namespace OpenPedalControl;

public partial class FirmwareWindow : Window
{
    private readonly FirmwareImage? _bundled;
    private FirmwareImage? _image;
    private FirmwareBackup? _backup;
    private FirmwareBackup? _resumeBackup;
    private FirmwareDevice? _device;
    private bool _busy, _checked, _writeFailed, _initializing = true;
    private bool _manualBootRequired, _resumeOnClose, _closingSafely;
    private StreamWriter? _journal;
    private readonly object _logSync = new object();
    private bool AlreadyInstalled => _image != null && !_image.IsRestore && _device?.InstalledVersion is Version installed && installed >= Version.Parse(_image.Version);
    private bool WantsInstall => !AlreadyInstalled || ForceReinstall.IsChecked == true || RestoreOriginal.IsChecked == true;

    public FirmwareWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _journal?.Dispose();
        try { _bundled = FirmwareImage.LoadBundled(); IncludedText.Text = "Included firmware: " + _bundled.Version; }
        catch (Exception ex) { Status.Text = ex.Message; }
        BackupPath.Text = FirmwareBackupLocator.Find() ?? "";
        _initializing = false;
        LoadFiles();
        DetectDevice();
    }
    private void DetectDevice()
    {
        try
        {
            var devices = FirmwareUsb.Enumerate();
            _device = devices.Count == 1 ? devices[0] : null;
            InstalledText.Text = _device?.InstalledVersion is Version version ? "Installed firmware: " + version
                : _device?.IsBootloader == true ? "Pedals are in update mode"
                : _device != null ? "Original controller firmware" : "Connect one pedal controller to USB";
            UpdateStatus();
        }
        catch (Exception ex) { _device = null; InstalledText.Text = "Pedals could not be detected"; Status.Text = ex.Message; }
        RefreshButtons();
    }
    private void OnRefresh(object sender, RoutedEventArgs e) { LoadFiles(); DetectDevice(); }
    private void OnBrowseBundle(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Firmware manifest (*.firmware.json)|*.firmware.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) { BundlePath.Text = dialog.FileName; LoadFiles(); }
    }
    private void OnBrowseBackup(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose your original OpenPedalControl backup folder" };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) { BackupPath.Text = dialog.SelectedPath; LoadFiles(); }
    }
    private void OnFilesChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _checked = false; _manualBootRequired = false;
        if (sender == RestoreOriginal || sender == ForceReinstall || sender == UseExternal) LoadFiles();
        else { _backup = null; _image = null; Status.Text = "Verify the selected files to continue."; RefreshButtons(); }
    }
    private bool LoadFiles()
    {
        _checked = false;
        _image = _bundled;
        try
        {
            _backup = FirmwareBackup.Load(BackupPath.Text);
            FirmwareBackupLocator.Remember(BackupPath.Text);
            BackupText.Text = "Original backup found and verified.";
        }
        catch (Exception) { _backup = null; BackupText.Text = "Original backup needed before installation."; }
        try
        {
            if (RestoreOriginal.IsChecked == true) _image = _backup == null ? null : FirmwareImage.Restore(_backup);
            else if (UseExternal.IsChecked == true) _image = FirmwareImage.LoadBundle(BundlePath.Text);
            Review.Text = _image == null ? "No compatible firmware loaded." :
                $"{_image.Version} · EFM8UB10F16G-QFN28 · {_image.PageCount} pages\nSHA-256: {_image.Sha256}\n" +
                (_image.IsRestore ? "Restores the original application and user settings." : $"3 axes · 12 bits · 4 ms USB · {_image.CpuHz / 1000000.0:0.0} MHz CPU. Existing profile is preserved.");
            UpdateStatus();
        }
        catch (Exception ex) { _image = null; Status.Text = "Firmware rejected: " + ex.Message; }
        RefreshButtons();
        return _image != null && _backup != null;
    }
    private void UpdateStatus()
    {
        Status.Text = AlreadyInstalled && !WantsInstall ? "Your pedals already have this firmware or a newer version. You can close this window."
            : _backup == null ? "Choose your original backup once. OpenPedalControl will remember it."
            : _device == null ? "Connect your pedals, then click Refresh."
            : _image?.IsRestore == true ? "Ready to prepare restoration of the original firmware and settings."
            : "Ready to prepare the installation. Your settings and curves will be kept.";
    }
    private void OnVerifyFiles(object sender, RoutedEventArgs e) => LoadFiles();
    private void EnsureJournal()
    {
        if (_journal != null) { Log("Selected image SHA-256 " + (_image?.Sha256 ?? "normal-mode return")); return; }
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenPedalControl", "firmware-sessions");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N") + ".log");
        _journal = new StreamWriter(new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        LogPath.Text = "Session log: " + file;
        Log("Session started. Image SHA-256 " + _image!.Sha256);
    }
    private void Log(string text)
    {
        lock (_logSync)
        {
            if (_journal == null) throw new IOException("Firmware journal is unavailable.");
            _journal.WriteLine(DateTime.UtcNow.ToString("O") + " " + text);
            _journal.Flush(); ((FileStream)_journal.BaseStream).Flush(true);
        }
    }
    private async Task Operate(Func<Task> action)
    {
        _busy = true; RefreshButtons();
        try { EnsureJournal(); await action(); }
        catch (Exception ex)
        {
            _checked = false;
            try { Log("Stopped: " + ex.Message); } catch (IOException) { }
            Status.Text = "Stopped: " + ex.Message + " No automatic retry was attempted.";
            Advanced.IsExpanded = true;
        }
        finally { _busy = false; RefreshButtons(); }
    }
    private async void OnPrepare(object sender, RoutedEventArgs e) => await PrepareAsync();
    private async Task PrepareAsync()
    {
        if (_busy || !LoadFiles() || !WantsInstall) return;
        await Operate(async () =>
        {
            Status.Text = "Preparing your pedals…";
            var devices = await Task.Run(FirmwareUsb.Enumerate);
            if (devices.Count != 1) throw new IOException("Connect only one pedal controller, then try again.");
            var device = devices[0];
            if (!device.IsBootloader && !device.IsCustom)
            {
                _manualBootRequired = true;
                Status.Text = "Hold FW/reset while reconnecting the pedal USB, then release the button and click Continue.";
                return;
            }
            if (device.IsCustom)
            {
                _device = device;
                if (!WantsInstall) { UpdateStatus(); return; }
                await Task.Run(FirmwareUsb.EnterBootloader);
                _resumeOnClose = true;
                _resumeBackup = _backup;
            }
            await ProbeAsync();
            if (_manualBootRequired) { _resumeOnClose = true; _resumeBackup = _backup; }
            _manualBootRequired = false;
        });
    }
    private async Task ProbeAsync()
    {
        await Task.Run(() =>
        {
            using var usb = FirmwareUsb.OpenBootloader();
            new FirmwareProgrammer(usb, false, Log).Probe(_backup!);
        });
        _checked = true;
        Status.Text = $"Ready to {(_image!.IsRestore ? "restore the original firmware" : "install firmware " + _image.Version)}. Nothing has been written yet.";
    }
    private async void OnEnterBoot(object sender, RoutedEventArgs e)
    {
        await Operate(async () =>
        {
            _checked = false;
            bool wasRunning = (await Task.Run(FirmwareUsb.Enumerate)).Any(d => d.IsCustom);
            await Task.Run(FirmwareUsb.EnterBootloader);
            _resumeOnClose |= wasRunning;
            if (wasRunning) _resumeBackup = _backup;
            Status.Text = "Update mode detected. Click Check controller.";
        });
    }
    private async void OnCheck(object sender, RoutedEventArgs e) => await Operate(ProbeAsync);
    private async void OnRestart(object sender, RoutedEventArgs e)
    {
        await Operate(async () =>
        {
            await Task.Run(() =>
            {
                using var usb = FirmwareUsb.OpenBootloader();
                var protocol = new FirmwareProgrammer(usb, false, Log);
                protocol.VerifyImage(_backup!, _image!); protocol.Run();
            });
            _checked = _resumeOnClose = false;
            Status.Text = "Your pedals have restarted. You can close this window.";
        });
    }
    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (!_checked || _image == null || _backup == null || _busy || !WantsInstall) return;
        FirmwareImage image = _image; FirmwareBackup backup = _backup;
        if (MessageBox.Show(this, $"{(image.IsRestore ? "Restore the original firmware" : "Install firmware " + image.Version)} on your pedals?\n\n" +
            (image.IsRestore ? "This also restores the original settings." : "Your saved calibration and curves will be kept.") +
            " Keep USB connected until installation finishes.", "Confirm firmware installation", MessageBoxButton.OKCancel,
            MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await Operate(async () =>
        {
            _checked = _resumeOnClose = false;
            Status.Text = "Installing and checking firmware. Keep USB connected…";
            Progress.Visibility = Visibility.Visible;
            var progress = new Progress<int>(value => Progress.Value = value);
            try
            {
                await Task.Run(() =>
                {
                    using var usb = FirmwareUsb.OpenBootloader();
                    var programmer = new FirmwareProgrammer(usb, true, Log);
                    programmer.Program(backup, image, value => ((IProgress<int>)progress).Report(value));
                    programmer.Run();
                });
                _writeFailed = false;
                await Task.Delay(1000);
                DetectDevice();
                Status.Text = "Firmware installed and verified. You can close this window.";
            }
            catch { _writeFailed = true; throw; }
        });
    }
    private void RefreshButtons()
    {
        if (InstallButton == null) return;
        bool files = _image != null && _backup != null;
        BundlePanel.IsEnabled = !_busy && UseExternal.IsChecked == true && RestoreOriginal.IsChecked != true;
        LocateBackupButton.Visibility = _backup == null ? Visibility.Visible : Visibility.Collapsed;
        LocateBackupButton.IsEnabled = BackupPath.IsEnabled = RestoreOriginal.IsEnabled = UseExternal.IsEnabled = ForceReinstall.IsEnabled = FilesButton.IsEnabled = RefreshButton.IsEnabled = !_busy;
        BootButton.IsEnabled = CheckButton.IsEnabled = files && !_busy;
        RestartButton.IsEnabled = files && !_busy && !_writeFailed;
        PrepareButton.Visibility = _checked ? Visibility.Collapsed : Visibility.Visible;
        PrepareButton.Content = !WantsInstall ? "Already installed" : _manualBootRequired ? "Continue" : "Prepare installation";
        PrepareButton.IsEnabled = files && _device != null && !_busy && WantsInstall;
        InstallButton.Visibility = _checked ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = files && _checked && !_busy && WantsInstall;
        CloseButton.IsEnabled = !_busy;
    }
    private async void OnClose(object sender, RoutedEventArgs e) => await CloseSafelyAsync();
    private async Task CloseSafelyAsync()
    {
        if (_busy) return;
        if (!_resumeOnClose) { Close(); return; }
        _closingSafely = true;
        bool restarted = false;
        await Operate(async () =>
        {
            Status.Text = "Returning your pedals to normal mode…";
            await Task.Run(() =>
            {
                using var usb = FirmwareUsb.OpenBootloader();
                var protocol = new FirmwareProgrammer(usb, false, Log);
                protocol.Probe(_resumeBackup!); protocol.Run();
            });
            restarted = true;
        });
        _resumeOnClose = _closingSafely = false;
        if (restarted) Close();
        else Status.Text += " Reconnect USB normally to return to your existing firmware, then close this window.";
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy) e.Cancel = true;
        else if (_resumeOnClose && !_closingSafely) { e.Cancel = true; _ = CloseSafelyAsync(); }
        base.OnClosing(e);
    }
}
