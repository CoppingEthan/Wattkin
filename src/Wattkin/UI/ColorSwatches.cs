using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Wattkin.Hardware;

namespace Wattkin.UI;

/// <summary>
/// The keyboard colour: white and MSI Center's palette (the colours its Mystic Light packets carry) as
/// round swatches, then one more that opens Windows' colour picker. When the colour isn't one of the
/// presets, that last swatch shows it, chosen; otherwise it shows a rainbow.
/// </summary>
internal sealed class ColorSwatches
{
    private static readonly (string Name, LightColor Color)[] Presets =
    [
        ("White", LightColor.White),
        ("Red", new(0xFF, 0x00, 0x00)),
        ("Orange", new(0xFF, 0x50, 0x00)),
        ("Yellow", new(0xFF, 0xFF, 0x00)),
        ("Green", new(0x00, 0xFF, 0x00)),
        ("Blue", new(0x00, 0x00, 0xFF)),
        ("Purple", new(0x96, 0x00, 0xFF)),
        ("Pink", new(0xFF, 0x00, 0xFF)),
    ];

    private static readonly Brush Rainbow = Freeze(new LinearGradientBrush(
        new GradientStopCollection(new[] { Colors.Red, Colors.Yellow, Colors.Lime, Colors.Cyan, Colors.Blue, Colors.Magenta }.Select((c, i) => new GradientStop(c, i / 5.0))),
        new Point(0, 0), new Point(1, 1)));

    private readonly List<RadioButton> _presets = [];
    private readonly RadioButton _other;
    private LightColor _shown = LightColor.White;
    private bool _loading;

    /// <param name="group">The RadioButton group name, unique within the window.</param>
    public ColorSwatches(Panel panel, string group)
    {
        foreach (var (name, color) in Presets)
        {
            var swatch = Swatch(panel, group, Fill(color), name);
            swatch.Tag = color;
            swatch.Checked += async (_, _) =>
            {
                if (!_loading)
                {
                    await App.Current.Hardware.SetKeyboardColorAsync(color);
                }
            };
            _presets.Add(swatch);
        }

        // Click, not Checked: picking another colour again must open the picker again.
        _other = Swatch(panel, group, Rainbow, "Another colour…");
        _other.Click += async (_, _) =>
        {
            if (PickOther(panel, _shown) is { } color)
            {
                await App.Current.Hardware.SetKeyboardColorAsync(color);
            }
            else
            {
                Show(_shown.Hex); // cancelled: the click checked it anyway
            }
        };
    }

    /// <summary>Marks the colour in use (settings' "#RRGGBB").</summary>
    public void Show(string hex)
    {
        var color = LightColor.Parse(hex) ?? LightColor.White;
        _shown = color;
        _loading = true;
        try
        {
            var preset = false;
            foreach (var swatch in _presets)
            {
                var match = (LightColor)swatch.Tag == color;
                swatch.IsChecked = match;
                preset |= match;
            }

            _other.IsChecked = !preset;
            _other.Background = preset ? Rainbow : Fill(color);
            _other.ToolTip = preset ? "Another colour…" : $"{color.Hex} (click for another colour)";
        }
        finally
        {
            _loading = false;
        }
    }

    private static RadioButton Swatch(Panel panel, string group, Brush fill, string name)
    {
        var swatch = new RadioButton { Style = (Style)panel.FindResource("Lc.Swatch"), GroupName = group, Background = fill, ToolTip = name };
        panel.Children.Add(swatch);
        return swatch;
    }

    /// <summary>Windows' colour picker, owned by the settings window. Null when cancelled.</summary>
    private static LightColor? PickOther(Panel panel, LightColor current)
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };
        var owner = Window.GetWindow(panel) is { } window ? new Owner(new WindowInteropHelper(window).Handle) : null;
        PickerOpen = true;
        try
        {
            return dialog.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK
                ? new LightColor(dialog.Color.R, dialog.Color.G, dialog.Color.B)
                : null;
        }
        finally
        {
            PickerOpen = false;
        }
    }

    /// <summary>True while Windows' colour picker is open: the flyout stays up behind it rather than hide on losing focus.</summary>
    public static bool PickerOpen { get; private set; }

    private static Brush Fill(LightColor color) => Freeze(new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)));

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private sealed class Owner(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle => handle;
    }
}
