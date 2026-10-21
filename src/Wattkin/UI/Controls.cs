using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Wattkin.Monitoring;
using Wattkin.Native;

namespace Wattkin.UI;

internal static class WindowEffects
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(IntPtr hwnd, bool toolWindow, bool clickThrough = false, bool noActivate = false)
    {
        var round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        SetDarkTitleBar(hwnd);

        var style = User32.GetWindowLongPtr(hwnd, User32.GWL_EXSTYLE).ToInt64();
        if (toolWindow)
        {
            style = (style | User32.WS_EX_TOOLWINDOW) & ~User32.WS_EX_APPWINDOW;
        }

        if (clickThrough)
        {
            style |= WS_EX_TRANSPARENT;
        }

        if (noActivate)
        {
            style |= User32.WS_EX_NOACTIVATE;
        }

        User32.SetWindowLongPtr(hwnd, User32.GWL_EXSTYLE, new IntPtr(style));
    }

    public static void SetDarkTitleBar(IntPtr hwnd)
    {
        var dark = App.Current.IsLightTheme ? 0 : 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    private const int DWMWA_CLOAK = 13;

    /// <summary>
    /// Shows a window only once it's in place. The compositor hides it ("cloaks" it) while it's shown,
    /// laid out and moved, then reveals it after WPF has drawn it there. Without this, a window's first
    /// showing after a start flashed half-drawn in the top-left corner, where Windows first puts a window
    /// (a user's report, 2026-10-16). It stays on its real monitor throughout, so mixed display scaling
    /// places it as before.
    /// </summary>
    public static void ShowWhenPlaced(Window window, Action showAndPlace)
    {
        // Already showing (an OSD updated while it's up): hiding it again would make it blink.
        if (window.IsVisible)
        {
            showAndPlace();
            return;
        }

        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        var cloak = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
        try
        {
            showAndPlace();
        }
        finally
        {
            // After the next render pass, so the first frame seen is the placed one.
            window.Dispatcher.BeginInvoke(() =>
            {
                var uncloak = 0;
                DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref uncloak, sizeof(int));
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }

    /// <summary>
    /// Hides a window from the screen for good while it's still "shown" to WPF, which lays it out and
    /// draws it as usual: for windows only ever rendered to images (--render). Call before Show.
    /// </summary>
    public static void KeepOffScreen(Window window)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        var cloak = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
    }
}

/// <summary>A custom-drawn element that redraws when the light/dark palette changes.</summary>
public abstract class ThemedElement : FrameworkElement
{
    protected ThemedElement()
    {
        Loaded += (_, _) => App.Current.PaletteChanged += InvalidateVisual;
        Unloaded += (_, _) => App.Current.PaletteChanged -= InvalidateVisual;
    }
}

/// <summary>The Wattkin mark: a W drawn as two Vs; the second V (for volts) in brand amber.</summary>
public sealed class BrandMark : ThemedElement
{
    protected override void OnRender(DrawingContext dc)
    {
        var h = ActualHeight;
        var w = ActualWidth;
        if (h <= 0 || w <= 0)
        {
            return;
        }

        var stroke = Math.Max(1.6, h * 0.17);
        var inset = stroke / 2;
        double top = inset, bottom = h - inset;
        double x0 = inset, x2 = w / 2, x4 = w - inset;
        double x1 = (x0 + x2) / 2, x3 = (x2 + x4) / 2;
        Pen Make(string key) => new((Brush)FindResource(key), stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, Make("Lc.Text"), Polyline(new Point(x0, top), new Point(x1, bottom), new Point(x2, top)));
        dc.DrawGeometry(null, Make("Lc.Brand"), Polyline(new Point(x2, top), new Point(x3, bottom), new Point(x4, top)));
    }

    private static StreamGeometry Polyline(Point a, Point b, Point c)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(a, false, false);
            ctx.LineTo(b, true, true);
            ctx.LineTo(c, true, true);
        }

        g.Freeze();
        return g;
    }
}

/// <summary>
/// A bar that fills left to right, with its text drawn inside. Text over the fill uses the on-accent
/// colour and text over the empty track the normal one, clipped exactly where the fill ends, so it
/// reads at 5% and at 100%.
/// </summary>
public sealed class MeterBar : ThemedElement
{
    private double _value;
    private string _left = "";
    private string _right = "";

    /// <summary>Text right after the end of the fill (inside it when it fits) instead of on the left.</summary>
    public bool LabelAtFill { get; set; }

    /// <param name="value">0..1; null shows an empty bar.</param>
    public void Set(double? value, string left, string right = "")
    {
        var v = value is { } x && double.IsFinite(x) ? Math.Clamp(x, 0, 1) : 0;
        if (Math.Abs(v - _value) < 0.002 && left == _left && right == _right)
        {
            return; // nothing visible changed: skip the redraw
        }

        _value = v;
        _left = left;
        _right = right;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 4 || h < 4)
        {
            return;
        }

        var radius = h / 2.5;
        var track = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle((Brush)FindResource("Lc.Window"), new Pen((Brush)FindResource("Lc.CardBorder"), 1), track, radius, radius);
        var fillWidth = Math.Round(w * _value);
        var fill = new Rect(0, 0, fillWidth, h);
        if (fillWidth >= 1)
        {
            dc.PushClip(new RectangleGeometry(fill));
            dc.DrawRoundedRectangle((Brush)FindResource("Lc.Accent"), null, track, radius, radius);
            dc.Pop();
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        const double pad = 8;
        FormattedText Text(string s, Brush brush, bool bold) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), 11.5, brush, dpi);

        void Draw(Brush brush)
        {
            if (_left.Length > 0)
            {
                var left = Text(_left, brush, LabelAtFill);
                var x = pad;
                if (LabelAtFill)
                {
                    // Inside the fill, against its end, when it fits; otherwise just after it.
                    x = fillWidth - left.Width - pad >= pad ? fillWidth - left.Width - pad : Math.Max(pad, fillWidth + pad);
                }

                dc.DrawText(left, new Point(x, (h - left.Height) / 2));
            }

            if (_right.Length > 0)
            {
                var right = Text(_right, brush, true);
                dc.DrawText(right, new Point(w - right.Width - pad, (h - right.Height) / 2));
            }
        }

        // Track-coloured text everywhere except over the fill, then on-accent text clipped to the fill.
        dc.PushClip(new RectangleGeometry(new Rect(fillWidth, 0, Math.Max(0, w - fillWidth), h)));
        Draw((Brush)FindResource("Lc.Text"));
        dc.Pop();
        if (fillWidth >= 1)
        {
            dc.PushClip(new RectangleGeometry(fill));
            Draw((Brush)FindResource("Lc.OnAccent"));
            dc.Pop();
        }
    }
}

/// <summary>Tiny line chart of recent watts.</summary>
public sealed class Sparkline : ThemedElement
{
    private (DateTime Time, double Watts)[] _points = [];

    public void SetPoints((DateTime Time, double Watts)[] points)
    {
        _points = points;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_points.Length < 2 || ActualWidth < 10 || ActualHeight < 10)
        {
            return;
        }

        var accent = (Brush)FindResource("Lc.Accent");
        var faint = (Brush)FindResource("Lc.Faint");
        var start = _points[0].Time;
        var span = Math.Max(1, (_points[^1].Time - start).TotalSeconds);
        var max = Math.Max(5, _points.Max(p => p.Watts) * 1.1);

        // The label gets its own band above the chart, so peaks never run into it.
        var label = new FormattedText(
            $"peak {_points.Max(p => p.Watts):0.#} W · last {Fmt.Duration(_points[^1].Time - start)}",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            10,
            faint,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(label, new Point(ActualWidth - label.Width, 0));
        var top = label.Height + 2;
        var h = ActualHeight - top - 1;
        if (h < 6)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, ActualHeight), true, true);
            foreach (var (time, watts) in _points)
            {
                var x = (time - start).TotalSeconds / span * ActualWidth;
                ctx.LineTo(new Point(x, top + h - (watts / max * h)), true, true);
            }

            ctx.LineTo(new Point(ActualWidth, ActualHeight), true, true);
        }

        geometry.Freeze();
        var fill = accent.Clone();
        fill.Opacity = 0.18;
        dc.DrawGeometry(fill, new Pen(accent, 1.4), geometry);
    }
}

/// <summary>Full-charge capacity over time vs design capacity.</summary>
public sealed class CapacityChart : ThemedElement
{
    private IReadOnlyList<CapacityPoint> _points = [];

    public void SetPoints(IReadOnlyList<CapacityPoint> points)
    {
        _points = points;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var text = (Brush)FindResource("Lc.SubText");
        var faint = (Brush)FindResource("Lc.Faint");
        var accent = (Brush)FindResource("Lc.Accent");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Label(string s) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, text, dpi);

        if (_points.Count < 2)
        {
            dc.DrawText(Label("Capacity history appears after a few days of data."), new Point(0, 0));
            return;
        }

        const double left = 44, bottom = 18, top = 6;
        var w = ActualWidth - left - 4;
        var h = ActualHeight - bottom - top;
        var design = _points.Max(p => p.DesignWh);
        var minY = Math.Floor(Math.Min(_points.Min(p => p.FullWh), design) * 0.9 / 5) * 5;
        var maxY = Math.Ceiling(design * 1.03);
        double Y(double v) => top + h - ((v - minY) / (maxY - minY) * h);
        var start = _points[0].Date;
        var span = Math.Max(1, (_points[^1].Date - start).TotalDays);
        double X(DateTime d) => left + ((d - start).TotalDays / span * w);

        var grid = new Pen(faint, 0.5) { DashStyle = DashStyles.Dot };
        for (var v = minY; v <= maxY; v += Math.Max(5, Math.Round((maxY - minY) / 4 / 5) * 5))
        {
            dc.DrawLine(grid, new Point(left, Y(v)), new Point(left + w, Y(v)));
            dc.DrawText(Label($"{v:0} Wh"), new Point(0, Y(v) - 7));
        }

        dc.DrawLine(new Pen(faint, 1.2) { DashStyle = DashStyles.Dash }, new Point(left, Y(design)), new Point(left + w, Y(design)));
        dc.DrawText(Label($"design {design:0.#} Wh"), new Point(left + 4, Y(design) - 15));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(X(_points[0].Date), Y(_points[0].FullWh)), false, false);
            foreach (var p in _points.Skip(1))
            {
                ctx.LineTo(new Point(X(p.Date), Y(p.FullWh)), true, true);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(accent, 2), geometry);
        dc.DrawText(Label(start.ToString("d MMM yyyy", CultureInfo.CurrentCulture)), new Point(left, ActualHeight - 14));
        var end = Label(_points[^1].Date.ToString("d MMM yyyy", CultureInfo.CurrentCulture));
        dc.DrawText(end, new Point(left + w - end.Width, ActualHeight - 14));
    }
}
