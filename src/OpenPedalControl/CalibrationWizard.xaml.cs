using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using OpenPedalControl.Hid;
using OpenPedalControl.ViewModels;

namespace OpenPedalControl;

public partial class CalibrationWizard : Window
{
    private readonly WizardViewModel _viewModel;
    private readonly DispatcherTimer _timer;

    public CalibrationWizard(HidDeviceReader reader, IReadOnlyList<ControlViewModel> controls)
    {
        InitializeComponent();
        _viewModel = new WizardViewModel(reader, controls);
        DataContext = _viewModel;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) =>
        {
            if (reader.IsClosed)
            {
                DialogResult = false; // Device unplugged mid-calibration.
                return;
            }
            _viewModel.Tick();
        };
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>The measurements taken, available once the dialog was closed with Save.</summary>
    public IReadOnlyList<WizardResult> Results => _viewModel.Results;

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsFinished)
        {
            DialogResult = _viewModel.Results.Count > 0;
            return;
        }
        _viewModel.Next();
    }

    private void OnSkipClick(object sender, RoutedEventArgs e) => _viewModel.Skip();
}
