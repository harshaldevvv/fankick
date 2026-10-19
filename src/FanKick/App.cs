using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using FanKick.Core;
using FanKick.Hardware;
using FanKick.Modes;
using FanKick.Stress;
using FanKick.UI;

namespace FanKick;

public sealed class App : Application
{
    public Settings Settings { get; private set; } = new();
    public Core.Engine? Engine { get; private set; }
    public List<ModeGroup> Modes { get; } = [];
    public List<string> Conflicts { get; private set; } = [];
    public StressTest Stress { get; } = new();
    // Settings found from other fan apps, offered once on first run and again from Settings.
    public List<ImportOffer> Offers { get; private set; } = [];
    public bool ShowImport { get; set; }
    bool firstRun;
    public bool Quitting { get; private set; }
    public string? HotkeyError { get; private set; }

    MainWindow? window;
    OverlayWindow? overlay;
    DispatcherTimer? saveTimer;
    string SettingsPath => Program.Demo ? Path.Combine(Settings.Dir, "settings-demo.json") : Settings.DefaultPath;
    static string ShowName => Program.Demo ? "FanKick.App.Demo.Show" : "FanKick.App.Show";

    public static WindowIcon Icon => new(AssetLoader.Open(new Uri("avares://FanKick/Assets/fankick.png")));

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var life = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        firstRun = !File.Exists(SettingsPath);
        Settings = Settings.Load(SettingsPath);
        Log.Write($"settings: {SettingsPath} ({(File.Exists(SettingsPath) ? "found" : "new")}), {Settings.Fans.Count} fan entries, {Settings.Curves.Count} curves");
        if (OperatingSystem.IsWindows()) AsusAcpi.WriteTwice = Settings.AsusWriteTwice;
        Modes.AddRange(Program.Demo ? DemoHardware.Modes() : PerfModes.Detect());
        Conflicts = Program.Demo ? [] : Backends.Conflicts();
        if (Conflicts.Count == 0) StartEngine();
        else Log.Write("waiting: other fan/hardware apps running: " + string.Join(", ", Conflicts));

        window = new MainWindow(this);
        overlay = new OverlayWindow(this);
        BuildTray();
        ApplyOverlay();
        ApplyHotkey();
        ListenForShow();
        WatchSettingsFile();
        life.ShutdownRequested += (_, _) => Shutdown(); // logoff / OS shutdown
        if (!Program.Minimized) window.Show();
        if (Program.ScreenshotDir is { } dir) _ = window.CaptureScreenshots(dir, overlay).ContinueWith(_ => Dispatcher.UIThread.Post(Quit));
        base.OnFrameworkInitializationCompleted();
    }

    public void StartEngine()
    {
        if (Engine != null) return;
        if (OperatingSystem.IsWindows() && Settings.Modes.TryGetValue("ASUS mode", out var asusMode)) AsusAcpi.StartMode = asusMode;
        var hw = Backends.Create(Program.Demo, Modes);
        Engine = new Core.Engine(hw, Settings);
        Engine.Learned += () => Dispatcher.UIThread.Post(Save);
        Engine.Updated += Protect;
        if (Program.Demo)
        {
            if (!File.Exists(SettingsPath)) foreach (var fs in Settings.Fans.Values) { fs.Mode = FanMode.Curve; fs.Curve = "Balanced"; }
            for (int i = 0; i < 240; i++) { Engine.Refresh(); Engine.Tick(1); } // fill the graphs
        }
        Engine.Start();
        Log.Write($"engine: {hw.Count} backend(s), {Engine.Sensors.Count} sensors, {Engine.Fans.Count} fans: " + string.Join(", ", Engine.Fans.Select(f => f.Name)));
        if (!Program.Demo && Engine.Fans.Count > 0 && !OperatingSystem.IsMacOS()) Guard.Spawn();
        if (!Program.Demo)
        {
            Offers = Importers.Find(Engine);
            ShowImport = firstRun && !Settings.ImportOffered && Offers.Count > 0; // upgrades don't get asked
            if (Offers.Count > 0) Log.Write("found settings from: " + string.Join(", ", Offers.Select(o => o.Source)));
        }
        // Modes picked in FanKick are set again at every start (some firmware, like ASUS's charge limit, forgets them).
        var saved = Settings.Modes.ToList();
        _ = Task.Run(() =>
        {
            foreach (var (title, option) in saved)
                if (Modes.FirstOrDefault(g => g.Title == title) is { } g && g.Options.Contains(option) && g.Current != option)
                    try { g.Set(option); } catch (Exception ex) { Log.Write($"{title}: {ex.Message}"); }
        });
        window?.Rebuild();
        Conflicts = [];
    }

    // CPU boost off while the engine asks for it (CPU temperature limit, CPU temperature lost, fans not following),
    // and back to what it was afterwards. Where the OS has no boost switch (macOS) the engine's full-speed fans remain.
    bool protecting;
    string? boostBefore;
    void Protect()
    {
        bool want = Engine?.ProtectCpu == true;
        if (want == protecting) return;
        protecting = want;
        var g = Modes.FirstOrDefault(m => m.Title == "CPU boost");
        if (g == null) return;
        _ = Task.Run(() =>
        {
            try
            {
                if (want) { boostBefore = g.Current; if (boostBefore != "Off") g.Set("Off"); }
                else if (boostBefore is { } b && b != "Off" && g.Options.Contains(b)) g.Set(b);
            }
            catch (Exception ex) { Log.Write("CPU boost: " + ex.Message); }
        });
    }

    // Settings changes are saved a second after the last edit.
    public void Save()
    {
        saveTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            saveTimer!.Stop();
            try { lock (Settings) Settings.Save(SettingsPath); ownSave = DateTime.UtcNow; } catch (Exception ex) { Log.Write("save: " + ex.Message); }
        });
        saveTimer.Stop();
        saveTimer.Start();
    }

    // settings.json edited outside FanKick (by hand, or by another tool): load it, a moment after the last change.
    DateTime ownSave;
    DispatcherTimer? reloadTimer;
    FileSystemWatcher? watcher; // a field: a watcher only held by a local is garbage-collected and goes quiet
    void WatchSettingsFile()
    {
        try
        {
            var w = watcher = new FileSystemWatcher(Settings.Dir, Path.GetFileName(SettingsPath)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler on = (_, _) => Dispatcher.UIThread.Post(() =>
            {
                reloadTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) => { reloadTimer!.Stop(); Reload(); });
                reloadTimer.Stop();
                reloadTimer.Start();
            });
            w.Changed += on;
            w.Created += on;
            w.Renamed += (s, e) => on(s, e);
            w.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Write("can't watch the settings file: " + ex.Message); }
    }

    void Reload()
    {
        if ((DateTime.UtcNow - ownSave).TotalSeconds < 3 || saveTimer?.IsEnabled == true) return; // our own save
        if (Settings.TryLoad(SettingsPath) is not { } fresh) return;
        lock (Settings) Settings.CopyFrom(fresh);
        if (OperatingSystem.IsWindows()) AsusAcpi.WriteTwice = Settings.AsusWriteTwice;
        Log.Write($"settings reloaded (changed outside FanKick): CPU limit {Settings.CpuLimit} °C, panic {Settings.PanicTemp} °C");
        ApplyOverlay();
        window?.Rebuild();
    }

    // The user's answer to the import offer: another app's settings, or null to keep the BIOS in control.
    public void ApplyImport(ImportOffer? offer)
    {
        lock (Settings)
        {
            offer?.Apply(Settings);
            Settings.ImportOffered = true;
        }
        ShowImport = false;
        Log.Write(offer == null ? "import offer: kept BIOS control" : $"imported settings from {offer.Source}");
        // Modes it brought (ASUS mode, charge limit) take effect now, not only at the next start.
        var modes = Settings.Modes.ToList();
        _ = Task.Run(() =>
        {
            foreach (var (title, option) in modes)
                if (Modes.FirstOrDefault(g => g.Title == title) is { } g && g.Options.Contains(option) && g.Current != option)
                    try { g.Set(option); } catch (Exception ex) { Log.Write($"{title}: {ex.Message}"); }
        });
        Save();
        window?.Rebuild();
    }

    public void ApplyPreset(string name)
    {
        lock (Settings)
        {
            if (name != "BIOS" && Settings.Find(name) == null) Settings.Curves.Add(Presets.Build(name));
            foreach (var fs in Settings.Fans.Values)
            {
                fs.Mode = name == "BIOS" ? FanMode.Auto : FanMode.Curve;
                if (name != "BIOS") fs.Curve = name;
            }
        }
        Log.Write("preset: " + name);
        Save();
        window?.Rebuild();
    }

    public void ApplyOverlay()
    {
        if (overlay == null) return;
        if (Settings.Overlay.Enabled && Engine != null) { overlay.Show(); overlay.Refresh(); }
        else overlay.Hide();
    }

    public void ToggleOverlay()
    {
        Settings.Overlay.Enabled = !Settings.Overlay.Enabled;
        ApplyOverlay();
        Save();
        window?.Rebuild();
    }

    public void ApplyHotkey() =>
        HotkeyError = Platform.SetHotkey(Settings.Overlay.Hotkey, () => Dispatcher.UIThread.Post(ToggleOverlay));

    public void ShowWindow()
    {
        if (window == null) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    void BuildTray()
    {
        var menu = new NativeMenu();
        void Item(NativeMenu m, string text, Action a) { var i = new NativeMenuItem(text); i.Click += (_, _) => a(); m.Add(i); }
        Item(menu, "Open FanKick", ShowWindow);
        Item(menu, "Overlay on/off", ToggleOverlay);
        var presets = new NativeMenu();
        foreach (var p in Presets.Names.Append("BIOS")) Item(presets, p, () => ApplyPreset(p));
        menu.Add(new NativeMenuItem("Fan preset") { Menu = presets });
        menu.Add(new NativeMenuItemSeparator());
        Item(menu, "Quit", Quit);
        var tray = new TrayIcon { Icon = Icon, ToolTipText = "FanKick", Menu = menu };
        tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, [tray]);
    }

    // A second launch asks this one to show its window (Windows; elsewhere the second launch just exits).
    void ListenForShow()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName);
        new Thread(() => { while (ev.WaitOne()) Dispatcher.UIThread.Post(ShowWindow); }) { IsBackground = true }.Start();
    }

    public static void SignalShow()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { EventWaitHandle.OpenExisting(ShowName).Set(); } catch { }
    }

    public void Quit()
    {
        Quitting = true;
        Shutdown();
        ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Shutdown();
    }

    bool shutDown;
    void Shutdown()
    {
        if (shutDown) return;
        shutDown = true;
        Stress.Stop();
        try { lock (Settings) Settings.Save(SettingsPath); } catch { }
        var e = Engine;
        Engine = null;
        e?.Dispose(); // fans back to the BIOS (or full speed), as set in Settings > Safety
        Guard.MarkCleanExit();
        Log.Write("clean exit");
    }
}
