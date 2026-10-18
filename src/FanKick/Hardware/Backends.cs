using System.Diagnostics;
using FanKick.Core;
using FanKick.Modes;

namespace FanKick.Hardware;

public static class Backends
{
    public static List<IHardware> Create(bool demo, List<ModeGroup> modes)
    {
        if (demo) return [new DemoHardware()];
        var list = new List<IHardware>();
        void Try(Func<IHardware?> make, string what)
        {
            try { if (make() is { } h) list.Add(h); }
            catch (Exception ex) { Log.Write($"{what}: {ex.Message}"); }
        }
        if (OperatingSystem.IsWindows())
        {
            Try(() => new WindowsHardware(), "LibreHardwareMonitor");
            if (Environment.IsPrivilegedProcess)
                Try(() => { var a = AsusAcpi.TryOpen(); if (a != null) modes.AddRange(a.Modes()); return a; }, "ASUS ACPI");
        }
        else if (OperatingSystem.IsLinux()) Try(() => new LinuxHardware(), "hwmon");
        else if (OperatingSystem.IsMacOS()) Try(() => new MacHardware(), "SMC");
        return list;
    }

    // Other programs that drive fans, or load a hardware driver. Two at once fight over the same fans, and two programs
    // using the PawnIO driver at the same moment can crash one of them, so FanKick waits until the user says go.
    static readonly string[] Conflicting =
    [
        "FanKick", "FanControl", "ArgusMonitor", "HWiNFO64", "HWiNFO32", "HWiNFO", "SpeedFan", "GHelper", "ArmouryCrate",
        "MSIAfterburner", "NoteBookFanControl", "LibreHardwareMonitor", "OpenHardwareMonitor", "OmenMon",
        "coolercontrold", "fancontrol", "nbfc_service", "thinkfan", "Macs Fan Control", "TG Pro",
    ];

    public static List<string> Conflicts()
    {
        int me = Environment.ProcessId;
        string? myPath = Environment.ProcessPath;
        var found = new List<string>();
        foreach (var name in Conflicting)
            foreach (var p in Process.GetProcessesByName(name))
                using (p)
                {
                    if (p.Id == me) continue;
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    if (path != null && path == myPath) continue; // our own guard
                    found.Add(name);
                    break;
                }
        return found.Distinct().ToList();
    }
}
