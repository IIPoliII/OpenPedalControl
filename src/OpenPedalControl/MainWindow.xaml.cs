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

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        _liveTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _liveTimer.Tick += (_, _) => _viewModel.Tick();

        // Plugging a device in produces a burst of notifications; wait for it to settle before re-scanning.
        _deviceChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _deviceChangeTimer.Tick += (_, _) =>
        {
            _deviceChangeTimer.Stop();
            _viewModel.RefreshDevices();
        };

        Loaded += (_, _) =>
        {
            _viewModel.RefreshDevices();
            _liveTimer.Start();
        };
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

    private void OnRemoveCalibrationClick(object sender, RoutedEventArgs e)
    {
        MessageBoxResult answer = MessageBox.Show(this,
            "Remove this device's calibration from Windows?\n\nGames will see the raw sensor range again until you calibrate anew.",
            "Remove calibration", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK) _viewModel.RemoveCalibration();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.HasDevice && _viewModel.IsDirty)
        {
            MessageBoxResult answer = MessageBox.Show(this,
                "Some settings are not saved to Windows yet.\n\nSave them before closing?",
                "OpenPedalControl", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (answer == MessageBoxResult.Yes) _viewModel.Save();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _liveTimer.Stop();
        _deviceChangeTimer.Stop();
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
