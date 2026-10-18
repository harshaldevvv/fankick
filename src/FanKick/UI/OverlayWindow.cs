using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using FanKick.Core;

namespace FanKick.UI;

// A small always-on-top readout. Off by default; Settings > Overlay picks the corner, offset, opacity and readings.
// On Windows it is click-through and never takes focus.
sealed class OverlayWindow : Window
{
    readonly App app;
    readonly StackPanel lines = new() { Spacing = 1 };
    readonly Border box;

    public OverlayWindow(App app)
    {
        this.app = app;
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Title = "FanKick overlay";
        Icon = App.Icon;
        box = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6), Child = lines };
        Content = box;
        new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { if (IsVisible) Refresh(); }).Start();
        Opened += (_, _) => { ClickThrough(); Place(); };
        SizeChanged += (_, _) => Place();
    }

    // The readings to show: the user's pick, or a sensible default set.
    public static IEnumerable<Sensor> Picked(Core.Engine e, OverlaySettings o)
    {
        var picked = o.Sensors.Select(e.Find).OfType<Sensor>().ToList();
        if (picked.Count > 1) return picked;
        return new[]
        {
            e.HottestSensor,
            e.Sensors.FirstOrDefault(s => s.Kind == SensorKind.Load && s.Name == "CPU Total"),
            e.Sensors.FirstOrDefault(s => s.Kind == SensorKind.Power && s.Name is "CPU Package" or "Package"),
            e.Sensors.FirstOrDefault(s => s.Kind == SensorKind.Temperature && s.Critical && s.Name.StartsWith("GPU")),
            e.Sensors.FirstOrDefault(s => s.Kind == SensorKind.Fan && !float.IsNaN(s.Value) && s.Value > 0),
        }.OfType<Sensor>();
    }

    public void Refresh()
    {
        var e = app.Engine;
        if (e == null) return;
        var o = app.Settings.Overlay;
        box.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(o.Opacity, 0.2f, 1f) * 255), 16, 17, 20));
        var shown = Picked(e, o).ToList();
        while (lines.Children.Count < shown.Count) lines.Children.Add(new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold });
        while (lines.Children.Count > shown.Count) lines.Children.RemoveAt(lines.Children.Count - 1);
        for (int i = 0; i < shown.Count; i++)
        {
            var s = shown[i];
            var tb = (TextBlock)lines.Children[i];
            tb.Text = $"{(s == e.HottestSensor ? "Hottest" : s.Name)}  {s.Format()}";
            tb.Foreground = s.Kind == SensorKind.Temperature ? Ui.TempBrush(s.Value) : Ui.Fg;
        }
        if (e.Panic || e.Alert != null)
        {
            lines.Children.Add(new TextBlock { Text = e.Panic ? "PANIC: fans at 100%" : "Fan alert: open FanKick", Foreground = Ui.Bad, FontSize = 13, FontWeight = FontWeight.SemiBold });
        }
    }

    void Place()
    {
        var screen = Screens.Primary;
        if (screen == null) return;
        var wa = screen.WorkingArea;
        var o = app.Settings.Overlay;
        double sc = screen.Scaling;
        int w = (int)(Bounds.Width * sc), h = (int)(Bounds.Height * sc), dx = (int)(o.OffsetX * sc), dy = (int)(o.OffsetY * sc);
        Position = o.Corner switch
        {
            Corner.TopRight => new PixelPoint(wa.Right - w - dx, wa.Y + dy),
            Corner.BottomLeft => new PixelPoint(wa.X + dx, wa.Bottom - h - dy),
            Corner.BottomRight => new PixelPoint(wa.Right - w - dx, wa.Bottom - h - dy),
            _ => new PixelPoint(wa.X + dx, wa.Y + dy),
        };
    }

    public void Moved() => Place();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);

    void ClickThrough()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } h) return;
        const int GwlExStyle = -20;
        const long Transparent = 0x20, Layered = 0x80000, ToolWindow = 0x80, NoActivate = 0x8000000;
        SetWindowLongPtr(h, GwlExStyle, (IntPtr)((long)GetWindowLongPtr(h, GwlExStyle) | Transparent | Layered | ToolWindow | NoActivate));
    }
}
