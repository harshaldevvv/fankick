namespace FanKick.Core;

// The control loop: reads every backend, works out each fan's target from its settings, moves it there at the
// fan's step rate, and keeps the safety rules (panic temperature, lost sensor, failing writes) above everything else.
public sealed class Engine : IDisposable
{
    public const string Hottest = "virtual/hottest";
    public const int HistoryLen = 300;

    readonly IHardware[] hardware;
    readonly Dictionary<string, Sensor> byId = [];
    readonly Dictionary<FanControl, IHardware> owner = [];
    readonly Dictionary<FanControl, float> level = [];
    readonly Dictionary<FanControl, int> failures = [];
    readonly Dictionary<FanControl, DateTime> stalledSince = [];
    readonly Dictionary<string, float[]> history = [];
    int histCount;
    Thread? thread;
    volatile bool stop;
    bool lostLogged;
    DateTime lastStatus = DateTime.MinValue;

    public Settings Settings { get; }
    public Sensor HottestSensor { get; } = new(Hottest, "Hottest CPU/GPU", "FanKick", SensorKind.Temperature);
    public IReadOnlyList<Sensor> Sensors { get; }
    public IReadOnlyList<FanControl> Fans { get; }
    public IReadOnlyList<string> Notices { get; }
    public string? Alert { get; private set; }
    public bool Panic { get; private set; }
    public event Action? Updated;

    public Engine(IEnumerable<IHardware> backends, Settings settings)
    {
        hardware = backends.ToArray();
        Settings = settings;
        Sensors = hardware.SelectMany(h => h.Sensors).Prepend(HottestSensor).ToList();
        foreach (var s in Sensors) { byId.TryAdd(s.Id, s); history.TryAdd(s.Id, new float[HistoryLen]); }
        foreach (var h in hardware) foreach (var f in h.Fans) owner[f] = h;
        Fans = owner.Keys.ToList();
        foreach (var f in Fans) settings.Fan(f.Id); // create every entry now: the loop must never grow the dictionary
        Notices = hardware.Length == 0
            ? ["FanKick couldn't open any hardware on this machine. The reason is in FanKick.log, in the settings folder."]
            : hardware.Select(h => h.Notice).OfType<string>().ToList();
    }

    public float Read(string id) => byId.TryGetValue(id, out var s) ? s.Value : float.NaN;
    public Sensor? Find(string id) => byId.GetValueOrDefault(id);

    public float[] History(string id)
    {
        lock (history)
        {
            if (!history.TryGetValue(id, out var h)) return [];
            return h[(HistoryLen - histCount)..];
        }
    }

    public void Start()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "fan control", Priority = ThreadPriority.Highest };
        thread.Start();
    }

    void Loop()
    {
        var last = DateTime.UtcNow;
        while (!stop)
        {
            var now = DateTime.UtcNow;
            try
            {
                // This loop freezes while the machine sleeps, so a long gap means it just woke up.
                if ((now - last).TotalSeconds > 60)
                {
                    Log.Write($"woke after {(now - last).TotalMinutes:F0} min");
                    foreach (var r in hardware.OfType<IResumable>())
                        try { r.Resume(); } catch (Exception ex) { Log.Write("resume: " + ex.Message); }
                }
                Refresh();
                Tick(Math.Clamp((now - last).TotalSeconds, 0, 5));
            }
            catch (Exception ex) { Log.Write("control loop: " + ex); }
            last = now;
            Updated?.Invoke();
            int wait = Settings.IntervalMs - (int)(DateTime.UtcNow - now).TotalMilliseconds;
            if (wait > 0) Thread.Sleep(wait);
        }
    }

    public void Refresh()
    {
        foreach (var h in hardware)
            try { h.Update(); }
            catch (Exception ex) { Log.Write(h.Name + ": update failed: " + ex.Message); }

        var temps = Sensors.Where(s => s.Kind == SensorKind.Temperature && s != HottestSensor && !float.IsNaN(s.Value)).ToList();
        var critical = temps.Where(s => s.Critical).ToList();
        var pool = critical.Count > 0 || Sensors.Any(s => s.Critical) ? critical : temps;
        HottestSensor.Value = pool.Count == 0 ? float.NaN : pool.Max(s => s.Value);

        lock (history)
        {
            foreach (var s in Sensors)
            {
                var h = history[s.Id];
                Array.Copy(h, 1, h, 0, HistoryLen - 1);
                h[^1] = s.Value;
            }
            if (histCount < HistoryLen) histCount++;
        }
    }

    public static float Step(float current, float target, float up, float down, double seconds)
    {
        if (target > current) return up <= 0 ? target : MathF.Min(target, current + up * (float)seconds);
        return down <= 0 ? target : MathF.Max(target, current - down * (float)seconds);
    }

    public void Tick(double seconds)
    {
        var alerts = new List<string>();
        float hot = HottestSensor.Value;
        if (!float.IsNaN(hot))
        {
            bool was = Panic;
            if (hot >= Settings.PanicTemp) Panic = true;
            else if (hot < Settings.PanicTemp - 10) Panic = false;
            if (Panic != was) Log.Write(Panic ? $"PANIC: {hot:F0} °C, all fans to 100%" : $"panic over ({hot:F0} °C)");
        }
        if (Panic) alerts.Add($"{hot:F0} °C is over the {Settings.PanicTemp} °C panic limit: every fan at full speed.");

        bool lost = false;
        lock (Settings)
            foreach (var fan in Fans)
            {
                var fs = Settings.Fan(fan.Id);
                float? target = null;
                bool now = Panic;
                if (Panic) target = 100;
                else if (fs.Mode == FanMode.Fixed) target = fs.Fixed;
                else if (fs.Mode == FanMode.Curve)
                {
                    var v = Settings.Find(fs.Curve)?.Evaluate(Read, Settings.Find);
                    if (v is { } p) target = p;
                    else
                    {
                        lost = true;
                        bool full = Settings.OnSensorLost == FailSafe.FullSpeed;
                        alerts.Add($"{fan.Name}: its curve has no sensor reading, so {(full ? "it runs at full speed" : "the BIOS drives it")}.");
                        if (full) { target = 100; now = true; }
                    }
                }
                if (target is { } t && !now) t = Math.Clamp(t, fs.Min, Math.Max(fs.Min, fs.Max));
                Apply(fan, target, now, fs, seconds);
                CheckStall(fan, alerts);
            }
        if (lost != lostLogged) { Log.Write(lost ? "sensor lost: " + string.Join(" ", alerts) : "sensors back"); lostLogged = lost; }
        // Once a minute, a line to check the machine by later: the hottest temperature and every fan.
        if ((DateTime.UtcNow - lastStatus).TotalSeconds >= 60)
        {
            lastStatus = DateTime.UtcNow;
            Log.Write($"status: hottest {hot:F0} °C{(Panic ? " PANIC" : "")}, " + string.Join(", ", Fans.Select(f =>
                $"{f.Name} {(f.Percent < 0 ? "BIOS" : f.Percent + "%")}{(f.RpmSensorId is { } r && !float.IsNaN(Read(r)) ? $" {Read(r):F0} rpm" : "")}")));
        }
        Alert = alerts.Count == 0 ? null : string.Join("\n", alerts);
    }

    void Apply(FanControl fan, float? target, bool now, FanSettings fs, double seconds)
    {
        var hw = owner[fan];
        if (target is not { } t)
        {
            if (fan.Percent == -1) return;
            try { hw.Restore(fan); } catch (Exception ex) { Log.Write($"{fan.Name}: restore failed: {ex.Message}"); }
            fan.Percent = -1;
            level.Remove(fan);
            return;
        }
        float cur = level.TryGetValue(fan, out var l) ? l : t; // coming from the BIOS we don't know its %: go straight there
        float next = now ? t : Step(cur, t, fs.StepUp, fs.StepDown, seconds);
        level[fan] = next;
        int pct = (int)MathF.Round(next);
        if (pct == fan.Percent) return;
        try
        {
            hw.Set(fan, pct);
            fan.Percent = pct;
            failures[fan] = 0;
        }
        catch (Exception ex)
        {
            int n = failures[fan] = failures.GetValueOrDefault(fan) + 1;
            Log.Write($"{fan.Name}: set {pct}% failed ({n}): {ex.Message}");
            if (n == 3) try { hw.Restore(fan); Log.Write($"{fan.Name}: handed back to the BIOS after 3 failed writes"); } catch { }
        }
    }

    // A fan asked for 40%+ that shows under 100 rpm for 15 s is stuck or unplugged: say so (the curve can't fix it).
    void CheckStall(FanControl fan, List<string> alerts)
    {
        if (fan.RpmSensorId is null) return;
        float rpm = Read(fan.RpmSensorId);
        if (fan.Percent >= 40 && rpm < 100)
        {
            var since = stalledSince.TryGetValue(fan, out var s) ? s : stalledSince[fan] = DateTime.UtcNow;
            if ((DateTime.UtcNow - since).TotalSeconds >= 15) alerts.Add($"{fan.Name} is not spinning (asked for {fan.Percent}%).");
        }
        else stalledSince.Remove(fan);
    }

    // Every fan back to the BIOS, or to full speed when that is the safer choice (a crash on a machine whose BIOS
    // can't read the CPU).
    public void RestoreAll(bool fullSpeed = false)
    {
        foreach (var fan in Fans)
            try
            {
                if (fullSpeed) { owner[fan].Set(fan, 100); fan.Percent = 100; }
                else { owner[fan].Restore(fan); fan.Percent = -1; }
            }
            catch (Exception ex) { Log.Write($"{fan.Name}: restore failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        stop = true;
        thread?.Join(5000);
        RestoreAll(Settings.OnExit == FailSafe.FullSpeed);
        foreach (var h in hardware) try { h.Dispose(); } catch { }
    }
}
