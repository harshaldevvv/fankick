using System.Diagnostics;
using System.Runtime.InteropServices;
using FanKick.Core;

namespace FanKick;

// Small OS-specific jobs: admin relaunch, start at login, the overlay hotkey.
static class Platform
{
    public static bool IsAdmin => Environment.IsPrivilegedProcess;
    static string Exe => Environment.ProcessPath ?? "FanKick";

    // Relaunch with admin/root rights. Returns false if the user said no or it isn't possible.
    public static bool RestartAsAdmin(string args)
    {
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows()) psi = new ProcessStartInfo(Exe, args) { UseShellExecute = true, Verb = "runas" };
            else if (OperatingSystem.IsMacOS())
            {
                string cmd = $"'{Exe}' {args} > /dev/null 2>&1 &";
                psi = new ProcessStartInfo("/usr/bin/osascript", ["-e", $"do shell script \"{cmd.Replace("\"", "\\\"")}\" with administrator privileges"]);
            }
            else
            {
                // pkexec drops the environment; pass what a GUI app needs to find the display.
                var env = new[] { "DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS" }
                    .Select(k => (k, v: Environment.GetEnvironmentVariable(k))).Where(x => x.v != null).Select(x => $"{x.k}={x.v}");
                psi = new ProcessStartInfo("pkexec", ["env", .. env, Exe, .. args.Split(' ', StringSplitOptions.RemoveEmptyEntries)]);
            }
            var p = Process.Start(psi);
            if (p == null) return false;
            if (OperatingSystem.IsMacOS()) { p.WaitForExit(); return p.ExitCode == 0; }
            return true;
        }
        catch (Exception ex) { Log.Write("restart as admin: " + ex.Message); return false; }
    }

    // ---- start at login (Windows) ----
    // A Run entry, so it shows in Task Manager > Startup apps (where the user can also turn it off), plus an on-demand
    // scheduled task with admin rights that the Run entry starts: fan access needs admin, and this way there is no UAC
    // prompt at every login. The task has no trigger of its own, so turning the entry off in Task Manager really stops it.
    public const string TaskName = "FanKick Autostart";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunValue = "FanKick";
    public static bool CanAutostart => OperatingSystem.IsWindows();

    public static bool Autostart
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) is string v && v.Contains(Exe, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            if (!OperatingSystem.IsWindows()) return;
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (!value)
            {
                k.DeleteValue(RunValue, false);
                Schtasks($"/Delete /TN \"{TaskName}\" /F");
                return;
            }
            string xml = Path.Combine(Path.GetTempPath(), "fankick-task.xml");
            File.WriteAllText(xml, TaskXml(), System.Text.Encoding.Unicode);
            var r = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{xml}\" /F");
            File.Delete(xml);
            if (r.code != 0) throw new IOException("Couldn't create the startup task (needs admin): " + r.output.Trim());
            k.SetValue(RunValue, $"\"{Exe}\" --autostart");
        }
    }

    // Started by the Run entry at login: hand over to the elevated task, or fall back to a UAC prompt.
    public static bool LaunchAutostartTask() => OperatingSystem.IsWindows() && Schtasks($"/Run /TN \"{TaskName}\"").code == 0;

    static string TaskXml() => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Author>harshal.dev</Author><Description>Starts FanKick with admin rights when its Startup entry runs at login.</Description></RegistrationInfo>
          <Principals><Principal id="Author"><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>4</Priority>
            <AllowHardTerminate>true</AllowHardTerminate>
          </Settings>
          <Actions Context="Author"><Exec><Command>{System.Security.SecurityElement.Escape(Exe)}</Command><Arguments>--minimized</Arguments></Exec></Actions>
        </Task>
        """;

    static (int code, string output) Schtasks(string args) => Modes.PerfModes.Run("schtasks.exe", args);

    // ---- global hotkey (Windows): a thread with its own message queue receives WM_HOTKEY ----
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    static uint hotkeyThread;

    // "Ctrl+Shift+F10" -> registers it; null if the text doesn't parse or another app holds it.
    public static string? SetHotkey(string text, Action pressed)
    {
        if (!OperatingSystem.IsWindows()) return "Global hotkeys are Windows only for now; use the tray menu.";
        if (hotkeyThread != 0) { PostThreadMessage(hotkeyThread, 0x0012 /* WM_QUIT */, 0, 0); hotkeyThread = 0; }
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = 0x4000; // MOD_NOREPEAT
        uint vk = 0;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= 2; break;
                case "alt": mods |= 1; break;
                case "shift": mods |= 4; break;
                case "win": mods |= 8; break;
                case var k when k.Length == 1 && char.IsLetterOrDigit(k[0]): vk = char.ToUpperInvariant(k[0]); break;
                case var k when k.StartsWith('f') && int.TryParse(k[1..], out int f) && f is >= 1 and <= 24: vk = (uint)(0x70 + f - 1); break;
                default: return $"Unknown key \"{part}\"";
            }
        if (vk == 0) return "Add a key, e.g. Ctrl+Shift+F10";
        string? error = null;
        using var ready = new ManualResetEventSlim();
        new Thread(() =>
        {
            hotkeyThread = GetCurrentThreadId();
            if (!RegisterHotKey(IntPtr.Zero, 1, mods, vk)) { error = "That hotkey is taken by another app"; hotkeyThread = 0; ready.Set(); return; }
            ready.Set();
            while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0)
                if (m.message == 0x0312) pressed(); // WM_HOTKEY
        }) { IsBackground = true, Name = "hotkey" }.Start();
        ready.Wait(2000);
        return error;
    }
}
