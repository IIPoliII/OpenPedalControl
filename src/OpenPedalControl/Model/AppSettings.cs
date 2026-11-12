using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace OpenPedalControl.Model;

/// <summary>App-wide settings, kept in %APPDATA%\OpenPedalControl\settings.json.</summary>
[DataContract]
public sealed class AppSettings
{
    /// <summary>vJoy device to send to (1..16), or 0 to take the first free one.</summary>
    [DataMember(Order = 0)] public int VJoyDevice { get; set; }

    /// <summary>Look for a new release each time the app starts.</summary>
    [DataMember(Order = 1)] public bool CheckForUpdates { get; set; } = true;

    /// <summary>Version that ran last; a different one at startup means the app was updated.</summary>
    [DataMember(Order = 2)] public string LastVersion { get; set; } = "";

    // The JSON serializer skips constructors: defaults for settings missing from the file.
    [OnDeserializing]
    private void OnDeserializing(StreamingContext context)
    {
        CheckForUpdates = true;
        LastVersion = "";
    }

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenPedalControl", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            byte[] json = new UTF8Encoding(false).GetBytes(File.ReadAllText(FilePath));
            using var stream = new MemoryStream(json);
            var settings = new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(stream) as AppSettings ?? new AppSettings();
            if (settings.VJoyDevice < 0 || settings.VJoyDevice > 16) settings.VJoyDevice = 0;
            return settings;
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        using FileStream stream = File.Create(FilePath);
        using var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, new UTF8Encoding(false), false, true, "  ");
        new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(writer, this);
    }
}
