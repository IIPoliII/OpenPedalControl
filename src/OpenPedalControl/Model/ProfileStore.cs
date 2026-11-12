using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;

namespace OpenPedalControl.Model;

/// <summary>
/// Keeps one JSON profile per device under %APPDATA%\OpenPedalControl\profiles. The profile remembers the
/// settings behind a calibration (deadzones, curve, axis roles), which Windows itself does not store.
/// </summary>
public static class ProfileStore
{
    public static string ProfileDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenPedalControl", "profiles");

    public static string GetPath(int vendorId, int productId, string serial)
    {
        string safeSerial = new string((serial ?? "").Where(char.IsLetterOrDigit).ToArray());
        string name = $"{vendorId:X4}_{productId:X4}" + (safeSerial.Length > 0 ? "_" + safeSerial : "") + ".json";
        return Path.Combine(ProfileDirectory, name);
    }

    private static readonly object CacheLock = new object();
    private static readonly Dictionary<string, DeviceProfile?> Cache = new Dictionary<string, DeviceProfile?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The profile of a device, read from disk once. The same object is handed to the window and to the
    /// virtual output, so edits take effect live.
    /// </summary>
    public static DeviceProfile? Load(int vendorId, int productId, string serial)
    {
        string path = GetPath(vendorId, productId, serial);
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(path, out DeviceProfile? profile))
            {
                profile = Read(path);
                Cache[path] = profile;
            }
            return profile;
        }
    }

    /// <summary>Makes a newly created profile the shared one for its device (not yet saved).</summary>
    public static void Adopt(DeviceProfile profile)
    {
        lock (CacheLock) Cache[GetPath(profile.VendorId, profile.ProductId, profile.Serial)] = profile;
    }

    /// <summary>Drops unsaved edits: the next Load reads the file again.</summary>
    public static void Forget(int vendorId, int productId, string serial)
    {
        lock (CacheLock) Cache.Remove(GetPath(vendorId, productId, serial));
    }

    public static void Save(DeviceProfile profile)
    {
        Write(profile, GetPath(profile.VendorId, profile.ProductId, profile.Serial));
        Adopt(profile);
    }

    /// <summary>Deletes the file and drops the cached profile.</summary>
    public static void Delete(int vendorId, int productId, string serial)
    {
        string path = GetPath(vendorId, productId, serial);
        if (File.Exists(path)) File.Delete(path);
        lock (CacheLock) Cache.Remove(path);
    }

    /// <summary>The profile as JSON text, used to tell whether something changed since the last save.</summary>
    public static string Snapshot(DeviceProfile profile)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(DeviceProfile)).WriteObject(stream, profile);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Reads a profile file. A missing or damaged file gives null.</summary>
    public static DeviceProfile? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            // Read as text first: the JSON serializer rejects the byte order mark some editors add.
            byte[] json = new UTF8Encoding(false).GetBytes(File.ReadAllText(path));
            using var stream = new MemoryStream(json);
            var profile = new DataContractJsonSerializer(typeof(DeviceProfile)).ReadObject(stream) as DeviceProfile;
            if (profile?.Controls == null) return null;
            profile.Controls.RemoveAll(c => c == null);
            return profile.Controls.Count == 0 ? null : profile;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Write(DeviceProfile profile, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        using (FileStream stream = File.Create(temp))
        using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, new UTF8Encoding(false), false, true, "  "))
        {
            new DataContractJsonSerializer(typeof(DeviceProfile)).WriteObject(writer, profile);
        }
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }
}
