using FanKick.Core;
using FanKick.Modes;

namespace FanKick.Hardware;

// A pretend desktop (CPU, GPU, board fans, an AIO pump) with simple thermal physics: heat in from load, heat out with
// fan speed. `--demo` runs on it, so the UI, the screenshots and the tests work without touching real fans.
public sealed class DemoHardware : IHardware
{
    readonly Random rng = new(7);
    readonly Sensor cpuT, cpuLoad, cpuW, cpuClk, gpuT, gpuLoad, gpuW, boardT, liquidT, nvmeT;
    readonly List<(FanControl fan, Sensor rpm, Sensor duty, int maxRpm)> fans = [];
    readonly List<Sensor> sensors = [];
    double t;
    float cpuHeat = 40, gpuHeat = 38;

    public string Name => "Demo PC";
    public string? Notice => "Demo mode: simulated hardware, no real fans are touched.";
    public IReadOnlyList<Sensor> Sensors => sensors;
    public IReadOnlyList<FanControl> Fans => fans.Select(f => f.fan).ToList();
    // Set by the stress test so the demo heats up like a real machine would.
    public static volatile bool CpuStress, GpuStress;

    public DemoHardware()
    {
        const string cpu = "Ryzen 7 7800X3D", gpu = "Radeon RX 7800 XT", board = "Nuvoton NCT6799D", aio = "Liquid cooler", disk = "NVMe SSD";
        cpuT = Add(new Sensor("demo/cpu/temp", "CPU Package", cpu, SensorKind.Temperature) { Critical = true });
        cpuLoad = Add(new Sensor("demo/cpu/load", "CPU Total", cpu, SensorKind.Load));
        cpuW = Add(new Sensor("demo/cpu/power", "CPU Package", cpu, SensorKind.Power));
        cpuClk = Add(new Sensor("demo/cpu/clock", "Core average", cpu, SensorKind.Clock));
        gpuT = Add(new Sensor("demo/gpu/temp", "GPU Core", gpu, SensorKind.Temperature) { Critical = true });
        gpuLoad = Add(new Sensor("demo/gpu/load", "GPU Core", gpu, SensorKind.Load));
        gpuW = Add(new Sensor("demo/gpu/power", "GPU Package", gpu, SensorKind.Power));
        boardT = Add(new Sensor("demo/board/temp", "System", board, SensorKind.Temperature));
        liquidT = Add(new Sensor("demo/aio/temp", "Liquid", aio, SensorKind.Temperature));
        nvmeT = Add(new Sensor("demo/nvme/temp", "Composite", disk, SensorKind.Temperature));
        AddFan("demo/board/fan1", "CPU fan", board, 2200);
        AddFan("demo/board/fan2", "Front fan 1", board, 1600);
        AddFan("demo/board/fan3", "Front fan 2", board, 1600);
        AddFan("demo/board/fan4", "Rear fan", board, 1500);
        AddFan("demo/gpu/fan", "GPU fan", gpu, 3200);
        AddFan("demo/aio/pump", "Pump", aio, 2800);
    }

    Sensor Add(Sensor s) { sensors.Add(s); return s; }

    void AddFan(string id, string name, string group, int maxRpm)
    {
        var rpm = Add(new Sensor(id + "/rpm", name, group, SensorKind.Fan));
        var duty = Add(new Sensor(id + "/duty", name + " duty", group, SensorKind.Control));
        fans.Add((new FanControl(id, name, group) { RpmSensorId = rpm.Id }, rpm, duty, maxRpm));
    }

    float Duty(int i) => fans[i].fan.Percent < 0 ? 45 : fans[i].fan.Percent; // the "BIOS" holds 45%

    public void Update()
    {
        t += 1;
        float cl = CpuStress ? 100 : Math.Clamp(18 + 14 * (float)Math.Sin(t / 23) + rng.Next(-6, 7), 2, 100);
        float gl = GpuStress ? 99 : Math.Clamp(8 + 10 * (float)Math.Sin(t / 41) + rng.Next(-4, 5), 0, 100);
        float cpuFan = (Duty(0) + Duty(5)) / 200, caseFan = (Duty(1) + Duty(2) + Duty(3)) / 300, gpuFan = Duty(4) / 100;
        float w = 22 + cl * 0.98f, gw = 18 + gl * 2.4f;
        float room = 24 + (1 - caseFan) * 6;
        cpuHeat += 0.12f * (room + w * (0.62f - 0.34f * cpuFan) - cpuHeat);
        gpuHeat += 0.10f * (room + gw * (0.30f - 0.16f * gpuFan) - gpuHeat);
        cpuT.Value = cpuHeat + rng.Next(-10, 11) / 10f;
        gpuT.Value = gpuHeat + rng.Next(-5, 6) / 10f;
        cpuLoad.Value = cl; cpuW.Value = w; cpuClk.Value = 4200 + cl * 8 + rng.Next(-40, 40);
        gpuLoad.Value = gl; gpuW.Value = gw;
        boardT.Value = room + 8 + cl * 0.05f;
        liquidT.Value = room + 4 + w * 0.06f * (1.2f - cpuFan);
        nvmeT.Value = room + 14;
        for (int i = 0; i < fans.Count; i++)
        {
            var (_, rpm, duty, max) = fans[i];
            duty.Value = Duty(i);
            rpm.Value = Duty(i) < 15 ? 0 : max * Duty(i) / 100 + rng.Next(-15, 16);
        }
    }

    // Pretend OS modes, held in memory.
    public static IEnumerable<ModeGroup> Modes()
    {
        ModeGroup Make(string title, string[] options, string current, string? note = null)
        {
            string v = current;
            return new ModeGroup(title, options, () => v, x => v = x) { Note = note };
        }
        yield return Make("Power profile", ["Power saver", "Balanced", "Performance"], "Balanced", "The OS power profile.");
        yield return Make("Platform profile", ["quiet", "balanced", "performance"], "balanced", "The firmware's thermal profile.");
        yield return Make("CPU boost", ["Off", "On"], "On", "Turbo clocks. Off is the strongest way to cut CPU heat.");
    }

    public void Set(FanControl fan, int percent) { }
    public void Restore(FanControl fan) { }
    public void Dispose() { }
}
