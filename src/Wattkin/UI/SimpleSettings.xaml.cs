using System.Windows;
using System.Windows.Controls;
using Wattkin.Features;
using Wattkin.Hardware;
using Wattkin.Monitoring;
using Wattkin.Platform;
using Wattkin.Settings;

namespace Wattkin.UI;

/// <summary>
/// The simple settings page: the everyday choices as Windows 11-style rows, one control each. It shares
/// the settings and hardware with the expert tabs; the settings window tells it when anything changed.
/// </summary>
public partial class SimpleSettings : UserControl
{
    private static readonly int[] Limits = [60, 70, 80, 90, 100];
    private bool _loading;
    private string _profileNames = "";
    private CalibrationSuggestion? _suggestion;
    private readonly ColorSwatches _keyboardColors;
    private readonly AutoOffDelayStepper _autoOffDelay = new();

    public SimpleSettings()
    {
        InitializeComponent();

        // A steady 48 px per wheel notch (Windows' usual three lines), not a whole group at a time.
        Scroller.PreviewMouseWheel += (_, e) =>
        {
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - (e.Delta / 120.0 * 48));
            e.Handled = true;
        };
        foreach (var limit in Limits)
        {
            var button = Segment($"{limit}%", "simpleLimit", limit);
            button.Checked += async (_, _) =>
            {
                if (!_loading)
                {
                    await App.Current.Hardware.SetChargeLimitAsync(limit, null);
                }
            };
            LimitPanel.Children.Add(button);
        }

        string[] levels = ["Off", "Low", "Mid", "High"];
        for (var i = 0; i < levels.Length; i++)
        {
            var level = i;
            var button = Segment(levels[i], "simpleKb", level);
            button.Checked += async (_, _) =>
            {
                if (!_loading)
                {
                    await App.Current.Hardware.SetKeyboardLevelAsync(level);
                }
            };
            KeyboardPanel.Children.Add(button);
        }

        _keyboardColors = new ColorSwatches(KeyboardColorPanel, "simpleKbColor");
        AutoOffDelayHost.Child = _autoOffDelay.Control;

        foreach (var (text, mode) in new[] { ("Integrated", FirmwareGpuMode.Integrated), ("Hybrid", FirmwareGpuMode.Hybrid) })
        {
            var button = Segment(text, "simpleGpu", mode);
            button.Checked += async (_, _) =>
            {
                if (!_loading)
                {
                    GpuPanel.IsEnabled = false;
                    string? error;
                    try
                    {
                        error = await App.Current.Hardware.RequestGpuModeAsync(mode);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("GPU mode switch failed", ex);
                        error = "The graphics switch didn't go through. Try again.";
                    }
                    finally
                    {
                        GpuPanel.IsEnabled = true;
                    }

                    if (error is not null)
                    {
                        MessageBox.Show(Window.GetWindow(this), error, "Graphics", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }

                    RefreshState();
                }
            };
            GpuPanel.Children.Add(button);
        }
    }

    /// <summary>"Expert view" at the bottom of the page.</summary>
    public event Action? ExpertRequested;

    private RadioButton Segment(string text, string group, object tag) =>
        new() { Content = text, Style = (Style)FindResource("Lc.Segment"), GroupName = group, Tag = tag, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(6, 6, 6, 6) };

    // ---- loading ------------------------------------------------------------------------------

    public void Load()
    {
        var s = App.Current.Store.Current;
        _loading = true;
        try
        {
            foreach (RadioButton b in LimitPanel.Children)
            {
                b.IsChecked = s.ChargeResumeBelow is null && s.ChargeLimit == (int)b.Tag;
            }

            LimitText.Text = LimitDescription(s);
            FullOnceToggle.IsChecked = s.FullChargeOnce;

            var names = string.Join("|", s.Profiles.Select(p => p.Name));
            if (names != _profileNames)
            {
                _profileNames = names;
                foreach (var box in new[] { AcProfile, DcProfile })
                {
                    Choices.FillProfiles(box, s.Profiles.Select(p => p.Name));
                }
            }

            Choices.Select(AcProfile, s.AcProfile ?? "");
            Choices.Select(DcProfile, s.BatteryProfile ?? "");
            AcText.Text = Describe(s.FindProfile(s.AcProfile));
            DcText.Text = Describe(s.FindProfile(s.BatteryProfile))
                + (s.AutoSwitchProfiles && s.LowBatteryThreshold > 0 && s.LowBatteryProfile is { } low ? $" At {s.LowBatteryThreshold}% or less it switches to {low} by itself." : "");

            AutoOffToggle.IsChecked = s.KeyboardTimeout != KeyboardTimeout.AlwaysOn;
            _keyboardColors.Show(s.KeyboardColor);
            _autoOffDelay.Show(s.KeyboardAutoOffSeconds);
            ScreenKeysToggle.IsChecked = s.ScreenHotkeys;
            TrayToggle.IsChecked = s.TrayShowsWatts;
            WidgetToggle.IsChecked = App.Current.IsWidgetVisible;
            UpdateToggle.IsChecked = s.AutoUpdate;
        }
        finally
        {
            _loading = false;
        }
    }

    private static string Describe(Profile? p) => p is null ? "Keeps whichever profile is on." : char.ToUpperInvariant(Fmt.ProfileSummary(p)[0]) + Fmt.ProfileSummary(p)[1..] + ".";

    private static string LimitDescription(AppSettings s)
    {
        if (s.ChargeResumeBelow is { } resume)
        {
            return $"Set in Expert view: charges to {s.ChargeLimit}% and starts again below {resume}%.";
        }

        if (!Limits.Contains(s.ChargeLimit))
        {
            return $"Set in Expert view: {s.ChargeLimit}%.";
        }

        return s.ChargeLimit switch
        {
            <= 60 => "Kindest to the battery when it's plugged in most of the time.",
            <= 80 => "A good balance between battery lifespan and time unplugged.",
            < 100 => "More time unplugged, a little more wear.",
            _ => "The most time unplugged. Batteries wear fastest kept full.",
        };
    }

    public void SetCalibrationSuggestion(CalibrationSuggestion? suggestion)
    {
        _suggestion = suggestion;
        RefreshState();
    }

    public void RefreshStartup(bool enabled)
    {
        _loading = true;
        StartToggle.IsChecked = enabled;
        _loading = false;
    }

    /// <summary>Everything that follows the hardware: enabled states, the keyboard level, graphics, notices.</summary>
    public void RefreshState()
    {
        var app = App.Current;
        var state = app.Hardware.State;
        _loading = true;
        try
        {
            var chargeEditable = state.CanWrite && !app.Hardware.IsCalibrating;
            LimitPanel.IsEnabled = FullOnceToggle.IsEnabled = chargeEditable;
            foreach (RadioButton b in KeyboardPanel.Children)
            {
                b.IsChecked = state.KeyboardLevel == (int)b.Tag;
            }

            KeyboardPanel.IsEnabled = state.CanWrite && state.Caps?.HasKeyboardLight != false;
            var usbLight = state.Caps?.HasKeyboardColor == true;
            KeyboardColorRow.Visibility = AutoOffDelayHost.Visibility = usbLight ? Visibility.Visible : Visibility.Collapsed;
            KeyboardColorPanel.IsEnabled = state.CanWrite;
            AutoOffDelayHost.IsEnabled = state.CanWrite && app.Store.Current.KeyboardTimeout != KeyboardTimeout.AlwaysOn;
            AutoOffText.Text = usbLight
                ? "When the laptop's keyboard isn't used for that long; its next key press brings the light back."
                : "After about 10 seconds; any key brings it back.";
            AutoOffToggle.IsEnabled = state.CanWrite && state.Caps?.HasKeyboardTimeout != false;
            AcProfile.IsEnabled = DcProfile.IsEnabled = state.CanWrite;
            ScreenKeysToggle.IsEnabled = app.Hardware.ControlsHardware;
            ScreenKeysText.Text = app.Screen.Problem is { } screenProblem && app.Store.Current.ScreenHotkeys
                ? screenProblem
                : "Alt+Page Down dims, Alt+Page Up brightens, down past the lowest setting, like f.lux.";

            RefreshGraphics(state);
            RefreshCalibration(state);
            RefreshNotice(state);
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshGraphics(HardwareState state)
    {
        var gpu = state.Gpu;
        var available = state.Caps?.CanSwitchGpuMode == true && gpu is { SwitchSupported: true, IntegratedSupported: true } && gpu.Current != FirmwareGpuMode.Discrete;
        GraphicsGroup.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available)
        {
            return;
        }

        foreach (RadioButton b in GpuPanel.Children)
        {
            b.IsChecked = (FirmwareGpuMode)b.Tag == gpu!.Requested;
        }

        GpuText.Text = gpu!.SwitchPending
            ? gpu.Requested == FirmwareGpuMode.Hybrid ? "The RTX comes back after a restart." : "The RTX switches off after a restart."
            : gpu.Current == FirmwareGpuMode.Hybrid
                ? "Hybrid: the RTX is there when an app needs it, and sleeps by itself otherwise."
                : "Integrated: the RTX is switched off. Choose Hybrid to use it (takes a restart).";
        RestartButton.Visibility = gpu.SwitchPending ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshCalibration(HardwareState state)
    {
        var running = App.Current.Store.Current.Calibration is not null;
        if (running)
        {
            CalibrationRow.Visibility = Visibility.Visible;
            CalibrationTitle.Text = "Calibrating the battery";
            var c = state.Calibration;
            CalibrationText.Text = c is null ? "Starting…" : $"{c.Message} About {Fmt.Duration(Fmt.Steady(c.TotalEta))} to go. Keep the charger plugged in.";
            CalibrationBar.Visibility = Visibility.Visible;
            CalibrationBar.Value = c is null ? 0 : c.Phase == CalibrationPhase.Discharging
                ? Math.Clamp(((100 - (c.Percent ?? 100)) / Math.Max(1, 100 - c.TargetPercent) * 50) + 50, 50, 100)
                : Math.Clamp((c.Percent ?? 0) / 2, 0, 50);
            CalibrationButton.Content = "Cancel";
            CalibrationButton.IsEnabled = true;
        }
        else if (_suggestion is { } advice && state.Caps?.CanSimulateUnplug == true)
        {
            CalibrationRow.Visibility = Visibility.Visible;
            CalibrationTitle.Text = "Recalibrate the battery gauge";
            CalibrationText.Text = $"It reports {advice.NowWh:0.0} Wh, down from about {advice.TypicalWh:0} Wh. A drop this quick is usually the gauge drifting, not wear. Takes a few hours on the charger.";
            CalibrationBar.Visibility = Visibility.Collapsed;
            CalibrationButton.Content = "Calibrate…";
            CalibrationButton.IsEnabled = state.CanWrite;
        }
        else
        {
            CalibrationRow.Visibility = Visibility.Collapsed;
        }
    }

    private Action? _noticeAction;

    private void RefreshNotice(HardwareState state)
    {
        (string Title, string Text, string Button, Action Act)? notice = null;
        if (!Elevation.IsElevated)
        {
            notice = ("Hardware controls are off", "Battery limits, modes and the keyboard need Wattkin to run as administrator.", "Restart as administrator", () => App.Current.RestartElevated());
        }
        else if (state.MsiSoftwareRunning)
        {
            notice = ("MSI Center is still running", "It can overwrite these settings. Uninstall MSI Center, then MSI NBFoundation Service, in Settings › Apps.", "Open Apps", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true }));
        }
        else if (state.Caps is { Support: EcSupport.Unknown } && state.Problem is { } problem)
        {
            notice = ("Monitoring only on this laptop", problem, "Details", () => ExpertRequested?.Invoke());
        }

        NoticeGroup.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
        if (notice is { } n)
        {
            NoticeTitle.Text = n.Title;
            NoticeText.Text = n.Text;
            NoticeButton.Content = n.Button;
            _noticeAction = n.Act;
        }
    }

    /// <summary>Live: the battery health line.</summary>
    public void Update(Snapshot s)
    {
        if (App.Current.Monitor.BatteryInfo is { DesignedCapacityMWh: > 0 } info)
        {
            var health = 100.0 * info.FullChargedCapacityMWh / info.DesignedCapacityMWh;
            HealthPercent.Text = $"{health:0}%";
            HealthText.Text = $"Holds {info.FullChargedCapacityMWh / 1000.0:0.0} Wh when full, of {info.DesignedCapacityMWh / 1000.0:0.0} Wh when new. Now {Fmt.Percent(s.Power.Percent)}.";
        }

        if (App.Current.Store.Current.Calibration is not null)
        {
            RefreshCalibration(App.Current.Hardware.State);
        }
    }

    public void RefreshUpdates(UpdateState? state)
    {
        UpdateText.Text = state is null ? "Checks GitHub once a day; updates are signed." : Updater.CanInstall ? state.Message : "Updates install into the copy in Program Files.";
    }

    // ---- handlers -----------------------------------------------------------------------------

    private async void OnFullOnce(object sender, RoutedEventArgs e) => await App.Current.Hardware.SetFullChargeOnceAsync(FullOnceToggle.IsChecked == true);

    private void OnProfileChoice(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AcProfile.SelectedItem is null || DcProfile.SelectedItem is null)
        {
            return;
        }

        App.Current.Store.Update(s =>
        {
            s.AutoSwitchProfiles = true;
            s.AcProfile = Choices.ProfileOf(AcProfile);
            s.BatteryProfile = Choices.ProfileOf(DcProfile);
        });
    }

    private void OnScreenKeys(object sender, RoutedEventArgs e)
    {
        var on = ScreenKeysToggle.IsChecked == true;
        App.Current.Store.Update(s => s.ScreenHotkeys = on);
        App.Current.Screen.SetHotkeys(on);
        RefreshState();
    }

    private async void OnAutoOff(object sender, RoutedEventArgs e) =>
        await App.Current.Hardware.SetKeyboardTimeoutAsync(AutoOffToggle.IsChecked == true ? KeyboardTimeout.AutoOff : KeyboardTimeout.AlwaysOn);

    private void OnTray(object sender, RoutedEventArgs e) => App.Current.Store.Update(s => s.TrayShowsWatts = TrayToggle.IsChecked == true);

    private void OnWidget(object sender, RoutedEventArgs e) => App.Current.SetWidgetVisible(WidgetToggle.IsChecked == true);

    private void OnUpdates(object sender, RoutedEventArgs e) => App.Current.Store.Update(s => s.AutoUpdate = UpdateToggle.IsChecked == true);

    private async void OnStartWithWindows(object sender, RoutedEventArgs e)
    {
        var wanted = StartToggle.IsChecked == true;
        if (!Elevation.IsElevated || !App.Current.Hardware.ControlsHardware)
        {
            RefreshStartup(!wanted);
            if (!Elevation.IsElevated && MessageBox.Show(Window.GetWindow(this), "Setting up start-with-Windows needs administrator rights. Restart Wattkin as administrator now?", "Wattkin", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                App.Current.RestartElevated();
            }

            return;
        }

        StartToggle.IsEnabled = false;
        var error = await Task.Run(() => wanted ? StartupTask.Enable() : StartupTask.Disable());
        StartToggle.IsEnabled = true;
        if (error is not null)
        {
            MessageBox.Show(Window.GetWindow(this), error, "Start with Windows", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshStartup(await Task.Run(StartupTask.IsEnabled));
    }

    private async void OnCalibration(object sender, RoutedEventArgs e)
    {
        // The hardware calls wait for the hardware lock: off the UI thread.
        var owner = Window.GetWindow(this);
        if (App.Current.Store.Current.Calibration is not null)
        {
            if (MessageBox.Show(owner, "Stop the calibration? Your normal charge limit takes over again.", "Battery calibration", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await Task.Run(App.Current.Hardware.CancelCalibration);
            }
        }
        else if (MessageBox.Show(owner,
                     "Calibration will:\n\n1. Charge the battery to 100%.\n2. Run it down to about 8% with the charger still plugged in.\n3. Hand back to your normal charge limit.\n\nKeep the charger connected the whole time. The laptop stays awake, you can keep using it, and you can cancel any time.\n\nStart now?",
                     "Battery calibration", MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK
                 && await Task.Run(() => App.Current.Hardware.StartCalibration(fast: false)) is { } error)
        {
            MessageBox.Show(owner, error, "Battery calibration", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshState();
    }

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this), "Restart now to switch the graphics mode?\n\nSave your work first. The laptop shuts down fully and starts again by itself. If it stays off, press the power button.",
                "Graphics", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            FirmwareGpuSwitch.ShutDownToApply();
        }
    }

    private void OnNotice(object sender, RoutedEventArgs e) => _noticeAction?.Invoke();

    private void OnExpert(object sender, RoutedEventArgs e) => ExpertRequested?.Invoke();
}
