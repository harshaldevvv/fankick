using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FanKick.Core;

namespace FanKick.Modes;

// One performance setting the OS or the firmware offers: a title, its choices, and how to read and change it.
public sealed class ModeGroup(string title, string[] options, Func<string?> get, Action<string> set)
{
    public string Title { get; } = title;
    public string[] Options { get; } = options;
    public string? Note { get; init; }
    public string? Current { get { try { return get(); } catch { return null; } } }
    public void Set(string option) { set(option); Log.Write($"{Title} -> {option}"); }
}

public static class PerfModes
{
    public static List<ModeGroup> Detect()
    {
        var list = new List<ModeGroup>();
        try
        {
            if (OperatingSystem.IsWindows()) list.AddRange(WindowsModes.All());
            else if (OperatingSystem.IsLinux()) list.AddRange(LinuxModes.All());
            else if (OperatingSystem.IsMacOS()) list.AddRange(MacModes.All());
        }
        catch (Exception ex) { Log.Write("modes: " + ex.Message); }
        return list;
    }

    public static (int code, string output) Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
            string o = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10000)) { p.Kill(); return (-1, o); }
            return (p.ExitCode, o);
        }
        catch { return (-1, ""); }
    }
}

[SupportedOSPlatform("windows")]
static class WindowsModes
{
    [DllImport("powrprof.dll")] static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr sub, uint access, uint index, byte[]? buffer, ref uint size);
    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)] static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[]? buffer, ref uint size);
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    // Windows 10 1709+ / 11 "Power mode" slider (undocumented but stable, used by the Settings app itself).
    [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid overlay);

    static readonly (string name, Guid id)[] Overlays =
    [
        ("Best efficiency", new("961cc777-2547-4f9d-8174-7d86181b8a7a")),
        ("Balanced", Guid.Empty),
        ("Best performance", new("ded574b5-45a0-4f42-8737-46345c09c238")),
    ];
    static Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00"), PerfBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    static readonly string[] BoostNames = ["Off", "On", "Aggressive"]; // PERFBOOSTMODE 0, 1, 2

    public static IEnumerable<ModeGroup> All()
    {
        var plans = Plans();
        if (plans.Count > 0)
            yield return new ModeGroup("Windows power plan", plans.Select(p => p.name).ToArray(),
                () => plans.FirstOrDefault(p => p.id == Active()).name,
                v => { var g = plans.First(p => p.name == v).id; PowerSetActiveScheme(IntPtr.Zero, ref g); });
        if (PowerGetEffectiveOverlayScheme(out _) == 0)
            yield return new ModeGroup("Windows power mode", Overlays.Select(o => o.name).ToArray(),
                () => PowerGetEffectiveOverlayScheme(out var g) == 0 ? Overlays.FirstOrDefault(o => o.id == g).name : null,
                v => PowerSetActiveOverlayScheme(Overlays.First(o => o.name == v).id))
            { Note = "The Settings > Power slider. Windows only applies it while the Balanced plan is active." };
        yield return new ModeGroup("CPU boost", BoostNames,
            () => { var g = Active(); return PowerReadACValueIndex(IntPtr.Zero, ref g, ref SubProcessor, ref PerfBoostMode, out var v) == 0 ? BoostNames[Math.Min(2, (int)v)] : null; },
            v =>
            {
                var g = Active();
                uint b = (uint)Array.IndexOf(BoostNames, v);
                PowerWriteACValueIndex(IntPtr.Zero, ref g, ref SubProcessor, ref PerfBoostMode, b);
                PowerWriteDCValueIndex(IntPtr.Zero, ref g, ref SubProcessor, ref PerfBoostMode, b);
                PowerSetActiveScheme(IntPtr.Zero, ref g); // re-activating applies it
            })
        { Note = "Turbo/boost clocks for the active plan. Off is the strongest way to cut CPU heat (often 25-30% less power at full load)." };
    }

    static Guid Active()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var p) != 0) return Guid.Empty;
        var g = Marshal.PtrToStructure<Guid>(p);
        Marshal.FreeHGlobal(p); // LocalFree
        return g;
    }

    static List<(string name, Guid id)> Plans()
    {
        var list = new List<(string, Guid)>();
        for (uint i = 0; ; i++)
        {
            uint size = 16;
            var buf = new byte[16];
            if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16 /* ACCESS_SCHEME */, i, buf, ref size) != 0) break;
            var g = new Guid(buf);
            uint n = 0;
            PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, null, ref n);
            var name = new byte[n];
            PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, name, ref n);
            list.Add((System.Text.Encoding.Unicode.GetString(name).TrimEnd('\0'), g));
        }
        return list;
    }
}

static class LinuxModes
{
    const string Cpu = "/sys/devices/system/cpu";

    public static IEnumerable<ModeGroup> All()
    {
        if (PerfModes.Run("powerprofilesctl", "get").code == 0)
        {
            var names = PerfModes.Run("powerprofilesctl", "list").output.Split('\n')
                .Select(l => l.Trim().TrimStart('*').Trim().TrimEnd(':')).Where(l => l is "power-saver" or "balanced" or "performance").ToArray();
            yield return new ModeGroup("Power profile", names.Length > 0 ? names : ["power-saver", "balanced", "performance"],
                () => PerfModes.Run("powerprofilesctl", "get").output.Trim(),
                v => { if (PerfModes.Run("powerprofilesctl", "set " + v).code != 0) throw new IOException("powerprofilesctl refused"); })
            { Note = "power-profiles-daemon (GNOME/KDE power menu). It also sets the platform profile below." };
        }
        if (File.Exists("/sys/firmware/acpi/platform_profile_choices"))
            yield return SysfsGroup("Platform profile", "/sys/firmware/acpi/platform_profile_choices", ["/sys/firmware/acpi/platform_profile"],
                "The firmware's thermal profile (ASUS, Lenovo, Dell, HP and others). Changing it needs root.");
        var cpus = Directory.Exists(Cpu) ? Directory.GetDirectories(Cpu, "cpu*").Where(d => File.Exists(d + "/cpufreq/scaling_governor")).ToArray() : [];
        if (cpus.Length > 0)
        {
            yield return SysfsGroup("CPU governor", cpus[0] + "/cpufreq/scaling_available_governors", cpus.Select(c => c + "/cpufreq/scaling_governor").ToArray(), null);
            if (File.Exists(cpus[0] + "/cpufreq/energy_performance_available_preferences"))
                yield return SysfsGroup("Energy preference (EPP)", cpus[0] + "/cpufreq/energy_performance_available_preferences",
                    cpus.Select(c => c + "/cpufreq/energy_performance_preference").ToArray(), "Ignored while the governor is \"performance\".");
        }
        string boost = Cpu + "/cpufreq/boost", noTurbo = Cpu + "/intel_pstate/no_turbo";
        if (File.Exists(boost))
            yield return new ModeGroup("CPU boost", ["Off", "On"], () => File.ReadAllText(boost).Trim() == "1" ? "On" : "Off", v => File.WriteAllText(boost, v == "On" ? "1" : "0"));
        else if (File.Exists(noTurbo))
            yield return new ModeGroup("CPU boost", ["Off", "On"], () => File.ReadAllText(noTurbo).Trim() == "0" ? "On" : "Off", v => File.WriteAllText(noTurbo, v == "On" ? "0" : "1"));
    }

    static ModeGroup SysfsGroup(string title, string choices, string[] targets, string? note) =>
        new(title, File.ReadAllText(choices).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            () => File.ReadAllText(targets[0]).Trim(),
            v => { foreach (var t in targets) File.WriteAllText(t, v); }) { Note = note };
}

static class MacModes
{
    public static IEnumerable<ModeGroup> All()
    {
        var g = PerfModes.Run("/usr/bin/pmset", "-g").output;
        // Newer Macs: powermode 0 automatic, 1 low power, 2 high power. Older: lowpowermode 0/1.
        string key = g.Contains(" powermode") ? "powermode" : g.Contains("lowpowermode") ? "lowpowermode" : "";
        if (key == "") yield break;
        string[] names = key == "powermode" ? ["Automatic", "Low Power", "High Power"] : ["Automatic", "Low Power"];
        yield return new ModeGroup("Energy mode", names, () =>
        {
            var line = PerfModes.Run("/usr/bin/pmset", "-g").output.Split('\n').FirstOrDefault(l => l.Trim().StartsWith(key + " "));
            return int.TryParse(line?.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Last(), out int v) && v < names.Length ? names[v] : null;
        }, v =>
        {
            if (PerfModes.Run("/usr/bin/pmset", $"-a {key} {Array.IndexOf(names, v)}").code != 0) throw new IOException("pmset needs FanKick to run as admin");
        }) { Note = "Low Power and High Power Mode from System Settings > Battery. High Power exists on some MacBook Pro and Mac models only." };
    }
}
