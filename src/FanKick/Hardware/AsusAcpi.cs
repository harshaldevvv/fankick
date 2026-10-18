using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FanKick.Core;
using FanKick.Modes;
using Microsoft.Win32.SafeHandles;

namespace FanKick.Hardware;

// ASUS laptops (ROG, TUF, Zenbook, Vivobook) on Windows, through the same ATKACPI driver path G-Helper uses: no extra
// driver beyond ASUS's own ATK package. Fans are driven by writing a flat 8-point fan curve (every point at our %),
// so the embedded controller runs the fan at exactly that speed whatever temperature it sees. Also the ASUS
// performance mode (Silent / Balanced / Turbo) and the battery charge limit.
[SupportedOSPlatform("windows")]
public sealed class AsusAcpi : Core.IHardware, IResumable, IWriteLimited
{
    const uint Ioctl = 0x0022240C, Dsts = 0x53545344, Devs = 0x53564544;
    const uint ModeDev = 0x00120075, ChargeDev = 0x00120057;
    static readonly (string name, uint curve, uint rpm)[] Known = [("CPU fan", 0x00110024, 0x00110013), ("GPU fan", 0x00110025, 0x00110014), ("Mid fan", 0x00110032, 0x00110031)];
    static readonly string[] ModeNames = ["Balanced", "Turbo", "Silent"]; // firmware values 0, 1, 2

    // Some BIOS versions (seen on a G513QE, BIOS 331) flip the fan's custom curve on/off at every write instead of
    // just setting it; there a speed change must be written twice. Off by default; Settings > ASUS turns it on.
    public static bool WriteTwice { get; set; }
    // The ASUS mode written when FanKick opens the device (the last one picked in Modes). Writing a mode switches every
    // custom curve off, so FanKick starts from a known state on any BIOS.
    public static string StartMode { get; set; } = "Balanced";

    readonly SafeFileHandle handle;
    readonly object gate = new();   // one DeviceIoControl at a time
    readonly object op = new();     // one multi-write sequence (set, mode change, restore) at a time
    readonly List<Sensor> sensors = [];
    readonly List<FanControl> fans = [];
    readonly Dictionary<FanControl, (uint curve, uint rpm, Sensor rpmSensor)> devs = [];
    readonly Dictionary<FanControl, int> applied = []; // fans whose custom curve is on right now, and at what %
    readonly Dictionary<FanControl, int> wanted = [];  // fans FanKick drives, and at what % (survives mode writes)
    int mode;

    public string Name => "ASUS ACPI";
    // Each curve write dips the fan by ~1,500 rpm, and on flip-on-write BIOSes every extra write is a chance to lose
    // track of whether the curve is on: write only real changes.
    public int MinStep => 10;
    public string? Notice => null;
    public IReadOnlyList<Sensor> Sensors => sensors;
    public IReadOnlyList<FanControl> Fans => fans;

    AsusAcpi(SafeFileHandle h, bool probeOnly = false)
    {
        handle = h;
        if (probeOnly) return;
        foreach (var (name, curve, rpm) in Find(this)) AddFan(name, curve, rpm);
        if (fans.Count > 0) WriteMode(Math.Max(0, Array.IndexOf(ModeNames, StartMode)));
    }

    // The fans this firmware has: a stored curve and a readable rpm. Models without a fan refuse the call (error 1)
    // or answer "unsupported" (0xFFFFFFFE); either just means no such fan.
    static IEnumerable<(string name, uint curve, uint rpm)> Find(AsusAcpi a)
    {
        foreach (var k in Known)
        {
            bool ok;
            try
            {
                var buf = a.Call(Dsts, Args(k.curve));
                uint first = BitConverter.ToUInt32(buf, 0);
                ok = !buf.Take(16).All(b => b == 0) && first is not (0xFFFFFFFE or 0xFFFFFFFF);
                if (ok) a.Call(Dsts, Args(k.rpm));
            }
            catch (IOException ex) { Log.Write($"ASUS ACPI: no {k.name} ({ex.Message})"); ok = false; }
            if (ok) yield return k;
        }
    }

    // `FanKick --probe-asus`: log which ASUS fans and speeds FanKick sees, reading only (no writes).
    public static void Probe()
    {
        var h = CreateFile(@"\\.\ATKACPI", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) { Log.Write("probe: no ATKACPI device (error " + Marshal.GetLastWin32Error() + ")"); return; }
        using (h)
        {
            var a = new AsusAcpi(h, probeOnly: true);
            foreach (var (name, curve, rpm) in Find(a))
                Log.Write($"probe: {name} (curve {curve:x8}) at {(BitConverter.ToUInt32(a.Call(Dsts, Args(rpm)), 0) & 0xFFFF) * 100} rpm");
            Log.Write("probe: done");
        }
    }

    void AddFan(string name, uint curve, uint rpm)
    {
        var s = new Sensor($"asus/{curve:x}/rpm", name, "ASUS", SensorKind.Fan);
        var f = new FanControl($"asus/{curve:x}", name, "ASUS") { RpmSensorId = s.Id };
        sensors.Add(s);
        fans.Add(f);
        devs[f] = (curve, rpm, s);
    }

    public static AsusAcpi? TryOpen()
    {
        var h = CreateFile(@"\\.\ATKACPI", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        try
        {
            var a = new AsusAcpi(h);
            if (a.fans.Count > 0) return a;
        }
        catch (Exception ex) { Log.Write("ASUS ACPI: " + ex.Message); }
        h.Dispose();
        return null;
    }

    public IEnumerable<ModeGroup> Modes()
    {
        yield return new ModeGroup("ASUS mode", ModeNames, () => ModeNames[mode], v =>
        {
            lock (op) { WriteMode(Math.Max(0, Array.IndexOf(ModeNames, v))); Reapply(); }
        }) { Note = "The same firmware modes as Armoury Crate. Fans you set in FanKick stay under FanKick's control." };
        string? charge = null;
        yield return new ModeGroup("Battery charge limit", ["100%", "80%", "60%"], () => charge, v =>
        {
            Call(Devs, Args(ChargeDev, uint.Parse(v.TrimEnd('%'))));
            charge = v;
        }) { Note = "Stops charging at this level to slow battery wear. The firmware can't report it back, so it shows blank until set. FanKick sets it again at every start." };
    }

    // A mode write switches every custom curve off (back to the firmware's own curves).
    void WriteMode(int m)
    {
        mode = m;
        Call(Devs, Args(ModeDev, (uint)m));
        applied.Clear();
        Thread.Sleep(500);
    }

    // Switch our curves back on after something turned them all off.
    void Reapply()
    {
        foreach (var (f, pct) in wanted.ToList()) Apply(f, pct);
    }

    // After standby the firmware may have dropped our curves: redo the startup sequence (mode, then every curve).
    public void Resume()
    {
        lock (op) { WriteMode(mode); Reapply(); }
        Log.Write("ASUS ACPI: mode and fan curves re-applied after standby");
    }

    public void Update()
    {
        foreach (var (curve, rpm, s) in devs.Values)
            s.Value = (BitConverter.ToUInt32(Call(Dsts, Args(rpm)), 0) & 0xFFFF) * 100;
    }

    public void Set(FanControl fan, int percent) { lock (op) Apply(fan, percent); }

    void Apply(FanControl fan, int pct)
    {
        // On a flipping BIOS a fan whose curve is on needs two writes (off, then on at the new %); one that is off, one.
        Write(devs[fan].curve, pct, WriteTwice && applied.ContainsKey(fan) ? 2 : 1);
        applied[fan] = wanted[fan] = pct;
    }

    // Flat curve: temperatures 0..70 °C, all at pct. The first point must be 0 °C, the temperature the EC sees for a
    // sensor it can't read.
    void Write(uint curve, int pct, int times)
    {
        var a = new byte[20];
        BitConverter.GetBytes(curve).CopyTo(a, 0);
        for (int i = 0; i < 8; i++) { a[4 + i] = (byte)(i * 10); a[12 + i] = (byte)Math.Clamp(pct, 0, 100); }
        for (int i = 0; i < times; i++) { if (i > 0) Thread.Sleep(300); Call(Devs, a); }
    }

    // Re-writing the mode turns every custom curve off; the other fans then get theirs back.
    public void Restore(FanControl fan)
    {
        lock (op)
        {
            wanted.Remove(fan);
            WriteMode(mode);
            Reapply();
        }
    }

    public void Dispose() => handle.Dispose();

    static byte[] Args(params uint[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();

    byte[] Call(uint method, byte[] args)
    {
        var buf = new byte[8 + args.Length];
        BitConverter.GetBytes(method).CopyTo(buf, 0);
        BitConverter.GetBytes((uint)args.Length).CopyTo(buf, 4);
        args.CopyTo(buf, 8);
        var outBuf = new byte[32];
        lock (gate)
            if (!DeviceIoControl(handle, Ioctl, buf, buf.Length, outBuf, outBuf.Length, out _, IntPtr.Zero))
                throw new IOException("ATKACPI call failed, error " + Marshal.GetLastWin32Error());
        return outBuf;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int ret, IntPtr ov);
}
