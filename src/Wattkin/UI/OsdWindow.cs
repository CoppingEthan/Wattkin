using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Wattkin.UI;

/// <summary>Small on-screen message for hotkeys (replaces MSI's OSD popups). Never takes focus.</summary>
internal sealed class OsdWindow : Window
{
    private readonly TextBlock _text;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.6) };

    public OsdWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        _text = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold };
        _text.SetResourceReference(TextBlock.ForegroundProperty, "Lc.Text");
        var border = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(22, 12, 22, 12), BorderThickness = new Thickness(1), Child = _text };
        border.SetResourceReference(Border.BackgroundProperty, "Lc.WidgetBg");
        border.SetResourceReference(Border.BorderBrushProperty, "Lc.CardBorder");
        Content = border;
        SourceInitialized += (_, _) => WindowEffects.Apply(new WindowInteropHelper(this).Handle, toolWindow: true, clickThrough: true, noActivate: true);
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)) { FillBehavior = FillBehavior.HoldEnd };
            fade.Completed += (_, _) =>
            {
                // A new message may have arrived during the fade; only hide if nothing is showing.
                if (!_timer.IsEnabled)
                {
                    Hide();
                }
            };
            BeginAnimation(OpacityProperty, fade);
        };
    }

    public void ShowMessage(string message)
    {
        _text.Text = message;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;

        // Hidden until it's centred: the first message after a start (e.g. "Wattkin updated to …") used
        // to flash in the top-left corner, where Windows first puts a window.
        WindowEffects.ShowWhenPlaced(this, () =>
        {
            Show();
            UpdateLayout();
            var area = SystemParameters.WorkArea;
            Left = area.Left + ((area.Width - ActualWidth) / 2);
            Top = area.Bottom - ActualHeight - 60;
        });
        _timer.Stop();
        _timer.Start();
    }
}
