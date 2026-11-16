using OpenPedalControl.Model;
using OpenPedalControl.Updates;
using Xunit;

namespace OpenPedalControl.Tests;

public class UpdaterTests
{
    [Fact]
    public void Installer_path_is_quoted_for_powershell()
    {
        Assert.Equal(@"'C:\Users\O''Neil 100%\setup.exe'", Updater.PowerShellQuote(@"C:\Users\O'Neil 100%\setup.exe"));
    }

    [Fact]
    public void Port_key_tells_identical_serial_less_devices_apart()
    {
        string a = ProfileStore.PortKey(@"\?\hid#vid_1209&pid_0002#7&1b2c3d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}");
        string b = ProfileStore.PortKey(@"\?\hid#vid_1209&pid_0002#7&9f8e7d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}");
        Assert.Equal("port71B2C3D00000", a);
        Assert.NotEqual(a, b);
    }
}
