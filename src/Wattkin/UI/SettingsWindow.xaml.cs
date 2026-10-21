using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Wattkin.Features;
using Wattkin.Hardware;
using Wattkin.Monitoring;
using Wattkin.Platform;
using Wattkin.Settings;

namespace Wattkin.UI;

public partial class SettingsWindow : Window
{
    private bool _loading;
    private bool _expert;
    private Profile? _editing;
    private IReadOnlyList<CapacityPoint> _history = [];
    private readonly AutoOffDelayStepper _kbDelay = new();
    private ColorSwatches? _kbColors;

    public SettingsWindow()
    {
        InitializeComponent();
        FanEditor.Changed += UpdateFanProblem;
        FanTableEditor.Changed += UpdateFanProblem;
        Simple.ExpertRequested += () => SetView(expert: true, save: true);
        SourceInitialized += (_, _) => WindowEffects.SetDarkTitleBar(new WindowInteropHelper(this).Handle);
        var app = App.Current;
        app.Hardware.StateChanged += OnHardwareChanged;
        app.Hardware.FanTestChanged += OnFanTestChanged;
        app.Store.Changed += OnStoreChanged;
        app.Screen.Changed += OnScreenChanged;
        app.Monitor.Demand(this, fast: true);
        if (app.Updates is { } updates)
        {
            updates.Changed += OnUpdatesChanged;
        }

        _registersTimer.Tick += async (_, _) => await RefreshRegistersAsync();
        Closed += (_, _) =>
        {
            _registersTimer.Stop();
            app.Hardware.StateChanged -= OnHardwareChanged;
            app.Hardware.FanTestChanged -= OnFanTestChanged;
            app.Store.Changed -= OnStoreChanged;
            app.Screen.Changed -= OnScreenChanged;
            app.Monitor.Demand(this, fast: false);
            if (app.Updates is { } updates)
            {
                updates.Changed -= OnUpdatesChanged;
            }
        };

        BuildChoices();
        Tabs.SelectionChanged += (_, e) =>
        {
            if (e.OriginalSource == Tabs)
            {
                DemandUpdates();
            }
        };
        SetView(app.Store.Current.ExpertView, save: false);
        LoadSettings(full: true);
        RefreshFromState();
        ShowStatus(FanTestText, app.Hardware.FanTestStatus);
        if (app.Monitor.Latest is { } latest)
        {
            Update(latest);
        }

        RefreshUpdates();
        RefreshScreen();
        _ = LoadHistoryAsync();
        _ = LoadFanProfilesAsync();
        _ = RefreshStartupTaskAsync();
        _ = app.Screen.RefreshAsync();
        app.Hardware.SafeRefreshStateAsync();
    }

    private void OnHardwareChanged() => Dispatcher.BeginInvoke(RefreshFromState);

    private void OnScreenChanged() => Dispatcher.BeginInvoke(() =>
    {
        RefreshScreen();
        Simple.RefreshState();
    });

    /// <summary>
    /// Live readings while open; per-app CPU only while the System tab (Expert) is showing, and the
    /// controller registers only while the Debug tab is.
    /// </summary>
    private void DemandUpdates()
    {
        App.Current.Monitor.Demand(this, fast: true, processes: _expert && Tabs.SelectedItem is TabItem { Tag: "system" });
        if (_expert && Tabs.SelectedItem is TabItem { Tag: "debug" })
        {
            if (!_registersTimer.IsEnabled)
            {
                _registersTimer.Start();
                _ = RefreshRegistersAsync();
            }
        }
        else
        {
            _registersTimer.Stop();
        }
    }

    private void OnUpdatesChanged() => Dispatcher.BeginInvoke(RefreshUpdates);

    private void RefreshUpdates()
    {
        if (App.Current.Updates is not { } updates)
        {
            UpdateStatus.Text = "";
            CheckUpdatesButton.IsEnabled = false;
            Simple.RefreshUpdates(null);
            return;
        }

        var state = updates.State;
        Simple.RefreshUpdates(state);
        UpdateStatus.Text = !Features.Updater.Enabled ? "Updates are turned off in this build."
            : Features.Updater.CanInstall ? state.Message
            : "Updates install into the copy in Program Files. Install Wattkin to get them.";
        CheckUpdatesButton.IsEnabled = state.Phase is not (Features.UpdatePhase.Checking or Features.UpdatePhase.Installing);
        InstallUpdateButton.Visibility = state.Available is not null && Features.Updater.CanInstall ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.IsEnabled = state.Phase != Features.UpdatePhase.Installing;
        if (state.Available is { } release)
        {
            InstallUpdateButton.Content = $"Install {release.Version.ToString(3)} now";
        }
    }

    private void OnAutoUpdate(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            App.Current.Store.Update(s => s.AutoUpdate = AutoUpdateToggle.IsChecked == true);
        }
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        if (App.Current.Updates is { } updates)
        {
            await updates.RunAsync(userAsked: true);
        }
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (App.Current.Updates is { } updates)
        {
            await updates.InstallNowAsync();
        }
    }

    /// <summary>Background changes refresh the mirror controls, never an edit in progress.</summary>
    private void OnStoreChanged() => Dispatcher.BeginInvoke(() => LoadSettings(full: false));

    private async Task RefreshStartupTaskAsync()
    {
        var enabled = await Task.Run(StartupTask.IsEnabled);
        _loading = true;
        StartWithWindows.IsChecked = enabled;
        _loading = false;
        Simple.RefreshStartup(enabled);
    }

    public void SelectTab(string? tag)
    {
        if (tag is null)
        {
            return;
        }

        foreach (TabItem item in Tabs.Items)
        {
            if ((string)item.Tag == tag && item.Visibility == Visibility.Visible)
            {
                Tabs.SelectedItem = item;
            }
        }
    }

    /// <summary>Simple: the everyday settings on one page. Expert: every control, in tabs.</summary>
    public void SetView(bool expert, bool save)
    {
        _expert = expert;
        _loading = true;
        try
        {
            (expert ? ExpertViewButton : SimpleViewButton).IsChecked = true;
        }
        finally
        {
            _loading = false;
        }

        Tabs.Visibility = expert ? Visibility.Visible : Visibility.Collapsed;
        Simple.Visibility = expert ? Visibility.Collapsed : Visibility.Visible;
        DemandUpdates();

        if (save && App.Current.Store.Current.ExpertView != expert)
        {
            App.Current.Store.Update(s => s.ExpertView = expert);
        }
    }

    private void OnViewChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            SetView(ExpertViewButton.IsChecked == true, save: true);
        }
    }

    // ---- choices -----------------------------------------------------------------------------

    private static ComboBoxItem Item(string text, object? tag) => new() { Content = text, Tag = tag };

    /// <summary>Selects the item for a value; a value the list doesn't offer is added, so saving can't silently erase it.</summary>
    private static void Select(ComboBox box, object? tag)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (Equals(item.Tag, tag))
            {
                box.SelectedItem = item;
                return;
            }
        }

        if (tag is null)
        {
            box.SelectedIndex = 0;
            return;
        }

        var extra = Item(tag switch
        {
            int hz when box.Name == "PRefresh" => $"{hz} Hz (not available right now)",
            int mhz when box.Name == "PMaxFreq" => $"{mhz / 1000.0:0.0#} GHz",
            int epp when box.Name == "PEpp" => $"{epp}",
            ShiftMode.Sport => "Sport (not offered on this laptop)",
            int v => $"{v}%",
            _ => tag.ToString() ?? "?",
        }, tag);
        box.Items.Add(extra);
        box.SelectedItem = extra;
    }

    private static T? TagOf<T>(ComboBox box) => box.SelectedItem is ComboBoxItem { Tag: T value } ? value : default;

    private bool? _shiftChoicesSport;

    /// <summary>The CPU power list: Sport only where the laptop has it (known once the hardware has been read).</summary>
    private void RebuildShiftChoices(bool sport)
    {
        if (_shiftChoicesSport == sport)
        {
            return;
        }

        _shiftChoicesSport = sport;
        var selected = PShift.SelectedItem as ComboBoxItem;
        PShift.Items.Clear();
        PShift.Items.Add(Item("Unchanged (for limits set in the BIOS or ThrottleStop)", null));
        PShift.Items.Add(Item("Eco: lowest power (MSI \"Super Battery\")", ShiftMode.Eco));
        PShift.Items.Add(Item("Balanced (MSI \"Balanced\" / \"Silent\")", ShiftMode.Balanced));
        if (sport)
        {
            PShift.Items.Add(Item("Sport: between Balanced and Extreme", ShiftMode.Sport));
        }

        PShift.Items.Add(Item("Extreme: highest power limits", ShiftMode.Turbo));
        var loading = _selectingProfile;
        _selectingProfile = true;
        Select(PShift, selected is not null ? selected.Tag : _editing?.Performance); // an unsaved choice stays
        _selectingProfile = loading;
    }

    private void BuildChoices()
    {
        RebuildShiftChoices(sport: false);

        PMaxFreq.Items.Add(Item("Unchanged", null));
        PMaxFreq.Items.Add(Item("No cap", (int?)0));
        foreach (var mhz in new[] { 1500, 1800, 2000, 2200, 2400, 2600, 2800, 3000, 3300, 3600, 4000, 4400 })
        {
            PMaxFreq.Items.Add(Item($"{mhz / 1000.0:0.0#} GHz", (int?)mhz));
        }

        PEpp.Items.Add(Item("Unchanged", null));
        foreach (var (value, text) in new[] { (0, "0: performance first"), (20, "20"), (33, "33"), (50, "50: balanced"), (70, "70"), (85, "85"), (100, "100: efficiency first") })
        {
            PEpp.Items.Add(Item(text, (int?)value));
        }

        // PFan: built from the fan profiles in LoadSettings (RebuildFanChoices).

        PWindows.Items.Add(Item("Unchanged", WindowsPowerMode.Unchanged));
        PWindows.Items.Add(Item("Best power efficiency", WindowsPowerMode.BestEfficiency));
        PWindows.Items.Add(Item("Balanced", WindowsPowerMode.Balanced));
        PWindows.Items.Add(Item("Best performance", WindowsPowerMode.BestPerformance));

        PCpuBoost.Items.Add(Item("Unchanged", null));
        PCpuBoost.Items.Add(Item("On", true));
        PCpuBoost.Items.Add(Item("Off: caps at base clock, big battery saver", false));

        PMaxCpu.Items.Add(Item("Unchanged", null));
        foreach (var value in new[] { 100, 99, 90, 80, 70, 50 })
        {
            PMaxCpu.Items.Add(Item(value == 99 ? "99% (also disables turbo)" : $"{value}%", (int?)value));
        }

        PRefresh.Items.Add(Item("Unchanged", null));
        foreach (var hz in DisplayModes.SupportedRefreshRates())
        {
            PRefresh.Items.Add(Item($"{hz} Hz", (int?)hz));
        }

        PKeyboard.Items.Add(Item("Unchanged", null));
        string[] levels = ["Off", "Low", "Mid", "High"];
        for (var i = 0; i < levels.Length; i++)
        {
            PKeyboard.Items.Add(Item(levels[i], (int?)i));
        }



        LowThreshold.Items.Add(Item("Off", 0));
        foreach (var value in new[] { 10, 15, 20, 25, 30, 40, 50 })
        {
            LowThreshold.Items.Add(Item($"≤ {value}%", value));
        }

        for (var i = 0; i < levels.Length; i++)
        {
            var level = i;
            var button = new RadioButton { Content = levels[i], Style = (Style)FindResource("Lc.Segment"), GroupName = "kbs", Tag = level };
            button.Checked += async (_, _) =>
            {
                if (!_loading)
                {
                    await App.Current.Hardware.SetKeyboardLevelAsync(level);
                }
            };
            KbLevelPanel.Children.Add(button);
        }

        foreach (var (text, force) in new[] { ("Profile", FanForce.None), ("Auto", FanForce.Auto), ("Silent", FanForce.Silent), ("Curve", FanForce.Curve), ("Full speed", FanForce.Full) })
        {
            var button = new RadioButton { Content = text, Style = (Style)FindResource("Lc.Segment"), GroupName = "fanforce", Tag = force };
            button.Checked += async (_, _) =>
            {
                if (!_loading)
                {
                    await App.Current.Hardware.ForceFansAsync(force);
                }
            };
            FanForcePanel.Children.Add(button);
        }

        _kbColors = new ColorSwatches(KbColorPanel, "settingsKbColor");
        KbDelayHost.Child = _kbDelay.Control;

        (string Text, KeyboardTimeout Value)[] timeouts = [("Always on", KeyboardTimeout.AlwaysOn), ("Auto-off", KeyboardTimeout.AutoOff), ("Auto-off on battery only", KeyboardTimeout.AutoOffOnBattery)];
        foreach (var (text, value) in timeouts)
        {
            var button = new RadioButton { Content = text, Style = (Style)FindResource("Lc.Segment"), GroupName = "kbt", Tag = value };
            button.Checked += async (_, _) =>
            {
                if (!_loading)
                {
                    await App.Current.Hardware.SetKeyboardTimeoutAsync(value);
                }
            };
            KbTimeoutPanel.Children.Add(button);
        }
    }

    private string _profileNames = "";
    private bool _limitEdited;

    /// <param name="full">Also reload the charge-limit sliders and the profile editor (opening, or after the user saved).</param>
    private void LoadSettings(bool full)
    {
        _loading = true;
        try
        {
            var s = App.Current.Store.Current;
            if (full || !_limitEdited)
            {
                StopSlider.Value = s.ChargeLimit;
                CustomResume.IsChecked = s.ChargeResumeBelow is not null;
                ResumeSlider.Value = s.ChargeResumeBelow ?? ChargePolicy.MaxResumeFor(s.ChargeLimit);
                _limitEdited = false;
                UpdateLimitText();
            }

            FullOnce.IsChecked = s.FullChargeOnce;
            Drain.IsChecked = s.DrainToLimit;

            RebuildFanChoices(s); // before the profile list, whose selection picks one of these
            FanUnrestricted.IsChecked = s.UnrestrictedFanCurves;
            var names = string.Join("|", s.Profiles.Select(p => p.Name));
            if (full || names != _profileNames)
            {
                _profileNames = names;
                var selected = (ProfileList.SelectedItem as string) ?? s.ActiveProfile;
                ProfileList.ItemsSource = s.Profiles.Select(p => p.Name).ToList();
                ProfileList.SelectedItem = s.Profiles.Any(p => p.Name == selected) ? selected : s.Profiles.FirstOrDefault()?.Name;

                foreach (var box in new[] { AcProfile, DcProfile, LowProfile })
                {
                    Choices.FillProfiles(box, s.Profiles.Select(p => p.Name)); // "Leave as is", then each profile
                }
            }

            AutoSwitch.IsChecked = s.AutoSwitchProfiles;
            Choices.Select(AcProfile, s.AcProfile ?? "");
            Choices.Select(DcProfile, s.BatteryProfile ?? "");
            Choices.Select(LowProfile, s.LowBatteryProfile ?? "");
            Select(LowThreshold, s.LowBatteryThreshold);

            foreach (RadioButton b in KbTimeoutPanel.Children)
            {
                b.IsChecked = (KeyboardTimeout)b.Tag == s.KeyboardTimeout;
            }

            _kbColors?.Show(s.KeyboardColor);
            _kbDelay.Show(s.KeyboardAutoOffSeconds);
            ScreenKeysToggle.IsChecked = s.ScreenHotkeys;

            Hotkeys.IsChecked = s.HandleHotkeys;
            Osd.IsChecked = s.ShowOsd;
            MuteLeds.IsChecked = s.SyncMuteLeds;

            TrayWatts.IsChecked = s.TrayShowsWatts;
            AutoUpdateToggle.IsChecked = s.AutoUpdate;
            WidgetToggle.IsChecked = App.Current.IsWidgetVisible;
            BatteryLogToggle.IsChecked = s.BatteryLogging;
            if (s.Calibration is { } running)
            {
                CalibrationFast.IsChecked = running.FastDischarge; // before a start, it's the user's choice for it
            }

            Simple.Load();
            CalibrationHistory.Text = s.CalibrationHistory.Count == 0
                ? "No calibrations yet."
                : "Past runs:\n" + string.Join("\n", s.CalibrationHistory.Take(5).Select(r =>
                    $"{r.StartedUtc.ToLocalTime():d MMM yyyy HH:mm}  {r.Phase}  {r.FullChargeBeforeMWh / 1000.0:0.0} Wh → {(r.FullChargeAfterMWh is { } a ? $"{a / 1000.0:0.0} Wh" : "?")}  {r.Outcome}"));
        }
        finally
        {
            _loading = false;
        }
    }

    // ---- live updates ----------------------------------------------------------------------------

    public void Update(Snapshot s)
    {
        var app = App.Current;
        var info = app.Monitor.BatteryInfo;
        if (info is not null)
        {
            DesignText.Text = $"{info.DesignedCapacityMWh / 1000.0:0.0} Wh";
            FullText.Text = $"{info.FullChargedCapacityMWh / 1000.0:0.0} Wh";
            HealthText.Text = info.DesignedCapacityMWh > 0 ? $"{100.0 * info.FullChargedCapacityMWh / info.DesignedCapacityMWh:0.0}%" : "—";
            CyclesText.Text = info.CycleCount > 0 ? info.CycleCount.ToString() : "not reported";
            var p = s.Power;
            BatteryDetails.Text = $"{info.Manufacturer ?? "?"} {info.DeviceName ?? ""} · {info.Chemistry} · now {Fmt.Percent(p.Percent, "0.0")} ({p.RemainingWh:0.0} Wh)"
                + (p.BatteryRateWatts switch
                {
                    > 0.05 and var r => $" · charging {r:0.0} W",
                    < -0.05 and var r => $" · discharging {-r:0.0} W",
                    not null => " · not charging",
                    _ => "",
                })
                + (s.BatteryTempC is { } bt ? $" · {bt:0}°C" : "");
        }

        RefreshCalibration();
        UpdateLive(s);
        DgpuNow.Text = (s.Dgpu.Name ?? "NVIDIA GPU") + ": " + Fmt.Dgpu(s.Dgpu);
        DgpuExplain.Text = s.Dgpu.State switch
        {
            DgpuState.RemovedByFirmware => "The firmware is in Integrated-only mode, so the RTX is powered off and invisible to Windows. Use 'Switch to Hybrid' below to get it back (needs a restart).",
            DgpuState.DisabledInWindows => "Disabled in Windows. That stops it powering down properly, so it uses more power than when it's simply asleep.",
            DgpuState.Sleeping => "Hybrid mode, idle: powered down, using practically no power. Apps that need it wake it, and it sleeps again afterwards.",
            DgpuState.Active => "Hybrid mode and powered up. Something is using it or just used it. Its power isn't included in the CPU figure.",
            _ => "",
        };
        DisabledCard.Visibility = s.Dgpu.State == DgpuState.DisabledInWindows ? Visibility.Visible : Visibility.Collapsed;
        ReEnableButton.IsEnabled = Elevation.IsElevated;
        UpdateDebug(s);
        Simple.Update(s);
    }

    private void UpdateDebug(Snapshot s)
    {
        var settings = App.Current.Store.Current;
        DebugFansNow.Text = s.Sensors is { } sensors
            ? $"Fan 1 {Fmt.Rpm(sensors.Fan1Rpm),7} rpm   Fan 2 {Fmt.Rpm(sensors.Fan2Rpm),7} rpm   CPU {Fmt.Temp(sensors.CpuTempC)}   GPU {Fmt.Temp(sensors.GpuTempC)}"
            : Elevation.IsElevated ? "Reading…" : "Fan speeds need administrator rights.";
        DebugTopSpeed.Text = settings.FanTopSpeedMeasuredUtc is { } measured
            ? $"Top speed {Fmt.Rpm(settings.Fan1MaxRpm)} / {Fmt.Rpm(settings.Fan2MaxRpm)} rpm, measured {measured.ToLocalTime():d MMM HH:mm}."
            : settings.Fan1MaxRpm is not null || settings.Fan2MaxRpm is not null
                ? $"Top speed not measured yet. Fastest seen: {Fmt.Rpm(settings.Fan1MaxRpm)} / {Fmt.Rpm(settings.Fan2MaxRpm)} rpm."
                : "Top speed not measured yet.";
        ShowStatus(OnBatteryText, App.Current.Hardware.RunningFromBatteryUntilUtc is { } until
            ? $"Running from the battery: {Math.Max(0, (until - DateTime.UtcNow).TotalSeconds):0} s left · {Fmt.Watts(s.Power.WattsNow)} {(s.Power.Source == PowerSource.BatteryMeasured ? "measured" : "(waiting for the battery to report)")}"
            : "");
    }

    /// <summary>A status line takes no room while it has nothing to say.</summary>
    private static void ShowStatus(TextBlock block, string text)
    {
        block.Text = text;
        block.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFanTestChanged() => Dispatcher.BeginInvoke(() =>
    {
        ShowStatus(FanTestText, App.Current.Hardware.FanTestStatus);
        RefreshDebugControls(App.Current.Hardware.State);
    });

    /// <summary>The Debug tab's buttons follow what the hardware can do and whether a test is running.</summary>
    private void RefreshDebugControls(HardwareState state)
    {
        var hardware = App.Current.Hardware;
        var fans = state.CanWrite && state.Caps?.HasCoolerBoost == true;
        _loading = true;
        try
        {
            foreach (RadioButton b in FanForcePanel.Children)
            {
                var force = (FanForce)b.Tag;
                b.IsChecked = force == hardware.ForcedFans;
                b.IsEnabled = fans && (!hardware.FanTestRunning || force == FanForce.None);
                b.Visibility = force == FanForce.Curve && state.Caps?.HasFanCurves != true ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        finally
        {
            _loading = false;
        }

        MeasureFansButton.IsEnabled = CompareFansButton.IsEnabled = fans && !hardware.FanTestRunning;
        var battery = state.CanWrite && state.Caps?.CanSimulateUnplug == true && !hardware.IsCalibrating;
        RunOnBatteryButton.IsEnabled = battery && hardware.RunningFromBatteryUntilUtc is null;
        StopOnBatteryButton.IsEnabled = hardware.RunningFromBatteryUntilUtc is not null;
        TestLightsButton.IsEnabled = state.CanWrite;
    }

    private readonly System.Windows.Threading.DispatcherTimer _registersTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _readingRegisters;

    private async Task RefreshRegistersAsync()
    {
        if (_readingRegisters)
        {
            return;
        }

        _readingRegisters = true;
        try
        {
            RegistersText.Text = await Task.Run(App.Current.Hardware.ReadRegisters);
        }
        catch (Exception ex)
        {
            Log.Error("Reading registers failed", ex);
        }
        finally
        {
            _readingRegisters = false;
        }
    }

    private async void OnMeasureFans(object sender, RoutedEventArgs e)
    {
        ShowStatus(FanTestText, "Starting…");
        ShowStatus(FanTestText, await App.Current.Hardware.MeasureFanTopSpeedAsync());
    }

    private async void OnCompareFans(object sender, RoutedEventArgs e)
    {
        ShowStatus(FanTestText, "Starting…");
        ShowStatus(FanTestText, await App.Current.Hardware.CompareFanModesAsync());
    }

    private void OnRunOnBattery(object sender, RoutedEventArgs e) => RunOnBattery(TimeSpan.FromMinutes(2));

    private void OnStopOnBattery(object sender, RoutedEventArgs e) => RunOnBattery(TimeSpan.Zero);

    private void RunOnBattery(TimeSpan duration)
    {
        if (App.Current.Hardware.RunFromBattery(duration) is { } error)
        {
            MessageBox.Show(this, error, "Wattkin", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        RefreshDebugControls(App.Current.Hardware.State);
    }

    private async void OnTestLights(object sender, RoutedEventArgs e)
    {
        TestLightsButton.IsEnabled = false;
        await App.Current.Hardware.TestLightsAsync();
        TestLightsButton.IsEnabled = true;
    }

    private void OnTestOsd(object sender, RoutedEventArgs e) => App.Current.ShowOsd("Test message from Wattkin");

    private void UpdateLive(Snapshot s)
    {
        var sb = new StringBuilder();
        var p = s.Power;
        sb.AppendLine($"Power      {Fmt.Watts(p.WattsNow)} now, {Fmt.Watts(p.AverageWatts)} average for time estimates · {Fmt.PowerHeadline(p)}");
        sb.AppendLine($"           CPU package {Fmt.Watts(p.CpuPackageWatts)} · cores {Fmt.Watts(s.Cpu.CoresWatts)} · graphics {Fmt.Watts(s.Cpu.GraphicsWatts)}");
        if (App.Current.Monitor.Power.RestOfSystemWatts is { } rest)
        {
            sb.AppendLine($"           learned rest-of-system draw {Fmt.Watts(rest)} ({App.Current.Monitor.Power.RestOfSystemSamples} on-battery samples)");
        }

        sb.AppendLine($"Battery    {Fmt.Percent(p.Percent, "0.0")} · {p.RemainingWh:0.00} / {p.FullWh:0.00} Wh · rate {p.BatteryRateWatts:+0.00;-0.00;0} W");
        sb.AppendLine($"CPU        {s.Cpu.UsagePercent:0}% · avg {Fmt.GHz(s.Cpu.AverageMHz)} · fastest core {Fmt.GHz(s.Cpu.MaxCoreMHz)}"
            + (s.Cpu.PerformanceCoreMHz is { } pc ? $" · P-cores {Fmt.GHz(pc)}" : "")
            + (s.Cpu.EfficiencyCoreMHz is { } ec ? $" · E-cores {Fmt.GHz(ec)}" : ""));
        sb.AppendLine($"Temps      CPU {Fmt.Temp(s.Sensors?.CpuTempC)} · GPU {Fmt.Temp(s.Sensors?.GpuTempC)} · ACPI zone {Fmt.Temp(s.ThermalZoneC)}");
        sb.AppendLine($"Fans       {Fmt.Fans(s.Sensors?.Fan1Rpm, s.Sensors?.Fan2Rpm)}");
        sb.AppendLine($"Memory     {Fmt.Bytes(s.Memory.UsedBytes)} / {Fmt.Bytes(s.Memory.TotalBytes)} ({s.Memory.LoadPercent}%) · committed {Fmt.Bytes(s.Memory.CommitUsedBytes)} / {Fmt.Bytes(s.Memory.CommitLimitBytes)}");
        foreach (var d in s.Disks)
        {
            sb.AppendLine($"{d.Name,-10} read {Fmt.Rate(d.ReadBytesPerSec)} · write {Fmt.Rate(d.WriteBytesPerSec)} · {d.ActivePercent:0}% busy");
        }

        sb.Append($"GPU        {Fmt.Dgpu(s.Dgpu)}");
        LiveText.Text = sb.ToString();
        ProcessText.Text = s.TopProcesses.Count == 0
            ? "Measuring…"
            : string.Join("\n", s.TopProcesses.Take(8).Select(x => $"{(x.Processes > 1 ? $"{x.Name} ({x.Processes})" : x.Name),-34} {x.CpuPercent,5:0.0}%"));

        // Static facts: rebuilt every 10 s at most (display-mode enumeration isn't free).
        if (DateTime.UtcNow - _infoUpdatedUtc < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _infoUpdatedUtc = DateTime.UtcNow;
        var info = App.Current.Monitor.Info;
        var state = App.Current.Hardware.State;
        var battery = App.Current.Monitor.BatteryInfo;
        InfoText.Text =
            $"Model      {info?.Model}\n" +
            $"BIOS       {info?.BiosVersion} · EC {state.EcFirmware ?? "(needs administrator)"} · MSI WMI {state.WmiVersion ?? "?"}\n" +
            $"CPU        {info?.CpuName} · {info?.PerformanceThreads} P-threads + {info?.EfficiencyThreads} E-threads\n" +
            $"Memory     {Fmt.Bytes(info?.InstalledMemoryBytes ?? 0)} {info?.MemoryType} · running {info?.MemoryConfiguredMTs} MT/s (rated {info?.MemoryRatedMTs}) · {info?.MemoryPart}\n" +
            $"Battery    {battery?.Manufacturer} {battery?.DeviceName} · {battery?.Chemistry} · design {battery?.DesignedCapacityMWh / 1000.0:0.0} Wh\n" +
            $"Display    {DisplayModes.CurrentRefreshRate()} Hz (available: {string.Join(", ", DisplayModes.SupportedRefreshRates())})";
    }

    private void RefreshFromState()
    {
        var state = App.Current.Hardware.State;
        _loading = true;
        try
        {
            ChargeStatusText.Text = (state.ChargeStatus, state.EcChargeLimit) switch
            {
                ({ Length: > 0 } status, { } ec) => $"{status} (controller set to {ec}%)",
                (_, { } ec) => $"The laptop's controller is set to stop at {ec}%.",
                (var status, null) => status,
            };
            foreach (RadioButton b in KbLevelPanel.Children)
            {
                b.IsChecked = state.KeyboardLevel == (int)b.Tag;
                b.IsEnabled = state.CanWrite;
            }

            foreach (RadioButton b in KbTimeoutPanel.Children)
            {
                b.IsEnabled = state.CanWrite;
            }

            WebcamToggle.IsChecked = state.Webcam;
            WebcamToggle.IsEnabled = state.CanWrite;
            // Null = this model has no EC webcam switch (EC 0x2F bit1 clear), or not connected yet.
            DevicesCard.Visibility = state.Webcam is null ? Visibility.Collapsed : Visibility.Visible;
            var caps = state.Caps;
            KbTimeoutPanel.IsEnabled = caps?.HasKeyboardTimeout != false;

            // The colour, and a delay of the user's choosing, only where the keys are lit over USB (auto-off is Wattkin's own there).
            var usbLight = caps?.HasKeyboardColor == true;
            KbColorSection.Visibility = KbDelayHost.Visibility = usbLight ? Visibility.Visible : Visibility.Collapsed;
            KbColorPanel.IsEnabled = state.CanWrite;
            KbDelayHost.IsEnabled = state.CanWrite && App.Current.Store.Current.KeyboardTimeout != KeyboardTimeout.AlwaysOn;
            KbTimeoutHint.Text = usbLight
                ? "These keys are lit by a USB controller (Mystic Light), which can't be read back or turn itself off: Wattkin keeps the level and colour, sets them again at startup and after sleep, and turns the light off itself when the laptop's own keyboard hasn't been used for the time you choose."
                : "Auto-off is built into the keyboard controller: the light goes off after about 10 seconds without typing and comes back on with a key press. The controller turns auto-off back on every boot, so Wattkin re-applies your choice at startup and after sleep.";
            RebuildShiftChoices(caps?.HasSportMode == true);

            // Calibration owns charging until it finishes or is cancelled.
            var chargeEditable = state.CanWrite && !App.Current.Hardware.IsCalibrating;
            ApplyLimit.IsEnabled = StopSlider.IsEnabled = CustomResume.IsEnabled = chargeEditable;
            FullOnce.IsEnabled = chargeEditable;
            Drain.IsEnabled = chargeEditable && caps?.CanSimulateUnplug == true;
            Drain.Visibility = caps is { CanSimulateUnplug: false } ? Visibility.Collapsed : Visibility.Visible;

            SupportText.Text = caps switch
            {
                null => Elevation.IsElevated ? "Reading the laptop's firmware…" : "Hardware support: needs administrator rights to check.",
                { Support: EcSupport.Verified } => $"Hardware support: verified on this firmware ({caps.MapSource}).",
                { Support: EcSupport.Community } => $"Hardware support: msi-ec register map ({caps.MapSource}), sanity-checked on this laptop at startup. If anything misbehaves, please report it with a diagnostics report.",
                { Support: EcSupport.Standard } => "Hardware support: MSI's standard layout (the registers MSI Center uses on all recent MSI laptops), sanity-checked on this laptop at startup. Please report how it works with a diagnostics report.",
                _ => state.Problem ?? "Hardware support: monitoring only on this firmware.",
            };
            if (state.Gpu is { } gpu)
            {
                FirmwareMode.Text = $"Current: {Describe(gpu.Current)}" + (gpu.SwitchPending ? $" · switching to {Describe(gpu.Requested)} at the next full shutdown" : "");
                ModeHybrid.IsEnabled = state.Caps?.CanSwitchGpuMode == true && gpu.SwitchSupported && gpu.Requested != FirmwareGpuMode.Hybrid;
                ModeIntegrated.IsEnabled = state.Caps?.CanSwitchGpuMode == true && gpu.SwitchSupported && gpu.IntegratedSupported && gpu.Requested != FirmwareGpuMode.Integrated;
            }
            else
            {
                FirmwareMode.Text = state.GpuProblem ?? (Elevation.IsElevated ? "Reading…" : "Needs administrator rights.");
                ModeHybrid.IsEnabled = ModeIntegrated.IsEnabled = false;
            }

            ElevationText.Text = Elevation.IsElevated
                ? "Running as administrator: hardware controls are available."
                : "Running without administrator rights: monitoring only.";
            ElevateButton.Visibility = Elevation.IsElevated ? Visibility.Collapsed : Visibility.Visible;
            MsiStatus.Text = state.MsiSoftwareRunning
                ? "MSI Center components are running right now. They can overwrite Wattkin's settings."
                : "No MSI Center components are running.";
            AboutText.Text = $"Wattkin {typeof(App).Assembly.GetName().Version?.ToString(3)} · EC {state.EcFirmware ?? "?"} · settings: {Fmt.FriendlyPath(App.Current.Store.FilePath)}";
        }
        finally
        {
            _loading = false;
        }

        RefreshCalibration();
        UpdateCalibrationSuggestion();
        RefreshDebugControls(state);
        Simple.RefreshState();
    }

    private static string Describe(FirmwareGpuMode mode) => mode switch
    {
        FirmwareGpuMode.Hybrid => "Hybrid (RTX available, sleeps when idle)",
        FirmwareGpuMode.Integrated => "Integrated only (RTX powered off)",
        _ => "Discrete only",
    };

    private void RefreshCalibration()
    {
        var state = App.Current.Hardware.State;
        var running = App.Current.Store.Current.Calibration is not null;
        CalibrationStart.IsEnabled = state.CanWrite && !running && state.Caps?.CanSimulateUnplug == true;
        CalibrationCancel.IsEnabled = running;
        if (state.Calibration is { } c && running)
        {
            CalibrationStatus.Text = c.Message;
            var percent = c.Percent ?? 0;
            CalibrationBar.Value = c.Phase == CalibrationPhase.Discharging ? Math.Clamp((100 - percent) / (100 - c.TargetPercent) * 50 + 50, 50, 100) : Math.Clamp(percent / 2, 0, 50);
            CalibrationEta.Text = $"Running for {Fmt.Duration(c.Elapsed)} · this step ~{Fmt.Duration(c.PhaseEta)} · finished in ~{Fmt.Duration(c.TotalEta)}";
        }
        else if (running)
        {
            CalibrationStatus.Text = "Starting…";
        }
        else
        {
            CalibrationStatus.Text = state.Calibration?.Message ?? "Not running.";
            CalibrationBar.Value = 0;
            CalibrationEta.Text = App.Current.Monitor.Latest is { } s && s.Power.FullWh is { } full
                ? $"Rough time on this laptop: charging to full ~{Fmt.Duration(TimeSpan.FromHours(full * (1 - ((s.Power.Percent ?? 50) / 100)) / 30 * 1.25))}, then discharging ~{Fmt.Duration(TimeSpan.FromHours(full * 0.92 / 8))} (or ~{Fmt.Duration(TimeSpan.FromHours(full * 0.92 / 20))} with faster discharge)."
                : "";
        }
    }

    private DateTime _infoUpdatedUtc = DateTime.MinValue;

    private async Task LoadHistoryAsync()
    {
        try
        {
            _history = await App.Current.BatteryLog.LoadCapacityHistoryAsync(App.Current.Store.Current.BatteryChangedUtc?.ToLocalTime());
            Chart.SetPoints(_history);
            UpdateCalibrationSuggestion();
        }
        catch (Exception ex)
        {
            Log.Error("Loading capacity history failed", ex);
        }
    }

    /// <summary>Suggests a calibration when the capacity reading fell well below its recent level.</summary>
    private void UpdateCalibrationSuggestion()
    {
        var app = App.Current;
        var settings = app.Store.Current;
        var full = app.Monitor.BatteryInfo is { FullChargedCapacityMWh: > 0 } info ? info.FullChargedCapacityMWh / 1000.0 : (double?)null;
        // Only a run that finished re-measured the gauge; cancelled or failed runs don't count.
        var lastCalibrated = settings.CalibrationHistory
            .Where(r => r.Phase == nameof(CalibrationPhase.Done) && r.FinishedUtc is not null)
            .Select(r => r.FinishedUtc)
            .Max();
        var advice = settings.Calibration is null && app.Hardware.State.Caps?.CanSimulateUnplug == true
            ? CalibrationAdvice.Check(_history, full, lastCalibrated, DateTime.Today)
            : null;
        Simple.SetCalibrationSuggestion(advice);
        CalibrationSuggestion.Visibility = advice is null ? Visibility.Collapsed : Visibility.Visible;
        if (advice is not null)
        {
            CalibrationSuggestionText.Text = $"The battery reports {advice.NowWh:0.0} Wh, down from about {advice.TypicalWh:0} Wh in the last two months. A drop this quick is usually the gauge drifting, not real wear. A calibration re-measures it: a few hours with the charger plugged in.";
        }
    }

    // ---- battery handlers ---------------------------------------------------------------------

    private void OnLimitSliders(object sender, RoutedEventArgs e)
    {
        // Sliders raise ValueChanged while the XAML is still being loaded.
        if (StopSlider is null || StopValue is null || ResumeSlider is null || ResumeValue is null || CustomResume is null || LimitExplain is null)
        {
            return;
        }

        if (!_loading)
        {
            _limitEdited = true; // the user is editing: background refreshes leave these alone
        }

        // The EC's own gap (stop − 10) is the narrowest band; a custom resume point can only be lower.
        var maxResume = ChargePolicy.MaxResumeFor((int)Math.Round(StopSlider.Value));
        ResumeSlider.Maximum = Math.Max(ResumeSlider.Minimum, maxResume);
        ResumeSlider.IsEnabled = CustomResume.IsChecked == true;
        if (CustomResume.IsChecked != true)
        {
            ResumeSlider.Value = Math.Max(ResumeSlider.Minimum, maxResume);
        }

        UpdateLimitText();
    }

    private void UpdateLimitText()
    {
        var stop = (int)Math.Round(StopSlider.Value);
        var resume = (int)Math.Round(ResumeSlider.Value);
        StopValue.Text = $"{stop}%";
        ResumeValue.Text = $"{resume}%";
        LimitExplain.Text = CustomResume.IsChecked == true && resume < ChargePolicy.MaxResumeFor(stop)
            ? $"Charging stops at {stop}% and starts again below {resume}%. The controller's own gap is 10%, so Wattkin widens it to a {resume}–{stop}% band. If Wattkin isn't running, the controller keeps the last value it was given, which is never above {stop}%."
            : $"Charging stops at {stop}% and starts again below {ChargePolicy.MaxResumeFor(stop)}%. The embedded controller does this by itself, even if Wattkin is closed or uninstalled.";
    }

    private async void OnApplyLimit(object sender, RoutedEventArgs e)
    {
        var stop = (int)Math.Round(StopSlider.Value);
        int? resume = CustomResume.IsChecked == true ? (int)Math.Round(ResumeSlider.Value) : null;
        _limitEdited = false;
        await App.Current.Hardware.SetChargeLimitAsync(stop, resume);
    }

    private async void OnFullOnce(object sender, RoutedEventArgs e) => await App.Current.Hardware.SetFullChargeOnceAsync(FullOnce.IsChecked == true);

    private async void OnDrain(object sender, RoutedEventArgs e) => await App.Current.Hardware.SetDrainToLimitAsync(Drain.IsChecked == true);

    private async void OnCalibrationStart(object sender, RoutedEventArgs e)
    {
        var fast = CalibrationFast.IsChecked == true;
        if (MessageBox.Show(this,
                "Calibration will:\n\n1. Charge the battery to 100%.\n2. Discharge it to about 8% while the charger stays plugged in.\n3. Hand back to your normal charge limit, which recharges it.\n\nKeep the charger connected the whole time. The laptop won't sleep while this runs. You can keep using it, and you can cancel any time.\n\nStart now?",
                "Battery calibration", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
        {
            return;
        }

        var error = await Task.Run(() => App.Current.Hardware.StartCalibration(fast)); // waits for the hardware lock
        if (error is not null)
        {
            MessageBox.Show(this, error, "Battery calibration", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshCalibration();
    }

    private async void OnCalibrationCancel(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Stop the calibration? Your normal charge limit takes over again.", "Battery calibration", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            await Task.Run(App.Current.Hardware.CancelCalibration);
            RefreshCalibration();
        }
    }

    private void OnCalibrationFast(object sender, RoutedEventArgs e)
    {
        // Only a running calibration changes now; otherwise the box is read when one starts.
        if (App.Current.Store.Current.Calibration is not null)
        {
            var fast = CalibrationFast.IsChecked == true;
            _ = Task.Run(() => App.Current.Hardware.SetCalibrationFast(fast)); // waits for the hardware lock
        }
    }

    private void OnOpenBatteryLog(object sender, RoutedEventArgs e) => OpenFolder(App.Current.BatteryLog.Directory);

    private async void OnRefreshHistory(object sender, RoutedEventArgs e) => await LoadHistoryAsync();

    // ---- profile handlers ---------------------------------------------------------------------

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        var selected = App.Current.Store.Current.FindProfile(ProfileList.SelectedItem as string);
        if (selected is null)
        {
            // Nothing selected (Ctrl+click on the selected one, or the list being refilled): the form
            // still shows the profile, so Save still saves it, while it exists.
            _editing = _editing is null ? null : App.Current.Store.Current.FindProfile(_editing.Name);
            return;
        }

        _editing = selected;

        _selectingProfile = true;
        try
        {
            PName.Text = _editing.Name;
            Select(PShift, _editing.Performance);
            Select(PFan, FanChoice(_editing));
            _fanChoice = FanChoice(_editing);
            Select(PWindows, _editing.WindowsMode);
            Select(PCpuBoost, _editing.CpuBoost);
            Select(PMaxCpu, _editing.MaxCpuPercent);
            Select(PMaxFreq, _editing.MaxFrequencyMHz);
            Select(PEpp, _editing.EnergyPreference);
            Select(PRefresh, _editing.RefreshRate);
            Select(PKeyboard, _editing.KeyboardLevel);
        }
        finally
        {
            _selectingProfile = false;
        }
    }

    // ---- a profile's fans: one choice ------------------------------------------------------------

    private bool _selectingProfile;
    private string? _fanChoice;
    private string _fanChoicesKey = "";

    /// <summary>A profile's fan setting as one choice: "auto", "silent", "default", "fan:{name}", "full", "off" or "unchanged".</summary>
    private static string FanChoice(Profile p) => p switch
    {
        { CoolerBoost: true } => "full",
        { Fan: null } => "unchanged",
        { Fan: FanMode.Auto } => "auto",
        { Fan: FanMode.Silent } => "silent",
        { FanProfileName: null } => "default",
        { FanProfileName: var n } when string.Equals(n, FanProfile.LaptopDefault, StringComparison.OrdinalIgnoreCase) => "default",
        { FanProfileName: var n } when string.Equals(n, FanProfile.Off, StringComparison.OrdinalIgnoreCase) => "off",
        { FanProfileName: var n } => "fan:" + n,
    };

    private static void ApplyFanChoice(Profile p, string? choice)
    {
        p.CoolerBoost = choice == "full";
        (p.Fan, p.FanProfileName) = choice switch
        {
            "unchanged" => ((FanMode?)null, (string?)null),
            "auto" or "full" => (FanMode.Auto, null), // full speed is Cooler Boost on top of Auto
            "silent" => (FanMode.Silent, null),
            "default" => (FanMode.Custom, null),
            "off" => (FanMode.Custom, FanProfile.Off),
            { } c when c.StartsWith("fan:", StringComparison.Ordinal) => (FanMode.Custom, c[4..]),
            _ => (p.Fan, p.FanProfileName),
        };
    }

    /// <summary>The Fans dropdown: the firmware's modes, the laptop's own curve, every fan profile, full speed and off.</summary>
    private void RebuildFanChoices(AppSettings s)
    {
        var key = string.Join("|", s.FanProfiles.Select(f => f.Name));
        if (key == _fanChoicesKey && PFan.Items.Count > 0)
        {
            return;
        }

        _fanChoicesKey = key;
        var selected = TagOf<string>(PFan);
        PFan.Items.Clear();
        PFan.Items.Add(Item("Auto (the laptop decides)", "auto"));
        PFan.Items.Add(Item("Silent (the laptop decides, quieter)", "silent"));
        PFan.Items.Add(Item($"{FanProfile.LaptopDefault} curve", "default"));
        foreach (var fan in s.FanProfiles)
        {
            PFan.Items.Add(Item($"Fan profile: {fan.Name}", "fan:" + fan.Name));
        }

        PFan.Items.Add(Item("Full speed", "full"));
        PFan.Items.Add(Item("Off (experimental)", "off"));
        PFan.Items.Add(Item("Unchanged", "unchanged"));

        // A renamed fan profile is a new choice: take it from the profile being edited, which followed the rename.
        // An unsaved choice in the editor stays, if it's still offered (a renamed fan profile is a new
        // choice: then the profile's own, which followed the rename).
        var stillOffered = selected is not null && PFan.Items.Cast<ComboBoxItem>().Any(i => Equals(i.Tag, selected));
        _selectingProfile = true;
        Select(PFan, stillOffered ? selected : _editing is { } editing ? FanChoice(editing) : "auto");
        _selectingProfile = false;
    }

    /// <summary>"Off" needs the experimental warning accepted first; declining puts the old choice back.</summary>
    private void OnProfileFanChanged(object sender, SelectionChangedEventArgs e)
    {
        var choice = TagOf<string>(PFan);
        if (!_selectingProfile && !_loading && choice == "off" && !ConfirmExperimental())
        {
            _selectingProfile = true;
            Select(PFan, _fanChoice ?? "auto");
            _selectingProfile = false;
            return;
        }

        _fanChoice = choice;
    }

    /// <summary>The experimental warning. True when the user accepts (remembered) or already had.</summary>
    private bool ConfirmExperimental()
    {
        if (App.Current.Store.Current.UnrestrictedFanCurves)
        {
            return true;
        }

        if (MessageBox.Show(this,
                "This is experimental.\n\nNormally Wattkin makes sure the fans reach full speed before the laptop gets hot. Allowing this lets you choose Off, and fan profiles that let the laptop run hot enough to slow itself down (thermal throttling). The firmware still runs the fans flat out at its hottest step (95°C on the Prestige 16 Studio).\n\nKeep an eye on your temperatures in the flyout.",
                "Fans", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return false;
        }

        App.Current.Store.Update(s => s.UnrestrictedFanCurves = true);
        Log.Info("Fans: the user accepted the experimental mode (fan profiles that let the laptop run hot, and Off).");
        return true;
    }

    // ---- Fan profiles tab ---------------------------------------------------------------------------

    private FanCurves? _firmwareCurves;
    private FanTable? _firmwareTable;
    private FanProfile? _editingFan; // null while the built-in "Laptop default" is shown

    /// <summary>The laptop's fan table gives the editor its real temperature steps; read once per window.</summary>
    private async Task LoadFanProfilesAsync()
    {
        _firmwareCurves = await Task.Run(App.Current.Hardware.ReadFanCurves);
        _firmwareTable = await Task.Run(App.Current.Hardware.ReadFullFanTable);
        RefreshFanProfileList();
    }

    private FanSpeeds Factory() => App.Current.Store.Current.FactoryFanCurve?.Clone() ?? FanSpeeds.From(_firmwareCurves!);

    /// <summary>Where Wattkin writes whole tables (temperatures too): the laptop's own table, else null (speeds only).</summary>
    private FanTable? FactoryTable() => _firmwareTable is null ? null : (App.Current.Store.Current.FactoryFanTable ?? _firmwareTable).Clone();

    /// <summary>A fan profile as a whole table, on a laptop that takes them.</summary>
    private static FanTable TableOf(FanProfile fan, FanTable factory) => fan.Table?.Clone() ?? factory.WithSpeeds(fan.Speeds);

    private void RefreshFanProfileList(string? select = null)
    {
        var settings = App.Current.Store.Current;
        List<string> names = [FanProfile.LaptopDefault, .. settings.FanProfiles.Select(f => f.Name)];
        var wanted = select ?? FanProfileList.SelectedItem as string ?? FanProfile.LaptopDefault;
        FanProfileList.ItemsSource = names;
        FanProfileList.SelectedItem = names.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase)) ?? FanProfile.LaptopDefault;
        FanUnrestricted.IsChecked = settings.UnrestrictedFanCurves;
    }

    private void OnFanProfileSelected(object? sender, SelectionChangedEventArgs? e)
    {
        if (FanProfileList.SelectedItem is not string name)
        {
            return;
        }

        _editingFan = App.Current.Store.Current.FindFanProfile(name);
        var builtIn = _editingFan is null;
        FanTitle.Text = name;
        FanNameRow.Visibility = FanSaveButtons.Visibility = builtIn ? Visibility.Collapsed : Visibility.Visible;
        FanDeleteButton.IsEnabled = !builtIn;
        if (_firmwareCurves is not { } firmware)
        {
            FanEditorPanel.Visibility = Visibility.Collapsed;
            FanNewButton.IsEnabled = FanDuplicateButton.IsEnabled = false;
            FanIntro.Text = "Fan profiles need administrator rights and an MSI laptop whose fan table Wattkin can read.";
            return;
        }

        FanEditorPanel.Visibility = Visibility.Visible;
        FanNewButton.IsEnabled = FanDuplicateButton.IsEnabled = true;
        var table = FactoryTable();
        FanIntro.Text = builtIn
            ? "The curve your laptop came with. It can't be changed here: duplicate it to make your own."
            : table is not null
                ? "Each fan's speed at each step, and the temperatures that start and end each step. Choose this fan profile for a profile in Profiles › Fans."
                : "Each fan's speed at each of the laptop's temperature steps. Choose this fan profile for a profile in Profiles › Fans.";
        FanName.Text = name;

        // Whole tables (temperatures too) where the laptop takes them; speeds at the laptop's own steps elsewhere.
        FanEditor.Visibility = table is null ? Visibility.Visible : Visibility.Collapsed;
        FanTableEditor.Visibility = table is null ? Visibility.Collapsed : Visibility.Visible;
        if (table is not null)
        {
            FanTableEditor.Show(builtIn ? table : TableOf(_editingFan!, table));
            FanTableEditor.IsEnabled = !builtIn;
        }
        else
        {
            FanEditor.Show(firmware, builtIn ? Factory() : _editingFan!.Speeds);
            FanEditor.IsEnabled = !builtIn;
        }

        UpdateFanProblem();
    }

    /// <summary>Problems are shown as you drag.</summary>
    private void UpdateFanProblem()
    {
        var unrestricted = App.Current.Store.Current.UnrestrictedFanCurves;
        var problem = _editingFan is null ? null : FactoryTable() is null ? FanEditor.Problem(unrestricted) : FanTableEditor.Problem(unrestricted);
        FanProblem.Text = problem ?? "";
        FanProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnFanProfileNew(object sender, RoutedEventArgs e)
    {
        if (_firmwareCurves is not null) // the laptop's table read (it gives a new profile its starting speeds)
        {
            AddFanProfile(UniqueFanName("My fan profile"), Factory(), FactoryTable());
        }
    }

    private void OnFanProfileDuplicate(object sender, RoutedEventArgs e)
    {
        if (_firmwareCurves is not null)
        {
            AddFanProfile(UniqueFanName((FanProfileList.SelectedItem as string ?? "Fan profile") + " copy"), _editingFan?.Speeds.Clone() ?? Factory(),
                FactoryTable() is { } factory ? (_editingFan is { } source ? TableOf(source, factory) : factory) : null);
        }
    }

    private void AddFanProfile(string name, FanSpeeds speeds, FanTable? table)
    {
        var fan = new FanProfile { Name = name, Speeds = table?.ToSpeeds() ?? speeds, Table = table };
        App.Current.Store.Update(s => s.FanProfiles = [.. s.FanProfiles, fan]);
        RefreshFanProfileList(select: name);
    }

    private async void OnFanProfileDelete(object sender, RoutedEventArgs e)
    {
        if (_editingFan is not { } fan
            || MessageBox.Show(this, $"Delete the fan profile \"{fan.Name}\"? Profiles that use it go back to the laptop default curve.", "Fan profiles", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
        {
            return;
        }

        var inUse = UsedByActiveProfile(fan.Name);
        App.Current.Store.Update(s =>
        {
            s.FanProfiles = [.. s.FanProfiles.Where(f => f != fan)];
            s.RenameFanProfile(fan.Name, null);
        });
        RefreshFanProfileList(select: FanProfile.LaptopDefault);
        if (inUse)
        {
            await App.Current.Hardware.ApplyProfileAsync(App.Current.Store.Current.ActiveProfile);
        }
    }

    private async void OnFanProfileSave(object sender, RoutedEventArgs e)
    {
        if (_editingFan is not { } fan)
        {
            return;
        }

        var settings = App.Current.Store.Current;
        var name = FanName.Text.Trim();
        var wholeTable = FactoryTable() is not null;
        var problem = name.Length == 0 ? "Give the fan profile a name."
            : FanProfile.IsBuiltIn(name) ? $"\"{name}\" is built in: choose another name."
            : !string.Equals(name, fan.Name, StringComparison.OrdinalIgnoreCase) && settings.FindFanProfile(name) is not null ? "A fan profile with that name already exists."
            : wholeTable ? FanTableEditor.Problem(settings.UnrestrictedFanCurves)
            : FanEditor.Problem(settings.UnrestrictedFanCurves);
        if (problem is not null)
        {
            MessageBox.Show(this, problem, "Fan profiles");
            return;
        }

        var saved = wholeTable
            ? new FanProfile { Name = name, Table = FanTableEditor.Value, Speeds = FanTableEditor.Value.ToSpeeds() }
            : new FanProfile { Name = name, Speeds = FanEditor.Value };
        var inUse = UsedByActiveProfile(fan.Name);
        App.Current.Store.Update(s =>
        {
            s.FanProfiles = [.. s.FanProfiles.Select(f => f == fan ? saved : f)];
            if (!string.Equals(fan.Name, name, StringComparison.Ordinal))
            {
                s.RenameFanProfile(fan.Name, name);
            }
        });

        // The list keeps the same name selected without raising SelectionChanged: point the editor at the
        // saved object, or the next Save or Delete would look for the replaced one and miss.
        _editingFan = saved;
        RefreshFanProfileList(select: name);

        // In use right now: the laptop gets the new curve straight away.
        if (inUse)
        {
            await App.Current.Hardware.ApplyProfileAsync(App.Current.Store.Current.ActiveProfile);
        }
    }

    private void OnFanProfileRevert(object sender, RoutedEventArgs e) => OnFanProfileSelected(null, null);

    private void OnFanUnrestricted(object sender, RoutedEventArgs e)
    {
        if (FanUnrestricted.IsChecked == true)
        {
            FanUnrestricted.IsChecked = ConfirmExperimental();
        }
        else
        {
            App.Current.Store.Update(s => s.UnrestrictedFanCurves = false);
            Log.Info("Fans: back to the safety rules (profiles set to Off use Silent instead).");
        }

        UpdateFanProblem();
    }

    private static bool UsedByActiveProfile(string fanProfile) =>
        App.Current.Store.Current is var s && s.FindProfile(s.ActiveProfile) is { Fan: FanMode.Custom, CoolerBoost: false } active
        && string.Equals(active.FanProfileName, fanProfile, StringComparison.OrdinalIgnoreCase);

    private static string UniqueFanName(string baseName)
    {
        var settings = App.Current.Store.Current;
        var name = baseName;
        for (var i = 2; settings.FindFanProfile(name) is not null || FanProfile.IsBuiltIn(name); i++)
        {
            name = $"{baseName} {i}";
        }

        return name;
    }

    private string? SaveProfile()
    {
        if (_editing is null)
        {
            return null;
        }

        var oldName = _editing.Name;
        var newName = string.IsNullOrWhiteSpace(PName.Text) ? oldName : PName.Text.Trim();
        var settings = App.Current.Store.Current;
        if (!string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase) && settings.FindProfile(newName) is not null)
        {
            MessageBox.Show(this, "A profile with that name already exists.", "Profiles");
            return null;
        }

        var editing = _editing;
        var fanChoice = TagOf<string>(PFan);
        App.Current.Store.Update(s =>
        {
            editing.Name = newName;
            editing.Performance = (PShift.SelectedItem as ComboBoxItem)?.Tag as ShiftMode?; // null: leave the firmware's mode alone
            ApplyFanChoice(editing, fanChoice);
            editing.WindowsMode = TagOf<WindowsPowerMode>(PWindows);
            editing.CpuBoost = (PCpuBoost.SelectedItem as ComboBoxItem)?.Tag as bool?;
            editing.MaxCpuPercent = (PMaxCpu.SelectedItem as ComboBoxItem)?.Tag as int?;
            editing.MaxFrequencyMHz = (PMaxFreq.SelectedItem as ComboBoxItem)?.Tag as int?;
            editing.EnergyPreference = (PEpp.SelectedItem as ComboBoxItem)?.Tag as int?;
            editing.RefreshRate = (PRefresh.SelectedItem as ComboBoxItem)?.Tag as int?;
            editing.KeyboardLevel = (PKeyboard.SelectedItem as ComboBoxItem)?.Tag as int?;

            Rename(s, oldName, newName);
        });
        LoadSettings(full: true);
        ProfileList.SelectedItem = newName;
        return newName;
    }

    private static void Rename(AppSettings s, string oldName, string newName)
    {
        if (s.ActiveProfile == oldName) s.ActiveProfile = newName;
        if (s.AcProfile == oldName) s.AcProfile = newName;
        if (s.BatteryProfile == oldName) s.BatteryProfile = newName;
        if (s.LowBatteryProfile == oldName) s.LowBatteryProfile = newName;
    }

    private void OnProfileSave(object sender, RoutedEventArgs e) => SaveProfile();

    private async void OnProfileApply(object sender, RoutedEventArgs e)
    {
        if (SaveProfile() is { } name)
        {
            await App.Current.Hardware.ApplyProfileAsync(name);
        }
    }

    private void OnProfileNew(object sender, RoutedEventArgs e) => AddProfile(new Profile { Name = UniqueName("New profile") });

    private void OnProfileDuplicate(object sender, RoutedEventArgs e)
    {
        if (_editing is not null)
        {
            var copy = _editing.Clone();
            copy.Name = UniqueName(_editing.Name + " copy");
            AddProfile(copy);
        }
    }

    private void AddProfile(Profile profile)
    {
        App.Current.Store.Update(s => s.Profiles = [.. s.Profiles, profile]);
        LoadSettings(full: true);
        ProfileList.SelectedItem = profile.Name;
    }

    private async void OnProfileDelete(object sender, RoutedEventArgs e)
    {
        var settings = App.Current.Store.Current;
        if (_editing is null || settings.Profiles.Count <= 1)
        {
            return;
        }

        var name = _editing.Name;
        var wasActive = string.Equals(settings.ActiveProfile, name, StringComparison.OrdinalIgnoreCase);
        App.Current.Store.Update(s =>
        {
            s.Profiles = [.. s.Profiles.Where(p => p.Name != name)];
            s.Normalize(); // rules that named it leave the profile as it is; the active one becomes the first
        });
        LoadSettings(full: true);
        if (wasActive)
        {
            // The laptop is still set up the deleted profile's way: put the new active one in place.
            await App.Current.Hardware.ApplyProfileAsync(App.Current.Store.Current.ActiveProfile);
        }
    }

    private static string UniqueName(string baseName)
    {
        var settings = App.Current.Store.Current;
        var name = baseName;
        for (var i = 2; settings.FindProfile(name) is not null; i++)
        {
            name = $"{baseName} {i}";
        }

        return name;
    }

    private void OnAutoChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        App.Current.Store.Update(s =>
        {
            s.AutoSwitchProfiles = AutoSwitch.IsChecked == true;
            s.AcProfile = Choices.ProfileOf(AcProfile); // null: leave the profile as it is
            s.BatteryProfile = Choices.ProfileOf(DcProfile);
            s.LowBatteryProfile = Choices.ProfileOf(LowProfile);
            s.LowBatteryThreshold = TagOf<int>(LowThreshold);
        });
    }

    private void OnRestoreWindowsPower(object sender, RoutedEventArgs e)
    {
        if (!App.Current.Hardware.ControlsHardware)
        {
            return;
        }

        App.Current.Store.Update(WindowsPower.RestoreOriginals);
        MessageBox.Show(this, "Processor turbo, max state, max frequency and performance-or-efficiency settings are back to what they were before Wattkin changed them.", "Wattkin");
    }

    // ---- keyboard & devices -------------------------------------------------------------------

    private void OnKeysChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        App.Current.Store.Update(s =>
        {
            s.HandleHotkeys = Hotkeys.IsChecked == true;
            s.ShowOsd = Osd.IsChecked == true;
            s.SyncMuteLeds = MuteLeds.IsChecked == true;
        });
    }

    private async void OnWebcam(object sender, RoutedEventArgs e) => await App.Current.Hardware.SetWebcamAsync(WebcamToggle.IsChecked == true);

    // ---- screen ---------------------------------------------------------------------------------

    private bool _screenUpdating;
    private DateTime _screenMovedUtc;
    private System.Windows.Threading.DispatcherTimer? _screenSettled;

    private System.Windows.Threading.DispatcherTimer NewSettleTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = ScreenBrightness.UserMoveSettles + TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (IsLoaded)
            {
                RefreshScreen();
            }
        };
        Closed += (_, _) => timer.Stop();
        return timer;
    }

    /// <summary>
    /// The brightness slider, the keys switch and any problem. The slider isn't moved while it's being
    /// dragged or just after it was moved (arrow keys): it would jump back to a value about to be replaced.
    /// </summary>
    private void RefreshScreen()
    {
        var screen = App.Current.Screen;
        _screenUpdating = true;
        try
        {
            ScreenSlider.Maximum = screen.MaxPosition;
            if (!ScreenSlider.IsMouseCaptureWithin && DateTime.UtcNow - _screenMovedUtc > ScreenBrightness.UserMoveSettles)
            {
                ScreenSlider.Value = screen.Position;
                ScreenValue.Text = ScreenBrightness.SliderText(screen.Position, screen.HasBacklight);
            }

            ScreenSlider.IsEnabled = ScreenKeysToggle.IsEnabled = App.Current.Hardware.ControlsHardware;
            ScreenProblem.Text = screen.Problem ?? "";
            ScreenProblem.Visibility = screen.Problem is null ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _screenUpdating = false;
        }
    }

    private void OnScreenSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_screenUpdating || !IsLoaded)
        {
            return;
        }

        var position = (int)Math.Round(e.NewValue);
        _screenMovedUtc = DateTime.UtcNow;
        _screenSettled ??= NewSettleTimer();
        _screenSettled.Stop();
        _screenSettled.Start(); // then shows where the screen really is (a dim f.lux refused, say)
        ScreenValue.Text = ScreenBrightness.SliderText(position, App.Current.Screen.HasBacklight);
        App.Current.Screen.RequestPosition(position);
    }

    private void OnScreenKeys(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var on = ScreenKeysToggle.IsChecked == true;
        App.Current.Store.Update(s => s.ScreenHotkeys = on);
        App.Current.Screen.SetHotkeys(on);
    }

    // ---- graphics -----------------------------------------------------------------------------

    private async void OnReEnable(object sender, RoutedEventArgs e)
    {
        ReEnableButton.IsEnabled = false;
        await ReEnableAsync();
    }

    private async Task ReEnableAsync()
    {
        var error = await App.Current.Hardware.ReEnableDgpuAsync();
        if (error is not null)
        {
            MessageBox.Show(this, error, "NVIDIA GPU", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private async void OnModeHybrid(object sender, RoutedEventArgs e) => await SwitchModeSafe(FirmwareGpuMode.Hybrid);

    private async void OnModeIntegrated(object sender, RoutedEventArgs e) => await SwitchModeSafe(FirmwareGpuMode.Integrated);

    private async Task SwitchModeSafe(FirmwareGpuMode mode)
    {
        ModeHybrid.IsEnabled = ModeIntegrated.IsEnabled = false;
        try
        {
            await SwitchMode(mode);
        }
        catch (Exception ex)
        {
            Log.Error("GPU mode switch failed", ex);
            MessageBox.Show(this, $"The switch failed: {ex.Message}", "GPU mode", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RefreshFromState();
        }
    }

    private async Task SwitchMode(FirmwareGpuMode mode)
    {
        var description = Describe(mode);
        if (MessageBox.Show(this,
                $"Switch the GPU to {description}?\n\nThis is the same firmware switch MSI Center uses. It takes effect after the laptop fully shuts down and starts again.",
                "GPU mode", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        var error = await App.Current.Hardware.RequestGpuModeAsync(mode);
        if (error is not null)
        {
            MessageBox.Show(this, error, "GPU mode", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show(this,
                "Switch armed. Shut down now to apply it?\n\nSave your work first. MSI's method powers the laptop off and normally starts it again by itself. If it stays off, press the power button.\n\nChoose No to apply it at your next full shutdown.",
                "GPU mode", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            FirmwareGpuSwitch.ShutDownToApply();
        }
    }

    // ---- general ------------------------------------------------------------------------------

    private async void OnStartWithWindows(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var wanted = StartWithWindows.IsChecked == true;
        if (!App.Current.Hardware.ControlsHardware)
        {
            await RefreshStartupTaskAsync();
            return;
        }

        if (!Elevation.IsElevated)
        {
            await RefreshStartupTaskAsync();
            if (MessageBox.Show(this, "Setting up start-with-Windows needs administrator rights. Restart Wattkin as administrator now?", "Wattkin", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                App.Current.RestartElevated();
            }

            return;
        }

        StartWithWindows.IsEnabled = false;
        var error = await Task.Run(() => wanted ? StartupTask.Enable() : StartupTask.Disable());
        StartWithWindows.IsEnabled = true;
        if (error is not null)
        {
            MessageBox.Show(this, error, "Start with Windows", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        await RefreshStartupTaskAsync();
    }

    private void OnGeneralChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        App.Current.Store.Update(s =>
        {
            s.TrayShowsWatts = TrayWatts.IsChecked == true;
            s.BatteryLogging = BatteryLogToggle.IsChecked == true;
        });
    }

    private void OnWidgetToggle(object sender, RoutedEventArgs e) => App.Current.SetWidgetVisible(WidgetToggle.IsChecked == true);

    private void OnElevate(object sender, RoutedEventArgs e) => App.Current.RestartElevated();

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Log.FilePath))
        {
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        }
        else
        {
            OpenFolder(Log.Directory);
        }
    }

    private async void OnDiagnostics(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"Wattkin-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt", Filter = "Text|*.txt" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        DiagnosticsButton.IsEnabled = false;
        DiagnosticsButton.Content = "Reading the hardware…";
        try
        {
            var path = dialog.FileName;
            await Task.Run(() => Diagnostics.Run(path)); // reads hardware for ~15 s: not on the UI thread
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Diagnostics failed", ex);
            MessageBox.Show(this, ex.Message, "Diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            DiagnosticsButton.IsEnabled = true;
            DiagnosticsButton.Content = "Save diagnostics report";
        }
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
