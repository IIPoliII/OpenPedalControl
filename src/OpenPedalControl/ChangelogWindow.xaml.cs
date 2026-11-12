using System;
using System.Collections.Generic;
using System.Windows;
using OpenPedalControl.Model;

namespace OpenPedalControl;

public partial class ChangelogWindow : Window
{
    /// <param name="updatedFrom">The version that was replaced, when shown right after an update.</param>
    public ChangelogWindow(string currentVersion, string? updatedFrom)
    {
        InitializeComponent();
        List<ChangelogRelease> releases = Changelog.Load();

        if (updatedFrom != null)
        {
            Heading.Text = $"Updated to {currentVersion}";
            Subheading.Text = $"OpenPedalControl was updated from {updatedFrom}. Here is what changed.";
            Version.TryParse(updatedFrom.TrimStart('v', 'V'), out Version? previous);
            foreach (ChangelogRelease release in releases)
            {
                release.IsNew = Version.TryParse(release.Version, out Version? version) && (previous == null || version > previous);
            }
        }
        else
        {
            Subheading.Text = $"You are running {currentVersion}. All changes so far:";
        }

        Releases.ItemsSource = releases;
    }
}
