using System.Text.Json;
using System.Text.Json.Serialization;

namespace FanKick.Core;

public enum FanMode { Auto, Fixed, Curve }
public enum FailSafe { FullSpeed, Bios }

public sealed class FanSettings
{
    public FanMode Mode { get; set; } = FanMode.Auto; // Auto = the BIOS / firmware drives it
    public int Fixed { get; set; } = 50;
    public string? Curve { get; set; }
    public int Min { get; set; }
    public int Max { get; set; } = 100;
    // How fast the speed may move, in % per second, so a short spike doesn't make the fans roar.
    public float StepUp { get; set; } = 10;
    public float StepDown { get; set; } = 3;
    // Learned by the fan watchdog: the rpm this fan reaches at each 10% step (key 0..10). A fan far below it is stuck.
    public Dictionary<int, int> Rpm { get; set; } = [];
}

public enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

public sealed class OverlaySettings
{
    public bool Enabled { get; set; }
    public Corner Corner { get; set; } = Corner.TopLeft;
    public int OffsetX { get; set; } = 12;
    public int OffsetY { get; set; } = 12;
    public float Opacity { get; set; } = 0.85f;
    public List<string> Sensors { get; set; } = [Engine.Hottest]; // empty = the default set
    public string Hotkey { get; set; } = "Ctrl+Shift+F10";
}

public sealed class Settings
{
    public List<Curve> Curves { get; set; } = Presets.Names.Select(Presets.Build).ToList();
    public Dictionary<string, FanSettings> Fans { get; set; } = [];
    // Any CPU/GPU core at or over this: every fan to 100% until 10 °C under it, whatever the curves say.
    public int PanicTemp { get; set; } = 95;
    // CPU at or over this for 6 s: every fan to 100% and CPU boost off, until it has been 10 °C under for a minute.
    // 0 = off.
    public int CpuLimit { get; set; } = 90;
    public FailSafe OnSensorLost { get; set; } = FailSafe.FullSpeed;
    public FailSafe OnCrash { get; set; } = FailSafe.FullSpeed;
    public FailSafe OnExit { get; set; } = FailSafe.Bios;
    public int IntervalMs { get; set; } = 1000;
    public OverlaySettings Overlay { get; set; } = new();
    // Performance modes picked in FanKick (group title -> option), set again at every start.
    public Dictionary<string, string> Modes { get; set; } = [];
    // ASUS laptops whose BIOS flips the custom fan curve at every write (see AsusAcpi).
    public bool AsusWriteTwice { get; set; }

    public FanSettings Fan(string id) => Fans.TryGetValue(id, out var f) ? f : Fans[id] = new FanSettings();
    public Curve? Find(string? name) => Curves.FirstOrDefault(c => c.Name == name);

    // %APPDATA%\FanKick, ~/.config/FanKick, or ~/Library/Application Support/FanKick on a Mac.
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(
        OperatingSystem.IsMacOS() ? Environment.SpecialFolder.LocalApplicationData : Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "FanKick");
    public static string DefaultPath => Path.Combine(Dir, "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Settings Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new() : new(); }
        catch (Exception ex) { Log.Write("bad settings file, using defaults: " + ex.Message); return new(); }
    }

    // For reloading a file changed outside FanKick: null (keep what we have) if it is missing or doesn't parse.
    public static Settings? TryLoad(string path)
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json); }
        catch (Exception ex) { Log.Write("settings file changed but can't be read, keeping the current settings: " + ex.Message); return null; }
    }

    // Take every value from another copy (the engine keeps its reference to this object).
    public void CopyFrom(Settings o)
    {
        Curves = o.Curves; Fans = o.Fans; PanicTemp = o.PanicTemp; CpuLimit = o.CpuLimit;
        OnSensorLost = o.OnSensorLost; OnCrash = o.OnCrash; OnExit = o.OnExit; IntervalMs = o.IntervalMs;
        Overlay = o.Overlay; Modes = o.Modes; AsusWriteTwice = o.AsusWriteTwice;
    }

    // Write to a temp file then swap, so a crash mid-save never leaves a half file.
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(path + ".tmp", path, true);
    }
}

public static class Log
{
    static readonly object Lock = new();
    public static string Path { get; set; } = System.IO.Path.Combine(Settings.Dir, "FanKick.log");

    public static void Write(string s)
    {
        lock (Lock)
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > 1 << 20) File.Move(Path, Path + ".old", true);
                File.AppendAllText(Path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + s + Environment.NewLine);
            }
            catch { }
    }
}
