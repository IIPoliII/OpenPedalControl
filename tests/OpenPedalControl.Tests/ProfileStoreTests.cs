using System;
using System.IO;
using System.Text;
using OpenPedalControl.Model;
using Xunit;

namespace OpenPedalControl.Tests;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OpenPedalControl.Tests." + Guid.NewGuid().ToString("N"));

    private string PathFor(string name) => Path.Combine(_directory, name);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private const string HandWritten =
        "{ \"DeviceName\": \"HE SIM PEDALS\", \"VendorId\": 4292, \"ProductId\": 35586, \"Serial\": \"X1\", \"Controls\": [ " +
        "{ \"Name\": \"Brake\", \"Kind\": \"Brake\", \"UsagePage\": 1, \"Usage\": 49, \"RawRest\": 76, \"RawFull\": 2850 } ] }";

    [Fact]
    public void Profile_survives_a_round_trip()
    {
        var profile = new DeviceProfile { DeviceName = "HE SIM PEDALS", VendorId = 0x10C4, ProductId = 0x8B02, Serial = "X1" };
        profile.Controls.Add(new ControlSettings
        {
            Name = "Throttle", Kind = nameof(ControlKind.Throttle), UsagePage = 1, Usage = 0x30,
            RawRest = 324, RawFull = 3110, BottomDeadzone = 2, TopDeadzone = 3, CurveMidpoint = 60,
        });

        string path = PathFor("roundtrip.json");
        ProfileStore.Write(profile, path);
        DeviceProfile? read = ProfileStore.Read(path);

        Assert.NotNull(read);
        Assert.Equal("X1", read!.Serial);
        ControlSettings throttle = Assert.Single(read.Controls);
        Assert.Equal(ControlKind.Throttle, throttle.GetKind());
        Assert.Equal(324, throttle.RawRest);
        Assert.Equal(3110, throttle.RawFull);
        Assert.Equal(3, throttle.TopDeadzone);
        Assert.Equal(60, throttle.CurveMidpoint);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Hand_edited_file_loads_with_or_without_byte_order_mark(bool byteOrderMark)
    {
        Directory.CreateDirectory(_directory);
        string path = PathFor("edited.json");
        File.WriteAllText(path, HandWritten, new UTF8Encoding(byteOrderMark));

        DeviceProfile? read = ProfileStore.Read(path);

        Assert.NotNull(read);
        ControlSettings brake = Assert.Single(read!.Controls);
        Assert.Equal(2850, brake.RawFull);
        Assert.Equal(50, brake.CurveMidpoint); // Left out of the file: falls back to linear.
        Assert.Equal(0, brake.BottomDeadzone);
    }

    [Fact]
    public void Missing_or_damaged_file_gives_no_profile()
    {
        Directory.CreateDirectory(_directory);
        Assert.Null(ProfileStore.Read(PathFor("missing.json")));

        string damaged = PathFor("damaged.json");
        File.WriteAllText(damaged, "{ \"Controls\": [ { \"Name\": ");
        Assert.Null(ProfileStore.Read(damaged));

        string empty = PathFor("empty.json");
        File.WriteAllText(empty, "{ \"Controls\": [] }");
        Assert.Null(ProfileStore.Read(empty));
    }

    [Fact]
    public void File_name_is_built_from_usb_ids_and_a_sanitised_serial()
    {
        Assert.EndsWith("10C4_8B02_SP1234567890.json", ProfileStore.GetPath(0x10C4, 0x8B02, "SP1234567890"));
        Assert.EndsWith("10C4_8B02_AB12.json", ProfileStore.GetPath(0x10C4, 0x8B02, "A/B\\1:2"));
        Assert.EndsWith("10C4_8B02.json", ProfileStore.GetPath(0x10C4, 0x8B02, ""));
    }
}
