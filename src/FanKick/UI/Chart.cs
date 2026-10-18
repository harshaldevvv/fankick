using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FanKick.UI;

// A plain line chart for the last few minutes of readings. Each series is scaled to the shared Min..Max range.
sealed class Chart : Control
{
    public sealed record Series(string Label, Func<float[]> Values, Color Color, string Unit);
    public List<Series> Lines { get; } = [];
    public float Min { get; set; } = 0;
    public float Max { get; set; } = 100;
    public bool AutoRange { get; set; } = true;

    static readonly Typeface Face = new("Inter");

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        ctx.DrawRectangle(Ui.CardBg, null, new Rect(b.Size), 8, 8);
        var data = Lines.Select(l => (l, v: l.Values())).ToList();
        float lo = Min, hi = Max;
        if (AutoRange)
        {
            var all = data.SelectMany(d => d.v).Where(x => !float.IsNaN(x)).ToArray();
            if (all.Length > 0) { lo = MathF.Floor(Math.Min(Min, all.Min()) / 10) * 10; hi = MathF.Ceiling(Math.Max(Max, all.Max()) / 10) * 10; }
        }
        if (hi <= lo) hi = lo + 10;
        double left = 36, top = 26, right = 10, bottom = 10;
        double w = b.Width - left - right, h = b.Height - top - bottom;
        if (w < 20 || h < 20) return;

        var grid = new Pen(Ui.Edge, 1);
        for (int i = 0; i <= 4; i++)
        {
            double y = top + h * i / 4;
            ctx.DrawLine(grid, new Point(left, y), new Point(left + w, y));
            Label(ctx, (hi - (hi - lo) * i / 4).ToString("0"), new Point(4, y - 7), Ui.Dim);
        }
        double x0 = left;
        foreach (var (line, v) in data)
        {
            // Legend: label and latest value.
            float last = v.Length > 0 ? v[^1] : float.NaN;
            string text = $"{line.Label} {(float.IsNaN(last) ? "-" : last.ToString("0"))}{line.Unit}";
            var ft = Label(ctx, text, new Point(x0, 4), new SolidColorBrush(line.Color));
            x0 += ft.Width + 16;
            if (v.Length < 2) continue;
            var pen = new Pen(new SolidColorBrush(line.Color), 2);
            int n = Math.Max(v.Length, Core.Engine.HistoryLen); // full window, so a fresh start fills in from the right
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                bool open = false;
                for (int i = 0; i < v.Length; i++)
                {
                    if (float.IsNaN(v[i])) { if (open) { g.EndFigure(false); open = false; } continue; }
                    var p = new Point(left + w * (n - v.Length + i) / (n - 1), top + h * (1 - (Math.Clamp(v[i], lo, hi) - lo) / (hi - lo)));
                    if (!open) { g.BeginFigure(p, false); open = true; } else g.LineTo(p);
                }
                if (open) g.EndFigure(false);
            }
            ctx.DrawGeometry(null, pen, geo);
        }
    }

    static FormattedText Label(DrawingContext ctx, string s, Point at, IBrush brush)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11, brush);
        ctx.DrawText(ft, at);
        return ft;
    }
}

