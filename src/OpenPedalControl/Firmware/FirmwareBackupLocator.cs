using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenPedalControl.Firmware;

/// <summary>Find only a hash-verified backup, within the app's known backup layout.</summary>
internal static class FirmwareBackupLocator
{
    private static string PreferencePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenPedalControl", "firmware-backup-folder.txt");

    internal static string? Find()
    {
        string remembered = "";
        try { if (new FileInfo(PreferencePath).Length < 4096) remembered = File.ReadAllText(PreferencePath).Trim(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Find(new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OpenPedalControl-backups", "pedalier"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "OpenPedalControl-backups", "pedalier"),
        }, remembered, folder => { FirmwareBackup.Load(folder); return true; });
    }

    internal static string? Find(IEnumerable<string> roots, string remembered, Func<string, bool> validate)
    {
        bool Accept(string folder)
        {
            try { return !string.IsNullOrWhiteSpace(folder) && validate(folder); }
            catch (IOException) { return false; }
            catch (InvalidDataException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
        }
        if (Accept(remembered)) return remembered;
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var level = new[] { root };
            for (int depth = 0; depth <= 2; depth++)
            {
                foreach (string path in level) if (Accept(path)) return path;
                if (depth < 2) level = level.SelectMany(Children).Take(256).ToArray();
            }
        }
        return null;
    }

    private static string[] Children(string path)
    {
        try { return Directory.EnumerateDirectories(path).Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0).Take(256).ToArray(); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    internal static void Remember(string folder)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, folder);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
