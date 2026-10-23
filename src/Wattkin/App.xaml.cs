using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Wattkin.Features;
using Wattkin.Monitoring;
using Wattkin.Platform;
using Wattkin.Settings;
using Wattkin.UI;
using Microsoft.Win32;

namespace Wattkin;

public partial class App : Application
{
    internal const string InstanceName = @"Local\Wattkin";

    private SingleInstance? _single;
    private bool _previewOnly;
    private bool _exiting;
    private bool _restarting;
    private double? _lastSavedRest;

    public static new App Current => (App)Application.Current;

    public SettingsStore Store { get; private set; } = null!;
    public SystemMonitor Monitor { get; private set; } = null!;
    public HardwareService Hardware { get; private set; } = null!;
    public ScreenBrightness Screen { get; private set; } = null!;
    public BatteryLog BatteryLog { get; } = new();
    public WattHistory History { get; } = new(TimeSpan.FromMinutes(10));

    private TrayIcon? _tray;
    private FlyoutWindow? _flyout;
    private WidgetWindow? _widget;
    private SettingsWindow? _settings;
    private OsdWindow? _osd;
    private Updater? _updater;

    /// <summary>Null in screenshot/measurement copies.</summary>
    internal Updater? Updates => _updater;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        HookExceptionLogging();
        ProcessHardening.Apply();

        // One-shot command-line actions (used by the installer and for diagnostics). An error ends the
        // process with a code: handled by the dispatcher instead, it would leave a windowless copy running
        // (App.xaml: ShutdownMode OnExplicitShutdown), and the next run would think Wattkin is running.
        int? exitCode;
        try
        {
            exitCode = RunCommand(e.Args);
        }
        catch (Exception ex)
        {
            Log.Error($"Command '{string.Join(' ', e.Args)}' failed", ex);
            exitCode = 1;
        }

        if (exitCode is { } code)
        {
            Shutdown(code);
            return;
        }

        // Small windows render just as well in software; skipping Direct3D saves memory and GPU wake-ups.
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        var renderFolder = Arg(e.Args, "--render");
        var measureSeconds = Arg(e.Args, "--measure");
        _previewOnly = renderFolder is not null || measureSeconds is not null;

        // Started from a download (or the build folder): install or update the copy in Program Files.
        // The portable build runs where it is; --portable runs this copy as it is (development).
        if (!_previewOnly && !Installation.RunningInstalled && !DataFolders.Portable && !e.Args.Contains("--portable"))
        {
            OfferInstall();
            Shutdown();
            return;
        }

        // The portable build has no logon task to start it as administrator, so it asks (UAC) each time it
        // starts; declined, it monitors only. A copy already running is shown instead, without asking.
        if (!_previewOnly && DataFolders.Portable && !Elevation.IsElevated && !e.Args.Contains("--elevated")
            && (SingleInstance.RequestShow(InstanceName) || Elevation.RestartElevated()))
        {
            Shutdown();
            return;
        }

        // Screenshots (--render) and measurements (--measure) never own anything: they only read, so
        // they can run next to the real copy without fighting it.
        if (!_previewOnly)
        {
            _single = SingleInstance.Acquire(InstanceName, waitForPrevious: e.Args.Contains("--elevated"));
            if (_single is null)
            {
                Shutdown(); // the running copy was asked to show its flyout
                return;
            }

            _single.Listen(
                onShow: () => Dispatcher.BeginInvoke(ToggleFlyout),
                onExit: () => Dispatcher.BeginInvoke(ExitApp));
        }

        ApplyPalette();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        // Settings and hardware belong to the copy that owns the single instance.
        var owner = _single is not null;
        Store = new SettingsStore(readOnly: !owner);
        Monitor = new SystemMonitor(Store.Current.LearnedRestOfSystemWatts, Store.Current.LearnedRestOfSystemSamples);
        Hardware = new HardwareService(Store, Monitor, controlsHardware: owner);
        Hardware.Osd += message => Dispatcher.BeginInvoke(() => ShowOsd(message));
        Hardware.OpenRequested += () => Dispatcher.BeginInvoke(ToggleFlyout);
        Screen = new ScreenBrightness(Store, controls: owner && !_previewOnly);
        Screen.Osd += message => Dispatcher.BeginInvoke(() => ShowOsd(message));
        Monitor.Updated += OnSnapshot;
        _lastSavedRest = Store.Current.LearnedRestOfSystemWatts;

        if (renderFolder is not null)
        {
            Monitor.Start();
            Task.Run(Hardware.Initialize);
            RenderPreview.Schedule(renderFolder);
            return;
        }

        if (measureSeconds is not null)
        {
            MeasureMode.Run(this, int.TryParse(measureSeconds, out var seconds) ? seconds : 30);
            return;
        }

        TaskbarMessages.AllowFromExplorer();
        _tray = new TrayIcon();
        Monitor.Start();
        Screen.Start();
        Task.Run(() =>
        {
            try
            {
                Hardware.Initialize();
            }
            catch (Exception ex)
            {
                Log.Error("Hardware initialization failed", ex);
            }
        });

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SessionEnding += (_, _) => OnSessionEnding();

        Installation.AfterStart();
        _updater = new Updater(Store, SafeToRestartForUpdate, RestartIntoUpdate);
        if (e.Args.Contains("--updated"))
        {
            Dispatcher.BeginInvoke(() => ShowOsd($"Wattkin updated to {Installation.CurrentVersion.ToString(3)}"), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        if (Store.Current.WidgetEnabled)
        {
            SetWidgetVisible(true);
        }

        if (!e.Args.Contains("--startup"))
        {
            Dispatcher.BeginInvoke(ToggleFlyout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        Log.Info($"Wattkin {typeof(App).Assembly.GetName().Version?.ToString(3)} started (elevated: {Elevation.IsElevated}, installed: {Elevation.IsInstalledSecurely}).");
    }

    /// <summary>Returns an exit code when the arguments asked for a one-shot action, else null.</summary>
    private static int? RunCommand(string[] args)
    {
        if (args.Contains("--diagnose"))
        {
            Diagnostics.Run(Arg(args, "--diagnose"));
            return 0;
        }

        if (args.Contains("--selftest"))
        {
            // --selftest read|write|power [report file]: the hardware self-test (tools\selftest.ps1 runs it).
            var at = Array.IndexOf(args, "--selftest");
            string? Next(int offset) => at + offset < args.Length && !args[at + offset].StartsWith("--", StringComparison.Ordinal) ? args[at + offset] : null;
            return SelfTest.Run(Next(1) ?? "read", Next(2));
        }

        if (args.Contains("--exit"))
        {
            return SingleInstance.RequestExit(InstanceName) ? 0 : 1;
        }

        if (args.Contains("--install"))
        {
            if (DataFolders.Portable)
            {
                if (!args.Contains("--quiet"))
                {
                    MessageBox.Show("This is the portable version, which runs from its folder. To install Wattkin, download Wattkin.exe from GitHub.", "Wattkin", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return 2;
            }

            if (!Elevation.IsElevated)
            {
                return Elevation.RunElevatedAndWait(string.Join(' ', args)) ?? ErrorCancelled;
            }

            // Default: start with Windows. An update keeps whatever the user chose.
            bool? autostart = args.Contains("--no-autostart") ? false : args.Contains("--keep-autostart") ? null : true;
            if (Installation.Install(autostart) is { } error)
            {
                if (!args.Contains("--quiet"))
                {
                    MessageBox.Show(error, "Wattkin", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                return 2;
            }

            // The installed copy inherits administrator rights from this process.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Installation.Exe) { UseShellExecute = false });
            return 0;
        }

        if (args.Contains("--update-from"))
        {
            // Maintainer test of the update path with a local signed build: the same signature and version
            // checks and the same swap as an update from GitHub, then a restart into the new exe.
            if (!Installation.RunningInstalled || !Elevation.IsElevated)
            {
                return 5;
            }

            var dir = Arg(args, "--update-from") ?? "";
            var exe = File.ReadAllBytes(Path.Combine(dir, Releases.ExeAsset));
            var signature = File.ReadAllText(Path.Combine(dir, Releases.SignatureAsset));
            if (!Releases.TryParseVersion(System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(dir, Releases.ExeAsset)).ProductVersion, out var version)
                || !Releases.Verify(version, exe, signature))
            {
                Log.Warn("--update-from: the build's signature doesn't verify; nothing changed.");
                return 3;
            }

            if (Installation.StageUpdate(exe, version) is { } stageError)
            {
                Log.Error(stageError);
                return 4;
            }

            Installation.StopRunningCopy();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Installation.Exe, "--elevated --startup --updated") { UseShellExecute = false });
            return 0;
        }

        if (args.Contains("--put-back"))
        {
            // The portable copy's "Remove from this PC": once the copy that asked (--put-back <its process id>)
            // has released the hardware and exited, put back what Wattkin changed outside its folder.
            if (!DataFolders.Portable || !Elevation.IsElevated || !int.TryParse(Arg(args, "--put-back"), out var asker))
            {
                return 5;
            }

            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(asker);
                if (string.Equals(process.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) && !process.WaitForExit(30_000))
                {
                    MessageBox.Show("Wattkin didn't close, so nothing was changed. Try again.", "Wattkin", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return 6;
                }
            }
            catch (ArgumentException)
            {
                // already exited
            }

            Installation.PutBack();
            Log.Info("Portable copy: put back what Wattkin changed on this PC.");
            MessageBox.Show("Done. The battery charges to 100% again, the fans are back on automatic and Windows' power settings are as they were. You can delete the Wattkin folder now.", "Wattkin", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        if (args.Contains("--uninstall"))
        {
            if (!Elevation.IsElevated)
            {
                return Elevation.RunElevatedAndWait(string.Join(' ', args)) ?? ErrorCancelled;
            }

            var quiet = args.Contains("--quiet");
            var removeData = args.Contains("--remove-data") || (!quiet && MessageBox.Show(
                "Also delete your Wattkin settings and battery history?\n\nChoose No to keep them in case you install Wattkin again.",
                "Uninstall Wattkin", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
            Installation.Uninstall(removeData);
            if (!quiet)
            {
                MessageBox.Show("Wattkin has been removed. Windows' power settings are back to how they were, and the battery charges to 100% again.", "Wattkin", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return 0;
        }

        return null;
    }

    private const int ErrorCancelled = 1223;

    /// <summary>
    /// A copy outside Program Files: install it, update the installed one, or just open the installed
    /// one when it's already this version or newer.
    /// </summary>
    private void OfferInstall()
    {
        if (Installation.InstalledVersion is { } installed && installed >= Installation.CurrentVersion)
        {
            if (!SingleInstance.RequestShow(InstanceName) && !StartupTask.TryRun())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Installation.Exe) { UseShellExecute = true });
            }

            return;
        }

        ApplyPalette();
        new InstallWindow().ShowDialog();
    }

    /// <summary>On the updater's thread: a restart now wouldn't interrupt a calibration or something on screen.</summary>
    private bool SafeToRestartForUpdate() =>
        !Hardware.IsCalibrating && Dispatcher.Invoke(() => _settings is null && _flyout?.IsVisible != true);

    /// <summary>Starts the swapped-in exe (it waits for this one to exit), then exits.</summary>
    private bool RestartIntoUpdate()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Installation.Exe, "--elevated --startup --updated") { UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Error("Starting the updated Wattkin failed", ex);
            return false;
        }

        _restarting = true; // the new copy puts a dimmed screen back at once: don't flash it bright in between
        Dispatcher.BeginInvoke(ExitApp);
        return true;
    }

    private static string? Arg(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : index >= 0 ? "" : null;
    }

    private void HookExceptionLogging()
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled UI exception", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled exception (process ending)", ex.ExceptionObject as Exception);
            Hardware?.OnShutdown(); // last chance to stop a drain
            Screen?.RestoreAfterCrash(); // and not leave the screen dimmed with nothing to brighten it
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("Unobserved task exception", ex.Exception);
            ex.SetObserved();
        };

        // The NotifyIcon runs WinForms message handling; route its exceptions to the log too.
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, ex) => Log.Error("Unhandled tray exception", ex.Exception);
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs args)
    {
        if (args.Category == UserPreferenceCategory.General)
        {
            Dispatcher.BeginInvoke(() => ApplyPalette());
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Hardware.OnSuspend();
                BatteryLog.Flush();
                break;
            case PowerModes.Resume:
                Hardware.OnResume();
                Screen.OnResume();
                break;
        }

        Monitor.Poke();
    }

    /// <summary>Sign-out or shutdown: WPF ends without ExitApp, so release hardware and save here.</summary>
    private void OnSessionEnding()
    {
        Screen.OnSessionEnding(); // the next start begins at normal brightness
        Hardware.OnShutdown();
        BatteryLog.Flush();
        SaveLearned(force: true);
    }

    private void OnSnapshot(Snapshot snapshot)
    {
        try
        {
            Hardware.OnSnapshot(snapshot);
            Hardware.RefreshStatusOnly();
            History.Add(snapshot.TimestampUtc, snapshot.Power.SystemWatts);
            if (Store.Current.BatteryLogging && !_previewOnly)
            {
                BatteryLog.Record(snapshot, Monitor.BatteryInfo);
            }

            SaveLearned(force: false);
            RememberFanMax(snapshot);
            TrackBattery();
        }
        catch (Exception ex)
        {
            Log.Error("Snapshot handling failed", ex);
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_exiting)
            {
                return; // the tray icon and windows may already be gone
            }

            // Each window on its own: one failing must not stop the others updating.
            Safe(() => _tray?.Update(snapshot));
            Safe(() => _flyout?.Update(snapshot));
            Safe(() => _widget?.Update(snapshot));
            Safe(() => _settings?.Update(snapshot));
        });
    }

    /// <summary>Keeps the fastest speed each fan has reached; saved only when a fan beats it (rare after the first days).</summary>
    private void RememberFanMax(Snapshot snapshot)
    {
        if (snapshot.Sensors is not { } sensors || _previewOnly)
        {
            return;
        }

        var settings = Store.Current;
        // Ignore impossible readings (a tach glitch would otherwise squash the bars for good).
        bool Beats(int? rpm, int? max) => rpm is > 0 and < 12000 && rpm >= (max ?? 0) + 100;
        if (Beats(sensors.Fan1Rpm, settings.Fan1MaxRpm) || Beats(sensors.Fan2Rpm, settings.Fan2MaxRpm))
        {
            Store.Update(s =>
            {
                s.Fan1MaxRpm = Math.Max(s.Fan1MaxRpm ?? 0, sensors.Fan1Rpm ?? 0);
                s.Fan2MaxRpm = Math.Max(s.Fan2MaxRpm ?? 0, sensors.Fan2Rpm ?? 0);
            });
        }
    }

    /// <summary>
    /// A different battery (see <see cref="BatterySwap"/>): its capacity history and the calibration
    /// advice start from today, not from the old battery's readings.
    /// </summary>
    private void TrackBattery()
    {
        var settings = Store.Current;
        if (_previewOnly || Monitor.BatteryInfo is not { } info
            || (settings.BatteryIdentity == info.Identity && settings.BatteryFullMWh == info.FullChargedCapacityMWh))
        {
            return;
        }

        var replaced = BatterySwap.IsNewBattery(settings.BatteryIdentity, settings.BatteryFullMWh, info);
        var previousFullMWh = settings.BatteryFullMWh; // Update changes this same object
        Store.Update(s =>
        {
            if (replaced)
            {
                s.BatteryChangedUtc = DateTime.UtcNow;
            }

            s.BatteryIdentity = info.Identity;
            s.BatteryFullMWh = info.FullChargedCapacityMWh;
        });
        if (replaced)
        {
            Log.Info($"New battery detected: {info.Manufacturer} {info.DeviceName}, {info.DesignedCapacityMWh / 1000.0:0.0} Wh design, {info.FullChargedCapacityMWh / 1000.0:0.0} Wh full (was {previousFullMWh / 1000.0:0.0} Wh), {info.CycleCount} cycles. Capacity history starts again.");
            Dispatcher.BeginInvoke(() => ShowOsd("New battery detected"));
        }
    }

    /// <summary>Saves the learned rest-of-system draw only when it has moved meaningfully.</summary>
    private void SaveLearned(bool force)
    {
        if (Monitor.Power.RestOfSystemWatts is not { } rest || (!force && _lastSavedRest is { } saved && Math.Abs(saved - rest) < 0.1))
        {
            return;
        }

        _lastSavedRest = rest;
        Store.Update(s =>
        {
            s.LearnedRestOfSystemWatts = rest;
            s.LearnedRestOfSystemSamples = Monitor.Power.RestOfSystemSamples;
        });
    }

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error("Window update failed", ex);
        }
    }

    public void ToggleFlyout()
    {
        _flyout ??= new FlyoutWindow();
        if (_flyout.IsVisible)
        {
            _flyout.HideFlyout();
        }
        else
        {
            _flyout.ShowNearTray();
        }
    }

    /// <param name="expert">The control asked for lives in the expert view (e.g. custom charge bands).</param>
    public void OpenSettings(string? tab = null, bool expert = false)
    {
        _flyout?.HideFlyout();
        if (_settings is null)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) =>
            {
                _settings = null;
                MemoryTrim.Soon();
            };
            _settings.Show();
        }

        if (expert)
        {
            _settings.SetView(expert: true, save: true);
        }

        _settings.SelectTab(tab);
        _settings.Activate();
        if (_settings.WindowState == WindowState.Minimized)
        {
            _settings.WindowState = WindowState.Normal;
        }
    }

    public bool IsWidgetVisible => _widget?.IsVisible == true;

    public void SetWidgetVisible(bool visible)
    {
        if (visible)
        {
            if (_widget is null)
            {
                _widget = new WidgetWindow();
                _widget.Closed += (_, _) => _widget = null; // also when closed with Alt+F4
                var widget = _widget;
                UI.WindowEffects.ShowWhenPlaced(widget, widget.Show); // revealed once Loaded has placed it
            }

            _widget.Show();
            if (Monitor.Latest is { } latest)
            {
                _widget.Update(latest);
            }
        }
        else
        {
            _widget?.Close();
            _widget = null;
            MemoryTrim.Soon();
        }

        if (Store.Current.WidgetEnabled != visible && !_exiting)
        {
            Store.Update(s => s.WidgetEnabled = visible);
        }
    }

    public void ShowOsd(string message)
    {
        if (!Store.Current.ShowOsd)
        {
            return;
        }

        _osd ??= new OsdWindow();
        _osd.ShowMessage(message);
    }

    public void RestartElevated()
    {
        if (Elevation.RestartElevated())
        {
            _restarting = true;
            ExitApp();
        }
    }

    /// <summary>Restart in place (same rights); the new copy waits for this one to exit.</summary>
    public void Restart()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--elevated --startup") { UseShellExecute = false });
            _restarting = true;
            ExitApp();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Error("Restart failed", ex);
        }
    }

    /// <summary>
    /// The portable copy's "Remove from this PC" (elevated). Like uninstalling, the putting back is done by
    /// another process once this one has released the hardware and exited (--put-back), so nothing this
    /// one still had in flight can change a setting back afterwards.
    /// </summary>
    public void RemoveFromThisPc()
    {
        try
        {
            // Started directly, not through the shell: it gets this copy's administrator rights.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, $"--put-back {Environment.ProcessId}") { UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Error("Starting the put-back failed", ex);
            return;
        }

        ExitApp();
    }

    public void ExitApp()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        Log.Info(_previewOnly ? "Wattkin preview exiting." : "Wattkin exiting.");

        // Hardware first (stop a drain, restore a calibration's limit), then sampling, then save.
        // Each step is isolated so one failure can't skip the next.
        void Step(string name, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error($"Shutdown step '{name}' failed", ex);
            }
        }

        Step("events", () =>
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        });
        Step("updates", () => _updater?.Dispose());
        Step("screen", () => Screen.Dispose(restoreScreen: !_restarting));
        Step("hardware", Hardware.Dispose);
        Step("monitor", Monitor.Dispose);
        Step("tray", () => _tray?.Dispose());
        if (!_previewOnly)
        {
            Step("battery log", BatteryLog.Flush);
            Step("settings", () => SaveLearned(force: true));
        }

        _single?.Dispose();
        Shutdown();
    }

    /// <summary>Follows the Windows light/dark app setting (<paramref name="forceLight"/> is for screenshots).</summary>
    internal void ApplyPalette(bool? forceLight = null)
    {
        var light = forceLight ?? false;
        if (forceLight is null)
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }

#pragma warning disable WPF0001
        ThemeMode = light ? ThemeMode.Light : ThemeMode.Dark;
#pragma warning restore WPF0001

        // One dictionary swap invalidates once, instead of once per brush.
        var palette = new ResourceDictionary();
        void Set(string key, string hex) => palette[key] = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));
        if (light)
        {
            Set("Lc.Window", "#F3F3F3");
            Set("Lc.CardBg", "#FFFFFF");
            Set("Lc.CardBorder", "#E0E0E0");
            Set("Lc.Text", "#1A1A1A");
            Set("Lc.SubText", "#5C5C5C");
            Set("Lc.Faint", "#8A8A8A");
            Set("Lc.Accent", "#9A6300");
            Set("Lc.Good", "#0F7B0F");
            Set("Lc.Warn", "#9D5D00");
            Set("Lc.Bad", "#C42B1C");
            Set("Lc.WidgetBg", "#E6F7F7F7");
            Set("Lc.OnAccent", "#FFFFFF");
        }
        else
        {
            Set("Lc.Window", "#202020");
            Set("Lc.CardBg", "#2B2B2B");
            Set("Lc.CardBorder", "#3A3A3A");
            Set("Lc.Text", "#FFFFFF");
            Set("Lc.SubText", "#C5C5C5");
            Set("Lc.Faint", "#8C8C8C");
            Set("Lc.Accent", "#FFB800");
            Set("Lc.Good", "#6CCB5F");
            Set("Lc.Warn", "#FCE100");
            Set("Lc.Bad", "#FF99A4");
            Set("Lc.WidgetBg", "#E6181818");
            Set("Lc.OnAccent", "#111111");
        }

        var merged = Resources.MergedDictionaries;
        if (_palette is not null)
        {
            merged.Remove(_palette);
        }

        merged.Add(palette);
        _palette = palette;
        IsLightTheme = light;
        PaletteChanged?.Invoke();
    }

    private ResourceDictionary? _palette;

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    public bool IsLightTheme { get; private set; }

    public event Action? PaletteChanged;
}

/// <summary>
/// An elevated process doesn't receive Explorer's "TaskbarCreated" broadcast (UIPI), so after an
/// Explorer restart the tray icon would never come back. Allow just that message through.
/// </summary>
internal static class TaskbarMessages
{
    private const uint MSGFLT_ADD = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilter(uint message, uint flag);

    public static void AllowFromExplorer()
    {
        var message = RegisterWindowMessage("TaskbarCreated");
        if (message != 0)
        {
            ChangeWindowMessageFilter(message, MSGFLT_ADD);
        }
    }
}

/// <summary>Developer aid: --measure N starts the app without UI for N seconds and logs its memory/CPU.</summary>
internal static class MeasureMode
{
    public static void Run(App app, int seconds)
    {
        var started = DateTime.UtcNow;
        var startCpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
        app.Monitor.Start();
        Task.Run(app.Hardware.Initialize);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            var p = System.Diagnostics.Process.GetCurrentProcess();
            p.Refresh();
            var cpu = p.TotalProcessorTime - startCpu;
            var line = $"measure: {seconds}s private {p.PrivateMemorySize64 / 1048576} MB, working set {p.WorkingSet64 / 1048576} MB, managed {GC.GetTotalMemory(false) / 1048576} MB, CPU {cpu.TotalMilliseconds:0} ms ({cpu.TotalMilliseconds / (DateTime.UtcNow - started).TotalMilliseconds / Environment.ProcessorCount * 100:0.000}% of machine), threads {p.Threads.Count}";
            Log.Info(line);
            Console.WriteLine(line);
            app.ExitApp();
        };
        timer.Start();
    }
}

/// <summary>Recent system watts for sparklines.</summary>
public sealed class WattHistory
{
    private readonly TimeSpan _window;
    private readonly Queue<(DateTime Time, double Watts)> _points = new();
    private readonly object _gate = new();

    public WattHistory(TimeSpan window) => _window = window;

    public void Add(DateTime timeUtc, double? watts)
    {
        if (watts is not { } w || !double.IsFinite(w))
        {
            return;
        }

        lock (_gate)
        {
            _points.Enqueue((timeUtc, w));
            while (_points.Count > 0 && timeUtc - _points.Peek().Time > _window)
            {
                _points.Dequeue();
            }
        }
    }

    public (DateTime Time, double Watts)[] Snapshot()
    {
        lock (_gate)
        {
            return [.. _points];
        }
    }
}
