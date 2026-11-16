using System;
using System.Windows;
using OpenPedalControl.ViewModels;

namespace OpenPedalControl;

public partial class SettingsWindow : Window
{
    private readonly Action _showChangelog;
    private readonly MainViewModel _viewModel;
    private readonly Func<Window, bool> _prepareFirmware;

    public SettingsWindow(MainViewModel viewModel, Action showChangelog, Func<Window, bool> prepareFirmware)
    {
        InitializeComponent();
        _showChangelog = showChangelog;
        _viewModel = viewModel;
        _prepareFirmware = prepareFirmware;
        DataContext = viewModel;
    }

    private void OnChangelogClick(object sender, RoutedEventArgs e) => _showChangelog();

    private void OnFirmwareClick(object sender, RoutedEventArgs e)
    {
        if (!_prepareFirmware(this)) return;
        if (!_viewModel.BeginFirmwareMaintenance()) return;
        try { new FirmwareWindow { Owner = this }.ShowDialog(); }
        finally { _viewModel.EndFirmwareMaintenance(); }
    }
}
