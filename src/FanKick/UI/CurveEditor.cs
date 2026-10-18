using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using FanKick.Core;

namespace FanKick.UI;

// Drag the points of a graph curve. Double-click empty space to add a point, right-click a point to remove it.
// Shows the live temperature of the curve's sensor and the % it gives right now.
sealed class CurveEditor : Control
{
    const float TMin = 20, TMax = 110;
    const double Pad = 36, Hit = 12;
    static readonly Typeface Face = new("Inter");

    public GraphCurve? Curve { get; set; }
    public Func<float> Live { get; set; } = () => float.NaN;
    public object Gate { get; set; } = new();
    public event Action? Changed;
    int drag = -1;

    public CurveEditor() { Focusable = true; Cursor = new Cursor(StandardCursorType.Hand); }

    Rect Area => new(Pad, 12, Math.Max(10, Bounds.Width - Pad - 12), Math.Max(10, Bounds.Height - 12 - Pad));
    Point ToScreen(CurvePoint p) => new(Area.X + Area.Width * (p.Temp - TMin) / (TMax - TMin), Area.Y + Area.Height * (1 - p.Percent / 100));
    CurvePoint FromScreen(Point s) => new(
        MathF.Round(Math.Clamp(TMin + (float)((s.X - Area.X) / Area.Width) * (TMax - TMin), 0, 125)),
        MathF.Round(Math.Clamp((float)(1 - (s.Y - Area.Y) / Area.Height) * 100, 0, 100)));

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(Ui.CardBg, null, new Rect(Bounds.Size), 8, 8);
        var a = Area;
        var grid = new Pen(Ui.Edge, 1);
        for (int t = (int)TMin; t <= TMax; t += 10)
        {
            double x = a.X + a.Width * (t - TMin) / (TMax - TMin);
            ctx.DrawLine(grid, new Point(x, a.Y), new Point(x, a.Bottom));
            Label(ctx, $"{t}°", new Point(x - 8, a.Bottom + 6));
        }
        for (int p = 0; p <= 100; p += 20)
        {
            double y = a.Y + a.Height * (1 - p / 100.0);
            ctx.DrawLine(grid, new Point(a.X, y), new Point(a.Right, y));
            Label(ctx, $"{p}%", new Point(2, y - 7));
        }
        if (Curve is null) return;
        List<CurvePoint> pts;
        lock (Gate) pts = [.. Curve.Points];
        if (pts.Count == 0) return;

        var line = new Pen(Ui.Accent, 2.5);
        var first = ToScreen(pts[0]);
        ctx.DrawLine(line, new Point(a.X, first.Y), first);
        for (int i = 1; i < pts.Count; i++) ctx.DrawLine(line, ToScreen(pts[i - 1]), ToScreen(pts[i]));
        var lastP = ToScreen(pts[^1]);
        ctx.DrawLine(line, lastP, new Point(a.Right, lastP.Y));

        float t0 = Live();
        if (!float.IsNaN(t0))
        {
            var now = ToScreen(new CurvePoint(Math.Clamp(t0, TMin, TMax), GraphCurve.Interpolate(pts, t0)));
            ctx.DrawLine(new Pen(Ui.Warn, 1, new DashStyle([4, 4], 0)), new Point(now.X, a.Y), new Point(now.X, a.Bottom));
            ctx.DrawEllipse(Ui.Warn, null, now, 5, 5);
            Label(ctx, $"{t0:0} °C → {GraphCurve.Interpolate(pts, t0):0}%", new Point(Math.Min(now.X + 8, a.Right - 90), a.Y + 4), Ui.Warn);
        }
        for (int i = 0; i < pts.Count; i++)
        {
            var p = ToScreen(pts[i]);
            ctx.DrawEllipse(i == drag ? Ui.Fg : Ui.Accent, new Pen(Ui.Bg, 2), p, 7, 7);
            if (i == drag) Label(ctx, $"{pts[i].Temp:0} °C, {pts[i].Percent:0}%", new Point(p.X + 10, p.Y - 20), Ui.Fg);
        }
    }

    static void Label(DrawingContext ctx, string s, Point at, IBrush? brush = null) =>
        ctx.DrawText(new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11, brush ?? Ui.Dim), at);

    int Nearest(Point s)
    {
        if (Curve is null) return -1;
        lock (Gate)
            for (int i = 0; i < Curve.Points.Count; i++)
            {
                var p = ToScreen(Curve.Points[i]);
                if (Math.Abs(p.X - s.X) <= Hit && Math.Abs(p.Y - s.Y) <= Hit) return i;
            }
        return -1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (Curve is null) return;
        var pos = e.GetPosition(this);
        int hit = Nearest(pos);
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            if (hit >= 0) lock (Gate) if (Curve.Points.Count > 2) Curve.Points.RemoveAt(hit);
            Done();
            return;
        }
        if (hit < 0 && e.ClickCount == 2)
        {
            var np = FromScreen(pos);
            lock (Gate)
            {
                if (Curve.Points.Any(p => p.Temp == np.Temp)) return;
                Curve.Points.Add(np);
                Curve.Points.Sort((x, y) => x.Temp.CompareTo(y.Temp));
                hit = Curve.Points.IndexOf(np);
            }
        }
        drag = hit;
        if (drag >= 0) e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (drag < 0 || Curve is null) return;
        var np = FromScreen(e.GetPosition(this));
        lock (Gate)
        {
            // Keep points in temperature order: a point can't pass its neighbours.
            float lo = drag > 0 ? Curve.Points[drag - 1].Temp + 1 : 0, hi = drag < Curve.Points.Count - 1 ? Curve.Points[drag + 1].Temp - 1 : 125;
            Curve.Points[drag] = np with { Temp = Math.Clamp(np.Temp, lo, Math.Max(lo, hi)) };
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (drag < 0) return;
        drag = -1;
        e.Pointer.Capture(null);
        Done();
    }

    void Done() { InvalidateVisual(); Changed?.Invoke(); }
}
