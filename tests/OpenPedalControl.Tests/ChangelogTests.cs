using System.Collections.Generic;
using System.Linq;
using OpenPedalControl.Model;
using Xunit;

namespace OpenPedalControl.Tests;

public class ChangelogTests
{
    [Fact]
    public void Parses_versions_dates_and_items()
    {
        const string markdown = "# Changelog\n\nIntro line.\n\n## 0.3.0 - 2027-01-02\n\n- First\n- Second\n\n## v0.2.0\n- Only\n";
        List<ChangelogRelease> releases = Changelog.Parse(markdown);

        Assert.Equal(new[] { "0.3.0", "0.2.0" }, releases.Select(r => r.Version));
        Assert.Equal("2027-01-02", releases[0].Date);
        Assert.Equal(new[] { "First", "Second" }, releases[0].Items);
        Assert.Equal("", releases[1].Date);
        Assert.Equal(new[] { "Only" }, releases[1].Items);
    }

    [Fact]
    public void Embedded_changelog_covers_the_running_version()
    {
        List<ChangelogRelease> releases = Changelog.Load();
        Assert.NotEmpty(releases);
        System.Version running = typeof(Changelog).Assembly.GetName().Version!;
        Assert.Contains(releases, r => r.Version == $"{running.Major}.{running.Minor}.{running.Build}");
    }
}
