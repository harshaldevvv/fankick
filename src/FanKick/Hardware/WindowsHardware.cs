using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FanKick.Core;
using LibreHardwareMonitor.Hardware;
using LhmHardware = LibreHardwareMonitor.Hardware.IHardware;

namespace FanKick.Hardware;

// Everything LibreHardwareMonitor can see: CPU, NVIDIA/AMD/Intel GPUs, motherboard Super I/O chips (fans and fan
// headers), AIO coolers and fan hubs over USB, drives, RAM, batteries, PSUs. CPU and motherboard access go through the
// PawnIO driver, which must be installed separately; GPUs work without it.
[SupportedOSPlatform("windows")]
public sealed class WindowsHardware : Core.IHardware
{
    readonly Computer pc;
    readonly List<Sensor> sensors = [];
    readonly List<(Sensor ours, ISensor theirs, LhmHardware top)> map = [];
    readonly List<FanControl> fans = [];
    readonly Dictionary<FanControl, IControl> controls = [];
    readonly string? nvidiaPnp;

    public string Name => "LibreHardwareMonitor";
    public string? Notice { get; }
    public IReadOnlyList<Sensor> Sensors => sensors;
    public IReadOnlyList<FanControl> Fans => fans;

    public WindowsHardware()
    {
        pc = new Computer
        {
            IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true, IsControllerEnabled = true,
            IsStorageEnabled = true, IsMemoryEnabled = true, IsBatteryEnabled = true, IsPsuEnabled = true,
        };
        pc.Open();
        nvidiaPnp = OnlyNvidiaPnpId();
        foreach (var hw in pc.Hardware) Walk(hw, hw);

        bool admin = Environment.IsPrivilegedProcess;
        bool cpuTemp = sensors.Any(s => s.Critical && !float.IsNaN(s.Value) && s.Group == pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu)?.Name);
        if (!admin) Notice = "Not running as administrator: CPU, motherboard and fan control are unavailable. Use \"Restart as administrator\".";
        else if (!PawnIoInstalled()) Notice = "The PawnIO driver is not installed, so CPU temperatures, motherboard sensors and fan headers can't be read. Get it from pawnio.eu (or: winget install namazso.PawnIO), then restart FanKick.";
        else if (!cpuTemp) Notice = "No CPU temperature found on this machine. Curves on \"Hottest CPU/GPU\" will use the GPU only.";
    }

    void Walk(LhmHardware hw, LhmHardware top)
    {
        try { hw.Update(); } catch { }
        foreach (var s in hw.Sensors)
        {
            var kind = s.SensorType switch
            {
                SensorType.Temperature => SensorKind.Temperature, SensorType.Fan => SensorKind.Fan, SensorType.Control => SensorKind.Control,
                SensorType.Load or SensorType.Level => SensorKind.Load, SensorType.Power => SensorKind.Power, SensorType.Clock => SensorKind.Clock,
                SensorType.Voltage => SensorKind.Voltage, SensorType.Data or SensorType.SmallData => SensorKind.Data, _ => SensorKind.Other,
            };
            bool critical = kind == SensorKind.Temperature && (
                hw.HardwareType == HardwareType.Cpu && s.Name is "CPU Package" or "Core (Tctl/Tdie)" or "Tctl" or "Tdie" or "Core (Tctl)" ||
                hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel && s.Name is "GPU Core");
            var ours = new Sensor(s.Identifier.ToString(), s.Name, hw.Name, kind) { Critical = critical, Cpu = critical && hw.HardwareType == HardwareType.Cpu };
            sensors.Add(ours);
            map.Add((ours, s, top));
            ours.Value = Copy(s);
            if (s.SensorType == SensorType.Control && s.Control is { } c)
            {
                var rpm = hw.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Fan && x.Index == s.Index)
                          ?? hw.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Fan && x.Name == s.Name);
                var fan = new FanControl(ours.Id, s.Name.Replace("Fan Control", "Fan"), hw.Name) { RpmSensorId = rpm?.Identifier.ToString() };
                fans.Add(fan);
                controls[fan] = c;
            }
        }
        foreach (var sub in hw.SubHardware) Walk(sub, top);
    }

    public void Update()
    {
        bool gpuAsleep = nvidiaPnp != null && !DeviceAwake(nvidiaPnp);
        foreach (var hw in pc.Hardware)
        {
            // Asking a sleeping laptop GPU for its temperature wakes it, and a sleeping GPU makes no heat.
            if (gpuAsleep && hw.HardwareType == HardwareType.GpuNvidia) continue;
            UpdateTree(hw);
        }
        foreach (var (ours, theirs, top) in map)
            ours.Value = gpuAsleep && top.HardwareType == HardwareType.GpuNvidia ? float.NaN : Copy(theirs);
    }

    // SmallData is in MB; we show GB.
    static float Copy(ISensor s) => s.Value is { } v ? s.SensorType == SensorType.SmallData ? v / 1024 : v : float.NaN;

    static void UpdateTree(LhmHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware) UpdateTree(sub);
    }

    public void Set(FanControl fan, int percent) => controls[fan].SetSoftware(percent);
    public void Restore(FanControl fan) => controls[fan].SetDefault();
    public void Dispose() => pc.Close();

    static bool PawnIoInstalled()
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
        return k != null;
    }

    // The NVIDIA GPU's device id when it is the only one (a laptop dGPU that may sleep); null otherwise.
    static string? OnlyNvidiaPnpId()
    {
        try
        {
            using var q = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_VideoController WHERE Name LIKE '%NVIDIA%'");
            var ids = q.Get().Cast<ManagementObject>().Select(o => (string)o["PNPDeviceID"]).ToList();
            return ids.Count == 1 ? ids[0] : null;
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid fmtid; public uint pid; }
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out uint devInst, string id, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[] buf, ref uint size, uint flags);

    // DEVPKEY_Device_PowerData: PD_MostRecentPowerState 1 = D0 (awake). Unknown counts as awake.
    static bool DeviceAwake(string pnp)
    {
        uint size = 64;
        if (CM_Locate_DevNodeW(out uint inst, pnp, 0) != 0) return true;
        var key = new DEVPROPKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 32 };
        var buf = new byte[64];
        if (CM_Get_DevNode_PropertyW(inst, ref key, out _, buf, ref size, 0) != 0) return true;
        return BitConverter.ToInt32(buf, 4) == 1;
    }
}
