using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Wattkin.Features;
using Wattkin.Hardware;
using Wattkin.Monitoring;
using Wattkin.Platform;
using Wattkin.Settings;

namespace Wattkin.UI;

public partial class FlyoutWindow : Window
{
    private static readonly int[] LimitChoices = [60, 70, 80, 90, 100];
    private readonly AutoOffDelayStepper _autoOffDelay = new();
    private ColorSwatches? _keyboardColors;
    private bool _updating;
    private string _profilesKey = "";
    private DateTime _hiddenAtUtc;
    private DateTime _screenMovedUtc;
    private readonly System.Windows.Threading.DispatcherTimer _screenSettled = new() { Interval = ScreenBrightness.UserMoveSettles + TimeSpan.FromMilliseconds(100) };

    private Rect _anchorArea;

    public FlyoutWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowEffects.Apply(new WindowInteropHelper(this).Handle, toolWindow: true);
        Deactivated += (_, _) =>
        {
            if (!ColorSwatches.PickerOpen)
            {
                HideFlyout();
            }
        };

        // Alt+F4 hides instead of destroying the one flyout the app reuses.
        Closing += (_, e) =>
        {
            e.Cancel = true;
            HideFlyout();
        };

        // Release live updates however the window got hidden.
        IsVisibleChanged += (_, _) =>
        {
            App.Current.Monitor.Demand(this, fast: IsVisible);
            if (IsVisible)
            {
                RefreshControls();
            }
            else
            {
                _hiddenAtUtc = DateTime.UtcNow;
                MemoryTrim.Soon();
            }
        };

        // Content can grow after showing (e.g. a banner appears); keep the bottom edge anchored.
        SizeChanged += (_, _) => Anchor();
        App.Current.Hardware.StateChanged += () => Dispatcher.BeginInvoke(RefreshIfVisible);
        App.Current.Store.Changed += () => Dispatcher.BeginInvoke(RefreshIfVisible);
        App.Current.Screen.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible)
            {
                RefreshScreen();
            }
        });
        _screenSettled.Tick += (_, _) =>
        {
            _screenSettled.Stop();
            if (IsVisible)
            {
                RefreshScreen();
            }
        };
        BuildStaticSegments();
    }

    private void RefreshIfVisible()
    {
        if (IsVisible)
        {
            RefreshControls();
        }
    }

    public void ShowNearTray()
    {
        // A click on the tray icon right after the flyout lost focus would otherwise re-open it.
        if (DateTime.UtcNow - _hiddenAtUtc < TimeSpan.FromMilliseconds(300))
        {
            return;
        }

        RefreshControls();

        // Hidden by the compositor until it's in place: the first opening after a start used to flash a
        // half-drawn flyout in the top-left corner. (The old Opacity = 0 did nothing: WPF ignores it on a
        // window without AllowsTransparency.)
        WindowEffects.ShowWhenPlaced(this, () =>
        {
            Show();
            if (App.Current.Monitor.Latest is { } latest)
            {
                Update(latest);
            }

            _anchorArea = TrayWorkArea();
            MaxHeight = Math.Max(300, _anchorArea.Height - 24);
            UpdateLayout();
            Anchor();
        });
        Activate();
        _ = App.Current.Screen.RefreshAsync(); // in case a brightness change was missed
    }

    public void HideFlyout()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    /// <summary>Bottom-right (or next to the taskbar wherever it is) of the monitor the cursor is on, in this window's DIPs.</summary>
    private Rect TrayWorkArea()
    {
        var cursor = System.Windows.Forms.Control.MousePosition;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var work = screen.WorkingArea;
        return new Rect(work.Left / dpi.DpiScaleX, work.Top / dpi.DpiScaleY, work.Width / dpi.DpiScaleX, work.Height / dpi.DpiScaleY);
    }

    private void Anchor()
    {
        if (!IsVisible || _anchorArea.IsEmpty)
        {
            return;
        }

        const double gap = 12;
        Left = Math.Max(_anchorArea.Left + gap, _anchorArea.Right - ActualWidth - gap);
        Top = Math.Max(_anchorArea.Top + gap, _anchorArea.Bottom - ActualHeight - gap);
    }

    public void Update(Snapshot s)
    {
        if (!IsVisible)
        {
            return;
        }

        var app = App.Current;
        var p = s.Power;
        var info = app.Monitor.Info;
        ModelText.Text = info?.Model.Replace("MSI ", "") ?? "";

        WattsText.Text = Fmt.WattsNumber(p.WattsNow);
        WattsCaption.Text = p.Source switch
        {
            PowerSource.BatteryMeasured => "On battery",
            PowerSource.AcEstimated => "On the charger · estimated",
            PowerSource.AcCpuOnly => "On the charger · CPU only",
            _ => "",
        };
        PercentText.Text = Fmt.Percent(p.Percent);
        BatteryLine.Text = p switch
        {
            { Percent: null } => "",
            { Source: PowerSource.BatteryMeasured, TimeToEmpty: { } left } => $"{Fmt.Duration(Fmt.Steady(left))} left",
            { Source: PowerSource.BatteryMeasured } => "Estimating…",
            { Charging: true, TimeToTarget: { } full } => $"Charging · {Fmt.Duration(Fmt.Steady(full))}",
            { Charging: true } => "Charging",
            { UnpluggedRuntimeEstimate: { } est } => $"≈{Fmt.Duration(Fmt.Steady(est))} on battery",
            _ => "Plugged in",
        };
        HeadlineDetails.Text = HeadlineTooltip(s, app);
        Spark.SetPoints(app.History.Snapshot());

        // CPU: usage on the left; package watts, clocks and temperature on the right, inside the bar.
        var cpu = s.Cpu;
        var temp = s.Sensors?.CpuTempC is { } t ? Fmt.Temp(t) : Fmt.Temp(s.ThermalZoneC);
        CpuBar.Set(cpu.UsagePercent / 100, $"{cpu.UsagePercent:0}%", Fmt.Join(p.CpuPackageWatts is { } pkg ? Fmt.Watts(pkg) : null, Fmt.GHz(cpu.AverageMHz), temp));

        // Fans: the faster one, against its measured top speed (or the fastest it has been seen to spin).
        var settings = app.Store.Current;
        if (s.Sensors is { } sensors && (sensors.Fan1Rpm is not null || sensors.Fan2Rpm is not null))
        {
            var (rpm, max) = (sensors.Fan1Rpm ?? 0) >= (sensors.Fan2Rpm ?? 0) ? (sensors.Fan1Rpm ?? 0, settings.Fan1MaxRpm) : (sensors.Fan2Rpm ?? 0, settings.Fan2MaxRpm);
            FanBar.Set(rpm / (double)Math.Max(max ?? 0, MinimumFanMax), rpm > 0 ? $"{rpm:N0} rpm" : "Off");
        }
        else
        {
            FanBar.Set(null, Elevation.IsElevated ? "—" : "Needs administrator");
        }

        var mem = s.Memory;
        RamBar.Set(mem.LoadPercent / 100.0, $"{Fmt.Bytes(mem.UsedBytes)} of {Fmt.Bytes(mem.TotalBytes)}", $"{mem.LoadPercent}%");

        var read = s.Disks.Sum(d => d.ReadBytesPerSec);
        var write = s.Disks.Sum(d => d.WriteBytesPerSec);
        var busy = s.Disks.Count > 0 ? s.Disks.Max(d => d.ActivePercent) : 0;
        DiskBar.Set(busy / 100, $"↓ {Fmt.Rate(read)}   ↑ {Fmt.Rate(write)}", $"{busy:0}%");

        UpdateGpus(s);
        ChargeStatus.Text = app.Hardware.State.Calibration?.Message ?? "";
        ChargeStatus.Visibility = ChargeStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Integrated GPU first, then the discrete one; the RTX shows its state when it isn't working.</summary>
    private void UpdateGpus(Snapshot s)
    {
        var integrated = s.Gpus.FirstOrDefault(g => !g.Adapter.Discrete);
        var discrete = s.Gpus.FirstOrDefault(g => g.Adapter.Discrete);
        Gpu1Label.Text = integrated?.Adapter.ShortName ?? "iGPU";
        Gpu1Bar.Set(integrated?.Percent / 100, integrated is null ? "—" : $"{integrated.Percent:0}%", integrated?.Watts is { } igpuWatts ? Fmt.Watts(igpuWatts) : "");

        var hasDiscrete = s.Dgpu.State != DgpuState.None;
        Gpu2Label.Visibility = Gpu2Bar.Visibility = hasDiscrete ? Visibility.Visible : Visibility.Collapsed;
        Gpu1Bar.Margin = new Thickness(0, 0, 0, hasDiscrete ? 6 : 0);
        Gpu1Label.Margin = Gpu1Bar.Margin;
        Gpu2Label.Text = (discrete?.Adapter.Name ?? s.Dgpu.Name ?? "").Contains("RTX", StringComparison.OrdinalIgnoreCase) ? "RTX" : "dGPU";
        var gpuTemp = s.Sensors?.GpuTempC is { } gt ? Fmt.Temp(gt) : null;
        switch (s.Dgpu.State)
        {
            case DgpuState.Active:
                Gpu2Bar.Set((discrete?.Percent ?? 0) / 100, $"{discrete?.Percent ?? 0:0}%", Fmt.Join(discrete?.Watts is { } w ? Fmt.Watts(w) : null, gpuTemp));
                break;
            case DgpuState.Sleeping:
                Gpu2Bar.Set(0, "Asleep");
                break;
            case DgpuState.RemovedByFirmware:
                Gpu2Bar.Set(0, "Off · Integrated only");
                break;
            case DgpuState.DisabledInWindows:
                Gpu2Bar.Set(0, "Disabled in Windows");
                break;
            default:
                Gpu2Bar.Set(discrete?.Percent / 100, discrete is null ? "—" : $"{discrete.Percent:0}%");
                break;
        }
    }

    /// <summary>The detail the headline no longer shows: where the number comes from and how it splits.</summary>
    private static string HeadlineTooltip(Snapshot s, App app)
    {
        var p = s.Power;
        var lines = new List<string> { Fmt.Sentence(Fmt.PowerHeadline(p)) };
        if (p.CpuPackageWatts is { } pkg)
        {
            lines.Add($"CPU {Fmt.Watts(pkg)} (cores {Fmt.Watts(s.Cpu.CoresWatts)}, integrated graphics {Fmt.Watts(s.Cpu.GraphicsWatts)})");
            if (p.Source == PowerSource.BatteryMeasured && p.SystemWatts is { } sys)
            {
                lines.Add($"Everything else (screen, SSD, Wi-Fi, RTX…) {Fmt.Watts(Math.Max(0, sys - pkg))}");
            }
        }

        if (p.AverageWatts is { } avg)
        {
            lines.Add($"Average over the last few minutes {Fmt.Watts(avg)}");
        }

        if (p.Session is { AverageWatts: { } sessionAvg } session)
        {
            lines.Add($"Unplugged {Fmt.Duration(session.Duration)} ago, averaging {Fmt.Watts(sessionAvg)}");
        }

        var status = app.Hardware.State.ChargeStatus;
        if (status.Length > 0)
        {
            lines.Add(status);
        }

        return string.Join("\n", lines.Where(l => l.Length > 0));
    }

    /// <summary>Until a fan's top speed is measured (or seen), treat this as its top speed (a typical laptop maximum).</summary>
    private const int MinimumFanMax = 5000;

    private RadioButton Segment(string text, string group, object tag) =>
        new() { Content = text, Style = (Style)FindResource("Lc.Segment"), GroupName = group, Tag = tag, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(4, 6, 4, 6) };

    private void BuildStaticSegments()
    {
        // 60–100% share the row equally; "Custom…" gets a little more room for its longer label.
        for (var i = 0; i <= LimitChoices.Length; i++)
        {
            LimitPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i == LimitChoices.Length ? 1.5 : 1, GridUnitType.Star) });
        }

        for (var i = 0; i < LimitChoices.Length; i++)
        {
            var limit = LimitChoices[i];
            var button = Segment($"{limit}%", "limit", limit);
            button.Checked += async (_, _) =>
            {
                if (!_updating)
                {
                    await App.Current.Hardware.SetChargeLimitAsync(limit, null);
                }
            };
            Grid.SetColumn(button, i);
            LimitPanel.Children.Add(button);
        }

        var custom = Segment("Custom…", "limit", -1);
        custom.Click += (_, _) => App.Current.OpenSettings("battery", expert: true);
        Grid.SetColumn(custom, LimitChoices.Length);
        LimitPanel.Children.Add(custom);

        string[] levels = ["Off", "Low", "Mid", "High"];
        for (var i = 0; i < levels.Length; i++)
        {
            var level = i;
            var button = Segment(levels[i], "kb", level);
            button.Checked += async (_, _) =>
            {
                if (!_updating)
                {
                    await App.Current.Hardware.SetKeyboardLevelAsync(level);
                }
            };
            KeyboardPanel.Children.Add(button);
        }

        _keyboardColors = new ColorSwatches(KeyboardColorPanel, "flyoutKbColor");
        foreach (var (text, timeout) in new[] { ("Never", KeyboardTimeout.AlwaysOn), ("Always", KeyboardTimeout.AutoOff), ("On battery", KeyboardTimeout.AutoOffOnBattery) })
        {
            var button = Segment(text, "kbTimeout", timeout);
            button.Checked += async (_, _) =>
            {
                if (!_updating)
                {
                    await App.Current.Hardware.SetKeyboardTimeoutAsync(timeout);
                }
            };
            KeyboardTimeoutPanel.Children.Add(button);
        }

        AutoOffDelayHost.Child = _autoOffDelay.Control;

        foreach (var (text, mode) in new[] { ("Integrated only", FirmwareGpuMode.Integrated), ("Hybrid (RTX)", FirmwareGpuMode.Hybrid) })
        {
            var button = Segment(text, "gpu", mode);
            button.Checked += async (_, _) =>
            {
                if (!_updating)
                {
                    await RequestGpuMode(mode);
                }
            };
            GraphicsPanel.Children.Add(button);
        }
    }

    /// <summary>Queues MSI's firmware switch; it takes effect at the next full shutdown or restart.</summary>
    private async Task RequestGpuMode(FirmwareGpuMode mode)
    {
        GraphicsPanel.IsEnabled = false;
        string? error;
        try
        {
            error = await App.Current.Hardware.RequestGpuModeAsync(mode);
        }
        catch (Exception ex)
        {
            Log.Error("GPU mode switch failed", ex);
            error = "The graphics switch didn't go through. Try again, or use Settings.";
        }
        finally
        {
            GraphicsPanel.IsEnabled = true; // the flyout is reused: never left greyed out
        }

        RefreshControls();
        if (error is not null)
        {
            // After the refresh, which sets the hint from the switch's state (and would clear this).
            GraphicsHint.Text = error;
            GraphicsHint.Visibility = Visibility.Visible;
        }
    }

    private void OnRestartForGpu(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        if (MessageBox.Show("Restart now to switch the graphics mode?\n\nSave your work first. The laptop shuts down fully and starts again by itself. If it stays off, press the power button.",
                "Wattkin", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            FirmwareGpuSwitch.ShutDownToApply();
        }
    }
    private void RefreshControls()
    {
        var app = App.Current;
        var settings = app.Store.Current;
        var state = app.Hardware.State;
        _updating = true;
        try
        {
            var key = string.Join("|", settings.Profiles.Select(p => p.Name));
            if (key != _profilesKey)
            {
                _profilesKey = key;
                ProfilePanel.Children.Clear();
                // One row when every name fits; otherwise two rows, still filling the width.
                var available = (ProfilePanel.ActualWidth > 0 ? ProfilePanel.ActualWidth : 330) / settings.Profiles.Count;
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var widest = settings.Profiles.Max(p => new System.Windows.Media.FormattedText(p.Name, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new System.Windows.Media.Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 12, Foreground, dpi).Width);
                ProfilePanel.Rows = widest + 24 <= available ? 1 : 2;
                foreach (var profile in settings.Profiles)
                {
                    var name = profile.Name;
                    var button = Segment(name, "profile", name);
                    button.Margin = new Thickness(2, 0, 2, ProfilePanel.Rows > 1 ? 4 : 0);
                    button.Checked += (_, _) => ProfileDetails.Text = Fmt.ProfileDetails(profile);
                    button.Checked += async (_, _) =>
                    {
                        if (!_updating)
                        {
                            await app.Hardware.ApplyProfileAsync(name);
                        }
                    };
                    ProfilePanel.Children.Add(button);
                }
            }

            // Until the EC has been read (needs admin) the real settings are unknown: highlight nothing.
            var known = state.Available && settings.HardwareAdopted;
            foreach (RadioButton button in ProfilePanel.Children)
            {
                button.IsChecked = known && (string)button.Tag == settings.ActiveProfile;
            }

            ProfileDetails.Text = known && settings.FindProfile(settings.ActiveProfile) is { } active ? Fmt.ProfileDetails(active) : "";
            ProfileDetails.Visibility = ProfileDetails.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            var isPreset = settings.ChargeResumeBelow is null && LimitChoices.Contains(settings.ChargeLimit) && !settings.FullChargeOnce;
            foreach (RadioButton button in LimitPanel.Children)
            {
                var tag = (int)button.Tag;
                button.IsChecked = known && (isPreset ? tag == settings.ChargeLimit : tag == -1);
                button.IsEnabled = (state.CanWrite && !app.Hardware.IsCalibrating) || tag == -1;
            }

            foreach (RadioButton button in KeyboardPanel.Children)
            {
                button.IsChecked = state.KeyboardLevel == (int)button.Tag;
                button.IsEnabled = state.CanWrite;
            }

            foreach (RadioButton button in ProfilePanel.Children)
            {
                button.IsEnabled = state.CanWrite;
            }

            // Colour and the delay only where the light is over USB (auto-off is Wattkin's own there).
            var usbLight = state.Caps?.HasKeyboardColor == true;
            KeyboardColorPanel.Visibility = AutoOffDelayHost.Visibility = usbLight ? Visibility.Visible : Visibility.Collapsed;
            KeyboardColorPanel.IsEnabled = state.CanWrite;
            _keyboardColors?.Show(settings.KeyboardColor);
            KeyboardTimeoutSection.Visibility = state.Caps?.HasKeyboardTimeout == true ? Visibility.Visible : Visibility.Collapsed;
            foreach (RadioButton button in KeyboardTimeoutPanel.Children)
            {
                button.IsChecked = known && (KeyboardTimeout)button.Tag == settings.KeyboardTimeout;
                button.IsEnabled = state.CanWrite;
            }

            _autoOffDelay.Show(settings.KeyboardAutoOffSeconds);
            AutoOffDelayHost.IsEnabled = state.CanWrite && settings.KeyboardTimeout != KeyboardTimeout.AlwaysOn;

            RefreshScreen();
            RefreshGraphics(state);

            ShowBanner(state);
            WidgetButton.Foreground = app.IsWidgetVisible ? (System.Windows.Media.Brush)FindResource("Lc.Accent") : (System.Windows.Media.Brush)FindResource("Lc.SubText");
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>The screen slider: the backlight, and left of 0% the extra dim. Not moved while it's being dragged.</summary>
    private void RefreshScreen()
    {
        var screen = App.Current.Screen;
        var updating = _updating;
        _updating = true;
        try
        {
            ScreenSlider.Maximum = screen.MaxPosition;
            if (!ScreenSlider.IsMouseCaptureWithin && DateTime.UtcNow - _screenMovedUtc > ScreenBrightness.UserMoveSettles)
            {
                ScreenSlider.Value = screen.Position;
                ScreenValue.Text = ScreenBrightness.SliderText(screen.Position, screen.HasBacklight);
            }

            ScreenSlider.IsEnabled = App.Current.Hardware.ControlsHardware;
            ScreenHint.Text = screen.Problem ?? "";
            ScreenHint.Visibility = screen.Problem is null ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _updating = updating;
        }
    }

    private void OnScreenSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || !IsLoaded)
        {
            return;
        }

        var position = (int)Math.Round(e.NewValue);
        _screenMovedUtc = DateTime.UtcNow;
        _screenSettled.Stop();
        _screenSettled.Start(); // then shows where the screen really is (a dim f.lux refused, say)
        ScreenValue.Text = ScreenBrightness.SliderText(position, App.Current.Screen.HasBacklight);
        App.Current.Screen.RequestPosition(position);
    }

    private void RefreshGraphics(HardwareState state)
    {
        var gpu = state.Gpu;
        var available = state.Caps?.CanSwitchGpuMode == true && gpu is { SwitchSupported: true, IntegratedSupported: true } && gpu.Current != FirmwareGpuMode.Discrete;
        GraphicsSection.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available)
        {
            return;
        }

        foreach (RadioButton button in GraphicsPanel.Children)
        {
            button.IsChecked = (FirmwareGpuMode)button.Tag == gpu!.Requested;
        }

        // Only something to say while a switch is waiting for the restart.
        GraphicsHint.Text = gpu!.SwitchPending
            ? gpu.Requested == FirmwareGpuMode.Hybrid ? "The RTX comes back after a restart." : "The RTX switches off after a restart."
            : "";
        GraphicsHint.Visibility = RestartButton.Visibility = gpu.SwitchPending ? Visibility.Visible : Visibility.Collapsed;
    }

    private enum BannerAction
    {
        None,
        Elevate,
        ShowMsiHelp,
        SaveReport,
    }

    private BannerAction _bannerAction;

    private void ShowBanner(HardwareState state)
    {
        (string? text, _bannerAction) = (Elevation.IsElevated, state) switch
        {
            (false, _) => ("Monitoring works now. Battery limits, profiles, fans and keyboard need administrator rights.", BannerAction.Elevate),
            (_, { MsiSoftwareRunning: true }) => ("MSI Center is still running and can overwrite these settings. Close or uninstall it.", BannerAction.ShowMsiHelp),
            // Hardware controls are off: a one-click report is what someone needs to help.
            (_, { Problem: { } problem, CanWrite: false }) => (problem, BannerAction.SaveReport),
            _ => ((string?)null, BannerAction.None),
        };

        Banner.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        BannerText.Text = text;
        BannerButton.Content = _bannerAction switch
        {
            BannerAction.Elevate => "Restart as administrator",
            BannerAction.ShowMsiHelp => "Show me how",
            BannerAction.SaveReport => "Save a diagnostics report",
            _ => null,
        };
        BannerButton.Visibility = _bannerAction == BannerAction.None ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnBannerAction(object sender, RoutedEventArgs e)
    {
        switch (_bannerAction)
        {
            case BannerAction.Elevate:
                App.Current.RestartElevated();
                break;
            case BannerAction.ShowMsiHelp:
                App.Current.OpenSettings("general");
                break;
            case BannerAction.SaveReport:
                BannerButton.IsEnabled = false;
                BannerButton.Content = "Reading the hardware…";
                await SaveReportToDesktopAsync();
                BannerButton.IsEnabled = true;
                BannerButton.Content = "Save a diagnostics report";
                break;
        }
    }

    /// <summary>Saves the read-only report on the desktop and shows it in Explorer, ready to attach to an issue.</summary>
    private static async Task SaveReportToDesktopAsync()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"Wattkin-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt");
        try
        {
            await Task.Run(() => Diagnostics.Run(path));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Diagnostics failed", ex);
            MessageBox.Show(ex.Message, "Diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnSettings(object sender, RoutedEventArgs e) => App.Current.OpenSettings();

    private void OnWidget(object sender, RoutedEventArgs e)
    {
        App.Current.SetWidgetVisible(!App.Current.IsWidgetVisible);
        RefreshControls();
    }
}
