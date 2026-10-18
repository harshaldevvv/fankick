using FanKick.Core;
using Xunit;

namespace FanKick.Tests;

static class TestSetup
{
    // Keep test runs out of the real FanKick.log.
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => Log.Path = Path.Combine(Path.GetTempPath(), "FanKick-tests.log");
}

sealed class FakeHardware : IHardware, IResumable
{
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
        var (e, hw) = Make(s => s.Fans["fan"] = new FanSettings { Mode = FanMode.Auto });
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

public class SafetyTests
{
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
        Make(s => s.Fans["fan"] = new FanSettings { Mode = FanMode.Fixed, Fixed = 100 });
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
        Make(s => s.Fans["fan"] = new FanSettings { Mode = FanMode.Fixed, Fixed = 40 });
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
