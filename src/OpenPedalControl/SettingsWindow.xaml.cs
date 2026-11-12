using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using OpenPedalControl.ViewModels;

namespace OpenPedalControl;

public partial class SettingsWindow : Window
{
    /// <summary>The installer leaves the vJoy setup next to the exe so it can also be installed later.</summary>
    private const string BundledVJoySetup = "vJoySetup.exe";

    private readonly MainViewModel _viewModel;
    private readonly Action _showChangelog;

    public SettingsWindow(MainViewModel viewModel, Action showChangelog)
    {
        InitializeComponent();
        _showChangelog = showChangelog;
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.RefreshVJoyDevices();
        UpdateMissingPanel();
        viewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.VJoyInstalled)) UpdateMissingPanel();
    }

    private void UpdateMissingPanel() =>
        MissingPanel.Visibility = _viewModel.VJoyInstalled ? Visibility.Collapsed : Visibility.Visible;

    private void OnChangelogClick(object sender, RoutedEventArgs e) => _showChangelog();

    private void OnInstallVJoyClick(object sender, RoutedEventArgs e)
    {
        string bundled = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", BundledVJoySetup);
        try
        {
            if (File.Exists(bundled))
            {
                using Process? setup = Process.Start(new ProcessStartInfo(bundled) { UseShellExecute = true, Verb = "runas" });
                setup?.WaitForExit();
                _viewModel.RefreshDevices();
                _viewModel.RefreshVJoyDevices();
            }
            else
            {
                // Portable exe: send the user to the official vJoy release.
                Process.Start(new ProcessStartInfo(MainViewModel.VJoyDownloadUrl) { UseShellExecute = true });
            }
        }
        catch (Win32Exception)
        {
            // The administrator prompt was declined.
        }
    }
}
