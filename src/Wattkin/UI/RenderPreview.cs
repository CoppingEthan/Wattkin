using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Wattkin.UI;

/// <summary>
/// Developer aid: Wattkin.exe --render &lt;folder&gt; renders the tray icon, flyout, widget and every
/// settings tab off-screen to PNG files, in the light and dark theme, without showing anything on the
/// desktop, then exits. It only reads: a running copy keeps owning the hardware and settings.
/// </summary>
internal static class RenderPreview
{
    public static void Schedule(string folder)
    {
        Directory.CreateDirectory(folder);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                RenderAll(folder);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(folder, "render-error.txt"), ex.ToString());
            }

            App.Current.ExitApp();
        };
        timer.Start();
    }

    /// <summary>Tray icons at the sizes Windows uses (100–200% scaling), actual size and 6× zoom, on light and dark taskbars.</summary>
    private static void RenderTray(string folder)
    {
        int[] sizes = [16, 20, 24, 32];
        string[] numbers = ["2", "9", "17", "54", "105"];
        const int zoom = 6;
        const int cell = 32 * zoom + 16;
        using var sheet = new System.Drawing.Bitmap((numbers.Length * cell) + 16, (sizes.Length * 2 * (cell + 40)) + 16);
        using var g = System.Drawing.Graphics.FromImage(sheet);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        var y = 8;
        foreach (var light in new[] { false, true })
        {
            foreach (var size in sizes)
            {
                using var bg = new System.Drawing.SolidBrush(light ? System.Drawing.Color.FromArgb(238, 238, 238) : System.Drawing.Color.FromArgb(32, 32, 32));
                g.FillRectangle(bg, 0, y - 8, sheet.Width, cell + 40);
                for (var i = 0; i < numbers.Length; i++)
                {
                    using var icon = TrayIcon.Render(numbers[i], new System.Drawing.Size(size, size), light);
                    var x = 8 + (i * cell);
                    g.DrawImage(icon, x, y, size * zoom, size * zoom);
                    g.DrawImage(icon, x, y + (size * zoom) + 8, size, size); // actual size
                }

                y += cell + 40;
            }
        }

        sheet.Save(Path.Combine(folder, "tray.png"), System.Drawing.Imaging.ImageFormat.Png);

        // For the README: a few readings on a taskbar strip at 150% scaling (24 px icons), shown at 2×.
        foreach (var light in new[] { false, true })
        {
            const int icon = 24, pad = 12, scale = 2;
            string[] readings = ["6", "17", "48"];
            using var strip = new System.Drawing.Bitmap(((readings.Length * (icon + pad)) + pad) * scale, (icon + (2 * pad)) * scale);
            using var sg = System.Drawing.Graphics.FromImage(strip);
            sg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            sg.Clear(light ? System.Drawing.Color.FromArgb(238, 238, 238) : System.Drawing.Color.FromArgb(28, 28, 28));
            for (var i = 0; i < readings.Length; i++)
            {
                using var bitmap = TrayIcon.Render(readings[i], new System.Drawing.Size(icon, icon), light);
                sg.DrawImage(bitmap, (pad + (i * (icon + pad))) * scale, pad * scale, icon * scale, icon * scale);
            }

            strip.Save(Path.Combine(folder, $"tray-{(light ? "light" : "dark")}.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    private static void RenderAll(string folder)
    {
        RenderTray(folder);
        var app = App.Current;
        foreach (var light in new[] { true, false })
        {
            app.ApplyPalette(light);
            RenderWindows(folder, light ? "light" : "dark");
        }

        app.ApplyPalette();
    }

    private static void RenderWindows(string folder, string theme)
    {
        var app = App.Current;

        // Shown windows ask for live data; clocks, disk rates and the busiest apps need a few samples.
        // Never on screen: Windows first puts a new window near the top-left corner, and the widget then
        // moves itself to its saved place (a blank widget flashed up there during every render).
        var flyout = new FlyoutWindow { ShowActivated = false, Left = -6000, Top = 0 };
        var widget = new WidgetWindow { ShowActivated = false, Left = -6000, Top = 0 };
        WindowEffects.KeepOffScreen(flyout);
        WindowEffects.KeepOffScreen(widget);
        _ = app.Screen.RefreshAsync(); // the screen slider at the real brightness, not "Normal"
        flyout.Show();
        widget.Show();
        Wait(TimeSpan.FromSeconds(6));
        if (app.Monitor.Latest is { } latest)
        {
            flyout.Update(latest);
            widget.Update(latest);
        }

        Save(flyout, Path.Combine(folder, $"flyout-{theme}.png"));
        Save(widget, Path.Combine(folder, $"widget-{theme}.png"));
        var flyoutHeight = ((FrameworkElement)flyout.Content).ActualHeight;
        flyout.Close();
        widget.Close();

        var settings = new SettingsWindow { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -6000, Top = 0 };
        WindowEffects.KeepOffScreen(settings);
        settings.Show();

        // Give the capacity history (powercfg battery report, a few seconds) time to load.
        Wait(TimeSpan.FromSeconds(8));
        if (app.Monitor.Latest is { } now)
        {
            settings.Update(now);
        }

        // Simple: one page, rendered tall enough to show all of it.
        settings.SetView(expert: false, save: false);
        Pump();
        Save(settings, Path.Combine(folder, $"settings-simple-window-{theme}.png"));
        var height = settings.Height;
        settings.Height = 1700;
        Pump();
        settings.UpdateLayout();
        Save(settings, Path.Combine(folder, $"settings-simple-{theme}.png"));
        settings.Height = height;

        // Expert: every tab.
        settings.SetView(expert: true, save: false);
        var tabs = (TabControl)settings.FindName("Tabs");
        foreach (TabItem tab in tabs.Items)
        {
            tabs.SelectedItem = tab;
            Pump();
            if (tab.Tag is "debug")
            {
                Wait(TimeSpan.FromSeconds(2)); // the registers are read in the background
            }

            // Fan profiles: a user one (Quiet, unless deleted) rather than the read-only laptop default.
            if (tab.Tag is "fans" && App.Current.Store.Current.FanProfiles.FirstOrDefault() is { } fan)
            {
                Wait(TimeSpan.FromSeconds(2)); // the laptop's fan table is read in the background
                ((ListBox)settings.FindName("FanProfileList")).SelectedItem = fan.Name;
                Pump();
            }

            Save(settings, Path.Combine(folder, $"settings-expert-{tab.Tag}-{theme}.png"));
        }

        // For the README's top image: the battery tab as tall as the flyout beside it, so they line up.
        // The window is made taller (more of the page shows), nothing is stretched.
        tabs.SelectedItem = tabs.Items.Cast<TabItem>().First(t => t.Tag is "battery");
        Pump();
        var page = (FrameworkElement)settings.Content;
        settings.Height = settings.ActualHeight + flyoutHeight - page.ActualHeight;
        Pump();
        settings.UpdateLayout();
        Save(settings, Path.Combine(folder, $"settings-expert-battery-tall-{theme}.png"));
        settings.Height = height;

        settings.Close();
    }

    private static void Wait(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            Pump();
            Thread.Sleep(50);
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void Save(Window window, string path)
    {
        window.UpdateLayout();
        Pump();
        var element = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        const double scale = 1.25;
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background is SolidColorBrush { Color.A: > 0 } bg ? bg : (Brush)window.FindResource("Lc.Window"), null, new Rect(0, 0, width, height));
            // The element's own layout box, not its drawn bounds: a plain VisualBrush would crop the empty margins.
            var offset = VisualTreeHelper.GetOffset(element);
            var brush = new VisualBrush(element) { Viewbox = new Rect(offset.X, offset.Y, width, height), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            dc.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }

        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
