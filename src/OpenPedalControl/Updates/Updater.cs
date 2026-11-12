using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;

namespace OpenPedalControl.Updates;

public sealed class UpdateInfo
{
    public UpdateInfo(Version version, string tag, string pageUrl, string? setupUrl, string? setupSha256, string? exeUrl, string? exeSha256)
    {
        Version = version;
        Tag = tag;
        PageUrl = pageUrl;
        SetupUrl = setupUrl;
        SetupSha256 = setupSha256;
        ExeUrl = exeUrl;
        ExeSha256 = exeSha256;
    }

    public Version Version { get; }
    public string Tag { get; }
    public string PageUrl { get; }
    public string? SetupUrl { get; }
    public string? ExeUrl { get; }

    /// <summary>SHA-256 that GitHub reports for each release file (hex, lower case).</summary>
    public string? SetupSha256 { get; }
    public string? ExeSha256 { get; }
}

/// <summary>Looks for new releases on GitHub and installs them.</summary>
public static class Updater
{
    private const string LatestReleaseApi = "https://api.github.com/repos/IIPoliII/OpenPedalControl/releases/latest";
    public const string ReleasesPage = "https://github.com/IIPoliII/OpenPedalControl/releases/latest";

    [DataContract]
    private sealed class Release
    {
        [DataMember(Name = "tag_name")] public string Tag { get; set; } = "";
        [DataMember(Name = "html_url")] public string Url { get; set; } = "";
        [DataMember(Name = "draft")] public bool Draft { get; set; }
        [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
        [DataMember(Name = "assets")] public List<Asset> Assets { get; set; } = new List<Asset>();
    }

    [DataContract]
    private sealed class Asset
    {
        [DataMember(Name = "name")] public string Name { get; set; } = "";
        [DataMember(Name = "browser_download_url")] public string Url { get; set; } = "";

        /// <summary>"sha256:&lt;hex&gt;", computed by GitHub when the file was uploaded.</summary>
        [DataMember(Name = "digest")] public string? Digest { get; set; }

        public string? Sha256 =>
            Digest != null && Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? Digest.Substring(7).ToLowerInvariant() : null;
    }

    public static Version CurrentVersion
    {
        get
        {
            Version v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    private static string ExePath => Assembly.GetExecutingAssembly().Location;

    /// <summary>True when the app was set up by the installer, false for the portable exe.</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(Path.GetDirectoryName(ExePath) ?? "", "unins000.exe"));

    private static HttpClient CreateClient()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub requires a User-Agent; a generic one, nothing about the user.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenPedalControl-Updater");
        return client;
    }

    /// <summary>The newest release if it is newer than this build, otherwise null. Throws on network errors.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        using HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using Stream stream = await client.GetStreamAsync(LatestReleaseApi).ConfigureAwait(false);
        var release = new DataContractJsonSerializer(typeof(Release)).ReadObject(stream) as Release;
        if (release == null || release.Draft || release.Prerelease) return null;
        if (!Version.TryParse(release.Tag.TrimStart('v', 'V'), out Version? latest)) return null;
        latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
        if (latest <= CurrentVersion) return null;

        Asset? setup = release.Assets.FirstOrDefault(a => a.Name.StartsWith("OpenPedalControl-Setup", StringComparison.OrdinalIgnoreCase)
                                                          && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        Asset? exe = release.Assets.FirstOrDefault(a => a.Name.Equals("OpenPedalControl.exe", StringComparison.OrdinalIgnoreCase));
        return new UpdateInfo(latest, release.Tag, release.Url, setup?.Url, setup?.Sha256, exe?.Url, exe?.Sha256);
    }

    /// <summary>
    /// Downloads the right file for this copy (installer or portable exe) and returns its path. The file is only
    /// kept when its SHA-256 matches the one GitHub reports for the release, so a damaged or swapped download is
    /// never run. (The app is not code-signed, so there is no Authenticode publisher to check.)
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo update)
    {
        string? url = IsInstalled ? update.SetupUrl : update.ExeUrl;
        string? expected = IsInstalled ? update.SetupSha256 : update.ExeSha256;
        if (url == null || !url.StartsWith("https://github.com/IIPoliII/OpenPedalControl/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This release has no file to update from. Download it from the releases page.");
        if (expected == null || expected.Length != 64)
            throw new InvalidOperationException("GitHub gave no checksum for this release, so it cannot be verified. Download it from the releases page.");

        // A fresh, randomly named folder inside the user's own temp folder (which only this user can write to).
        string folder = Path.Combine(Path.GetTempPath(), "OpenPedalControl-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, IsInstalled ? $"OpenPedalControl-Setup-{update.Version}.exe" : "OpenPedalControl.exe");

        using HttpClient client = CreateClient();
        using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using (FileStream file = File.Create(target))
        {
            await response.Content.CopyToAsync(file).ConfigureAwait(false);
        }

        string actual = Sha256Of(target);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(target);
            throw new InvalidOperationException("The download does not match the checksum GitHub published, so it was deleted.");
        }
        return target;
    }

    public static string Sha256Of(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using FileStream stream = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Starts the update once this process has exited (the installer refuses to run while the app is open,
    /// and the portable exe cannot be replaced while it runs). The caller must exit right after.
    /// </summary>
    public static void ApplyAfterExit(string downloaded)
    {
        int pid = Process.GetCurrentProcess().Id;
        string wait = $"powershell -NoProfile -Command \"Wait-Process -Id {pid} -ErrorAction SilentlyContinue\"";
        string command = IsInstalled
            // Silent update: same folder and choices; the installer starts the app again afterwards.
            ? $"{wait} & start \"\" \"{downloaded}\" /SILENT /NORESTART"
            : $"{wait} & move /y \"{downloaded}\" \"{ExePath}\" & start \"\" \"{ExePath}\"";

        Process.Start(new ProcessStartInfo("cmd.exe", "/c " + command)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }
}
