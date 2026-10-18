using FanKick.Core;

namespace FanKick.Hardware;

// Linux: every hwmon device the kernel exposes (k10temp, coretemp, nct67xx, it87, amdgpu, nvme, asus, thinkpad,
// dell-smm...), plus RAPL CPU power, CPU load and clocks from /proc and cpufreq, amdgpu RDNA3+ fan curves, and NVIDIA
// GPUs through NVML. Reading needs no privileges; changing fans needs root.
public sealed class LinuxHardware : IHardware
{
    const string Hwmon = "/sys/class/hwmon";
    readonly List<Sensor> sensors = [];
    readonly List<(Sensor s, string path, float scale)> files = [];
    readonly List<FanControl> fans = [];
    readonly Dictionary<FanControl, FanPath> paths = [];
    readonly Nvml? nvml;
    readonly Sensor? cpuLoad, cpuClock, raplW;
    readonly string? raplPath;
    readonly string[] freqFiles;
    long lastIdle, lastTotal, lastEnergy;
    DateTime lastRapl;

    abstract record FanPath;
    sealed record PwmPath(string Pwm, string Enable, string? Original) : FanPath;
    sealed record OdCurvePath(string File, int MinT, int MaxT, int MinPct) : FanPath;

    public string Name => "Linux hwmon";
    public string? Notice { get; }
    public IReadOnlyList<Sensor> Sensors => sensors;
    public IReadOnlyList<FanControl> Fans => fans;

    public LinuxHardware()
    {
        bool root = Environment.UserName == "root";
        foreach (var dir in Directory.Exists(Hwmon) ? Directory.GetDirectories(Hwmon).Order().ToArray() : [])
            try { AddHwmon(dir, root); } catch (Exception ex) { Log.Write($"{dir}: {ex.Message}"); }

        const string cpu = "CPU";
        cpuLoad = Add(new Sensor("linux/cpu/load", "CPU Total", cpu, SensorKind.Load));
        freqFiles = Directory.Exists("/sys/devices/system/cpu") ? Directory.GetFiles("/sys/devices/system/cpu", "scaling_cur_freq", SearchOption.AllDirectories) : [];
        if (freqFiles.Length > 0) cpuClock = Add(new Sensor("linux/cpu/clock", "Core average", cpu, SensorKind.Clock));
        raplPath = "/sys/class/powercap/intel-rapl:0/energy_uj"; // AMD Zen also reports here (intel-rapl driver name)
        if (File.Exists(raplPath) && TryRead(raplPath, out _)) raplW = Add(new Sensor("linux/cpu/power", "CPU Package", cpu, SensorKind.Power));
        else raplPath = null;

        nvml = Nvml.TryLoad();
        if (nvml != null) { sensors.AddRange(nvml.Sensors); fans.AddRange(nvml.Fans); }

        if (!root && fans.Count > 0) Notice = "Not running as root: readings work, fan control and some modes don't. Start FanKick with sudo, or use \"Restart as administrator\".";
        else if (fans.Count == 0) Notice = "No controllable fans found. On laptops the firmware often keeps them; on desktops load your Super I/O driver (e.g. nct6775 or it87) with modprobe.";
        Update();
    }

    Sensor Add(Sensor s) { sensors.Add(s); return s; }

    void AddHwmon(string dir, bool root)
    {
        string chip = ReadText(dir + "/name") ?? Path.GetFileName(dir);
        string id = "hwmon/" + chip + "/" + (Path.GetFileName(ResolveDevice(dir)) ?? Path.GetFileName(dir));
        string group = chip switch { "k10temp" or "coretemp" or "zenpower" => "CPU", _ => chip };
        foreach (var f in Directory.GetFiles(dir).Select(Path.GetFileName).Order())
        {
            var (kind, scale) = f switch
            {
                _ when f!.StartsWith("temp") && f.EndsWith("_input") => (SensorKind.Temperature, 1000f),
                _ when f.StartsWith("fan") && f.EndsWith("_input") => (SensorKind.Fan, 1f),
                _ when f.StartsWith("in") && f.EndsWith("_input") => (SensorKind.Voltage, 1000f),
                _ when f.StartsWith("power") && (f.EndsWith("_average") || f.EndsWith("_input")) => (SensorKind.Power, 1e6f),
                _ when f.StartsWith("freq") && f.EndsWith("_input") => (SensorKind.Clock, 1e6f),
                _ => (SensorKind.Other, 0f),
            };
            if (kind == SensorKind.Other) continue;
            string stem = f[..f.IndexOf('_')];
            string label = ReadText($"{dir}/{stem}_label") ?? stem;
            bool critical = kind == SensorKind.Temperature && (chip is "k10temp" or "zenpower" && label is "Tctl" or "Tdie" || chip == "coretemp" && label.StartsWith("Package")
                            || chip is "amdgpu" or "i915" or "xe" && label is "edge" or "junction" or "temp1" && !label.StartsWith("mem"));
            var s = Add(new Sensor($"{id}/{stem}", label, group, kind) { Critical = critical });
            files.Add((s, $"{dir}/{f}", scale));
        }
        // pwmN with pwmN_enable = a fan header we can drive (1 = manual, 2+ = automatic modes).
        foreach (var pwm in Directory.GetFiles(dir, "pwm*").Where(p => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p), @"^pwm\d+$")))
        {
            string n = Path.GetFileName(pwm)[3..], enable = pwm + "_enable";
            if (!File.Exists(enable)) continue;
            var duty = Add(new Sensor($"{id}/pwm{n}", $"Fan {n} duty", group, SensorKind.Control));
            files.Add((duty, pwm, 2.55f));
            var fan = new FanControl($"{id}/pwm{n}", (ReadText($"{dir}/fan{n}_label") ?? $"Fan {n}"), group)
            { RpmSensorId = File.Exists($"{dir}/fan{n}_input") ? $"{id}/fan{n}" : null };
            fans.Add(fan);
            paths[fan] = new PwmPath(pwm, enable, ReadText(enable));
        }
        // amdgpu RDNA3+: no manual PWM, but a 5-point fan curve under gpu_od (Linux 6.7+). Driven with a flat curve.
        string od = ResolveDevice(dir) + "/gpu_od/fan_ctrl/fan_curve";
        if (chip == "amdgpu" && !paths.Values.OfType<PwmPath>().Any(p => p.Pwm.StartsWith(dir)) && File.Exists(od))
        {
            var text = ReadText(od) ?? "";
            var t = System.Text.RegularExpressions.Regex.Match(text, @"FAN_CURVE\(hotspot temp\):\s*(\d+)C\s*(\d+)C");
            var p = System.Text.RegularExpressions.Regex.Match(text, @"FAN_CURVE\(fan speed\):\s*(\d+)%\s*(\d+)%");
            if (t.Success && p.Success)
            {
                var fan = new FanControl($"{id}/od", "GPU fan", group) { RpmSensorId = File.Exists(dir + "/fan1_input") ? $"{id}/fan1" : null };
                fans.Add(fan);
                paths[fan] = new OdCurvePath(od, int.Parse(t.Groups[1].Value), int.Parse(t.Groups[2].Value), int.Parse(p.Groups[1].Value));
            }
        }
    }

    static string ResolveDevice(string dir)
    {
        try { return new DirectoryInfo(dir + "/device").ResolveLinkTarget(true)?.FullName ?? dir + "/device"; }
        catch { return dir + "/device"; }
    }

    static string? ReadText(string path) { try { return File.ReadAllText(path).Trim(); } catch { return null; } }
    static bool TryRead(string path, out long v) { v = 0; return ReadText(path) is { } t && long.TryParse(t, out v); }

    public void Update()
    {
        foreach (var (s, path, scale) in files)
            s.Value = TryRead(path, out long v) ? v / scale : float.NaN;
        ReadCpu();
        nvml?.Update();
    }

    void ReadCpu()
    {
        // /proc/stat first line: user nice system idle iowait irq softirq steal ...
        var parts = (ReadText("/proc/stat") ?? "").Split('\n')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
        if (parts.Length >= 5 && cpuLoad != null)
        {
            long idle = parts[3] + parts[4], total = parts.Sum();
            if (lastTotal > 0 && total > lastTotal) cpuLoad.Value = 100f * (1 - (idle - lastIdle) / (float)(total - lastTotal));
            (lastIdle, lastTotal) = (idle, total);
        }
        if (cpuClock != null)
        {
            var v = freqFiles.Select(f => TryRead(f, out long k) ? k / 1000f : float.NaN).Where(x => !float.IsNaN(x)).ToArray();
            cpuClock.Value = v.Length == 0 ? float.NaN : v.Average();
        }
        if (raplPath != null && raplW != null && TryRead(raplPath, out long e))
        {
            var now = DateTime.UtcNow;
            if (lastEnergy > 0 && e >= lastEnergy) raplW.Value = (e - lastEnergy) / 1e6f / (float)(now - lastRapl).TotalSeconds;
            (lastEnergy, lastRapl) = (e, now);
        }
    }

    public void Set(FanControl fan, int percent)
    {
        if (nvml != null && nvml.Owns(fan)) { nvml.Set(fan, percent); return; }
        switch (paths[fan])
        {
            case PwmPath p:
                if (ReadText(p.Enable) != "1") File.WriteAllText(p.Enable, "1");
                File.WriteAllText(p.Pwm, ((int)Math.Round(Math.Clamp(percent, 0, 100) * 2.55)).ToString());
                break;
            case OdCurvePath o:
                int pct = Math.Max(o.MinPct, Math.Clamp(percent, 0, 100));
                for (int i = 0; i < 5; i++) File.WriteAllText(o.File, $"{i} {o.MinT + i * (o.MaxT - o.MinT) / 4} {pct}");
                File.WriteAllText(o.File, "c");
                break;
        }
    }

    public void Restore(FanControl fan)
    {
        if (nvml != null && nvml.Owns(fan)) { nvml.Restore(fan); return; }
        switch (paths[fan])
        {
            // Back to what it was when we started (usually 2 = automatic); "1" (manual) would leave it stuck, so then 2.
            case PwmPath p: File.WriteAllText(p.Enable, p.Original is null or "1" ? "2" : p.Original); break;
            case OdCurvePath o: File.WriteAllText(o.File, "r"); break;
        }
    }

    public void Dispose() => nvml?.Dispose();
}
