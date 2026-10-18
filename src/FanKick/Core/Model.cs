namespace FanKick.Core;

public enum SensorKind { Temperature, Fan, Control, Load, Power, Clock, Voltage, Data, Other }

// One reading. Backends own the list and refresh Value in Update(); NaN = no reading right now.
public sealed class Sensor(string id, string name, string group, SensorKind kind)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Group { get; } = group;   // the hardware it belongs to ("Ryzen 5 5600H", "nct6798", "RTX 3050 Ti")
    public SensorKind Kind { get; } = kind;
    public float Value { get; set; } = float.NaN;
    // CPU package / GPU core temps: the panic temperature and the "Hottest CPU/GPU" source watch these.
    public bool Critical { get; init; }

    public string Unit => Kind switch
    {
        SensorKind.Temperature => "°C", SensorKind.Fan => "rpm", SensorKind.Control or SensorKind.Load => "%",
        SensorKind.Power => "W", SensorKind.Clock => "MHz", SensorKind.Voltage => "V", SensorKind.Data => "GB", _ => "",
    };

    public string Format() => float.IsNaN(Value) ? "-" : Kind switch
    {
        SensorKind.Voltage => Value.ToString("F3") + " V",
        SensorKind.Power or SensorKind.Data => Value.ToString("F1") + " " + Unit,
        _ => Value.ToString("F0") + " " + Unit,
    };
}

// One fan (or pump) we can drive. Percent is what we last asked for (-1 = firmware/BIOS in charge).
public sealed class FanControl(string id, string name, string group)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Group { get; } = group;
    public string? RpmSensorId { get; init; }
    public int Percent { get; set; } = -1;
}

public interface IHardware : IDisposable
{
    string Name { get; }
    // Shown as a banner: what is missing and how to fix it ("Install PawnIO for CPU and motherboard sensors").
    string? Notice { get; }
    IReadOnlyList<Sensor> Sensors { get; }
    IReadOnlyList<FanControl> Fans { get; }
    void Update();
    void Set(FanControl fan, int percent);
    // Hand the fan back to the BIOS / firmware / driver default.
    void Restore(FanControl fan);
}
