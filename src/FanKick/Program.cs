using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using FanKick.Core;
using FanKick.Hardware;

namespace FanKick;

static class Program
{
    public static bool Demo { get; private set; }
    public static bool Minimized { get; private set; }
    public static string? ScreenshotDir { get; private set; }

    [STAThread]
    static int Main(string[] args)
    {
        int g = Array.IndexOf(args, "--guard");
        if (g >= 0 && g + 1 < args.Length && int.TryParse(args[g + 1], out int pid)) return Guard.Run(pid);
        // Used by the Windows installer and uninstaller.
        int a = Array.IndexOf(args, "--set-autostart");
        if (a >= 0 && a + 1 < args.Length)
            try { Platform.Autostart = args[a + 1] == "on"; return 0; }
            catch (Exception ex) { Log.Write("autostart: " + ex.Message); return 1; }
        if (args.Contains("--probe-asus")) { if (OperatingSystem.IsWindows()) AsusAcpi.Probe(); return 0; }
        int s = Array.IndexOf(args, "--screenshots");
        if (s >= 0 && s + 1 < args.Length) ScreenshotDir = Path.GetFullPath(args[s + 1]);
        Demo = args.Contains("--demo") || ScreenshotDir != null;
        Minimized = args.Contains("--minimized") || args.Contains("--autostart");
        if (Demo) Log.Path = Path.Combine(Settings.Dir, "FanKick-demo.log");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("crash: " + e.ExceptionObject);

        // Windows: fans and most sensors need admin. At login, the Startup entry hands over to the elevated task (no
        // prompt). Otherwise ask once; if refused, carry on read-only.
        if (OperatingSystem.IsWindows() && !Demo && !Platform.IsAdmin && !args.Contains("--no-elevate"))
        {
            if (args.Contains("--autostart") && Platform.LaunchAutostartTask()) return 0;
            var relaunch = args.Where(a => a != "--autostart").ToList();
            if (Minimized && !relaunch.Contains("--minimized")) relaunch.Add("--minimized");
            if (Platform.RestartAsAdmin(string.Join(' ', relaunch.Append("--no-elevate")))) return 0;
        }

        using var single = new Mutex(true, Demo ? "FanKick.App.Demo" : "FanKick.App", out bool first);
        if (!first && args.Contains("--restart")) // the old copy is on its way out: wait for it
            try { first = single.WaitOne(TimeSpan.FromSeconds(15)); } catch (AbandonedMutexException) { first = true; }
        if (!first) { App.SignalShow(); return 0; }
        Log.Write($"FanKick {typeof(Program).Assembly.GetName().Version} starting{(Demo ? " (demo)" : "")}, {Environment.OSVersion}, admin={Platform.IsAdmin}");
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace()
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        return 0;
    }
}

// A second copy of FanKick that watches the first. If the app dies without a clean exit (crash, killed), the guard
// takes the fans: full speed, or back to the BIOS, as set in Settings > Safety. Not used on macOS, where the SMC
// already takes fans back when the controlling process exits.
static class Guard
{
    static string Marker(int pid) => Path.Combine(Settings.Dir, $"clean-exit-{pid}");
    static bool spawned;

    public static void Spawn()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--guard {Environment.ProcessId}") { UseShellExecute = false, CreateNoWindow = true });
            spawned = true;
        }
        catch (Exception ex) { Log.Write("guard: could not start: " + ex.Message); }
    }

    public static void MarkCleanExit()
    {
        if (spawned) try { File.WriteAllText(Marker(Environment.ProcessId), ""); } catch { }
    }

    public static int Run(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.WaitForExit(); }
        catch (ArgumentException) { } // already gone
        if (File.Exists(Marker(pid))) { File.Delete(Marker(pid)); return 0; }

        var settings = Settings.Load(Settings.DefaultPath);
        bool full = settings.OnCrash == FailSafe.FullSpeed;
        Log.Write($"guard: FanKick (pid {pid}) stopped without a clean exit, setting every fan to {(full ? "full speed" : "BIOS control")}");
        if (OperatingSystem.IsWindows())
        {
            AsusAcpi.WriteTwice = settings.AsusWriteTwice;
            if (settings.Modes.TryGetValue("ASUS mode", out var m)) AsusAcpi.StartMode = m;
        }
        foreach (var hw in Backends.Create(false, []))
            foreach (var fan in hw.Fans)
                try { if (full) hw.Set(fan, 100); else hw.Restore(fan); }
                catch (Exception ex) { Log.Write($"guard: {fan.Name}: {ex.Message}"); }
        // Exit without closing the backends: closing hands some Super I/O fans back to their defaults.
        return 0;
    }
}
