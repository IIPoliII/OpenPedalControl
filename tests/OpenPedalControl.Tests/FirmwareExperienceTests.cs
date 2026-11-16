using System;
using System.IO;
using OpenPedalControl.Firmware;
using Xunit;

namespace OpenPedalControl.Tests;

public sealed class FirmwareExperienceTests
{
    [Fact]
    public void Firmware_is_available_inside_the_portable_app_without_any_external_files()
    {
        FirmwareImage image = FirmwareImage.LoadBundled();
        Assert.Equal("1.13.0", image.Version);
        Assert.Equal("ed64c2201542dcb76b32ef1c8ffcacd7fba81ffdd41994162e6b268f43a2c226", image.Sha256);
        Assert.Equal(29, image.PageCount);
        Assert.False(image.IsRestore);
        Assert.Equal(24500000, image.CpuHz);
    }

    [Theory]
    [InlineData(0x0113, "1.13.0")]
    [InlineData(0x0114, "1.14.0")]
    [InlineData(0x0200, "2.0.0")]
    [InlineData(0x01AF, null)]
    public void Installed_firmware_version_decodes_usb_bcd_only_for_our_controller(int bcd, string? expected)
    {
        var device = new FirmwareDevice { Product = "OpenPedalControl Pedals", Vid = 0x1209, Pid = 2,
            Page = 1, Input = 9, Feature = 57, VersionBcd = (ushort)bcd };
        Assert.Equal(expected, device.InstalledVersion?.ToString());
        device.Product = "HE SIM PEDALS";
        Assert.Null(device.InstalledVersion);
    }

    [Fact]
    public void Backup_discovery_finds_known_layout_but_requires_validation_and_stays_bounded()
    {
        string root = Path.Combine(Path.GetTempPath(), "opc-discovery-" + Guid.NewGuid().ToString("N"));
        string unit = Path.Combine(root, "unit");
        string valid = Path.Combine(unit, "date");
        string deep = Path.Combine(valid, "too-deep");
        Directory.CreateDirectory(deep);
        try
        {
            Assert.Equal(valid, FirmwareBackupLocator.Find(new[] { root }, "", p => p == valid));
            Assert.Equal(unit, FirmwareBackupLocator.Find(new[] { root }, unit, p => p == unit || p == valid));
            Assert.Null(FirmwareBackupLocator.Find(new[] { root }, "", p => p == deep));
            Assert.Null(FirmwareBackupLocator.Find(new[] { root }, "", p => throw new InvalidDataException("Wrong backup hash")));
        }
        finally { Directory.Delete(deep); Directory.Delete(valid); Directory.Delete(unit); Directory.Delete(root); }
    }
}
