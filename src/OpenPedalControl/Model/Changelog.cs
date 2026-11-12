using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace OpenPedalControl.Model;

public sealed class ChangelogRelease
{
    public ChangelogRelease(string version, string date, IReadOnlyList<string> items)
    {
        Version = version;
        Date = date;
        Items = items;
    }

    public string Version { get; }
    public string Date { get; }
    public IReadOnlyList<string> Items { get; }
    public string Title => "Version " + Version;

    /// <summary>Set for the release that was just installed, so the window can highlight it.</summary>
    public bool IsNew { get; set; }
}

/// <summary>Reads CHANGELOG.md, which is embedded in the exe so it always matches the running version.</summary>
public static class Changelog
{
    public static List<ChangelogRelease> Parse(string markdown)
    {
        var releases = new List<ChangelogRelease>();
        string? version = null, date = "";
        var items = new List<string>();

        void Flush()
        {
            if (version != null) releases.Add(new ChangelogRelease(version, date ?? "", items.ToArray()));
            items.Clear();
        }

        foreach (string raw in markdown.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                string heading = line.Substring(3).Trim();
                int dash = heading.IndexOf(" - ", StringComparison.Ordinal);
                version = (dash >= 0 ? heading.Substring(0, dash) : heading).Trim().TrimStart('v', 'V');
                date = dash >= 0 ? heading.Substring(dash + 3).Trim() : "";
            }
            else if (version != null && line.StartsWith("- ", StringComparison.Ordinal))
            {
                items.Add(line.Substring(2).Trim());
            }
        }
        Flush();
        return releases;
    }

    public static List<ChangelogRelease> Load()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
        if (stream == null) return new List<ChangelogRelease>();
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
