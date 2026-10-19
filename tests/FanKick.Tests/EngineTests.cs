using FanKick.Core;
using Xunit;

namespace FanKick.Tests;

static class TestSetup
{
    // Keep test runs out of the real FanKick.log.
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => Log.Path = Path.Combine(Path.GetTempPath(), "FanKick-tests.log");
}

sealed class FakeHardware : IHardware, IResumable, IWriteLimited, IHasPaths
{
    public string? PathOf(string id) => id switch { "fan" => "/sys/class/hwmon/hwmon2/pwm1", "cpu" => "/sys/class/hwmon/hwmon1/temp1_input", _ => null };
    public int MinStep { get; set; }
    public readonly Sensor Cpu = new("cpu", "CPU", "cpu", SensorKind.Temperature) { Critical = true, Cpu = true, Value = 50 };
    public readonly Sensor Disk = new("disk", "Disk", "disk", SensorKind.Temperature) { Value = 40 };
    public readonly Sensor Rpm = new("rpm", "Fan", "board", SensorKind.Fan);
    public readonly FanControl Fan = new("fan", "Fan", "board") { RpmSensorId = "rpm" };
    public void Resume() => Calls.Add("resume");
    public readonly List<string> Calls = [];
    public bool FailWrites;
    public string Name => "fake";
    public string? Notice => null;
    public IReadOnlyList<Sensor> Sensors => [Cpu, Disk, Rpm];
    public IReadOnlyList<FanControl> Fans => [Fan];
    public void Update() { }
    public void Set(FanControl fan, int percent) { if (FailWrites) throw new IOException("nope"); Calls.Add("set " + percent); }
    public void Restore(FanControl fan) => Calls.Add("restore");
    public void Dispose() { }
}

public class EngineTests
{
    static readonly Func<string, Curve?> None = _ => null;

    [Fact]
    public void Interpolates_and_clamps()
    {
        var pts = new List<CurvePoint> { new(40, 20), new(60, 60), new(80, 100) };
        Assert.Equal(20, GraphCurve.Interpolate(pts, 10));
        Assert.Equal(40, GraphCurve.Interpolate(pts, 50));
        Assert.Equal(80, GraphCurve.Interpolate(pts, 70));
        Assert.Equal(100, GraphCurve.Interpolate(pts, 99));
        Assert.Equal(100, GraphCurve.Interpolate([], 50)); // no points: fail loud, not silent
    }

    [Fact]
    public void Hysteresis_follows_rises_and_holds_small_falls()
    {
        float t = 60;
        var c = new GraphCurve { Sensor = "x", Hysteresis = 4, Points = [new(40, 0), new(80, 100)] };
        Assert.Equal(50, c.Evaluate(_ => t, None));
        t = 58; Assert.Equal(50, c.Evaluate(_ => t, None));   // within 4 °C: hold
        t = 62; Assert.Equal(55, c.Evaluate(_ => t, None));   // any rise: follow
        t = 58; Assert.Equal(45, c.Evaluate(_ => t, None));   // 4 °C under: follow down
        t = float.NaN; Assert.Null(c.Evaluate(_ => t, None)); // no reading: null so the engine fails safe
    }

    [Fact]
    public void Mix_takes_max_and_fails_safe_on_a_missing_member()
    {
        var a = new FlatCurve { Name = "a", Percent = 30 };
        var b = new FlatCurve { Name = "b", Percent = 70 };
        var g = new GraphCurve { Name = "g", Sensor = "gone", Points = [new(0, 0)] };
        var all = new List<Curve> { a, b, g };
        Curve? Find(string n) => all.FirstOrDefault(c => c.Name == n);
        Assert.Equal(70, new MixCurve { Curves = ["a", "b"] }.Evaluate(_ => float.NaN, Find));
        Assert.Equal(50, new MixCurve { Function = MixFunction.Average, Curves = ["a", "b"] }.Evaluate(_ => float.NaN, Find));
        Assert.Null(new MixCurve { Curves = ["a", "g"] }.Evaluate(_ => float.NaN, Find));
        var self = new MixCurve { Name = "self", Curves = ["self"] };
        all.Add(self);
        Assert.Null(self.Evaluate(_ => 0, Find));
    }

    [Fact]
    public void Step_limits_rate_both_ways()
    {
        Assert.Equal(40, Engine.Step(30, 80, 10, 2, 1));
        Assert.Equal(80, Engine.Step(75, 80, 10, 2, 1));
        Assert.Equal(78, Engine.Step(80, 30, 10, 2, 1));
        Assert.Equal(30, Engine.Step(80, 30, 10, 0, 1)); // 0 = instant
    }

    static (Engine e, FakeHardware hw) Make(Action<Settings> setup)
    {
        var hw = new FakeHardware();
        var s = new Settings();
        setup(s);
        var e = new Engine([hw], s);
        e.Refresh();
        return (e, hw);
    }

    [Fact]
    public void Curve_mode_steps_toward_target()
    {
        var (e, hw) = Make(s =>
        {
            s.Curves.Add(new GraphCurve { Name = "c", Sensor = "cpu", Points = [new(40, 20), new(80, 100)] });
            s.Fans["fan"] = new FanSettings { Mode = FanMode.Curve, Curve = "c", StepUp = 10, StepDown = 5 };
        });
        e.Tick(1);
        Assert.Equal(40, hw.Fan.Percent);             // first write goes straight to target (50 °C -> 40%)
        hw.Cpu.Value = 80; e.Refresh(); e.Tick(1);
        Assert.Equal(50, hw.Fan.Percent);             // then at most +10%/s
        Assert.Null(e.Alert);
    }

    [Fact]
    public void Panic_overrides_everything_including_auto_fans()
    {
        var (e, hw) = Make(s => { s.PanicTemp = 95; s.Fans["fan"] = new FanSettings { Mode = FanMode.Auto }; });
        e.Tick(1);
        Assert.Empty(hw.Calls);                        // auto: BIOS in charge, we don't touch it
        hw.Cpu.Value = 96; e.Refresh(); e.Tick(1);
        Assert.True(e.Panic);
        Assert.Equal(100, hw.Fan.Percent);
        hw.Cpu.Value = 90; e.Refresh(); e.Tick(1);
        Assert.True(e.Panic);                          // holds until 10 °C under the limit
        hw.Cpu.Value = 84; e.Refresh(); e.Tick(1);
        Assert.False(e.Panic);
        Assert.Equal("restore", hw.Calls[^1]);         // and hands the fan back
    }

    [Fact]
    public void Lost_sensor_goes_full_speed_or_bios()
    {
        var (e, hw) = Make(s =>
        {
            s.OnSensorLost = FailSafe.FullSpeed;
            s.Curves.Add(new GraphCurve { Name = "c", Sensor = "cpu", Points = [new(0, 20)] });
            s.Fans["fan"] = new FanSettings { Mode = FanMode.Curve, Curve = "c" };
        });
        e.Tick(1);
        hw.Cpu.Value = float.NaN; e.Refresh(); e.Tick(1);
        Assert.Equal(100, hw.Fan.Percent);
        Assert.NotNull(e.Alert);
        e.Settings.OnSensorLost = FailSafe.Bios; e.Tick(1);
        Assert.Equal(-1, hw.Fan.Percent);
        Assert.Equal("restore", hw.Calls[^1]);
    }

    [Fact]
    public void Hottest_uses_critical_sensors_only()
    {
        var (e, hw) = Make(_ => { });
        hw.Disk.Value = 70;
        e.Refresh();
        Assert.Equal(50, e.HottestSensor.Value);
    }

    [Fact]
    public void Failing_writes_hand_fan_back_after_three()
    {
        var (e, hw) = Make(s => s.Fans["fan"] = new FanSettings { Mode = FanMode.Fixed, Fixed = 60 });
        hw.FailWrites = true;
        for (int i = 0; i < 3; i++) e.Tick(1);
        Assert.Equal("restore", hw.Calls[^1]);
    }

    [Fact]
    public void Settings_round_trip_keeps_curve_types()
    {
        var path = Path.GetTempFileName();
        var s = new Settings();
        s.Curves.Add(new MixCurve { Name = "m", Curves = ["Silent", "Full"] });
        s.Fans["x"] = new FanSettings { Mode = FanMode.Curve, Curve = "m" };
        s.Save(path);
        var r = Settings.Load(path);
        Assert.IsType<MixCurve>(r.Find("m"));
        Assert.IsType<GraphCurve>(r.Find("Silent"));
        Assert.Equal(FanMode.Curve, r.Fans["x"].Mode);
        File.Delete(path);
    }
}

public class SettingsFileTests
{
    [Fact]
    public void Hand_written_settings_load()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            { "Fans": { "asus/110024": { "Mode": "Fixed", "Fixed": 100 } }, "OnExit": "FullSpeed", "Modes": { "ASUS mode": "Turbo" } }
            """);
        var s = Settings.Load(path);
        Assert.Equal(FanMode.Fixed, s.Fans["asus/110024"].Mode);
        Assert.Equal(FailSafe.FullSpeed, s.OnExit);
        Assert.Equal("Turbo", s.Modes["ASUS mode"]);
        var hw = new FakeHardware();
        s.Fans["fan"] = s.Fans["asus/110024"];
        var e = new Engine([hw], s);
        e.Refresh(); e.Tick(1);
        Assert.Equal(100, hw.Fan.Percent);
        File.Delete(path);
    }
}

public class DefaultsTests
{
    [Fact]
    public void A_fresh_install_leaves_a_normal_pc_alone()
    {
        var s = new Settings();
        Assert.Equal(0, s.CpuLimit);                           // CPU protection is opt-in
        Assert.Equal(100, s.PanicTemp);
        Assert.Equal(FailSafe.Bios, s.OnSensorLost);
        Assert.Equal(FailSafe.Bios, s.OnCrash);
        Assert.Equal(FailSafe.Bios, s.OnExit);
        Assert.Equal("", s.Overlay.Hotkey);
        Assert.False(s.Overlay.Enabled);
        Assert.Empty(s.Modes);

        var hw = new FakeHardware();
        var t = new DateTime(2026, 1, 1);
        var e = new Engine([hw], s) { Now = () => t };
        void Run(int secs) { for (int i = 0; i <= secs; i++) { e.Refresh(); e.Tick(1); t = t.AddSeconds(1); } }
        hw.Cpu.Value = 95;                                     // normal for many CPUs under load
        Run(120);
        hw.Cpu.Value = float.NaN;                              // sensor gone
        Run(30);
        Assert.Empty(hw.Calls);                                // no fan touched: the BIOS stays in charge
        Assert.False(e.ProtectCpu);                            // and CPU boost is left alone
    }
}

public class SafetyTests
{
    [Fact]
    public void Write_limited_backends_only_get_real_changes()
    {
        hw.MinStep = 10;
        Make(s => { s.Curves.Add(new GraphCurve { Name = "c", Sensor = "cpu", Hysteresis = 0, Points = [new(40, 0), new(90, 100)] });
                    s.Fans["fan"] = new FanSettings { Mode = FanMode.Curve, Curve = "c", StepUp = 0, StepDown = 0 }; });
        Assert.Equal(20, hw.Fan.Percent);                      // 50 C: first write always happens
        hw.Cpu.Value = 54; Run(1);
        Assert.Equal(20, hw.Fan.Percent);                      // +8: held
        hw.Cpu.Value = 56; Run(1);
        Assert.Equal(32, hw.Fan.Percent);                      // +12: written
        hw.Cpu.Value = 48; Run(1);
        Assert.Equal(32, hw.Fan.Percent);                      // -16: held (down needs 20)
        hw.Cpu.Value = 45; Run(1);
        Assert.Equal(10, hw.Fan.Percent);                      // -22: written
        hw.Cpu.Value = 89.6f; Run(1);
        hw.Cpu.Value = 95; Run(1);
        Assert.Equal(100, hw.Fan.Percent);                     // 100% always goes through
    }

    DateTime t = new(2026, 1, 1);
    readonly FakeHardware hw = new();
    Engine e = null!;

    Engine Make(Action<Settings> setup)
    {
        var s = new Settings();
        setup(s);
        e = new Engine([hw], s) { Now = () => t };
        Run(0);
        return e;
    }

    // Advance the clock `secs` seconds, one tick per second.
    void Run(int secs)
    {
        for (int i = 0; i <= secs; i++) { e.Refresh(); e.Tick(1); if (i < secs) t = t.AddSeconds(1); }
    }

    [Fact]
    public void Watchdog_learns_then_reapplies_a_slow_fan_and_sees_it_recover()
    {
        hw.Rpm.Value = 5200;
        Make(s => s.Fans["fan"] = new FanSettings { Mode = FanMode.Fixed, Fixed = 100 });
        Run(31);
        Assert.Equal(5200, e.Settings.Fans["fan"].Rpm[10]);   // learned once settled
        hw.Rpm.Value = 2900;                                   // like a fan the firmware took back after sleep
        Run(9);
        Assert.DoesNotContain("resume", hw.Calls);             // must stay slow for 10 s first
        Run(2);
        Assert.Contains("resume", hw.Calls);
        hw.Rpm.Value = 5250;
        Run(15);
        Assert.False(e.FansFailing);
        Assert.Null(e.Alert);
    }

    [Fact]
    public void Watchdog_raises_the_alarm_after_three_failed_tries()
    {
        hw.Rpm.Value = 5200;
        Make(s => { s.CpuLimit = 90; s.Fans["fan"] = new FanSettings { Mode = FanMode.Fixed, Fixed = 100 }; });
        Run(31);
        hw.Rpm.Value = 3000;
        Run(400);                                              // tries back off: 60 s, then 120 s
        Assert.Equal(3, hw.Calls.Count(c => c == "resume"));
        Assert.True(e.FansFailing);
        Assert.True(e.ProtectCpu);
        Assert.Contains("isn't following", e.Alert);
        hw.Rpm.Value = 5200;
        Run(12);
        Assert.False(e.FansFailing);
    }

    [Fact]
    public void Watchdog_catches_a_dead_fan_without_learning_first()
    {
        hw.Rpm.Value = 0;
        Make(s => s.Fans["fan"] = new FanSettings { Mode = FanMode.Fixed, Fixed = 60 });
        Run(80);                                               // a fan from standstill gets 75 s, then 10 s slow
        Assert.DoesNotContain("resume", hw.Calls);
        Run(10);
        Assert.Contains("resume", hw.Calls);
    }

    [Fact]
    public void Watchdog_acts_within_40_s_on_a_spinning_fan_that_stops()
    {
        hw.Rpm.Value = 4800;
        Make(s => s.Fans["fan"] = new FanSettings { Mode = FanMode.Fixed, Fixed = 80 });
        Run(31);
        hw.Rpm.Value = 0;                                      // the firmware took it (as after a long standby)
        Run(9);
        Assert.DoesNotContain("resume", hw.Calls);
        Run(2);
        Assert.Contains("resume", hw.Calls);                  // 30 s grace from the last write is long past
        hw.Rpm.Value = 2500;                                   // climbing back: must not be judged for 60 s
        Run(50);
        Assert.Single(hw.Calls, c => c == "resume");
    }

    [Fact]
    public void A_moving_curve_does_not_hide_a_dead_fan()
    {
        // Seen live: the firmware took the CPU fan (0 rpm) while a curve kept changing its % every ~30 s; each write
        // restarted a 75 s grace and the watchdog waited 3.5 minutes.
        hw.Rpm.Value = 4800;
        Make(s => { s.Curves.Add(new GraphCurve { Name = "c", Sensor = "cpu", Hysteresis = 0, Points = [new(40, 40), new(90, 100)] });
                    s.Fans["fan"] = new FanSettings { Mode = FanMode.Curve, Curve = "c", StepUp = 0, StepDown = 0 }; });
        Run(31);
        hw.Rpm.Value = 0;
        for (int i = 0; i < 4 && !hw.Calls.Contains("resume"); i++)
        {
            hw.Cpu.Value = i % 2 == 0 ? 70 : 52;                // the curve moves the fan every 10 s
            Run(10);
        }
        Assert.Contains("resume", hw.Calls);                   // within 40 s of the fan stopping
    }

    [Fact]
    public void Cpu_limit_forces_full_speed_until_ten_under_for_a_minute()
    {
        Make(s => { s.CpuLimit = 80; s.Fans["fan"] = new FanSettings { Mode = FanMode.Fixed, Fixed = 40 }; });
        Assert.Equal(40, hw.Fan.Percent);
        hw.Cpu.Value = 82;
        Run(5);
        Assert.False(e.LimitActive);                           // 6 s at or over it
        Run(1);
        Assert.True(e.LimitActive);
        Assert.True(e.ProtectCpu);
        Assert.Equal(100, hw.Fan.Percent);
        hw.Cpu.Value = 75;                                     // under the limit but not 10 under: holds
        Run(120);
        Assert.True(e.LimitActive);
        hw.Cpu.Value = 69;
        Run(59);
        Assert.True(e.LimitActive);
        Run(1);
        Assert.False(e.LimitActive);
        Run(25);                                               // back down at the fan's 3 %/s step-down rate
        Assert.Equal(40, hw.Fan.Percent);
    }

    [Fact]
    public void Lost_cpu_temperature_goes_full_speed()
    {
        Make(s => { s.OnSensorLost = FailSafe.FullSpeed; s.Fans["fan"] = new FanSettings { Mode = FanMode.Fixed, Fixed = 40 }; });
        hw.Cpu.Value = float.NaN;
        Run(4);
        Assert.False(e.CpuLost);
        Run(1);
        Assert.True(e.CpuLost);
        Assert.Equal(100, hw.Fan.Percent);
        hw.Cpu.Value = 50;
        Run(1);
        Assert.False(e.CpuLost);
    }
}

sealed class FakeAsus : IHardware
{
    public readonly Sensor Cpu = new("cpu", "Core (Tctl/Tdie)", "Ryzen", SensorKind.Temperature) { Critical = true, Cpu = true, Value = 50 };
    public readonly Sensor Gpu = new("gpu", "GPU Core", "RTX", SensorKind.Temperature) { Critical = true, Value = 45 };
    public string Name => "fake ASUS";
    public string? Notice => null;
    public IReadOnlyList<Sensor> Sensors => [Cpu, Gpu];
    public IReadOnlyList<FanControl> Fans { get; } = [new("asus/110024", "CPU fan", "ASUS"), new("asus/110025", "GPU fan", "ASUS")];
    public void Update() { }
    public void Set(FanControl fan, int percent) { }
    public void Restore(FanControl fan) { }
    public void Dispose() { }
}

public class ImportTests
{
    [Fact]
    public void FanControl_new_layout_maps_fans_curves_and_sensors()
    {
        var e = new Engine([new FakeHardware()], new Settings());
        const string json = """
            { "__VERSION__": "268", "FanControl": {
                "FanCurves": [
                  { "Name": "CPU", "SelectedTempSource": { "Identifier": "cpu" }, "Points": ["40,20", "80.5,100"],
                    "HysteresisConfig": { "HysteresisValueDown": 3 } },
                  { "Name": "Board", "SelectedTempSource": { "Identifier": "/lpc/missing/temperature/0" }, "Points": ["30,30", "60,90"] },
                  { "Name": "Both", "SelectedFanCurves": [ { "Name": "CPU" }, { "Name": "Board" } ], "SelectedMixFunction": 0 },
                  { "Name": "Quiet", "Percent": 35 } ],
                "Controls": [
                  { "NickName": "Front", "Identifier": "fan", "Enable": true, "SelectedFanCurve": { "Name": "Both" },
                    "MinimumPercent": 15, "SelectedCommandStepUp": 8, "SelectedCommandStepDown": 4, "ManualControl": false },
                  { "NickName": "Gone", "Identifier": "/lpc/nct6798d/control/5", "Enable": true, "SelectedFanCurve": { "Name": "CPU" } } ] } }
            """;
        var o = Importers.FromFanControl(json, e)!;
        Assert.Contains(o.Summary, l => l.Contains("Gone") && l.Contains("not found"));
        Assert.Contains(o.Summary, l => l.Contains("Board") && l.Contains("hottest"));
        var s = new Settings();
        o.Apply(s);
        Assert.Equal(FanMode.Curve, s.Fans["fan"].Mode);
        Assert.Equal("Both", s.Fans["fan"].Curve);
        Assert.Equal(15, s.Fans["fan"].Min);
        Assert.Equal(8, s.Fans["fan"].StepUp);
        var cpu = Assert.IsType<GraphCurve>(s.Find("CPU"));
        Assert.Equal("cpu", cpu.Sensor);
        Assert.Equal(3, cpu.Hysteresis);
        Assert.Equal(80.5f, cpu.Points[1].Temp);
        Assert.Equal(Engine.Hottest, Assert.IsType<GraphCurve>(s.Find("Board")).Sensor);
        Assert.Equal(MixFunction.Max, Assert.IsType<MixCurve>(s.Find("Both")).Function);
        Assert.Equal(35, Assert.IsType<FlatCurve>(s.Find("Quiet")).Percent);
    }

    [Fact]
    public void FanControl_old_layout_with_manual_fan_and_auto_curve()
    {
        var e = new Engine([new FakeHardware()], new Settings());
        const string json = """
            { "Main": {
                "FanCurves": [ { "Name": "Auto", "SelectedTempSource": { "Identifier": "cpu" }, "IdleTemperature": 35, "LoadTemperature": 70, "MinFanSpeed": 25, "MaxFanSpeed": 90 } ],
                "Controls": [ { "Name": "Pump", "Identifier": "fan", "Enable": true, "ManualControl": true, "ManualControlValue": 70, "SelectedFanCurve": { "Name": "Auto" } } ] } }
            """;
        var s = new Settings();
        Importers.FromFanControl(json, e)!.Apply(s);
        Assert.Equal(FanMode.Fixed, s.Fans["fan"].Mode);
        Assert.Equal(70, s.Fans["fan"].Fixed);
        var auto = Assert.IsType<GraphCurve>(s.Find("Auto"));
        Assert.Equal([new CurvePoint(35, 25), new CurvePoint(70, 90)], auto.Points);
    }

    [Fact]
    public void FanControl_with_every_fan_disabled_offers_nothing()
    {
        var e = new Engine([new FakeHardware()], new Settings());
        Assert.Null(Importers.FromFanControl("""{ "FanControl": { "FanCurves": [], "Controls": [ { "Identifier": "fan", "Enable": false } ] } }""", e));
    }

    [Fact]
    public void GHelper_mode_charge_limit_and_curves_only_when_applied()
    {
        var e = new Engine([new FakeAsus()], new Settings());
        const string on = """
            { "performance_mode": 1, "charge_limit": 80, "auto_apply_1": 1,
              "fan_profile_cpu_1": "1E-3F-44-48-4C-50-54-62-11-1A-22-29-34-43-51-5A",
              "fan_profile_gpu_1": "1E-3F-44-48-4C-50-54-62-11-1A-22-29-34-43-51-5A" }
            """;
        var s = new Settings();
        Importers.FromGHelper(on, e)!.Apply(s);
        Assert.Equal("Turbo", s.Modes["ASUS mode"]);
        Assert.Equal("80%", s.Modes["Battery charge limit"]);
        var c = Assert.IsType<GraphCurve>(s.Find(s.Fans["asus/110024"].Curve));
        Assert.Equal("cpu", c.Sensor);
        Assert.Equal(new CurvePoint(30, 17), c.Points[0]);
        Assert.Equal(new CurvePoint(98, 90), c.Points[7]);
        Assert.Equal("gpu", Assert.IsType<GraphCurve>(s.Find(s.Fans["asus/110025"].Curve)).Sensor);

        // Custom mode 3 built on Silent, curves not applied there: mode only, fans stay with the firmware.
        var s2 = new Settings();
        Importers.FromGHelper("""{ "performance_mode": 3, "mode_base_3": 2, "fan_profile_cpu_3": "1E-3F-44-48-4C-50-54-62-11-1A-22-29-34-43-51-5A" }""", e)!.Apply(s2);
        Assert.Equal("Silent", s2.Modes["ASUS mode"]);
        Assert.DoesNotContain(s2.Fans, f => f.Value.Mode != FanMode.Auto);
    }

    [Fact]
    public void Linux_fancontrol_becomes_straight_line_curves()
    {
        var e = new Engine([new FakeHardware()], new Settings());
        const string conf = "# Configuration file generated by pwmconfig\nINTERVAL=10\nFCTEMPS=hwmon2/pwm1=hwmon1/temp1_input\nFCFANS=hwmon2/pwm1=hwmon2/fan1_input\nMINTEMP=hwmon2/pwm1=40\nMAXTEMP=hwmon2/pwm1=70\nMINSTART=hwmon2/pwm1=150\nMINSTOP=hwmon2/pwm1=51\n";
        var s = new Settings();
        Importers.FromFancontrol(conf, e)!.Apply(s);
        var c = Assert.IsType<GraphCurve>(s.Find(s.Fans["fan"].Curve));
        Assert.Equal("cpu", c.Sensor);
        Assert.Equal(0, c.Points[0].Percent);          // MINPWM default 0 at/below MINTEMP
        Assert.Equal(20, c.Points[1].Percent);         // MINSTOP 51/255
        Assert.Equal(new CurvePoint(70, 100), c.Points[2]);
    }
}
