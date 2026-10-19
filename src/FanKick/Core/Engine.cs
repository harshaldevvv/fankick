namespace FanKick.Core;

// The control loop: reads every backend, works out each fan's target from its settings, moves it there at the
// fan's step rate, and keeps the safety rules above everything else: panic temperature, CPU temperature limit, lost
// sensors, failing writes, and a watchdog that re-applies control to a fan that stops following it.
public sealed class Engine : IDisposable
{
    public const string Hottest = "virtual/hottest";
    public const int HistoryLen = 300;

    readonly IHardware[] hardware;
    readonly Dictionary<string, Sensor> byId = [];
    readonly Dictionary<FanControl, IHardware> owner = [];
    readonly Dictionary<FanControl, float> level = [];
    readonly Dictionary<FanControl, int> failures = [];
    readonly Dictionary<FanControl, Watch> watch = [];
    readonly Dictionary<string, float[]> history = [];
    int histCount;
    Thread? thread;
    volatile bool stop;
    bool lostLogged;
    DateTime lastStatus = DateTime.MinValue;
    string? recordFile;
    DateTime lastRecord;
    DateTime? overSince, underSince, cpuMissingSince;
    bool cpuSeen;

    // Per fan: when its speed last changed, and the watchdog's state.
    sealed class Watch
    {
        public DateTime Changed;
        // Seconds to wait after Changed before judging: longer when the fan starts from standstill or after a re-apply
        // (ASUS fans take most of a minute to climb back after a mode write).
        public double Grace = 30;
        // When the fan was first seen off its normal speed (during the grace too). While set, our own speed changes
        // don't restart the clock: a curve that keeps moving must not keep a dead fan from being noticed.
        public DateTime? BadSince;
        public DateTime? SlowSince, OkSince;
        public DateTime NextTry;
        public int Tries;
    }

    public Settings Settings { get; }
    public Sensor HottestSensor { get; } = new(Hottest, "Hottest CPU/GPU", "FanKick", SensorKind.Temperature);
    public IReadOnlyList<Sensor> Sensors { get; }
    public IReadOnlyList<FanControl> Fans { get; }
    public IReadOnlyList<string> Notices { get; }
    public string? Alert { get; private set; }
    public bool Panic { get; private set; }
    // CPU at or over Settings.CpuLimit (see CpuTemperature).
    public bool LimitActive { get; private set; }
    // The CPU temperature was readable and is not any more.
    public bool CpuLost { get; private set; }
    // A fan ignored three re-applies in a row.
    public bool FansFailing { get; private set; }
    // The app turns CPU boost off while this holds; only for users who opted in with a CPU temperature limit.
    public bool ProtectCpu => Settings.CpuLimit > 0 && (LimitActive || CpuLost || FansFailing);
    public event Action? Updated;
    // Raised when the watchdog learns a fan's speed, so the app can save it.
    public event Action? Learned;
    // The clock, swappable for tests.
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    public Engine(IEnumerable<IHardware> backends, Settings settings)
    {
        hardware = backends.ToArray();
        Settings = settings;
        Sensors = hardware.SelectMany(h => h.Sensors).Prepend(HottestSensor).ToList();
        foreach (var s in Sensors) { byId.TryAdd(s.Id, s); history.TryAdd(s.Id, new float[HistoryLen]); }
        foreach (var h in hardware) foreach (var f in h.Fans) owner[f] = h;
        Fans = owner.Keys.ToList();
        foreach (var f in Fans) settings.Fan(f.Id); // create every entry now: the loop must never grow the dictionary
        foreach (var f in Fans)
        {
            var fs = settings.Fan(f.Id);
            Log.Write($"fan {f.Id} ({f.Name}): {fs.Mode}{fs.Mode switch { FanMode.Fixed => $" {fs.Fixed}%", FanMode.Curve => $" \"{fs.Curve}\"", _ => "" }}");
        }
        Notices = hardware.Length == 0
            ? ["FanKick couldn't open any hardware on this machine. The reason is in FanKick.log, in the settings folder."]
            : hardware.Select(h => h.Notice).OfType<string>().ToList();
    }

    public float Read(string id) => byId.TryGetValue(id, out var s) ? s.Value : float.NaN;
    public string? PathOf(string id) => hardware.OfType<IHasPaths>().Select(h => h.PathOf(id)).FirstOrDefault(p => p != null);
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
                    Resume();
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

    // After sleep, firmware may have dropped our settings (ASUS laptops do; desktop Super I/O chips can reset on S3):
    // redo each backend's startup sequence, or write every driven fan again.
    public void Resume()
    {
        foreach (var h in hardware)
            try
            {
                if (h is IResumable r) r.Resume();
                else foreach (var f in h.Fans.Where(f => f.Percent >= 0)) h.Set(f, f.Percent);
            }
            catch (Exception ex) { Log.Write($"{h.Name}: resume failed: {ex.Message}"); }
        var t = Now();
        foreach (var w in watch.Values) { w.Changed = t; w.Grace = 60; w.SlowSince = null; w.BadSince = null; }
    }

    public static float Step(float current, float target, float up, float down, double seconds)
    {
        if (target > current) return up <= 0 ? target : MathF.Min(target, current + up * (float)seconds);
        return down <= 0 ? target : MathF.Max(target, current - down * (float)seconds);
    }

    public void Tick(double seconds)
    {
        var alerts = new List<string>();
        var clock = Now();
        float hot = HottestSensor.Value;
        CpuTemperature(clock, alerts);
        if (!float.IsNaN(hot))
        {
            bool was = Panic;
            if (hot >= Settings.PanicTemp) Panic = true;
            else if (hot < Settings.PanicTemp - 10) Panic = false;
            if (Panic != was) Log.Write(Panic ? $"PANIC: {hot:F0} °C, all fans to 100%" : $"panic over ({hot:F0} °C)");
        }
        if (Panic) alerts.Add($"{hot:F0} °C is over the {Settings.PanicTemp} °C panic limit: every fan at full speed.");
        bool full = Panic || LimitActive || (CpuLost && Settings.OnSensorLost == FailSafe.FullSpeed);

        bool lost = false;
        lock (Settings)
            foreach (var fan in Fans)
            {
                var fs = Settings.Fan(fan.Id);
                float? target = null;
                bool now = full;
                if (full) target = 100;
                else if (fs.Mode == FanMode.Fixed) target = fs.Fixed;
                else if (fs.Mode == FanMode.Curve)
                {
                    var v = Settings.Find(fs.Curve)?.Evaluate(Read, Settings.Find);
                    if (v is { } p) target = p;
                    else
                    {
                        lost = true;
                        bool toFull = Settings.OnSensorLost == FailSafe.FullSpeed;
                        alerts.Add($"{fan.Name}: its curve has no sensor reading, so {(toFull ? "it runs at full speed" : "the BIOS drives it")}.");
                        if (toFull) { target = 100; now = true; }
                    }
                }
                if (target is { } t && !now) target = Math.Clamp(t, fs.Min, Math.Max(fs.Min, fs.Max));
                Apply(fan, target, now, fs, seconds, clock);
                Watchdog(fan, fs, alerts, clock);
            }
        FansFailing = watch.Values.Any(w => w.Tries >= 3 && w.SlowSince != null);
        if (lost != lostLogged) { Log.Write(lost ? "sensor lost: " + string.Join(" ", alerts) : "sensors back"); lostLogged = lost; }
        // Once a minute, a line to check the machine by later: the hottest temperature and every fan.
        if ((DateTime.UtcNow - lastStatus).TotalSeconds >= 60)
        {
            lastStatus = DateTime.UtcNow;
            Log.Write($"status: hottest {hot:F0} °C{(Panic ? " PANIC" : "")}{(LimitActive ? " LIMIT" : "")}{(CpuLost ? " NO-CPU-TEMP" : "")}{(FansFailing ? " FANS-FAILING" : "")}, " + string.Join(", ", Fans.Select(f =>
                $"{f.Name} {(f.Percent < 0 ? "BIOS" : f.Percent + "%")}{(f.RpmSensorId is { } r && !float.IsNaN(Read(r)) ? $" {Read(r):F0} rpm" : "")}")));
        }
        Alert = alerts.Count == 0 ? null : string.Join("\n", alerts);
        Record(clock);
    }

    // Every reading and every fan's % as one CSV row every 2 s, while Settings.Record is on. A new file per recording.
    void Record(DateTime clock)
    {
        if (!Settings.Record) { if (recordFile != null) Log.Write("recording stopped: " + recordFile); recordFile = null; return; }
        if ((clock - lastRecord).TotalSeconds < 2) return;
        lastRecord = clock;
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            if (recordFile == null)
            {
                var dir = Path.Combine(Settings.Dir, "records");
                Directory.CreateDirectory(dir);
                recordFile = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd HHmmss", ic) + ".csv");
                static string Q(string s) => "\"" + s.Replace("\"", "'") + "\"";
                File.WriteAllText(recordFile, string.Join(",", Sensors.Select(s => Q($"{s.Group} / {s.Name} ({s.Unit})"))
                    .Prepend("time").Concat(Fans.Select(f => Q($"{f.Name} (% set)")))) + Environment.NewLine,
                    new System.Text.UTF8Encoding(true)); // with a BOM, so Excel shows °C right
                Log.Write("recording to " + recordFile);
            }
            File.AppendAllText(recordFile, string.Join(",", Sensors.Select(s => float.IsNaN(s.Value) ? "" : s.Value.ToString("0.##", ic))
                .Prepend(DateTime.Now.ToString("HH:mm:ss", ic)).Concat(Fans.Select(f => f.Percent < 0 ? "" : f.Percent.ToString(ic)))) + Environment.NewLine);
        }
        catch (Exception ex) { Log.Write("recording: " + ex.Message); recordFile = null; }
    }

    void Apply(FanControl fan, float? target, bool now, FanSettings fs, double seconds, DateTime clock)
    {
        var hw = owner[fan];
        if (target is not { } t)
        {
            if (fan.Percent == -1) return;
            try { hw.Restore(fan); Log.Write($"{fan.Name} -> BIOS"); } catch (Exception ex) { Log.Write($"{fan.Name}: restore failed: {ex.Message}"); }
            fan.Percent = -1;
            level.Remove(fan);
            return;
        }
        float cur = level.TryGetValue(fan, out var l) ? l : t; // coming from the BIOS we don't know its %: go straight there
        float next = now ? t : Step(cur, t, fs.StepUp, fs.StepDown, seconds);
        level[fan] = next;
        int pct = (int)MathF.Round(next);
        if (pct == fan.Percent) return;
        if (hw is IWriteLimited wl && fan.Percent >= 0 && pct != 100
            && (pct > fan.Percent ? pct - fan.Percent < wl.MinStep : fan.Percent - pct < 2 * wl.MinStep)) return;
        try
        {
            hw.Set(fan, pct);
            var wt = W(fan, clock);
            if (wt.BadSince == null)
            {
                wt.Changed = clock;
                // A fan we start from (nearly) stopped can take a minute to spin up; one that should already spin can't.
                wt.Grace = fan.Percent < 20 && fan.RpmSensorId is { } rid && Read(rid) < 100 ? 75 : 30;
            }
            if (fan.Percent < 0 || Math.Abs(pct - fan.Percent) >= 10 || pct == 100) Log.Write($"{fan.Name} -> {pct}%");
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

    Watch W(FanControl fan, DateTime clock) => watch.TryGetValue(fan, out var w) ? w : watch[fan] = new Watch { Changed = clock };

    // CPU temperature limit, and the fail-safe for a CPU temperature that stops reading.
    void CpuTemperature(DateTime clock, List<string> alerts)
    {
        var temps = Sensors.Where(s => s.Cpu && !float.IsNaN(s.Value)).Select(s => s.Value).ToList();
        float cpu = temps.Count == 0 ? float.NaN : temps.Max();
        if (!float.IsNaN(cpu))
        {
            cpuSeen = true;
            cpuMissingSince = null;
            if (CpuLost) { CpuLost = false; Log.Write($"CPU temperature readable again ({cpu:F0} °C)"); }
        }
        else if (cpuSeen && !CpuLost && (clock - (cpuMissingSince ??= clock)).TotalSeconds >= 5)
        {
            CpuLost = true;
            Log.Write("CPU temperature lost: fans to " + (Settings.OnSensorLost == FailSafe.FullSpeed ? "full speed" : "BIOS control") + (Settings.CpuLimit > 0 ? ", CPU boost off" : ""));
        }
        if (CpuLost) alerts.Add("The CPU temperature can't be read any more, so " + (Settings.OnSensorLost == FailSafe.FullSpeed ? "every fan runs at full speed" : "fans on curves are back with the BIOS") + (Settings.CpuLimit > 0 ? " and CPU boost is off." : "."));

        int lim = Settings.CpuLimit;
        if (lim > 0 && cpu >= lim) { overSince ??= clock; underSince = null; }
        else if (lim <= 0 || cpu <= lim - 10) { underSince ??= clock; overSince = null; }
        else { overSince = null; underSince = null; }
        if (!LimitActive && overSince is { } o && (clock - o).TotalSeconds >= 6)
        {
            LimitActive = true;
            Log.Write($"CPU {cpu:F0} °C, over the {lim} °C limit: every fan to 100%, CPU boost off");
        }
        else if (LimitActive && (lim <= 0 || underSince is { } u && (clock - u).TotalSeconds >= 60))
        {
            LimitActive = false;
            Log.Write($"CPU {cpu:F0} °C, under the limit for a minute: fans and boost back to normal");
        }
        if (LimitActive) alerts.Add($"The CPU reached the {lim} °C limit: every fan at full speed and CPU boost off until it has been under {lim - 10} °C for a minute.");
    }

    // Fan watchdog. A fan we drive that reads far below the speed it normally reaches at that % (learned per 10% step),
    // or not at all, is not following us: firmware that dropped our setting after sleep, another program, or a locked
    // controller. Re-apply control (the backend's startup sequence where it has one), check again, back off between
    // tries, and after three failed tries raise the alarm (the app then turns CPU boost off).
    void Watchdog(FanControl fan, FanSettings fs, List<string> alerts, DateTime clock)
    {
        var w = W(fan, clock);
        if (fan.RpmSensorId is null || fan.Percent < 0) { w.SlowSince = null; return; }
        float rpm = Read(fan.RpmSensorId);
        if (float.IsNaN(rpm) || rpm > 20000) return;
        int step = Math.Clamp(fan.Percent / 10, 0, 10);
        int normal = fs.Rpm.GetValueOrDefault(step);
        bool dead = fan.Percent >= 30 && rpm < 100;
        bool slow = dead || (fan.Percent >= 20 && normal > 0 && rpm < normal * 0.7f);
        double settled = (clock - w.Changed).TotalSeconds;
        if (!slow)
        {
            w.SlowSince = null;
            w.BadSince = null;
            if (w.Tries > 0 && (clock - (w.OkSince ??= clock)).TotalSeconds >= 10)
            {
                Log.Write($"watchdog: {fan.Name} back to {rpm:F0} rpm at {fan.Percent}%");
                w.Tries = 0;
                w.OkSince = null;
            }
            if (w.Tries == 0 && settled >= 30 && rpm > 100 && (normal == 0 || rpm > normal * 1.03f))
            {
                fs.Rpm[step] = (int)rpm;
                Learned?.Invoke();
            }
            return;
        }
        w.OkSince = null;
        w.BadSince ??= clock;
        if (settled < w.Grace) return;
        w.SlowSince ??= clock;
        if (w.Tries >= 3)
            alerts.Add($"{fan.Name} isn't following FanKick: {rpm:F0} rpm at {fan.Percent}%{(normal > 0 ? $" (normally {normal})" : "")}.{(Settings.CpuLimit > 0 ? " CPU boost is off until it does." : "")} If it started after sleep, hibernating or shutting down fully (not restarting) resets the fan controller on some laptops.");
        if ((clock - w.SlowSince.Value).TotalSeconds < 10 || clock < w.NextTry) return;
        w.Tries++;
        Log.Write($"watchdog: {fan.Name} at {rpm:F0} rpm for {fan.Percent}%{(normal > 0 ? $" (normally {normal})" : "")}: re-applying (try {w.Tries})");
        try
        {
            if (owner[fan] is IResumable r)
            {
                r.Resume();
                // That redid every fan of this backend: give them all time to climb back before judging them.
                foreach (var f in owner.Where(o => o.Value == owner[fan]).Select(o => o.Key))
                {
                    var wf = W(f, clock);
                    wf.Changed = clock;
                    wf.Grace = 60;
                    wf.SlowSince = null;
                    wf.BadSince = null;
                }
            }
            else owner[fan].Set(fan, fan.Percent);
        }
        catch (Exception ex) { Log.Write($"watchdog: {fan.Name}: {ex.Message}"); }
        w.Changed = clock;
        w.Grace = 60;
        w.SlowSince = null;
        w.BadSince = null;
        w.NextTry = clock.AddSeconds(Math.Min(600, 60 << Math.Min(w.Tries - 1, 4)));
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
