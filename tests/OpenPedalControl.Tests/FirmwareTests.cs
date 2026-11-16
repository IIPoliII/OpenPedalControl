using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenPedalControl.Firmware;
using Xunit;

namespace OpenPedalControl.Tests;

public sealed class FirmwareTests
{
    private static string Record(int address, byte[] data, byte kind = 0)
    {
        byte[] record = new[] { (byte)data.Length, (byte)(address >> 8), (byte)address, kind }.Concat(data).ToArray();
        return ":" + BitConverter.ToString(record.Concat(new[] { unchecked((byte)-record.Sum(b => (int)b)) }).ToArray()).Replace("-", "");
    }
    private static string Hex => Record(0, new byte[] { 2, 0, 3, 0x22 }) + "\n" + Record(40, Enumerable.Range(0, 130).Select(i => (byte)(i * 19)).ToArray()) + "\n:00000001FF\n";
    private static FirmwareBackup Backup()
    {
        byte[] p = Enumerable.Range(0, 16384).Select(i => (byte)(i * 13 + 7)).ToArray();
        p[0] = 2; p[1] = 0; p[2] = 3; p[3] = 0x22;
        byte[] n = Enumerable.Range(0, 1024).Select(i => (byte)(i * 7 + 19)).ToArray();
        n[0x3BF] = 0; n[0x3FE] = 0xA5; n[0x3FF] = 0;
        return new FirmwareBackup(p, n);
    }

    [Fact]
    public void Crc_and_command_match_published_protocol_vectors()
    {
        Assert.Equal(0x31C3, FirmwareProgrammer.Crc(Encoding.ASCII.GetBytes("123456789")));
        byte[] report = FirmwareProgrammer.Encode(0x30, new byte[] { 0x32, 0x41 }, false);
        Assert.Equal(65, report.Length);
        Assert.Equal(new byte[] { 0, 0x24, 3, 0x30, 0x32, 0x41 }, report.Take(6));
    }
    [Theory]
    [InlineData(0x35, 0)]
    [InlineData(0x32, 0x3A00)]
    [InlineData(0x32, 0xFB80)]
    [InlineData(0x32, 1)]
    [InlineData(0x33, 0x3A00)]
    [InlineData(0x33, 0xFB80)]
    [InlineData(0x33, 0xFFFF)]
    public void Protected_and_invalid_commands_never_encode(int command, int address)
    {
        var data = new List<byte> { (byte)(address >> 8), (byte)address };
        if (command == 0x33) data.Add(0);
        Assert.ThrowsAny<Exception>(() => FirmwareProgrammer.Encode((byte)command, data.ToArray(), true));
    }
    [Fact]
    public void Page_crossing_oversized_and_readonly_writes_are_rejected()
    {
        foreach (byte[] data in new[] { new byte[] { 1, 255, 0, 0 }, new byte[] { 0xFB, 0x7F, 0, 0 }, new byte[62], new byte[2] })
            Assert.ThrowsAny<Exception>(() => FirmwareProgrammer.Encode(0x33, data, true));
        Assert.Throws<InvalidOperationException>(() => FirmwareProgrammer.Encode(0x32, new byte[] { 0, 0 }, false));
        Assert.ThrowsAny<Exception>(() => FirmwareProgrammer.Encode(0x34, new byte[] { 0xFF, 0xC0, 0xFF, 0xFF, 0, 0 }, false));
    }
    [Fact]
    public void Image_rejects_bad_checksums_duplicates_and_every_protected_range()
    {
        Assert.Throws<InvalidDataException>(() => FirmwareImage.FromHex(Hex.Replace(":00000001FF", ":0000000100")));
        Assert.Throws<InvalidDataException>(() => FirmwareImage.FromHex(Hex.Replace(":00000001FF", Record(0, new byte[] { 2 }) + "\n:00000001FF")));
        Assert.Throws<InvalidDataException>(() => FirmwareImage.FromHex(Hex.Replace(":00000001FF", "")));
        Assert.Throws<InvalidDataException>(() => FirmwareImage.FromHex(Hex + Record(10, new byte[] { 0 })));
        foreach (int a in new[] { 0x3A00, 0x3FFF, 0x4000, 0xF800, 0xFB7F, 0xFB80, 0xFBFF, 0xFFC0 })
            Assert.Throws<InvalidDataException>(() => FirmwareImage.FromHex(Hex.Replace(":00000001FF", Record(a, new byte[] { 0 }) + "\n:00000001FF")));
        string extended = Record(0, new byte[] { 0, 1 }, 4) + "\n" + Record(0, new byte[] { 2, 0, 3, 0x22 }) + "\n:00000001FF";
        Assert.Throws<InvalidDataException>(() => FirmwareImage.FromHex(extended));
        Assert.Throws<InvalidDataException>(() => FirmwareImage.FromHex(Record(0, new byte[] { 2, 0x3A, 0 }) + "\n:00000001FF"));
    }
    [Fact]
    public void Bundle_checks_hash_target_profile_and_specification_before_loading()
    {
        string folder = Path.Combine(Path.GetTempPath(), "opc-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string imagePath = Path.Combine(folder, "pedals.ihx"), manifestPath = Path.Combine(folder, "pedals.firmware.json");
        try
        {
            File.WriteAllText(imagePath, Hex, new UTF8Encoding(false));
            string manifest = "{\"schema\":1,\"target\":\"" + FirmwareImage.Target + "\",\"version\":\"1.13.0\",\"image\":\"pedals.ihx\",\"sha256\":\"" + FirmwareImage.Hash(File.ReadAllBytes(imagePath)) + "\",\"profile_version\":1,\"adc_bits\":12,\"usb_interval_ms\":4,\"axes\":3,\"cpu_hz\":24500000,\"adc_mux\":[14,13,12]}";
            File.WriteAllText(manifestPath, manifest);
            FirmwareImage image = FirmwareImage.LoadBundle(manifestPath);
            Assert.Equal("1.13.0", image.Version);
            Assert.Equal(29, image.PageCount);
            foreach (string invalid in new[] {
                manifest.Replace(FirmwareImage.Target, "PIC18F2458"), manifest.Replace("\"profile_version\":1", "\"profile_version\":2"),
                manifest.Replace("\"adc_bits\":12", "\"adc_bits\":21"), manifest.Replace("\"usb_interval_ms\":4", "\"usb_interval_ms\":1"),
                manifest.Replace("pedals.ihx", "../pedals.ihx"), manifest.Replace("[14,13,12]", "[12,13,14]"), manifest.Replace("24500000", "96000000") })
            {
                File.WriteAllText(manifestPath, invalid);
                Assert.Throws<InvalidDataException>(() => FirmwareImage.LoadBundle(manifestPath));
            }
            File.WriteAllText(manifestPath, manifest);
            File.AppendAllText(imagePath, "\n");
            Assert.Throws<InvalidDataException>(() => FirmwareImage.LoadBundle(manifestPath));
        }
        finally { File.Delete(imagePath); File.Delete(manifestPath); Directory.Delete(folder); }
    }
    [Fact]
    public void Normal_joystick_and_pic_cannot_match_bootloader()
    {
        var d = new FirmwareDevice { Vid = 0x10C4, Pid = 0x8B02, Page = 1, Usage = 5, Input = 17 };
        Assert.False(d.IsBootloader);
        d.Page = 0xFF00; d.Usage = 1; d.Input = 5; d.Output = 65;
        Assert.True(d.IsBootloader);
        d.Feature = 18; Assert.False(d.IsBootloader);
        d.Feature = 0; d.Vid = 0x16C0; d.Pid = 0x05B7; Assert.False(d.IsBootloader);
    }
    [Fact]
    public void Readonly_probe_cannot_erase_or_program()
    {
        FirmwareBackup b = Backup(); var model = new FlashModel(b);
        var p = new FirmwareProgrammer(model, false, _ => { });
        p.Probe(b);
        Assert.DoesNotContain(model.Calls, call => call.command is 0x32 or 0x33 or 0x35 or 0x36);
        Assert.Throws<InvalidOperationException>(() => p.Program(b, FirmwareImage.FromHex(Hex), _ => { }));
        Assert.Equal(b.Program, model.Memory.Take(16384));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Program_and_restore_verify_all_pages_and_activate_last(bool restore)
    {
        FirmwareBackup b = Backup(); var model = new FlashModel(b);
        FirmwareImage image = restore ? FirmwareImage.Restore(b) : FirmwareImage.FromHex(Hex);
        for (int i = 0; i < 0x380; i++) model.Memory[0xF800 + i] = 0x42;
        byte[] beforeNv = model.Memory.Skip(0xF800).Take(1024).ToArray();
        byte[] exposed = image.Code; exposed[0] = 0; // Image must be immutable after review.
        var p = new FirmwareProgrammer(model, true, _ => { });
        p.Program(b, image, _ => { });
        Assert.Equal(image.Code, model.Memory.Take(0x3A00));
        Assert.Equal(b.Program.Skip(0x3A00), model.Memory.Skip(0x3A00).Take(0x600));
        Assert.Equal(restore ? b.Nv : beforeNv, model.Memory.Skip(0xF800).Take(1024));
        var writes = model.Calls.Where(c => c.command == 0x33).ToList();
        Assert.Equal(new byte[] { 0, 0, 2 }, writes.Last().data);
        Assert.DoesNotContain(writes.Take(writes.Count - 1), w => w.data[0] == 0 && w.data[1] == 0);
        Assert.DoesNotContain(model.Calls, c => c.command is 0x35 or 0x36);
        Assert.Equal(restore ? 43 : 29, model.Calls.Count(c => c.command == 0x32));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disconnect_before_or_after_every_command_stops_without_retry_or_restart(bool restore)
    {
        FirmwareBackup b = Backup(); FirmwareImage image = restore ? FirmwareImage.Restore(b) : FirmwareImage.FromHex(Hex);
        var baseline = new FlashModel(b);
        new FirmwareProgrammer(baseline, true, _ => { }).Program(b, image, _ => { });
        if (!restore)
        {
            // Golden packet stream produced independently by the hardware-tested Python engine.
            Assert.Equal("569d624fafa177fadd879efdb74e9b6afb44db616d85536ab78c300b6d773968", FirmwareImage.Hash(baseline.Calls.SelectMany(c => FirmwareProgrammer.Encode(c.command, c.data, true)).ToArray()));
        }
        for (int cut = 0; cut < baseline.Calls.Count; cut++)
        foreach (bool after in new[] { false, true })
        {
            var model = new FlashModel(b) { FailAt = cut, FailAfter = after };
            var programmer = new FirmwareProgrammer(model, true, _ => { });
            Assert.ThrowsAny<IOException>(() => programmer.Program(b, image, _ => { }));
            int calls = model.Calls.Count;
            Assert.Throws<IOException>(programmer.Run);
            Assert.Equal(calls, model.Calls.Count);
            Assert.Equal(b.Program.Skip(0x3A00), model.Memory.Skip(0x3A00).Take(0x600));
            Assert.Equal(b.Nv.Skip(0x380), model.Memory.Skip(0xFB80).Take(128));
            if (!restore) Assert.Equal(b.Nv, model.Memory.Skip(0xF800).Take(1024));
            Assert.DoesNotContain(model.Calls, c => c.command is 0x35 or 0x36);
        }
    }
    [Fact]
    public void Wrong_protected_page_crc_rejects_target_before_first_erase()
    {
        FirmwareBackup b = Backup(); var model = new FlashModel(b);
        model.Memory[0xFB90] ^= 1;
        var programmer = new FirmwareProgrammer(model, true, _ => { });
        Assert.Throws<IOException>(() => programmer.Program(b, FirmwareImage.FromHex(Hex), _ => { }));
        Assert.DoesNotContain(model.Calls, c => c.command is 0x32 or 0x33);
    }

    [Fact]
    public void Incomplete_image_cannot_pass_readonly_verification_for_restart()
    {
        FirmwareBackup b = Backup(); var model = new FlashModel(b);
        Assert.Throws<IOException>(() => new FirmwareProgrammer(model, false, _ => { }).VerifyImage(b, FirmwareImage.FromHex(Hex)));
        Assert.DoesNotContain(model.Calls, c => c.command is 0x32 or 0x33 or 0x36);
    }
    private sealed class FlashModel : IFirmwareTransport
    {
        public byte[] Memory = Enumerable.Repeat((byte)255, 65536).ToArray();
        public List<(byte command, byte[] data)> Calls = new List<(byte, byte[])>();
        public int FailAt = -1;
        public bool FailAfter;
        private bool _setup;
        public FlashModel(FirmwareBackup backup)
        { Buffer.BlockCopy(backup.Program, 0, Memory, 0, 16384); Buffer.BlockCopy(backup.Nv, 0, Memory, 0xF800, 1024); }
        private static ushort ReferenceCrc(byte[] bytes)
        {
            // Independent byte-wise reference, same XMODEM known vector as the Python engine.
            int crc = 0;
            foreach (byte b in bytes)
            {
                int x = ((crc >> 8) ^ b) & 255;
                x ^= x >> 4;
                crc = ((crc << 8) ^ (x << 12) ^ (x << 5) ^ x) & 65535;
            }
            return (ushort)crc;
        }
        public byte[] Exchange(byte[] report)
        {
            Assert.Equal(65, report.Length); Assert.Equal(0x24, report[1]);
            byte command = report[3]; byte[] data = report.Skip(4).Take(report[2] - 1).ToArray();
            int call = Calls.Count; Calls.Add((command, data));
            if (call == FailAt && !FailAfter) throw new IOException("Disconnect before command");
            byte status = 0x40;
            if (command == 0) status = 0x90;
            else if (command == 0x30) Assert.Equal(new byte[] { 0x32, 0x41 }, data);
            else if (command == 0x31) { Assert.Equal(new byte[] { 0xA5, 0xF1, 0 }, data); _setup = true; }
            else
            {
                Assert.True(_setup); int a = data[0] * 256 + data[1];
                if (command == 0x34)
                {
                    int end = data[2] * 256 + data[3];
                    if (ReferenceCrc(Memory.Skip(a).Take(end - a + 1).ToArray()) != data[4] * 256 + data[5]) status = 0x43;
                }
                else if (command == 0x32)
                {
                    int size = a < 0x3A00 ? 512 : 64;
                    Assert.True((a < 0x3A00 && a + size <= 0x3A00) || (a >= 0xF800 && a + size <= 0xFB80));
                    for (int i = 0; i < size; i++) Memory[a + i] = 255;
                }
                else if (command == 0x33)
                {
                    Assert.InRange(data.Length, 3, 61);
                    for (int i = 2; i < data.Length; i++)
                    {
                        int pos = a + i - 2;
                        Assert.True(pos < 0x3A00 || (pos >= 0xF800 && pos < 0xFB80));
                        Memory[pos] &= data[i];
                    }
                }
                else throw new Exception("Unexpected command");
            }
            if (call == FailAt) throw new IOException("Lost acknowledgement after command");
            return new byte[] { 0, status, 0, 8, 8 };
        }
        public void Dispose() { }
    }
}
