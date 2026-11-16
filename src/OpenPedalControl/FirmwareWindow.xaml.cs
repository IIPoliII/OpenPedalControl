using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using OpenPedalControl.Firmware;

namespace OpenPedalControl;

public partial class FirmwareWindow : Window
{
    private FirmwareImage? _image;
    private FirmwareBackup? _backup;
    private bool _busy, _checked, _writeFailed;
    private StreamWriter? _journal;
    private readonly object _logSync = new object();

    public FirmwareWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _journal?.Dispose();
        RefreshButtons();
    }
    private void OnBrowseBundle(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Firmware manifest (*.firmware.json)|*.firmware.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) BundlePath.Text = dialog.FileName;
    }
    private void OnBrowseBackup(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Folder with the original program and nonvolatile BIN backups" };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) BackupPath.Text = dialog.SelectedPath;
    }
    private void OnFilesChanged(object sender, RoutedEventArgs e)
    {
        _image = null; _backup = null; _checked = false;
        if (Review == null) return; // XAML child initialization.
        Review.Text = "Files changed. Verify the selected files before continuing.";
        RefreshButtons();
    }
    private void OnVerifyFiles(object sender, RoutedEventArgs e)
    {
        try
        {
            _backup = FirmwareBackup.Load(BackupPath.Text);
            _image = RestoreOriginal.IsChecked == true ? FirmwareImage.Restore(_backup) : FirmwareImage.LoadBundle(BundlePath.Text);
            _checked = false; Progress.Value = 0;
            Review.Text = $"{(_image.IsRestore ? "Restore" : "Install/update")} · {_image.Version}\nEFM8UB10F16G-QFN28 · {_image.PageCount} flash pages\nSHA-256: {_image.Sha256}\n" +
                (_image.IsRestore ? "Original application and user settings from the verified backup." : $"Profile v1 · 3 axes · 12-bit range · 4 ms USB interval · {_image.CpuHz / 1000000.0:0.0} MHz CPU (bundle specification). Four auxiliary inputs and button inputs are not implemented.");
            Status.Text = "File integrity and address ranges verified. Check the controller next. No flash written.";
        }
        catch (Exception ex) { _image = null; _backup = null; Status.Text = "Files rejected: " + ex.Message; }
        RefreshButtons();
    }
    private void EnsureJournal()
    {
        if (_journal != null) { Log("Selected image SHA-256 " + _image!.Sha256); return; }
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
            _journal.Flush();
            ((FileStream)_journal.BaseStream).Flush(true);
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
            Status.Text = "Stopped: " + ex.Message + " No automatic retry or restart. Use FW/reset on reconnection if needed. See the session log.";
        }
        finally { _busy = false; RefreshButtons(); }
    }
    private async void OnEnterBoot(object sender, RoutedEventArgs e)
    {
        await Operate(async () =>
        {
            _checked = false; Status.Text = "Requesting update mode…";
            await Task.Run(FirmwareUsb.EnterBootloader);
            Log("Update mode detected. No flash erase/program command sent.");
            Status.Text = "Update mode detected. Click Check controller.";
        });
    }
    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        await Operate(async () =>
        {
            _checked = false; Status.Text = "Checking chip, bootloader and protected pages…";
            await Task.Run(() =>
            {
                using var usb = FirmwareUsb.OpenBootloader();
                new FirmwareProgrammer(usb, false, Log).Probe(_backup!);
            });
            _checked = true;
            Status.Text = "Controller verified against the backup. Ready to review installation. No flash written.";
        });
    }
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
            _checked = false; Status.Text = "Controller restart requested. Close this assistant to reload the device.";
        });
    }
    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (!_checked || _image == null || _backup == null || _busy) return;
        FirmwareImage image = _image; FirmwareBackup backup = _backup;
        if (MessageBox.Show(this, $"{(image.IsRestore ? "Restore" : "Install")} {image.Version} on the verified pedal controller?\n\nSHA-256: {image.Sha256}\n\nThe application will be erased and replaced. " +
            (image.IsRestore ? "Original user settings will also be restored." : "Existing profile data is preserved.") +
            " Bootloader and protection pages are excluded. Keep USB connected.", "Confirm firmware installation", MessageBoxButton.OKCancel,
            MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await Operate(async () =>
        {
            _checked = false; Status.Text = "Programming and verifying the application. Keep USB connected…";
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
                Status.Text = "Firmware verified; restart requested. Close this assistant, then calibrate the pedals and save to controller.";
            }
            catch { _writeFailed = true; throw; }
        });
    }
    private void RefreshButtons()
    {
        if (InstallButton == null) return;
        bool files = _image != null && _backup != null;
        BundlePanel.IsEnabled = !_busy && RestoreOriginal.IsChecked != true;
        BackupBrowseButton.IsEnabled = BackupPath.IsEnabled = RestoreOriginal.IsEnabled = FilesButton.IsEnabled = !_busy;
        BootButton.IsEnabled = CheckButton.IsEnabled = files && !_busy;
        RestartButton.IsEnabled = files && !_busy && !_writeFailed;
        InstallButton.IsEnabled = files && _checked && !_busy;
        CloseButton.IsEnabled = !_busy;
    }
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy) e.Cancel = true;
        base.OnClosing(e);
    }
}
