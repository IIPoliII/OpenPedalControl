using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using OpenPedalControl.ViewModels;

namespace OpenPedalControl;

public partial class MainWindow : Window
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVNODES_CHANGED = 0x0007;

    private readonly MainViewModel _viewModel = new MainViewModel();
    private readonly DispatcherTimer _liveTimer;
    private readonly DispatcherTimer _deviceChangeTimer;
    private TrayIcon? _tray;
    private bool _exiting;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        _liveTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _liveTimer.Tick += (_, _) =>
        {
            if (IsVisible) _viewModel.Tick();
        };

        // Plugging a device in produces a burst of notifications; wait for it to settle before re-scanning.
        _deviceChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _deviceChangeTimer.Tick += (_, _) =>
        {
            _deviceChangeTimer.Stop();
            _viewModel.RefreshDevices();
        };
    }

    /// <summary>True once the window finished starting, so later errors need not end the app.</summary>
    public bool IsInitializedForUse { get; private set; }

    /// <summary>Starts device handling. When hidden, the app lives in the tray and only feeds vJoy.</summary>
    public void Start(bool hidden)
    {
        // A window handle is needed for plug/unplug notifications even while the window stays hidden.
        new WindowInteropHelper(this).EnsureHandle();
        _tray = new TrayIcon(ShowFromTray, ExitApp, () => _viewModel.StartWithWindows, v => _viewModel.StartWithWindows = v);
        _viewModel.ExitForUpdate += (_, _) => ExitForUpdate();
        _viewModel.RefreshDevices();
        _liveTimer.Start();
        IsInitializedForUse = true;
        _viewModel.CheckForUpdatesAtStartup();
        if (!hidden) Show();

        string? updatedFrom = _viewModel.TakeUpdatedFromVersion();
        if (updatedFrom == null) return;
        if (hidden)
        {
            // Started in the tray (after an update or with Windows): a notification, the full log on click.
            _tray.ShowMessage($"OpenPedalControl updated to {_viewModel.Version}",
                "Click here to see what's new.", () => ShowChangelog(updatedFrom));
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(() => ShowChangelog(updatedFrom)), DispatcherPriority.ApplicationIdle);
        }
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(WindowProc);
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_DEVICECHANGE && wParam.ToInt64() == DBT_DEVNODES_CHANGED)
        {
            _deviceChangeTimer.Stop();
            _deviceChangeTimer.Start();
        }
        return IntPtr.Zero;
    }

    private void OnCalibrateClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Reader == null || _viewModel.Controls.Count == 0) return;

        var wizard = new CalibrationWizard(_viewModel.Reader, _viewModel.Controls.ToList()) { Owner = this };
        if (wizard.ShowDialog() == true) _viewModel.ApplyWizardResults(wizard.Results);
    }

    /// <summary>The "What's new" window; <paramref name="updatedFrom"/> highlights what changed since that version.</summary>
    public void ShowChangelog(string? updatedFrom)
    {
        ShowFromTray();
        new ChangelogWindow(_viewModel.Version, updatedFrom) { Owner = this }.ShowDialog();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (!IsVisible) ShowFromTray();
        new SettingsWindow(_viewModel, () => ShowChangelog(null)) { Owner = this }.ShowDialog();
        _viewModel.RefreshDevices(); // vJoy may have been installed meanwhile.
    }

    private void OnRemoveCalibrationClick(object sender, RoutedEventArgs e)
    {
        MessageBoxResult answer = MessageBox.Show(this,
            "Remove this device's calibration and settings?\n\nGames will see the raw sensor range again until you calibrate anew.",
            "Remove calibration", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK) _viewModel.RemoveCalibration();
    }

    /// <summary>Asks about unsaved settings. Returns false when the user cancelled.</summary>
    private bool ResolveUnsaved()
    {
        if (!IsVisible || !_viewModel.HasDevice || !_viewModel.IsDirty) return true;
        MessageBoxResult answer = MessageBox.Show(this,
            "Some settings are not saved yet.\n\nSave them now?",
            "OpenPedalControl", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) _viewModel.Save();
        else _viewModel.RevertUnsaved();
        return true;
    }

    /// <summary>The downloaded update waits for this process to end; unsaved edits are kept by saving them.</summary>
    private void ExitForUpdate()
    {
        if (_viewModel.HasDevice && _viewModel.IsDirty) _viewModel.Save();
        _exiting = true;
        Close();
    }

    private void ExitApp()
    {
        if (!ResolveUnsaved())
        {
            ShowFromTray();
            return;
        }
        _exiting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            if (!ResolveUnsaved())
            {
                e.Cancel = true;
                return;
            }

            // While pedals are sent to vJoy, closing the window only hides it: games still need the output.
            if (_viewModel.IsFeedingVJoy)
            {
                e.Cancel = true;
                Hide();
                if (!_trayHintShown)
                {
                    _trayHintShown = true;
                    _tray?.ShowMessage("OpenPedalControl is still running",
                        "Your pedals are being sent to vJoy. Right-click the tray icon to exit.");
                }
                return;
            }
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _liveTimer.Stop();
        _deviceChangeTimer.Stop();
        _tray?.Dispose();
        _viewModel.Dispose();
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
