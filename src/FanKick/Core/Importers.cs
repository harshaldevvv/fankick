using System.Globalization;
using System.Text.Json;

namespace FanKick.Core;

// What another fan app on this machine was set to, offered on first run (never applied without the user's click).
public sealed record ImportOffer(string Source, List<string> Summary, Action<Settings> Apply);

// Finds settings left by other fan apps and turns them into FanKick settings. Only what maps cleanly onto this
// machine's fans and sensors is offered; the rest is listed as skipped.
public static class Importers
{
    public static List<ImportOffer> Find(Engine e)
    {
        var offers = new List<ImportOffer>();
        void Try(string what, Func<ImportOffer?> f)
        {
            try { if (f() is { } o) offers.Add(o); }
            catch (Exception ex) { Log.Write($"import from {what}: {ex.Message}"); }
        }
        if (OperatingSystem.IsWindows())
        {
            Try("FanControl", () => FanControlFile() is { } p ? FromFanControl(File.ReadAllText(p), e, p) : null);
            Try("G-Helper", () => GHelperFile() is { } p ? FromGHelper(File.ReadAllText(p), e) : null);
        }
        if (OperatingSystem.IsLinux() && File.Exists("/etc/fancontrol"))
            Try("fancontrol", () => FromFancontrol(File.ReadAllText("/etc/fancontrol"), e));
        return offers;
    }

    // ---------- FanControl (Rem0o), Windows: userConfig.json. Same LibreHardwareMonitor ids as ours. ----------
    static string? FanControlFile()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] dirs = [Path.Combine(docs, "FanControl"), Path.Combine(pf86, "FanControl", "Configurations"), Path.Combine(pf, "FanControl", "Configurations"),
                         Path.Combine(local, "Programs", "FanControl", "Configurations")];
        return dirs.Select(d => Path.Combine(d, "userConfig.json")).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    public static ImportOffer? FromFanControl(string json, Engine e, string path = "userConfig.json")
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // Older versions keep everything under "Main", newer under "FanControl".
        if (!root.TryGetProperty("FanControl", out var main) && !root.TryGetProperty("Main", out main)) return null;
        var curves = new List<Curve>();
        var skipped = new List<string>();
        if (main.TryGetProperty("FanCurves", out var fcs))
            foreach (var c in fcs.EnumerateArray())
            {
                string name = Str(c, "Name") ?? "Imported";
                string? sensor = c.TryGetProperty("SelectedTempSource", out var src) ? Str(src, "Identifier") : null;
                if (c.TryGetProperty("Points", out var pts))
                {
                    var points = pts.EnumerateArray().Select(p => p.GetString()!.Split(',')).Where(a => a.Length == 2)
                        .Select(a => new CurvePoint(F(a[0]), Math.Clamp(F(a[1]), 0, 100))).OrderBy(p => p.Temp).ToList();
                    float hyst = c.TryGetProperty("HysteresisConfig", out var h) && h.TryGetProperty("HysteresisValueDown", out var hd) ? (float)hd.GetDouble() : 2;
                    curves.Add(new GraphCurve { Name = name, Sensor = Map(e, sensor, skipped, name), Points = points, Hysteresis = hyst });
                }
                else if (c.TryGetProperty("SelectedFanCurves", out var members))
                    curves.Add(new MixCurve
                    {
                        Name = name,
                        Curves = members.EnumerateArray().Select(m => Str(m, "Name")).OfType<string>().ToList(),
                        // FanControl: 0 Max, 1 Min, 2 Average (Sum and Subtract have no FanKick equivalent: Max is the safe one).
                        Function = Int(c, "SelectedMixFunction") switch { 1 => MixFunction.Min, 2 => MixFunction.Average, _ => MixFunction.Max },
                    });
                else if (c.TryGetProperty("Percent", out var flat))
                    curves.Add(new FlatCurve { Name = name, Percent = (float)flat.GetDouble() });
                else if (c.TryGetProperty("IdleTemperature", out var idle) && c.TryGetProperty("LoadTemperature", out var load))
                    // Auto/trigger curves: a straight line from idle to load is the closest plain curve.
                    curves.Add(new GraphCurve
                    {
                        Name = name, Sensor = Map(e, sensor, skipped, name),
                        Points = [new((float)idle.GetDouble(), Int(c, "MinFanSpeed") ?? Int(c, "IdleFanSpeed") ?? 20), new((float)load.GetDouble(), Int(c, "MaxFanSpeed") ?? Int(c, "LoadFanSpeed") ?? 100)],
                    });
                else skipped.Add($"curve \"{name}\" (a type FanKick doesn't have)");
            }

        var fans = new Dictionary<string, FanSettings>();
        if (main.TryGetProperty("Controls", out var controls))
            foreach (var c in controls.EnumerateArray())
            {
                string id = Str(c, "Identifier") ?? "";
                string label = Str(c, "NickName") ?? Str(c, "Name") ?? id;
                if (c.TryGetProperty("Enable", out var en) && en.ValueKind == JsonValueKind.False) continue; // left to the BIOS there too
                if (e.Fans.All(f => f.Id != id)) { skipped.Add($"fan \"{label}\" (not found on this machine)"); continue; }
                var fs = new FanSettings
                {
                    Min = Int(c, "MinimumPercent") ?? 0,
                    StepUp = Int(c, "SelectedCommandStepUp") ?? 10,
                    StepDown = Int(c, "SelectedCommandStepDown") ?? 3,
                };
                if (c.TryGetProperty("ManualControl", out var manual) && manual.ValueKind == JsonValueKind.True)
                {
                    fs.Mode = FanMode.Fixed;
                    fs.Fixed = Int(c, "ManualControlValue") ?? 50;
                }
                else if (c.TryGetProperty("SelectedFanCurve", out var sel) && Str(sel, "Name") is { } curve && curves.Any(x => x.Name == curve))
                {
                    fs.Mode = FanMode.Curve;
                    fs.Curve = curve;
                }
                else continue;
                fans[id] = fs;
            }
        if (fans.Count == 0) return null;

        var summary = new List<string> { $"{fans.Count} fan{(fans.Count == 1 ? "" : "s")} and {curves.Count} curve{(curves.Count == 1 ? "" : "s")} from {path}" };
        summary.AddRange(fans.Select(kv => $"{e.Fans.First(f => f.Id == kv.Key).Name}: {(kv.Value.Mode == FanMode.Fixed ? $"fixed {kv.Value.Fixed}%" : $"curve \"{kv.Value.Curve}\"")}"));
        if (skipped.Count > 0) summary.Add("Skipped: " + string.Join("; ", skipped.Distinct()));
        return new ImportOffer("FanControl", summary, s =>
        {
            foreach (var c in curves) { s.Curves.RemoveAll(x => x.Name == c.Name); s.Curves.Add(c); }
            foreach (var (id, fs) in fans) s.Fans[id] = fs;
        });
    }

    // A sensor id from the other app, if this machine has it; otherwise the hottest CPU/GPU (and say so).
    static string Map(Engine e, string? id, List<string> skipped, string curve)
    {
        if (id != null && e.Find(id) is { Kind: SensorKind.Temperature }) return id;
        skipped.Add($"curve \"{curve}\" follows the hottest CPU/GPU here (its sensor wasn't found)");
        return Engine.Hottest;
    }

    // ---------- G-Helper (ASUS), Windows: %APPDATA%\GHelper\config.json ----------
    static string? GHelperFile()
    {
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GHelper", "config.json");
        return File.Exists(p) ? p : null;
    }

    static readonly string[] AsusModes = ["Balanced", "Turbo", "Silent"]; // G-Helper and the firmware: 0, 1, 2

    public static ImportOffer? FromGHelper(string json, Engine e)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        int mode = Int(r, "performance_mode") ?? 0;
        int baseMode = mode <= 2 ? mode : Int(r, "mode_base_" + mode) ?? 0; // custom modes sit on a built-in one
        string? modeName = baseMode is >= 0 and <= 2 ? AsusModes[baseMode] : null;
        int? charge = Int(r, "charge_limit");
        string? chargeOption = charge is { } c and >= 40 and <= 100 ? (c >= 90 ? "100%" : c >= 70 ? "80%" : "60%") : null;

        // Custom fan curves only if G-Helper was applying them in that mode.
        var curves = new List<Curve>();
        var fans = new Dictionary<string, FanSettings>();
        if (Int(r, "auto_apply_" + mode) == 1)
            foreach (var (dev, fanId, label, sensor) in new[]
            {
                ("cpu", "asus/110024", "CPU fan", e.Sensors.FirstOrDefault(s => s.Cpu)?.Id),
                ("gpu", "asus/110025", "GPU fan", e.Sensors.FirstOrDefault(s => s.Critical && !s.Cpu && s.Name.StartsWith("GPU"))?.Id),
                ("mid", "asus/110032", "Mid fan", e.Sensors.FirstOrDefault(s => s.Cpu)?.Id),
            })
            {
                if (Str(r, $"fan_profile_{dev}_{mode}") is not { } hex || e.Fans.All(f => f.Id != fanId)) continue;
                var b = hex.Split('-').Select(x => Convert.ToByte(x, 16)).ToArray();
                if (b.Length != 16) continue;
                var name = $"G-Helper {label}";
                curves.Add(new GraphCurve { Name = name, Sensor = sensor ?? Engine.Hottest, Points = Enumerable.Range(0, 8).Select(i => new CurvePoint(b[i], Math.Min((int)b[8 + i], 100))).ToList() });
                fans[fanId] = new FanSettings { Mode = FanMode.Curve, Curve = name };
            }
        if (modeName == null && chargeOption == null && fans.Count == 0) return null;

        var summary = new List<string>();
        if (modeName != null) summary.Add($"ASUS mode: {modeName}");
        if (chargeOption != null) summary.Add($"Battery charge limit: {chargeOption}");
        summary.Add(fans.Count > 0 ? $"Custom fan curves for {string.Join(" and ", fans.Keys.Select(k => e.Fans.First(f => f.Id == k).Name))}" : "Fans: firmware curves (as in G-Helper)");
        return new ImportOffer("G-Helper", summary, s =>
        {
            if (modeName != null) s.Modes["ASUS mode"] = modeName;
            if (chargeOption != null) s.Modes["Battery charge limit"] = chargeOption;
            foreach (var c in curves) { s.Curves.RemoveAll(x => x.Name == c.Name); s.Curves.Add(c); }
            foreach (var (id, fs) in fans) s.Fans[id] = fs;
        });
    }

    // ---------- lm-sensors fancontrol, Linux: /etc/fancontrol ----------
    // FCTEMPS=hwmon2/pwm1=hwmon1/temp1_input   MINTEMP=hwmon2/pwm1=40   MAXTEMP=...=70   MINSTOP=...=60   MAXPWM=...=255
    public static ImportOffer? FromFancontrol(string text, Engine e, string root = "/sys/class/hwmon")
    {
        var vars = text.Split('\n').Select(l => l.Trim()).Where(l => l.Contains('=') && !l.StartsWith('#'))
            .Select(l => l.Split('=', 2)).ToDictionary(a => a[0], a => a[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Split('=')).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]));
        if (!vars.TryGetValue("FCTEMPS", out var temps)) return null;
        int Val(string key, string pwm, int dflt) => vars.TryGetValue(key, out var d) && d.TryGetValue(pwm, out var v) && int.TryParse(v, out int n) ? n : dflt;
        string Full(string rel) => rel.StartsWith('/') ? rel : $"{root}/{rel}";

        var curves = new List<Curve>();
        var fans = new Dictionary<string, FanSettings>();
        var skipped = new List<string>();
        foreach (var (pwm, temp) in temps)
        {
            var fan = e.Fans.FirstOrDefault(f => e.PathOf(f.Id) == Full(pwm));
            var sensor = e.Sensors.FirstOrDefault(s => e.PathOf(s.Id) == Full(temp));
            if (fan == null) { skipped.Add($"{pwm} (no such fan here)"); continue; }
            int minT = Val("MINTEMP", pwm, 40), maxT = Val("MAXTEMP", pwm, 70);
            float minP = Val("MINSTOP", pwm, 100) / 2.55f, maxP = Val("MAXPWM", pwm, 255) / 2.55f;
            var name = $"fancontrol {fan.Name}";
            curves.Add(new GraphCurve
            {
                Name = name, Sensor = sensor?.Id ?? Engine.Hottest, Hysteresis = 2,
                Points = [new(minT, MathF.Round(Val("MINPWM", pwm, 0) / 2.55f)), new(minT + 0.1f, MathF.Round(minP)), new(maxT, MathF.Round(maxP))],
            });
            fans[fan.Id] = new FanSettings { Mode = FanMode.Curve, Curve = name };
            if (sensor == null) skipped.Add($"{temp} (follows the hottest CPU/GPU instead)");
        }
        if (fans.Count == 0) return null;
        var summary = new List<string> { $"{fans.Count} fan{(fans.Count == 1 ? "" : "s")} from /etc/fancontrol (straight-line curves)" };
        if (skipped.Count > 0) summary.Add("Skipped: " + string.Join("; ", skipped));
        return new ImportOffer("fancontrol (lm-sensors)", summary, s =>
        {
            foreach (var c in curves) { s.Curves.RemoveAll(x => x.Name == c.Name); s.Curves.Add(c); }
            foreach (var (id, fs) in fans) s.Fans[id] = fs;
        });
    }

    static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int? Int(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)Math.Round(v.GetDouble()) : null;
    static float F(string s) => float.Parse(s.Trim(), CultureInfo.InvariantCulture);
}
