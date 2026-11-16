using System.Security.Cryptography;
using System.Text;
using OpenPedalControl.Model;
using OpenPedalControl.Updates;
using Xunit;

namespace OpenPedalControl.Tests;

public class UpdaterTests
{
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("installer bytes");

    private static string Sign(RSACryptoServiceProvider rsa, byte[] data) =>
        System.Convert.ToBase64String(rsa.SignData(data, CryptoConfig.MapNameToOID("SHA256")));

    [Fact]
    public void Signature_from_the_matching_key_verifies()
    {
        using var rsa = new RSACryptoServiceProvider(2048) { PersistKeyInCsp = false };
        Assert.True(Updater.VerifySignature(Installer, Sign(rsa, Installer), rsa.ToXmlString(false)));
    }

    [Fact]
    public void Altered_file_wrong_key_or_garbage_signature_is_rejected()
    {
        using var rsa = new RSACryptoServiceProvider(2048) { PersistKeyInCsp = false };
        using var other = new RSACryptoServiceProvider(2048) { PersistKeyInCsp = false };
        string signature = Sign(rsa, Installer);
        Assert.False(Updater.VerifySignature(Encoding.UTF8.GetBytes("installer bytez"), signature, rsa.ToXmlString(false)));
        Assert.False(Updater.VerifySignature(Installer, Sign(other, Installer), rsa.ToXmlString(false)));
        Assert.False(Updater.VerifySignature(Installer, "not base64!", rsa.ToXmlString(false)));
    }

    [Fact]
    public void Embedded_release_key_is_a_public_rsa_3072_key()
    {
        using var rsa = new RSACryptoServiceProvider { PersistKeyInCsp = false };
        rsa.FromXmlString(Updater.SigningPublicKey);
        Assert.Equal(3072, rsa.KeySize);
        Assert.True(rsa.PublicOnly);
    }

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
