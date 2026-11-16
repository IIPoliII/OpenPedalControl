using System;
using System.Windows;
using OpenPedalControl.ViewModels;

namespace OpenPedalControl;

public partial class SettingsWindow : Window
{
    private readonly Action _showChangelog;

    public SettingsWindow(MainViewModel viewModel, Action showChangelog)
    {
        InitializeComponent();
        _showChangelog = showChangelog;
        DataContext = viewModel;
    }

    private void OnChangelogClick(object sender, RoutedEventArgs e) => _showChangelog();
}
