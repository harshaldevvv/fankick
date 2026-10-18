using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FanKick.Core;
using FanKick.Hardware;
using static FanKick.UI.Ui;

namespace FanKick.UI;

sealed class MainWindow : Window
{
    readonly App app;
    readonly TabControl tabs = new() { Padding = new Thickness(0) };
    readonly List<Action> refreshers = [];
    Curve? selected;

    Settings S => app.Settings;

    public MainWindow(App app)
    {
        this.app = app;
        Title = Program.Demo ? "FanKick (demo)" : "FanKick";
        Icon = App.Icon;
        Width = 1200; Height = 800; MinWidth = 900; MinHeight = 560;
        Background = Bg;
        Content = tabs;
        Rebuild();
        new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { if (IsVisible) foreach (var r in refreshers.ToList()) r(); }).Start();
        // Closing the window keeps FanKick running in the tray: the fans still need it.
        Closing += (_, e) => { if (!app.Quitting) { e.Cancel = true; Hide(); } };
    }

    public void Rebuild()
    {
        int sel = Math.Max(0, tabs.SelectedIndex);
        refreshers.Clear();
        tabs.Items.Clear();
        if (app.Engine == null) { tabs.Items.Add(Tab("FanKick", Paused())); return; }
        tabs.Items.Add(Tab("Dashboard", Dashboard()));
        tabs.Items.Add(Tab("Fans & curves", Fans()));
        tabs.Items.Add(Tab("Modes", ModesTab()));
        tabs.Items.Add(Tab("Stress test", StressTab()));
        tabs.Items.Add(Tab("Settings", SettingsTab()));
        tabs.SelectedIndex = Math.Min(sel, tabs.Items.Count - 1);
    }

    static TabItem Tab(string header, Control content) => new()
    {
        Header = header, FontSize = 15,
        Content = new ScrollViewer { Content = new Border { Padding = new Thickness(20, 12, 20, 24), Child = content } },
    };

    // ---------- shown instead of everything while other fan apps are running ----------
    Control Paused() => Col(14,
        Heading("Another fan or hardware app is running"),
        Txt($"Found: {string.Join(", ", app.Conflicts)}. Two apps driving the same fans fight each other, and two apps using the PawnIO driver at the same time can crash one of them (and leave fans uncontrolled)."),
        Txt("Close it, then press Start. FanKick has not touched any hardware yet."),
        Row(8, Btn("Start", () => { app.StartEngine(); app.ApplyOverlay(); }, true), Btn("Quit", app.Quit)));

    // ---------- Dashboard ----------
    Control Dashboard()
    {
        var e = app.Engine!;
        var root = Col(16);
        root.Children.Add(Banners(e));

        Sensor? Pick(SensorKind k, Func<Sensor, bool> f) => e.Sensors.FirstOrDefault(s => s.Kind == k && f(s));
        var cpuLoad = Pick(SensorKind.Load, s => s.Name == "CPU Total");
        var gpuLoad = Pick(SensorKind.Load, s => s.Name == "GPU Core");
        var power = Pick(SensorKind.Power, s => s.Name is "CPU Package" or "Package" or "System total") ?? Pick(SensorKind.Power, _ => true);
        var tiles = new UniformGrid { Columns = 4 };
        tiles.Children.Add(Tile("Hottest CPU/GPU", () => e.HottestSensor.Format(), () => TempBrush(e.HottestSensor.Value)));
        tiles.Children.Add(Tile("CPU load", () => cpuLoad?.Format() ?? "-"));
        tiles.Children.Add(Tile(power == null ? "Power" : $"{power.Name} power", () => power?.Format() ?? "-"));
        tiles.Children.Add(Tile("Fans", () =>
        {
            var rpm = e.Sensors.Where(s => s.Kind == SensorKind.Fan && !float.IsNaN(s.Value)).ToList();
            return rpm.Count == 0 ? $"{e.Fans.Count} controllable" : $"{rpm.Count} at {rpm.Average(s => s.Value):0} rpm avg";
        }));
        root.Children.Add(tiles);

        var chart = new Chart { Height = 200 };
        chart.Lines.Add(new("Hottest", () => e.History(Engine.Hottest), Series[1], " °C"));
        if (cpuLoad != null) chart.Lines.Add(new("CPU load", () => e.History(cpuLoad.Id), Series[0], "%"));
        if (gpuLoad != null) chart.Lines.Add(new("GPU load", () => e.History(gpuLoad.Id), Series[2], "%"));
        refreshers.Add(chart.InvalidateVisual);
        root.Children.Add(chart);

        var filter = new TextBox { Watermark = "Filter readings (e.g. fan, gpu, temp)", Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(Row(12, Heading($"Every reading ({e.Sensors.Count})"), filter));
        var cards = new WrapPanel();
        var rows = new List<(Control row, Sensor s)>();
        foreach (var g in e.Sensors.Where(s => s != e.HottestSensor).GroupBy(s => s.Group))
        {
            var list = Col(3, Txt(g.Key, 14, Fg, FontWeight.SemiBold));
            foreach (var s in g)
            {
                var value = Txt("", 13, Fg); value.HorizontalAlignment = HorizontalAlignment.Right;
                var name = Muted($"{s.Name}", 13);
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                row.Children.Add(name);
                Grid.SetColumn(value, 1);
                row.Children.Add(value);
                list.Children.Add(row);
                rows.Add((row, s));
                void Update() { value.Text = s.Format(); if (s.Kind == SensorKind.Temperature) value.Foreground = TempBrush(s.Value); }
                Update();
                refreshers.Add(Update);
            }
            cards.Children.Add(new Border { Width = 360, Margin = new Thickness(0, 0, 12, 12), Child = Box(list, new Thickness(14, 10)) });
        }
        filter.TextChanged += (_, _) =>
        {
            var q = filter.Text?.Trim() ?? "";
            foreach (var (row, s) in rows)
                row.IsVisible = q == "" || $"{s.Group} {s.Name} {s.Kind}".Contains(q, StringComparison.OrdinalIgnoreCase);
        };
        root.Children.Add(cards);
        return root;
    }

    Control Banners(Core.Engine e)
    {
        var box = Col(8);
        string last = "\0";
        void Update()
        {
            string key = string.Join("|", e.Notices) + "|" + e.Alert;
            if (key == last) return;
            last = key;
            box.Children.Clear();
            foreach (var n in e.Notices) box.Children.Add(Banner(n, false));
            if (e.Alert != null) box.Children.Add(Banner(e.Alert, true));
            if (!Platform.IsAdmin && !Program.Demo)
                box.Children.Add(Btn("Restart as administrator", () => { if (Platform.RestartAsAdmin("--no-elevate")) app.Quit(); }));
        }
        Update();
        refreshers.Add(Update);
        return box;
    }

    Control Tile(string label, Func<string> value, Func<IBrush>? color = null)
    {
        var v = Txt("", 26, Fg, FontWeight.SemiBold);
        void Update() { v.Text = value(); if (color != null) v.Foreground = color(); }
        Update();
        refreshers.Add(Update);
        return new Border { Margin = new Thickness(0, 0, 12, 0), Child = Box(Col(4, Muted(label), v), new Thickness(16, 12)) };
    }

    // ---------- Fans & curves ----------
    Control Fans()
    {
        var e = app.Engine!;
        var root = Col(16);
        var presetRow = Row(8);
        foreach (var p in Presets.Names.Append("BIOS")) presetRow.Children.Add(Btn(p, () => app.ApplyPreset(p)));
        presetRow.Children.Add(new Border { Width = 24 });
        presetRow.Children.Add(Btn("Import...", Import));
        presetRow.Children.Add(Btn("Export...", Export));
        root.Children.Add(Box(Col(8, Heading("Presets"),
            Muted("Point every fan at one curve (all on the hottest CPU/GPU temperature). BIOS hands every fan back to the firmware. Import and export save the curves and fan settings to a file."),
            presetRow)));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("440,*") };
        var fanList = Col(10);
        if (e.Fans.Count == 0) fanList.Children.Add(Box(Txt("No controllable fans found. See the banner on the Dashboard for why.")));
        foreach (var f in e.Fans) fanList.Children.Add(FanCard(f));
        grid.Children.Add(fanList);
        var editor = CurvePanel();
        editor.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        root.Children.Add(grid);
        return root;
    }

    static readonly string[] ModeNames = ["BIOS (auto)", "Fixed", "Curve"];

    Control FanCard(FanControl f)
    {
        var e = app.Engine!;
        var fs = S.Fan(f.Id);
        var live = Muted("");
        void Update()
        {
            var rpm = f.RpmSensorId is { } id ? e.Read(id) : float.NaN;
            live.Text = $"{(float.IsNaN(rpm) ? "" : $"{rpm:0} rpm, ")}{(f.Percent < 0 ? "BIOS in control" : $"{f.Percent}%")}";
        }
        Update();
        refreshers.Add(Update);

        var mode = new ComboBox { ItemsSource = ModeNames, SelectedIndex = (int)fs.Mode, Width = 150 };
        var fixedSlider = new Slider { Minimum = 0, Maximum = 100, Value = fs.Fixed, Width = 200, TickFrequency = 5, IsSnapToTickEnabled = true };
        var fixedText = Txt($"{fs.Fixed}%");
        var fixedRow = Row(8, fixedSlider, fixedText);
        var names = S.Curves.Select(c => c.Name).ToList();
        var curve = new ComboBox { ItemsSource = names, SelectedItem = fs.Curve, Width = 200, PlaceholderText = "Pick a curve" };
        void Show() { fixedRow.IsVisible = fs.Mode == FanMode.Fixed; curve.IsVisible = fs.Mode == FanMode.Curve; }
        Show();
        mode.SelectionChanged += (_, _) =>
        {
            lock (S)
            {
                fs.Mode = (FanMode)Math.Max(0, mode.SelectedIndex);
                if (fs.Mode == FanMode.Curve && fs.Curve == null) { fs.Curve = names.FirstOrDefault(); curve.SelectedItem = fs.Curve; }
            }
            Show(); app.Save();
        };
        fixedSlider.PropertyChanged += (_, a) => { if (a.Property == RangeBase.ValueProperty) { fs.Fixed = (int)fixedSlider.Value; fixedText.Text = $"{fs.Fixed}%"; app.Save(); } };
        curve.SelectionChanged += (_, _) => { lock (S) fs.Curve = curve.SelectedItem as string; app.Save(); };

        var limits = new Expander
        {
            Header = "Limits and timing",
            Content = Col(6,
                Labeled("Minimum %", Number(fs.Min, 0, 100, v => { fs.Min = (int)v; app.Save(); })),
                Labeled("Maximum %", Number(fs.Max, 0, 100, v => { fs.Max = (int)v; app.Save(); })),
                Labeled("Step up (%/s)", Number(fs.StepUp, 0, 100, v => { fs.StepUp = (float)v; app.Save(); }), "How fast it may speed up. 0 = at once."),
                Labeled("Step down (%/s)", Number(fs.StepDown, 0, 100, v => { fs.StepDown = (float)v; app.Save(); }), "How fast it may slow down.")),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = CardHover,
        };
        return Box(Col(8,
            Row(8, Heading(f.Name), Muted(f.Group)),
            live,
            Row(8, mode, curve, fixedRow),
            limits), new Thickness(14, 12));
    }

    Control CurvePanel()
    {
        var e = app.Engine!;
        selected ??= S.Curves.FirstOrDefault(c => S.Fans.Values.Any(f => f.Mode == FanMode.Curve && f.Curve == c.Name)) ?? S.Curves.FirstOrDefault();
        var host = Col(12);
        var picker = new ComboBox { ItemsSource = S.Curves.Select(c => c.Name).ToList(), SelectedItem = selected?.Name, Width = 220 };
        picker.SelectionChanged += (_, _) => { selected = S.Find(picker.SelectedItem as string); Rebuild(); };
        void Add(Curve c)
        {
            int n = 1;
            string baseName = c.Name;
            while (S.Find(c.Name) != null) c.Name = $"{baseName} {++n}";
            lock (S) S.Curves.Add(c);
            selected = c; app.Save(); Rebuild();
        }
        var toolbar = Row(8, picker,
            Btn("New graph", () => Add(new GraphCurve { Name = "My curve", Points = [new(40, 25), new(60, 40), new(75, 70), new(85, 100)] })),
            Btn("New mix", () => Add(new MixCurve { Name = "Mix" })),
            Btn("New flat", () => Add(new FlatCurve { Name = "Flat" })),
            Btn("Delete", () =>
            {
                if (selected == null) return;
                lock (S)
                {
                    S.Curves.Remove(selected);
                    foreach (var fs in S.Fans.Values.Where(f => f.Curve == selected.Name)) { fs.Curve = null; fs.Mode = FanMode.Auto; }
                }
                selected = S.Curves.FirstOrDefault(); app.Save(); Rebuild();
            }));
        host.Children.Add(Heading("Curve editor"));
        host.Children.Add(toolbar);
        if (selected == null) { host.Children.Add(Muted("No curves. Add one above.")); return Box(host); }

        var name = new TextBox { Text = selected.Name, Width = 220 };
        name.LostFocus += (_, _) =>
        {
            var n = name.Text?.Trim();
            if (string.IsNullOrEmpty(n) || n == selected.Name || S.Find(n) != null) { name.Text = selected.Name; return; }
            lock (S)
            {
                foreach (var fs in S.Fans.Values.Where(f => f.Curve == selected.Name)) fs.Curve = n;
                foreach (var m in S.Curves.OfType<MixCurve>()) m.Curves = m.Curves.Select(c => c == selected.Name ? n : c).ToList();
                selected.Name = n;
            }
            app.Save(); Rebuild();
        };
        host.Children.Add(Labeled("Name", name));
        int users = S.Fans.Values.Count(f => f.Mode == FanMode.Curve && f.Curve == selected.Name);
        host.Children.Add(Muted(users == 0 ? "No fan uses this curve yet: pick it in a fan's Curve box." : $"Used by {users} fan{(users == 1 ? "" : "s")}."));

        switch (selected)
        {
            case GraphCurve g:
                var temps = e.Sensors.Where(s => s.Kind == SensorKind.Temperature).ToList();
                var sensor = new ComboBox
                {
                    ItemsSource = temps.Select(s => s == e.HottestSensor ? "Hottest CPU/GPU" : $"{s.Group}: {s.Name}").ToList(),
                    SelectedIndex = temps.FindIndex(s => s.Id == g.Sensor), Width = 320,
                };
                sensor.SelectionChanged += (_, _) => { if (sensor.SelectedIndex >= 0) { lock (S) g.Sensor = temps[sensor.SelectedIndex].Id; app.Save(); } };
                host.Children.Add(Labeled("Temperature sensor", sensor));
                host.Children.Add(Labeled("Hysteresis (°C)", Number(g.Hysteresis, 0, 20, v => { g.Hysteresis = (float)v; app.Save(); }, 0.5, "0.0"),
                    "Rises are followed at once; falls wait until it is this much cooler."));
                var ed = new CurveEditor { Curve = g, Gate = S, Height = 340, Live = () => e.Read(g.Sensor) };
                ed.Changed += app.Save;
                refreshers.Add(ed.InvalidateVisual);
                host.Children.Add(ed);
                host.Children.Add(Muted("Drag points to change them. Double-click to add a point, right-click a point to remove it."));
                break;
            case MixCurve m:
                var fn = new ComboBox { ItemsSource = Enum.GetNames<MixFunction>(), SelectedIndex = (int)m.Function, Width = 150 };
                fn.SelectionChanged += (_, _) => { m.Function = (MixFunction)fn.SelectedIndex; app.Save(); };
                host.Children.Add(Labeled("Combine with", fn, "Max: the fan follows whichever curve wants more."));
                var checks = Col(4);
                foreach (var c in S.Curves.Where(c => c != m))
                {
                    var cb = new CheckBox { Content = c.Name, IsChecked = m.Curves.Contains(c.Name) };
                    cb.IsCheckedChanged += (_, _) =>
                    {
                        lock (S) m.Curves = cb.IsChecked == true ? [.. m.Curves.Append(c.Name).Distinct()] : [.. m.Curves.Where(x => x != c.Name)];
                        app.Save();
                    };
                    checks.Children.Add(cb);
                }
                host.Children.Add(Labeled("Curves", checks, "If any of them loses its sensor, the mix fails safe."));
                break;
            case FlatCurve f:
                var sl = new Slider { Minimum = 0, Maximum = 100, Value = f.Percent, Width = 260 };
                var t = Txt($"{f.Percent:0}%");
                sl.PropertyChanged += (_, a) => { if (a.Property == RangeBase.ValueProperty) { f.Percent = (float)Math.Round(sl.Value); t.Text = $"{f.Percent:0}%"; app.Save(); } };
                host.Children.Add(Labeled("Speed", Row(8, sl, t)));
                break;
        }
        return Box(host);
    }

    async void Import()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import FanKick profile", FileTypeFilter = [new("FanKick profile") { Patterns = ["*.json"] }] });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        var imported = Settings.Load(path);
        lock (S)
        {
            S.Curves = imported.Curves;
            foreach (var (id, fs) in imported.Fans) S.Fans[id] = fs; // fans this machine doesn't have are kept, harmlessly
        }
        selected = null; app.Save(); Rebuild();
    }

    async void Export()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export FanKick profile", SuggestedFileName = "fankick-profile.json", DefaultExtension = "json" });
        if (file?.TryGetLocalPath() is not { } path) return;
        lock (S) S.Save(path);
    }

    // ---------- Modes ----------
    Control ModesTab()
    {
        var root = Col(16);
        root.Children.Add(Muted("Performance modes this OS and this machine offer. Fan curves stay in FanKick's hands whatever the mode."));
        if (app.Modes.Count == 0) root.Children.Add(Box(Txt("No performance modes found on this machine.")));
        foreach (var g in app.Modes)
        {
            var buttons = Row(8);
            var status = Muted("");
            var radios = g.Options.Select(o => new RadioButton { Content = o, GroupName = "mode:" + g.Title }).ToList();
            void Show(string? cur) { foreach (var r in radios) r.IsChecked = (string?)r.Content == cur; }
            foreach (var r in radios)
            {
                buttons.Children.Add(r);
                r.IsCheckedChanged += async (_, _) =>
                {
                    if (r.IsChecked != true || (string?)r.Content == g.Current) return;
                    string opt = (string)r.Content!;
                    status.Text = "Applying...";
                    try { await Task.Run(() => g.Set(opt)); status.Text = ""; }
                    catch (Exception ex) { status.Text = "Couldn't change it: " + ex.Message; }
                    Show(await Task.Run(() => g.Current));
                };
            }
            _ = Task.Run(() => g.Current).ContinueWith(t => Dispatcher.UIThread.Post(() => Show(t.Result)));
            var col = Col(8, Heading(g.Title));
            if (g.Note != null) col.Children.Add(Muted(g.Note));
            col.Children.Add(buttons);
            col.Children.Add(status);
            root.Children.Add(Box(col));
        }
        return root;
    }

    // ---------- Stress test ----------
    static readonly (string name, TimeSpan time)[] Durations = [("1 min", TimeSpan.FromMinutes(1)), ("5 min", TimeSpan.FromMinutes(5)), ("10 min", TimeSpan.FromMinutes(10)), ("30 min", TimeSpan.FromMinutes(30)), ("Until stopped", TimeSpan.Zero)];

    Control StressTab()
    {
        var e = app.Engine!;
        var st = app.Stress;
        var cpu = new CheckBox { Content = "CPU (every core)", IsChecked = true };
        var gpu = new CheckBox { Content = "GPU (every GPU, via OpenCL)", IsChecked = Program.ScreenshotDir != null };
        var dur = new ComboBox { ItemsSource = Durations.Select(d => d.name).ToList(), SelectedIndex = 1, Width = 150 };
        int cutoff = Math.Min(90, S.PanicTemp - 3);
        var cut = Number(cutoff, 50, 105, v => cutoff = (int)v);
        var status = Txt(st.Status);
        var go = new Button { Content = "Start", Padding = new Thickness(18, 6), Background = Accent, Foreground = Brush("#0b1220") };
        go.Click += (_, _) =>
        {
            if (st.Running) st.Stop();
            else st.Start(e, cpu.IsChecked == true, gpu.IsChecked == true, Durations[Math.Max(0, dur.SelectedIndex)].time, cutoff, Program.Demo);
        };
        refreshers.Add(() => { status.Text = st.Status; go.Content = st.Running ? "Stop" : "Start"; });

        Sensor? Pick(SensorKind k, string n) => e.Sensors.FirstOrDefault(s => s.Kind == k && s.Name == n);
        var chart = new Chart { Height = 300 };
        chart.Lines.Add(new("Hottest", () => e.History(Engine.Hottest), Series[1], " °C"));
        if (Pick(SensorKind.Load, "CPU Total") is { } cl) chart.Lines.Add(new("CPU load", () => e.History(cl.Id), Series[0], "%"));
        if (Pick(SensorKind.Load, "GPU Core") is { } gl) chart.Lines.Add(new("GPU load", () => e.History(gl.Id), Series[2], "%"));
        var pw = e.Sensors.FirstOrDefault(s => s.Kind == SensorKind.Power && s.Name is "CPU Package" or "Package");
        if (pw != null) chart.Lines.Add(new("CPU power", () => e.History(pw.Id), Series[3], " W"));
        refreshers.Add(chart.InvalidateVisual);

        return Col(16,
            Box(Col(10,
                Heading("CPU and GPU load test"),
                Muted("Loads the chips fully to check cooling and fan curves. The load stops at once if the hottest CPU/GPU stays at the cutoff for 3 seconds, or at the panic temperature. Fans follow your curves during the test."),
                Row(16, cpu, gpu),
                Labeled("Duration", dur),
                Labeled("Safety cutoff (°C)", cut),
                Row(12, go, status))),
            chart);
    }

    // ---------- Settings ----------
    static readonly string[] FailNames = ["Full speed", "BIOS control"];

    Control SettingsTab()
    {
        var e = app.Engine!;
        ComboBox Fail(FailSafe v, Action<FailSafe> set)
        {
            var c = new ComboBox { ItemsSource = FailNames, SelectedIndex = (int)v, Width = 180 };
            c.SelectionChanged += (_, _) => { set((FailSafe)c.SelectedIndex); app.Save(); };
            return c;
        }
        var safety = Col(8, Heading("Safety"),
            Labeled("Panic temperature (°C)", Number(S.PanicTemp, 60, 110, v => { S.PanicTemp = (int)v; app.Save(); }), "Any CPU/GPU core at this: every fan to 100% until 10 °C under it."),
            Labeled("If a curve's sensor is lost", Fail(S.OnSensorLost, v => S.OnSensorLost = v)),
            Labeled("If FanKick crashes", Fail(S.OnCrash, v => S.OnCrash = v), OperatingSystem.IsMacOS() ? "On a Mac the SMC takes the fans back by itself." : "A small guard process applies this if FanKick dies."),
            Labeled("When FanKick quits", Fail(S.OnExit, v => S.OnExit = v)),
            Labeled("Update every (ms)", Number(S.IntervalMs, 250, 5000, v => { S.IntervalMs = (int)v; app.Save(); }, 250)));

        var o = S.Overlay;
        var enabled = new ToggleSwitch { IsChecked = o.Enabled, OnContent = "On", OffContent = "Off" };
        enabled.IsCheckedChanged += (_, _) => { if (enabled.IsChecked != o.Enabled) app.ToggleOverlay(); };
        var corner = new ComboBox { ItemsSource = Enum.GetNames<Corner>(), SelectedIndex = (int)o.Corner, Width = 160 };
        corner.SelectionChanged += (_, _) => { o.Corner = (Corner)corner.SelectedIndex; app.Save(); app.ApplyOverlay(); };
        var opacity = new Slider { Minimum = 0.2, Maximum = 1, Value = o.Opacity, Width = 200 };
        opacity.PropertyChanged += (_, a) => { if (a.Property == RangeBase.ValueProperty) { o.Opacity = (float)opacity.Value; app.Save(); } };
        var hotkey = new TextBox { Text = o.Hotkey, Width = 180 };
        var hotkeyStatus = Muted(app.HotkeyError ?? "");
        var metrics = Col(2);
        var picked = OverlayWindow.Picked(e, o).Select(s => s.Id).ToHashSet();
        foreach (var s in e.Sensors.Where(s => s.Kind is SensorKind.Temperature or SensorKind.Load or SensorKind.Power or SensorKind.Fan or SensorKind.Clock))
        {
            var cb = new CheckBox { Content = s == e.HottestSensor ? "Hottest CPU/GPU" : $"{s.Group}: {s.Name} ({s.Unit})", IsChecked = picked.Contains(s.Id) };
            cb.IsCheckedChanged += (_, _) =>
            {
                if (o.Sensors.Count <= 1) o.Sensors = [.. picked]; // first edit: start from what is shown
                if (cb.IsChecked == true) { if (!o.Sensors.Contains(s.Id)) o.Sensors.Add(s.Id); } else o.Sensors.Remove(s.Id);
                app.Save(); app.ApplyOverlay();
            };
            metrics.Children.Add(cb);
        }
        var overlay = Col(8, Heading("Overlay"),
            Muted("A small always-on-top readout. Off by default."),
            Labeled("Show overlay", enabled),
            Labeled("Corner", corner),
            Labeled("Offset X / Y", Row(8, Number(o.OffsetX, 0, 2000, v => { o.OffsetX = (int)v; app.Save(); app.ApplyOverlay(); }), Number(o.OffsetY, 0, 2000, v => { o.OffsetY = (int)v; app.Save(); app.ApplyOverlay(); }))),
            Labeled("Background opacity", opacity),
            Labeled("Hotkey", Row(8, hotkey, Btn("Apply", () => { o.Hotkey = hotkey.Text ?? ""; app.Save(); app.ApplyHotkey(); hotkeyStatus.Text = app.HotkeyError ?? "Saved."; }), hotkeyStatus),
                OperatingSystem.IsWindows() ? "Shows and hides the overlay from anywhere. Empty = none." : "Global hotkeys are Windows only for now; use the tray menu."),
            Labeled("Readings", new ScrollViewer { Content = metrics, MaxHeight = 260 }));

        var system = Col(8, Heading("System"));
        if (Platform.CanAutostart && !Program.Demo)
        {
            var auto = new ToggleSwitch { IsChecked = Platform.Autostart, OnContent = "On", OffContent = "Off" };
            var autoStatus = Muted("");
            auto.IsCheckedChanged += (_, _) => { try { Platform.Autostart = auto.IsChecked == true; autoStatus.Text = ""; } catch (Exception ex) { autoStatus.Text = ex.Message; } };
            system.Children.Add(Labeled("Start at login", Row(8, auto, autoStatus), "Runs as a scheduled task with admin rights, so there is no prompt."));
        }
        if (!Platform.IsAdmin && !Program.Demo)
            system.Children.Add(Labeled("Permissions", Btn("Restart as administrator", () => { if (Platform.RestartAsAdmin("--no-elevate")) app.Quit(); })));
        if (OperatingSystem.IsWindows() && e.Fans.Any(f => f.Group == "ASUS"))
        {
            var twice = new CheckBox { Content = "Each fan curve write toggles the custom curve (some ROG BIOS versions)", IsChecked = S.AsusWriteTwice };
            twice.IsCheckedChanged += (_, _) => { S.AsusWriteTwice = twice.IsChecked == true; if (OperatingSystem.IsWindows()) AsusAcpi.WriteTwice = S.AsusWriteTwice; app.Save(); };
            system.Children.Add(Labeled("ASUS", twice, "Turn on if ASUS fans ignore every other change."));
        }
        system.Children.Add(Labeled("Files", Row(8,
            Btn("Open settings folder", () => Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(Settings.Dir))),
            Muted(Settings.Dir))));
        system.Children.Add(Labeled("Version", Txt($"FanKick {typeof(App).Assembly.GetName().Version?.ToString(3)}  ·  github.com/harshaldevvv/fankick")));
        system.Children.Add(Btn("Quit FanKick", app.Quit));

        return Col(16, Box(safety), Box(overlay), Box(system));
    }

    // ---------- `--screenshots dir`: each tab and the overlay to PNG, for the README ----------
    public async Task CaptureScreenshots(string dir, OverlayWindow overlay)
    {
        Directory.CreateDirectory(dir);
        await Task.Delay(1500);
        string[] names = ["dashboard", "curves", "modes", "stress", "settings"];
        for (int i = 0; i < tabs.Items.Count && i < names.Length; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => tabs.SelectedIndex = i);
            if (i == 3) await Dispatcher.UIThread.InvokeAsync(() => app.Stress.Start(app.Engine!, true, true, TimeSpan.FromMinutes(5), 90, true));
            await Task.Delay(i == 3 ? 6000 : 1200);
            await Dispatcher.UIThread.InvokeAsync(() => Save(this, Path.Combine(dir, names[i] + ".png")));
        }
        app.Stress.Stop();
        await Dispatcher.UIThread.InvokeAsync(() => { app.Settings.Overlay.Enabled = true; app.ApplyOverlay(); });
        await Task.Delay(1500);
        await Dispatcher.UIThread.InvokeAsync(() => Save(overlay, Path.Combine(dir, "overlay.png")));
    }

    static void Save(TopLevel w, string path)
    {
        var size = new PixelSize((int)(w.ClientSize.Width * w.RenderScaling), (int)(w.ClientSize.Height * w.RenderScaling));
        using var bmp = new RenderTargetBitmap(size, new Vector(96 * w.RenderScaling, 96 * w.RenderScaling));
        bmp.Render(w);
        bmp.Save(path);
    }
}
