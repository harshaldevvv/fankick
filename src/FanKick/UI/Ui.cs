using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FanKick.UI;

// Colours and small builders so the UI code reads as layout, not styling.
static class Ui
{
    public static readonly IBrush Bg = Brush("#141518"), CardBg = Brush("#1d1f24"), CardHover = Brush("#25272d"), Edge = Brush("#2c2f36"),
        Fg = Brush("#ececf0"), Dim = Brush("#9a9da6"), Accent = Brush("#38bdf8"), Warn = Brush("#f59e0b"), Bad = Brush("#ef4444"), Good = Brush("#22c55e"),
        WarnBg = Brush("#2a2214"), BadBg = Brush("#2c1517");

    // Series colours for charts (distinct on dark, no two similar hues next to each other).
    public static readonly Color[] Series = [Color.Parse("#38bdf8"), Color.Parse("#fb923c"), Color.Parse("#a3e635"), Color.Parse("#c084fc"), Color.Parse("#f472b6"), Color.Parse("#facc15")];

    public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static IBrush TempBrush(float t) => float.IsNaN(t) ? Dim : t >= 90 ? Bad : t >= 75 ? Warn : Good;

    public static TextBlock Txt(string s, double size = 13, IBrush? fg = null, FontWeight weight = FontWeight.Normal) =>
        new() { Text = s, FontSize = size, Foreground = fg ?? Fg, FontWeight = weight, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    public static TextBlock Heading(string s) => Txt(s, 15, Fg, FontWeight.SemiBold);
    public static TextBlock Muted(string s, double size = 12) => Txt(s, size, Dim);

    public static Border Box(Control child, Thickness? pad = null) => new()
    {
        Background = CardBg, CornerRadius = new CornerRadius(10), Padding = pad ?? new Thickness(16), Child = child,
        BorderBrush = Edge, BorderThickness = new Thickness(1),
    };

    public static Border Banner(string text, bool bad) => new()
    {
        Background = bad ? BadBg : WarnBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8),
        Child = Txt(text, 13, bad ? Bad : Warn),
    };

    public static StackPanel Col(double spacing = 8, params Control[] kids) { var p = new StackPanel { Spacing = spacing }; p.Children.AddRange(kids); return p; }
    public static StackPanel Row(double spacing = 8, params Control[] kids) { var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing }; p.Children.AddRange(kids); return p; }

    public static Button Btn(string text, Action click, bool primary = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 6), CornerRadius = new CornerRadius(6) };
        if (primary) { b.Background = Accent; b.Foreground = Brush("#0b1220"); }
        b.Click += (_, _) => click();
        return b;
    }

    public static NumericUpDown Number(double value, double min, double max, Action<double> changed, double step = 1, string fmt = "0")
    {
        var n = new NumericUpDown { Value = (decimal)value, Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step, FormatString = fmt, Width = 130 };
        n.ValueChanged += (_, e) => { if (e.NewValue is { } v) changed((double)v); };
        return n;
    }

    public static Grid Labeled(string label, Control control, string? hint = null)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), Margin = new Thickness(0, 2) };
        var l = Col(2, Txt(label));
        if (hint != null) l.Children.Add(Muted(hint, 11));
        g.Children.Add(l);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }
}
