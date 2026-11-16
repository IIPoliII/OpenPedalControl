using System;
using System.IO;
using System.Linq;

namespace OpenPedalControl.Firmware;

internal interface IFirmwareTransport : IDisposable
{
    byte[] Exchange(byte[] report);
}

/// <summary>AN945 application-only engine. A failed session cannot issue further commands.</summary>
internal sealed class FirmwareProgrammer
{
    private readonly IFirmwareTransport _transport;
    private readonly bool _writable;
    private readonly Action<string> _log;
    private bool _failed;
    public FirmwareProgrammer(IFirmwareTransport transport, bool writable, Action<string> log)
    { _transport = transport; _writable = writable; _log = log; }

    internal static ushort Crc(byte[] data)
    {
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++) crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021 : 0));
        }
        return crc;
    }
    internal static int PageSize(int address)
    {
        if (address >= 0 && address < FirmwareImage.ApplicationEnd) return 512;
        if (address >= FirmwareImage.NvStart && address < FirmwareImage.NvEnd) return 64;
        throw new InvalidDataException("Protected flash address.");
    }
    internal static byte[] Encode(byte command, byte[] data, bool writable)
    {
        int address = data.Length >= 2 ? data[0] * 256 + data[1] : -1;
        if (command == 0x32 || command == 0x33)
        {
            if (!writable) throw new InvalidOperationException("This session cannot erase or program flash.");
            int size = PageSize(address);
            if (command == 0x32)
            {
                if (data.Length != 2 || address % size != 0) throw new InvalidDataException("Invalid erase page.");
            }
            else if (data.Length < 3 || data.Length > 61 || PageSize(address + data.Length - 3) != size ||
                     address / size != (address + data.Length - 3) / size)
                throw new InvalidDataException("Write crosses an allowed page or exceeds one USB packet.");
        }
        else
        {
            bool valid = command switch
            {
                0 => data.Length == 0,
                0x30 => data.SequenceEqual(new byte[] { 0x32, 0x41 }),
                0x31 => data.SequenceEqual(new byte[] { 0xA5, 0xF1, 0 }),
                0x36 => data.SequenceEqual(new byte[] { 0, 0 }),
                0x34 => data.Length == 6 && ValidCrcRange(address, data[2] * 256 + data[3]),
                _ => false, // No Lock command, full-chip erase or bootloader write exists.
            };
            if (!valid) throw new InvalidDataException("Unsupported bootloader command or range.");
        }
        var report = new byte[65];
        report[1] = 0x24; report[2] = (byte)(data.Length + 1); report[3] = command;
        Buffer.BlockCopy(data, 0, report, 4, data.Length);
        return report;
    }
    private static bool ValidCrcRange(int start, int end) => start <= end &&
        ((start >= 0 && end < 0x4000) || (start >= 0xF800 && end < 0xFC00));

    private void Command(byte command, byte[] data, byte expected = 0x40)
    {
        if (_failed) throw new IOException("Session stopped after an earlier error. Reconnect before retrying.");
        byte[] report = Encode(command, data, _writable);
        try
        {
            _log($"Command {command:X2}" + (data.Length >= 2 ? $" address/data {data[0]:X2}{data[1]:X2}" : ""));
            byte[] reply = _transport.Exchange(report);
            if (reply.Length != 5 || reply[0] != 0 || reply[1] != expected)
                throw new IOException($"Bootloader rejected command {command:X2} or returned an invalid reply.");
            _log($"Reply {reply[1]:X2}");
        }
        catch { _failed = true; throw; }
    }
    private void Verify(int address, byte[] data)
    {
        int end = address + data.Length - 1;
        ushort crc = Crc(data);
        Command(0x34, new[] { (byte)(address >> 8), (byte)address, (byte)(end >> 8), (byte)end, (byte)(crc >> 8), (byte)crc });
    }
    private void CheckPreserved(FirmwareBackup backup)
    {
        Verify(0x3A00, backup.Program.Skip(0x3A00).ToArray());
        Verify(0xFB80, backup.Nv.Skip(0x380).ToArray());
    }
    public void Probe(FirmwareBackup backup)
    {
        Command(0x30, new byte[] { 0x32, 0x41 });
        Command(0, Array.Empty<byte>(), 0x90);
        Command(0x31, new byte[] { 0xA5, 0xF1, 0 }); // RAM setup only.
        CheckPreserved(backup);
        _log("Target, bootloader and protection-page CRCs verified. No flash written.");
    }
    private void Write(int address, byte[] bytes) => Command(0x33,
        new[] { (byte)(address >> 8), (byte)address }.Concat(bytes).ToArray());

    public void VerifyImage(FirmwareBackup backup, FirmwareImage image)
    {
        Probe(backup);
        Verify(0, image.Code);
        foreach (var page in image.Pages().Where(p => p.address >= FirmwareImage.NvStart)) Verify(page.address, page.data);
        CheckPreserved(backup);
        _log("Selected application image verified without writing flash.");
    }

    /// <summary>True once <see cref="Program"/> has started erasing; before that the existing firmware is intact.</summary>
    public bool FlashTouched { get; private set; }

    public void Program(FirmwareBackup backup, FirmwareImage image, Action<int> progress)
    {
        if (!_writable) throw new InvalidOperationException("Read-only firmware session.");
        Probe(backup); // Repeat the checks even after the separate review step.
        FlashTouched = true; // From here the old application is gone until the new one is fully written.
        Command(0x32, new byte[] { 0, 0 });
        Verify(0, Enumerable.Repeat((byte)255, 512).ToArray());
        int done = 0;
        foreach (var page in image.Pages())
        {
            if (page.address != 0) Command(0x32, new[] { (byte)(page.address >> 8), (byte)page.address });
            byte[] data = (byte[])page.data.Clone();
            if (page.address == 0) data[0] = 255;
            for (int offset = page.address == 0 ? 1 : 0; offset < data.Length; offset += 59)
            {
                byte[] chunk = data.Skip(offset).Take(59).ToArray();
                if (chunk.Any(b => b != 255)) Write(page.address + offset, chunk);
            }
            Verify(page.address, data);
            _log($"Page {page.address:X4} verified");
            progress(++done * 95 / image.PageCount);
        }
        byte[] code = image.Code;
        code[0] = 255;
        Verify(0, code);
        CheckPreserved(backup);
        _log("All pages checked; activating reset vector last");
        Write(0, image.Code.Take(1).ToArray());
        Verify(0, image.Code.Take(512).ToArray());
        Verify(0, image.Code);
        CheckPreserved(backup);
        progress(100);
        _log("Application verified. Protected areas unchanged.");
    }
    public void Run() => Command(0x36, new byte[] { 0, 0 });
}
