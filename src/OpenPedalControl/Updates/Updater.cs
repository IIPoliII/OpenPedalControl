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
    public UpdateInfo(Version version, string tag, string pageUrl, string? setupUrl, string? setupSha256)
    {
        Version = version;
        Tag = tag;
        PageUrl = pageUrl;
        SetupUrl = setupUrl;
        SetupSha256 = setupSha256;
    }

    public Version Version { get; }
    public string Tag { get; }
    public string PageUrl { get; }
    public string? SetupUrl { get; }

    /// <summary>SHA-256 that GitHub reports for the installer (hex, lower case).</summary>
    public string? SetupSha256 { get; }
}

/// <summary>Looks for new releases on GitHub and installs them.</summary>
public static class Updater
{
    private const string LatestReleaseApi = "https://api.github.com/repos/IIPoliII/OpenPedalControl/releases/latest";
    public const string ReleasesPage = "https://github.com/IIPoliII/OpenPedalControl/releases/latest";
    private const string DownloadPrefix = "https://github.com/IIPoliII/OpenPedalControl/releases/download/";


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

    /// <summary>
    /// True when the app was set up by the installer. Other copies (an old portable exe, a development build)
    /// update by running the installer with its wizard shown, which moves them to an installed copy.
    /// </summary>
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
        return new UpdateInfo(latest, release.Tag, release.Url, setup?.Url, setup?.Sha256);
    }

    /// <summary>
    /// Downloads the installer and returns its path. The file is kept only when its SHA-256 matches the one GitHub
    /// reports, so a damaged or swapped download is never run. Releases are not signed: the trust anchor is this
    /// GitHub repository and its release workflow.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo update)
    {
        string? url = update.SetupUrl;
        string? expected = update.SetupSha256;
        if (url == null || !url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This release has no installer to update from. Download it from the releases page.");
        if (expected == null || expected.Length != 64)
            throw new InvalidOperationException("GitHub gave no checksum for this release, so it cannot be verified. Download it from the releases page.");

        // A fresh, randomly named folder inside the user's own temp folder (which only this user can write to).
        string folder = Path.Combine(Path.GetTempPath(), "OpenPedalControl-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, $"OpenPedalControl-Setup-{update.Version}.exe");

        try
        {
            using HttpClient client = CreateClient();
            using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using FileStream file = File.Create(target);
                await response.Content.CopyToAsync(file).ConfigureAwait(false);
            }

            if (!string.Equals(Sha256Of(target), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The download does not match the checksum GitHub published, so it was deleted.");
        }
        catch
        {
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
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
    /// Starts the installer once this process has exited (it refuses to run while the app is open). An installed
    /// copy updates silently and is started again by the installer; any other copy shows the setup wizard.
    /// The caller must exit right after.
    /// </summary>
    public static void ApplyAfterExit(string installer)
    {
        int pid = Process.GetCurrentProcess().Id;
        string arguments = IsInstalled ? "'/SILENT','/NORESTART'" : "'/NORESTART'";
        // PowerShell gets the script encoded, so no path (with spaces, quotes or %) is ever re-parsed by cmd.
        string script = $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; " +
                        $"Start-Process -FilePath {PowerShellQuote(installer)} -ArgumentList {arguments}";
        string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

        Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + encoded)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    internal static string PowerShellQuote(string value) => "'" + value.Replace("'", "''") + "'";
}
