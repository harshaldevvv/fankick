using System.Runtime.InteropServices;
using FanKick.Core;

namespace FanKick.Hardware;

// NVIDIA GPUs on Linux through NVML (libnvidia-ml, shipped with the driver): temperature, load, power, clock, memory
// and fans. Works headless and on Wayland; setting fan speed needs root.
sealed class Nvml : IDisposable
{
    const string Lib = "nvidia-ml";
    [DllImport(Lib)] static extern int nvmlInit_v2();
    [DllImport(Lib)] static extern int nvmlShutdown();
    [DllImport(Lib)] static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport(Lib)] static extern int nvmlDeviceGetHandleByIndex_v2(uint i, out IntPtr dev);
    [DllImport(Lib)] static extern int nvmlDeviceGetName(IntPtr dev, byte[] name, uint len);
    [DllImport(Lib)] static extern int nvmlDeviceGetTemperature(IntPtr dev, int sensor, out uint c);
    [DllImport(Lib)] static extern int nvmlDeviceGetUtilizationRates(IntPtr dev, out Util u);
    [DllImport(Lib)] static extern int nvmlDeviceGetPowerUsage(IntPtr dev, out uint mw);
    [DllImport(Lib)] static extern int nvmlDeviceGetClockInfo(IntPtr dev, int type, out uint mhz);
    [DllImport(Lib)] static extern int nvmlDeviceGetMemoryInfo(IntPtr dev, out Mem m);
    [DllImport(Lib)] static extern int nvmlDeviceGetNumFans(IntPtr dev, out uint n);
    [DllImport(Lib)] static extern int nvmlDeviceGetFanSpeed_v2(IntPtr dev, uint fan, out uint pct);
    [DllImport(Lib)] static extern int nvmlDeviceSetFanSpeed_v2(IntPtr dev, uint fan, uint pct);
    [DllImport(Lib)] static extern int nvmlDeviceSetDefaultFanSpeed_v2(IntPtr dev, uint fan);
    [StructLayout(LayoutKind.Sequential)] struct Util { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] struct Mem { public ulong Total, Free, Used; }

    sealed record Gpu(IntPtr Dev, Sensor Temp, Sensor Load, Sensor Power, Sensor Clock, Sensor Mem, Sensor[] FanDuty);

    readonly List<Gpu> gpus = [];
    readonly Dictionary<FanControl, (IntPtr dev, uint idx)> fanMap = [];
    public List<Sensor> Sensors { get; } = [];
    public List<FanControl> Fans { get; } = [];

    public static Nvml? TryLoad()
    {
        try { return nvmlInit_v2() == 0 ? new Nvml() : null; }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    Nvml()
    {
        nvmlDeviceGetCount_v2(out uint n);
        for (uint i = 0; i < n; i++)
        {
            if (nvmlDeviceGetHandleByIndex_v2(i, out var dev) != 0) continue;
            var buf = new byte[96];
            nvmlDeviceGetName(dev, buf, (uint)buf.Length);
            string name = System.Text.Encoding.ASCII.GetString(buf).TrimEnd('\0').Replace("NVIDIA ", "");
            string id = $"nvml/{i}";
            Sensor S(string k, string label, SensorKind kind, bool crit = false) { var s = new Sensor($"{id}/{k}", label, name, kind) { Critical = crit }; Sensors.Add(s); return s; }
            var temp = S("temp", "GPU Core", SensorKind.Temperature, true);
            var load = S("load", "GPU Core", SensorKind.Load);
            var power = S("power", "GPU Package", SensorKind.Power);
            var clock = S("clock", "GPU Core", SensorKind.Clock);
            var mem = S("mem", "GPU Memory Used", SensorKind.Data);
            nvmlDeviceGetNumFans(dev, out uint nf);
            var duty = new Sensor[nf];
            for (uint f = 0; f < nf; f++)
            {
                duty[f] = S($"fan{f}", nf == 1 ? "GPU fan" : $"GPU fan {f + 1}", SensorKind.Control);
                var fan = new FanControl($"{id}/fan{f}", duty[f].Name, name);
                Fans.Add(fan);
                fanMap[fan] = (dev, f);
            }
            gpus.Add(new Gpu(dev, temp, load, power, clock, mem, duty));
        }
    }

    // ponytail: skips reads while the driver has runtime-suspended a laptop dGPU; NVML being loaded may still keep some
    // GPUs from suspending. A per-device lazy init is the upgrade if that shows up.
    static bool AllSuspended() =>
        Directory.Exists("/sys/bus/pci/drivers/nvidia") &&
        Directory.GetDirectories("/sys/bus/pci/drivers/nvidia").Select(d => d + "/power/runtime_status").Where(File.Exists)
            .Select(f => File.ReadAllText(f).Trim()).DefaultIfEmpty("active").All(s => s == "suspended");

    public void Update()
    {
        bool asleep = AllSuspended();
        foreach (var g in gpus)
        {
            float V(int rc, float v) => !asleep && rc == 0 ? v : float.NaN;
            g.Temp.Value = V(nvmlDeviceGetTemperature(g.Dev, 0, out uint t), t);
            g.Load.Value = V(nvmlDeviceGetUtilizationRates(g.Dev, out var u), u.Gpu);
            g.Power.Value = V(nvmlDeviceGetPowerUsage(g.Dev, out uint mw), mw / 1000f);
            g.Clock.Value = V(nvmlDeviceGetClockInfo(g.Dev, 0, out uint mhz), mhz);
            g.Mem.Value = V(nvmlDeviceGetMemoryInfo(g.Dev, out var m), m.Used / 1073741824f);
            for (uint f = 0; f < g.FanDuty.Length; f++) g.FanDuty[f].Value = V(nvmlDeviceGetFanSpeed_v2(g.Dev, f, out uint p), p);
        }
    }

    public bool Owns(FanControl fan) => fanMap.ContainsKey(fan);

    public void Set(FanControl fan, int pct)
    {
        var (dev, f) = fanMap[fan];
        int rc = nvmlDeviceSetFanSpeed_v2(dev, f, (uint)Math.Clamp(pct, 0, 100));
        if (rc != 0) throw new IOException($"NVML set fan failed ({rc}{(rc == 4 ? ": needs root" : "")})");
    }

    public void Restore(FanControl fan)
    {
        var (dev, f) = fanMap[fan];
        nvmlDeviceSetDefaultFanSpeed_v2(dev, f);
    }

    public void Dispose() { try { nvmlShutdown(); } catch { } }
}
