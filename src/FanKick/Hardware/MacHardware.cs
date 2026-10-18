using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FanKick.Core;

namespace FanKick.Hardware;

// macOS: the System Management Controller through IOKit's AppleSMC service, on Intel and Apple Silicon. Every
// temperature key that reads sensibly is shown; fans are driven by switching them to forced mode and writing a target
// rpm. Reading needs no privileges; writing needs root. The SMC takes fans back within seconds of the controlling
// process exiting, which is a built-in crash fail-safe.
[SupportedOSPlatform("macos")]
public sealed class MacHardware : IHardware
{
    const string IOKit = "IOKit";
    [DllImport(IOKit)] static extern IntPtr IOServiceMatching(string name);
    [DllImport(IOKit)] static extern uint IOServiceGetMatchingService(uint mainPort, IntPtr matching);
    [DllImport(IOKit)] static extern int IOServiceOpen(uint service, uint task, uint type, out uint conn);
    [DllImport(IOKit)] static extern int IOServiceClose(uint conn);
    [DllImport(IOKit)] static extern int IOObjectRelease(uint obj);
    [DllImport(IOKit)] static extern int IOConnectCallStructMethod(uint conn, uint selector, byte[] input, nuint inSize, byte[] output, ref nuint outSize);

    const int KeyDataSize = 80;
    const byte CmdReadBytes = 5, CmdWriteBytes = 6, CmdReadIndex = 8, CmdReadKeyInfo = 9;

    readonly uint conn;
    readonly object gate = new();
    readonly List<Sensor> sensors = [];
    readonly List<(Sensor s, string key)> reads = [];
    readonly List<FanControl> fans = [];
    readonly Dictionary<FanControl, int> fanIndex = [];
    readonly bool hasFtst;

    public string Name => "Apple SMC";
    public string? Notice { get; }
    public IReadOnlyList<Sensor> Sensors => sensors;
    public IReadOnlyList<FanControl> Fans => fans;

    public MacHardware()
    {
        uint svc = IOServiceGetMatchingService(0, IOServiceMatching("AppleSMC"));
        if (svc == 0) throw new IOException("AppleSMC not found");
        int rc = IOServiceOpen(svc, MachTaskSelf(), 0, out conn);
        IOObjectRelease(svc);
        if (rc != 0) throw new IOException($"AppleSMC open failed ({rc:x})");

        uint count = Read("#KEY") is { } k ? BinaryPrimitives.ReadUInt32BigEndian(k.bytes) : 0;
        for (uint i = 0; i < count; i++)
        {
            var key = KeyAt(i);
            if (key is null || key[0] != 'T') continue;
            if (Value(key) is not { } v || v <= 0 || v > 150) continue; // unused slots read 0 or garbage
            var (name, group, critical) = Describe(key);
            var s = new Sensor("smc/" + key, name, group, SensorKind.Temperature) { Critical = critical, Cpu = critical && group == "CPU", Value = v };
            sensors.Add(s);
            reads.Add((s, key));
        }
        if (Value("PSTR") is { } p && p > 0) { var s = new Sensor("smc/PSTR", "System total", "Power", SensorKind.Power); sensors.Add(s); reads.Add((s, "PSTR")); }

        hasFtst = Read("Ftst") != null;
        int nf = (int)(Value("FNum") ?? 0);
        for (int f = 0; f < nf; f++)
        {
            var rpm = new Sensor($"smc/F{f}Ac", nf == 1 ? "Fan" : $"Fan {f + 1}", "Fans", SensorKind.Fan);
            sensors.Add(rpm);
            reads.Add((rpm, $"F{f}Ac"));
            var fan = new FanControl($"smc/fan{f}", rpm.Name, "Fans") { RpmSensorId = rpm.Id };
            fans.Add(fan);
            fanIndex[fan] = f;
        }
        Notice = nf == 0 ? "This Mac has no fans: monitoring only."
            : Environment.UserName != "root" ? "Readings work; fan control and energy modes need admin. Use \"Restart as administrator\"." : null;
    }

    static (string name, string group, bool critical) Describe(string key) => key switch
    {
        "TC0P" => ("CPU proximity", "CPU", true), "TC0D" or "TC0E" or "TC0F" => ("CPU die", "CPU", true), "TCXC" => ("CPU PECI", "CPU", true),
        "TG0P" => ("GPU proximity", "GPU", true), "TG0D" => ("GPU die", "GPU", true),
        "TB0T" or "TB1T" or "TB2T" => ("Battery " + key[2], "Battery", false), "Tm0P" => ("Mainboard", "SMC", false),
        "TA0P" => ("Ambient", "SMC", false), "TW0P" => ("Wireless", "SMC", false), "Ts0P" => ("Palm rest", "SMC", false),
        _ when key.StartsWith("Tp") => ($"CPU core {key[2..]}", "CPU", true),  // Apple Silicon performance cores
        _ when key.StartsWith("Te") => ($"CPU E-core {key[2..]}", "CPU", true), // efficiency cores
        _ when key.StartsWith("Tg") => ($"GPU {key[2..]}", "GPU", true),
        _ when key.StartsWith("TH") => ($"SSD {key[2..]}", "SSD", false),
        _ when key.StartsWith("TB") => ($"Battery {key[2..]}", "Battery", false),
        _ => ($"Sensor {key}", "SMC", false),
    };

    public void Update()
    {
        foreach (var (s, key) in reads) s.Value = Value(key) ?? float.NaN;
    }

    // Mode key: F0Md on Intel/M1-M4, F0md (lowercase) on M5; very old Intel Macs only have the FS! bitmask.
    string? ModeKey(int f) => Read($"F{f}Md") != null ? $"F{f}Md" : Read($"F{f}md") != null ? $"F{f}md" : null;

    public void Set(FanControl fan, int percent)
    {
        int f = fanIndex[fan];
        float min = Value($"F{f}Mn") ?? 0, max = Value($"F{f}Mx") ?? 6000;
        var mode = ModeKey(f);
        if (mode is null) { var fs = Value("FS! ") ?? 0; Write("FS! ", (ushort)((int)fs | (1 << f))); }
        else if (Value(mode) != 1)
        {
            // M3/M4: unlock with Ftst, then keep asking until thermalmonitord lets go (about 3-6 s).
            if (hasFtst) Write("Ftst", 1);
            var until = DateTime.UtcNow.AddSeconds(hasFtst ? 8 : 0);
            do
            {
                try { Write(mode, 1); } catch when (DateTime.UtcNow < until) { }
                if (Value(mode) == 1) break;
                Thread.Sleep(300);
            } while (DateTime.UtcNow < until);
        }
        Write($"F{f}Tg", min + (max - min) * Math.Clamp(percent, 0, 100) / 100f);
    }

    public void Restore(FanControl fan)
    {
        int f = fanIndex[fan];
        if (ModeKey(f) is { } mode) Write(mode, 0);
        else { var fs = Value("FS! ") ?? 0; Write("FS! ", (ushort)((int)fs & ~(1 << f))); }
        if (hasFtst && fans.All(x => x == fan || x.Percent < 0)) Write("Ftst", 0);
    }

    public void Dispose() => IOServiceClose(conn);

    // ---- SMC protocol: an 80-byte struct in, the same struct out ----
    // key @0, keyInfo.dataSize @28, keyInfo.dataType @32, result @40, data8 (command) @42, data32 @44, bytes @48.
    byte[]? Call(uint key, byte cmd, uint size = 0, uint data32 = 0, byte[]? bytes = null)
    {
        var input = new byte[KeyDataSize];
        var output = new byte[KeyDataSize];
        BitConverter.GetBytes(key).CopyTo(input, 0);
        BitConverter.GetBytes(size).CopyTo(input, 28);
        input[42] = cmd;
        BitConverter.GetBytes(data32).CopyTo(input, 44);
        bytes?.CopyTo(input, 48);
        nuint outSize = KeyDataSize;
        int rc;
        lock (gate) rc = IOConnectCallStructMethod(conn, 2, input, KeyDataSize, output, ref outSize);
        if (rc == unchecked((int)0xe00002c2)) throw new UnauthorizedAccessException("Changing fans on a Mac needs FanKick to run as admin");
        return rc != 0 || output[40] != 0 ? null : output;
    }

    static uint FourCC(string k) => BinaryPrimitives.ReadUInt32BigEndian(System.Text.Encoding.ASCII.GetBytes(k.PadRight(4)));

    (string type, byte[] bytes)? Read(string key)
    {
        var info = Call(FourCC(key), CmdReadKeyInfo);
        if (info is null) return null;
        uint size = BitConverter.ToUInt32(info, 28);
        string type = System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt32(info, 32))));
        var data = Call(FourCC(key), CmdReadBytes, size);
        return data is null ? null : (type, data[48..(48 + (int)Math.Min(size, 32))]);
    }

    string? KeyAt(uint i)
    {
        var o = Call(0, CmdReadIndex, 0, i);
        return o is null ? null : System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt32(o, 0))));
    }

    float? Value(string key) => Read(key) is { } r ? Decode(r.type, r.bytes) : null;

    static float? Decode(string type, byte[] b) => type switch
    {
        "flt " when b.Length >= 4 => BitConverter.ToSingle(b, 0),
        "sp78" when b.Length >= 2 => BinaryPrimitives.ReadInt16BigEndian(b) / 256f,
        "fpe2" when b.Length >= 2 => BinaryPrimitives.ReadUInt16BigEndian(b) / 4f,
        "ui8 " when b.Length >= 1 => b[0],
        "ui16" when b.Length >= 2 => BinaryPrimitives.ReadUInt16BigEndian(b),
        "ui32" when b.Length >= 4 => BinaryPrimitives.ReadUInt32BigEndian(b),
        _ => null,
    };

    // Write in the key's own type.
    void Write(string key, float v)
    {
        var r = Read(key) ?? throw new IOException($"SMC key {key} not found");
        byte[] b = r.type switch
        {
            "flt " => BitConverter.GetBytes(v),
            "fpe2" => BigEndian((ushort)(v * 4)),
            "sp78" => BigEndian((ushort)(short)(v * 256)),
            "ui16" => BigEndian((ushort)v),
            "ui32" => [.. BitConverter.GetBytes((uint)v).Reverse()],
            _ => [(byte)v],
        };
        if (Call(FourCC(key), CmdWriteBytes, (uint)b.Length, 0, b) is null) throw new IOException($"SMC write {key} refused");
    }

    static byte[] BigEndian(ushort v) => [(byte)(v >> 8), (byte)v];

    static uint MachTaskSelf()
    {
        var lib = NativeLibrary.Load("/usr/lib/libSystem.dylib");
        return (uint)Marshal.ReadInt32(NativeLibrary.GetExport(lib, "mach_task_self_"));
    }
}
