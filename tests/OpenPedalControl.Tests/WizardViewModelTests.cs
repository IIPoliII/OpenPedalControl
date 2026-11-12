using System.Collections.Generic;
using System.Linq;
using OpenPedalControl.Model;
using OpenPedalControl.ViewModels;
using Xunit;

namespace OpenPedalControl.Tests;

public class WizardViewModelTests
{
    // Resting values as measured on a real controller.
    private const int RestX = 324, RestY = 76, RestZ = 384;

    private static WizardResult ResultFor(WizardViewModel wizard, ControlKind kind) =>
        wizard.Results.Single(r => r.Control.Kind == kind);

    [Fact]
    public void Waits_for_the_first_report_before_the_release_step_can_be_confirmed()
    {
        var device = new FakeDevice();
        var wizard = new WizardViewModel(device, device.CreateControls());

        wizard.Tick();
        Assert.False(wizard.CanGoNext);
        Assert.NotEqual("", wizard.Hint);

        device.Report(RestX, RestY, RestZ);
        wizard.Tick();
        Assert.True(wizard.CanGoNext);
        Assert.Equal("", wizard.Hint);
    }

    [Fact]
    public void Finds_the_axis_of_each_pedal_and_records_rest_and_furthest_position()
    {
        var device = new FakeDevice();
        List<ControlViewModel> controls = device.CreateControls();
        var wizard = new WizardViewModel(device, controls);

        // Wiring differs from the template's guess: throttle on Z, clutch on X.
        device.Report(RestX, RestY, RestZ);
        wizard.Tick();
        wizard.Next();

        // Throttle: noise alone must not count as a press.
        device.Set("Z", RestZ + 3);
        device.Set("Y", RestY - 2);
        wizard.Tick();
        Assert.False(wizard.CanGoNext);

        foreach (int z in new[] { 900, 2200, 3105, 3090, 1500, RestZ + 1 })
        {
            device.Set("Z", z);
            wizard.Tick();
        }
        Assert.True(wizard.CanGoNext);
        wizard.Next();

        // Brake: peak force is kept after the pedal is released.
        foreach (int y in new[] { 600, 1900, 2799, 2400, RestY })
        {
            device.Set("Y", y);
            wizard.Tick();
        }
        wizard.Next();

        // Clutch.
        foreach (int x in new[] { 1500, 2950, RestX })
        {
            device.Set("X", x);
            wizard.Tick();
        }
        wizard.Next();

        Assert.True(wizard.IsFinished);
        Assert.True(wizard.CanGoNext);
        Assert.Equal("Save", wizard.NextText);

        WizardResult throttle = ResultFor(wizard, ControlKind.Throttle);
        Assert.Same(device["Z"], throttle.Axis);
        Assert.Equal(RestZ, throttle.Rest);
        Assert.Equal(3105, throttle.Full);

        WizardResult brake = ResultFor(wizard, ControlKind.Brake);
        Assert.Same(device["Y"], brake.Axis);
        Assert.Equal(RestY, brake.Rest);
        Assert.Equal(2799, brake.Full);

        WizardResult clutch = ResultFor(wizard, ControlKind.Clutch);
        Assert.Same(device["X"], clutch.Axis);
        Assert.Equal(RestX, clutch.Rest);
        Assert.Equal(2950, clutch.Full);
    }

    [Fact]
    public void A_pedal_already_measured_is_ignored_in_later_steps()
    {
        var device = new FakeDevice();
        var wizard = new WizardViewModel(device, device.CreateControls());
        device.Report(RestX, RestY, RestZ);
        wizard.Tick();
        wizard.Next();

        device.Set("X", 3000);
        wizard.Tick();
        wizard.Next(); // Throttle = X, still held down.

        wizard.Tick();
        Assert.False(wizard.CanGoNext); // Holding the throttle does not satisfy the brake step.

        device.Set("Y", 2500);
        wizard.Tick();
        Assert.True(wizard.CanGoNext);
        wizard.Next();
        Assert.Same(device["Y"], ResultFor(wizard, ControlKind.Brake).Axis);
    }

    [Fact]
    public void Inverted_sensor_is_recorded_with_full_below_rest()
    {
        var device = new FakeDevice();
        var wizard = new WizardViewModel(device, device.CreateControls());
        device.Report(3500, RestY, RestZ);
        wizard.Tick();
        wizard.Next();

        foreach (int x in new[] { 2000, 610, 640, 3490 })
        {
            device.Set("X", x);
            wizard.Tick();
        }
        wizard.Next();

        WizardResult throttle = ResultFor(wizard, ControlKind.Throttle);
        Assert.Equal(3500, throttle.Rest);
        Assert.Equal(610, throttle.Full);
    }

    [Fact]
    public void Skipping_everything_leaves_nothing_to_save()
    {
        var device = new FakeDevice();
        var wizard = new WizardViewModel(device, device.CreateControls());
        device.Report(RestX, RestY, RestZ);
        wizard.Tick();
        wizard.Next();

        wizard.Skip();
        wizard.Skip();
        wizard.Skip();

        Assert.True(wizard.IsFinished);
        Assert.False(wizard.CanGoNext);
        Assert.Empty(wizard.Results);
    }

    [Fact]
    public void Skipped_pedal_is_left_out_of_the_results()
    {
        var device = new FakeDevice();
        var wizard = new WizardViewModel(device, device.CreateControls());
        device.Report(RestX, RestY, RestZ);
        wizard.Tick();
        wizard.Next();

        device.Set("X", 3000);
        wizard.Tick();
        wizard.Next();          // throttle
        device.Set("Y", 2500);
        wizard.Tick();
        wizard.Next();          // brake
        wizard.Skip();          // no clutch fitted

        Assert.Equal(2, wizard.Results.Count);
        Assert.DoesNotContain(wizard.Results, r => r.Control.Kind == ControlKind.Clutch);
        Assert.True(wizard.CanGoNext);
    }

    [Fact]
    public void Handbrake_has_a_single_step()
    {
        var device = new FakeDevice();
        List<ControlViewModel> controls = device.CreateControls("HE HANDBRAKE", 0x10C4, 0x8B03);
        Assert.Single(controls);
        var wizard = new WizardViewModel(device, controls);

        device.Report(300, 0, 0);
        wizard.Tick();
        wizard.Next();
        device.Set("X", 2600);
        wizard.Tick();
        wizard.Next();

        Assert.True(wizard.IsFinished);
        Assert.Equal(2600, wizard.Results.Single().Full);
    }
}
