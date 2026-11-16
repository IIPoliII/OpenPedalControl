using System;
using System.Reflection;
using OpenPedalControl.Updates;
using OpenPedalControl.ViewModels;
using Xunit;

namespace OpenPedalControl.Tests;

public sealed class FirmwareMaintenanceTests
{
    [Fact]
    public void Firmware_maintenance_and_app_update_cannot_overlap()
    {
        using var vm = new MainViewModel();
        typeof(MainViewModel).GetField("_update", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, new UpdateInfo(new Version(99, 0, 0), "v99.0.0", "", null, null, null));
        Assert.True(vm.InstallUpdateCommand.CanExecute(null));
        Assert.True(vm.BeginFirmwareMaintenance());
        Assert.True(vm.IsFirmwareMaintenance);
        Assert.False(vm.CanMaintainFirmware);
        Assert.False(vm.InstallUpdateCommand.CanExecute(null));
        Assert.False(vm.BeginFirmwareMaintenance());
        vm.RefreshDevices(true); // Maintenance must not reopen a reader in the background.
        Assert.Null(vm.Reader);
        vm.EndFirmwareMaintenance();
        Assert.False(vm.IsFirmwareMaintenance);
        Assert.True(vm.InstallUpdateCommand.CanExecute(null));
        var busy = typeof(MainViewModel).GetMethod("SetUpdateBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        busy.Invoke(vm, new object[] { true });
        Assert.False(vm.BeginFirmwareMaintenance());
        Assert.False(vm.IsFirmwareMaintenance);
        Assert.False(vm.InstallUpdateCommand.CanExecute(null));
        busy.Invoke(vm, new object[] { false });
        Assert.True(vm.CanMaintainFirmware);
    }
}
