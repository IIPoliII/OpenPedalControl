using System;
using System.Reflection;
using Microsoft.Win32;

namespace OpenPedalControl;

/// <summary>"Start with Windows": a per-user Run entry that launches the app hidden in the tray.</summary>
public static class AutoStart
{
    public const string TrayArgument = "--tray";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenPedalControl";

    private static string Command => $"\"{Assembly.GetExecutingAssembly().Location}\" {TrayArgument}";

    public static bool IsEnabled
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, false);
    }

    /// <summary>Keeps the entry pointing at this exe after the portable app was moved.</summary>
    public static void RepairPath()
    {
        try
        {
            if (IsEnabled) SetEnabled(true);
        }
        catch (Exception)
        {
            // Not worth bothering the user about; the toggle in the window shows the real state.
        }
    }
}
