using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace OpenPedalControl.Firmware;

/// <summary>Immutable application image, validated entirely before any USB connection.</summary>
public sealed class FirmwareImage
{
    public const string Target = "HE_SIM_PEDALS_EFM8UB10F16G_QFN28";
    internal const int ApplicationEnd = 0x3A00;
    internal const int NvStart = 0xF800, NvEnd = 0xFB80;
    private readonly byte[] _code;
    private readonly byte[]? _nv;
    private FirmwareImage(byte[] code, byte[]? nv, string version, string hash, int cpuHz = 24500000)
    { _code = code; _nv = nv; Version = version; Sha256 = hash; CpuHz = cpuHz; }
    public string Version { get; }
    public string Sha256 { get; }
    public int CpuHz { get; private set; }
    public bool IsRestore => _nv != null;
    public int PageCount => ApplicationEnd / 512 + (_nv?.Length ?? 0) / 64;
    internal byte[] Code => (byte[])_code.Clone();
    internal IEnumerable<(int address, byte[] data)> Pages()
    {
        for (int a = 0; a < ApplicationEnd; a += 512) yield return (a, _code.Skip(a).Take(512).ToArray());
        if (_nv != null)
            for (int a = 0; a < _nv.Length; a += 64) yield return (NvStart + a, _nv.Skip(a).Take(64).ToArray());
    }

    [DataContract]
    private sealed class Manifest
    {
        [DataMember(Name = "schema")] public int Schema { get; set; }
        [DataMember(Name = "target")] public string TargetName { get; set; } = "";
        [DataMember(Name = "version")] public string Version { get; set; } = "";
        [DataMember(Name = "image")] public string Image { get; set; } = "";
        [DataMember(Name = "sha256")] public string Hash { get; set; } = "";
        [DataMember(Name = "profile_version")] public int ProfileVersion { get; set; }
        [DataMember(Name = "adc_bits")] public int AdcBits { get; set; }
        [DataMember(Name = "usb_interval_ms")] public int Interval { get; set; }
        [DataMember(Name = "axes")] public int Axes { get; set; }
        [DataMember(Name = "cpu_hz")] public int CpuHz { get; set; }
        [DataMember(Name = "adc_mux")] public int[] Mux { get; set; } = Array.Empty<int>();
    }

    public static FirmwareImage LoadBundle(string manifestPath)
    {
        var info = new FileInfo(manifestPath);
        if (info.Length > 16384) throw new InvalidDataException("Firmware manifest is too large.");
        using var stream = File.OpenRead(manifestPath);
        return LoadManifest(stream, name =>
        {
            string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, name);
            if (new FileInfo(path).Length > 256000) throw new InvalidDataException("Firmware image is too large.");
            return File.ReadAllBytes(path);
        });
    }

    public static FirmwareImage LoadBundled()
    {
        var assembly = typeof(FirmwareImage).Assembly;
        using var manifest = assembly.GetManifestResourceStream("Firmware.pedals.firmware.json")
            ?? throw new InvalidDataException("The included firmware is unavailable. Reinstall OpenPedalControl.");
        return LoadManifest(manifest, name =>
        {
            using var stream = assembly.GetManifestResourceStream("Firmware." + name)
                ?? throw new InvalidDataException("The included firmware image is unavailable.");
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return bytes.ToArray();
        });
    }

    private static FirmwareImage LoadManifest(Stream stream, Func<string, byte[]> readImage)
    {
        var m = new DataContractJsonSerializer(typeof(Manifest)).ReadObject(stream) as Manifest;
        if (m == null || m.Schema != 1 || m.TargetName != Target || m.ProfileVersion != 1 ||
            m.AdcBits != 12 || m.Interval != 4 || m.Axes != 3 || (m.CpuHz != 24500000 && m.CpuHz != 48000000) ||
            m.Mux == null || !m.Mux.SequenceEqual(new[] { 14, 13, 12 }) || !System.Version.TryParse(m.Version, out _))
            throw new InvalidDataException("This firmware bundle is not supported by this app. Expected the three-input EFM8 board, profile v1, 12-bit axes and 4 ms USB interval.");
        if (string.IsNullOrWhiteSpace(m.Image) || m.Image != Path.GetFileName(m.Image) ||
            m.Image.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("The firmware image must be next to its manifest.");
        byte[] raw = readImage(m.Image);
        string hash = Hash(raw);
        if (!string.Equals(hash, m.Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Firmware checksum does not match its manifest.");
        FirmwareImage image = FromHex(System.Text.Encoding.ASCII.GetString(raw), m.Version, hash);
        image.CpuHz = m.CpuHz;
        return image;
    }

    internal static FirmwareImage FromHex(string text, string version = "test", string hash = "test")
    {
        var data = new Dictionary<long, byte>();
        long baseAddress = 0;
        bool ended = false;
        foreach (string value in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = value.Trim();
            if (line.Length == 0) continue;
            if (ended || line[0] != ':' || line.Length % 2 != 1 || line.Length > 521)
                throw new InvalidDataException("Invalid Intel HEX record.");
            var record = new byte[(line.Length - 1) / 2];
            for (int i = 0; i < record.Length; i++)
            {
                string pair = line.Substring(1 + 2 * i, 2);
                if (!pair.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f'))
                    throw new InvalidDataException("Invalid Intel HEX digit.");
                record[i] = Convert.ToByte(pair, 16);
            }
            if (record.Length < 5 || record.Length != record[0] + 5 || (record.Sum(b => (int)b) & 255) != 0)
                throw new InvalidDataException("Invalid Intel HEX length or checksum.");
            int size = record[0], offset = record[1] * 256 + record[2], kind = record[3];
            if (kind == 0)
            {
                if (offset + size > 65536) throw new InvalidDataException("HEX record crosses its address bank.");
                for (int i = 0; i < size; i++)
                {
                    long a = baseAddress + offset + i;
                    if (a >= ApplicationEnd || data.ContainsKey(a))
                        throw new InvalidDataException("Image contains duplicate or protected addresses. Only application firmware is accepted.");
                    data.Add(a, record[4 + i]);
                }
            }
            else if (kind == 1 && size == 0 && offset == 0) ended = true;
            else if ((kind == 2 || kind == 4) && size == 2 && offset == 0)
                baseAddress = (long)(record[4] * 256 + record[5]) << (kind == 2 ? 4 : 16);
            else throw new InvalidDataException("Unsupported Intel HEX record type.");
        }
        if (!ended || !data.TryGetValue(0, out byte op) || op != 2 || !data.ContainsKey(1) || !data.ContainsKey(2))
            throw new InvalidDataException("Incomplete firmware reset vector or missing HEX end record.");
        int entry = data[1] * 256 + data[2];
        if (entry < 3 || entry >= ApplicationEnd || !data.ContainsKey(entry))
            throw new InvalidDataException("Reset vector does not reach application code.");
        var code = Enumerable.Repeat((byte)255, ApplicationEnd).ToArray();
        foreach (var item in data) code[(int)item.Key] = item.Value;
        return new FirmwareImage(code, null, version, hash);
    }

    internal static FirmwareImage Restore(FirmwareBackup backup) => new FirmwareImage(
        backup.Program.Take(ApplicationEnd).ToArray(), backup.Nv.Take(NvEnd - NvStart).ToArray(),
        "Original backup", Hash(backup.Program.Concat(backup.Nv).ToArray()));

    internal static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
}

public sealed class FirmwareBackup
{
    private readonly byte[] _program, _nv;
    internal byte[] Program => (byte[])_program.Clone();
    internal byte[] Nv => (byte[])_nv.Clone();
    internal FirmwareBackup(byte[] program, byte[] nv)
    {
        if (program.Length != 0x4000 || nv.Length != 1024 || nv[0x3BF] != 0 || nv[0x3FE] != 0xA5 || nv[0x3FF] != 0)
            throw new InvalidDataException("Invalid original backup size, boot signature or protection bytes.");
        _program = (byte[])program.Clone(); _nv = (byte[])nv.Clone();
    }
    public static FirmwareBackup Load(string folder)
    {
        string programPath = Path.Combine(folder, "program-0000-3fff.bin");
        string nvPath = Path.Combine(folder, "nonvolatile-f800-fbff.bin");
        if (new FileInfo(programPath).Length != 16384 || new FileInfo(nvPath).Length != 1024)
            throw new InvalidDataException("Incomplete original backup files.");
        byte[] p = File.ReadAllBytes(programPath);
        byte[] n = File.ReadAllBytes(nvPath);
        if (FirmwareImage.Hash(p) != "288c0b9a926ffc8c58abc4b20451d26461a6228fcaa03add05c86afd9eebb445" ||
            FirmwareImage.Hash(n) != "ce63f41254fe47809219539dfd5a8650c3c09f578f437bc7235a752a950aefaf")
            throw new InvalidDataException("This assistant currently supports only the verified bench-board backup. These backup files do not match it.");
        return new FirmwareBackup(p, n);
    }
}
