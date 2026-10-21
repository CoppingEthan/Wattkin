using System.Diagnostics;
using System.Runtime.InteropServices;
using Wattkin.Hardware;
using Wattkin.Monitoring;
using Wattkin.Platform;
using Wattkin.Settings;

namespace Wattkin.Features;

public sealed record HardwareState(
    bool Available,
    string? Problem,
    string? EcFirmware,
    string? WmiVersion,
    bool CanWrite,
    int? EcChargeLimit,
    ShiftMode? Shift,
    FanMode? Fan,
    bool? CoolerBoost,
    int? KeyboardLevel,
    bool? KeyboardAutoOff,
    bool? Webcam,
    bool? SimulatingUnplug,
    FirmwareGpuInfo? Gpu,
    string? GpuProblem,
    string ChargeStatus,
    CalibrationProgress? Calibration,
    string ActiveProfile,
    bool MsiSoftwareRunning,
    EcCapabilities? Caps = null)
{
    public static HardwareState Initial(string problem) =>
        new(false, problem, null, null, false, null, null, null, null, null, null, null, null, null, null, "", null, "", false);
}

/// <summary>What this laptop's register map supports (drives which controls the UI shows).</summary>
public sealed record EcCapabilities(
    EcSupport Support,
    string? MapSource,
    bool CanSimulateUnplug,
    bool CanSwitchGpuMode,
    bool HasCoolerBoost,
    bool HasKeyboardLight,
    bool HasKeyboardTimeout,
    int KeyboardMaxLevel,
    bool HasFanCurves,
    bool HasFullFanTable = false,
    bool HasSportMode = false,
    bool HasKeyboardColor = false);

/// <summary>
/// Owns the EC connection and everything that writes to hardware.
///
/// The charge limit (0xD7) and "run from battery while plugged in" (0xBF bit0) have exactly one writer:
/// <see cref="ReconcileCharge"/>. It runs on the monitor thread, reads both registers from the EC,
/// asks the calibration or charge controller what they should be, applies an independent safety
/// guard, and writes only the difference. Nothing trusts a remembered value, so a failed write or a
/// crash is corrected on the next pass. When nothing is changing it checks only every 30 s.
/// </summary>
public sealed partial class HardwareService : IDisposable
{
    internal static readonly string[] MsiProcesses = ["MSI.CentralServer", "MSI_Central_Service", "MSIAPService", "OmApSvcBroker"];
    private static readonly TimeSpan ActiveReconcile = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdleReconcile = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SimulateReassert = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SimulateSettle = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UnknownPercentLimit = TimeSpan.FromSeconds(10);
    private const double AbsoluteDischargeFloor = 5;

    private readonly SettingsStore _store;
    private readonly SystemMonitor _monitor;
    private readonly bool _controls;
    private readonly AudioMute _audio = new();
    private readonly HotkeyEvents _hotkeys = new();
    private readonly object _gate = new();

    private MsiAcpi? _acpi;
    private volatile EmbeddedController? _ec;
    private ChargeController? _charge;
    private CalibrationController? _calibration;
    private Timer? _stateTimer;
    private int _stateBusy;
    private bool? _lastMicLed;
    private bool? _lastSpeakerLed;
    private bool _msiRunning;
    private volatile bool _disposed;
    private int _suspendGeneration;
    private volatile bool _suspended;

    // Charge reconcile state (monitor thread, under _gate).
    private volatile bool _reconcileNow = true;
    private DateTime _lastReconcileUtc = DateTime.MinValue;
    private DateTime _activeUntilUtc = DateTime.UtcNow + ActiveWindow;
    private DateTime _lastSimulateAssertUtc = DateTime.MinValue;
    private DateTime _simulateClearedUtc = DateTime.MinValue;
    private DateTime _percentUnknownSinceUtc = DateTime.MaxValue;
    private volatile bool _simulatingSeen;
    private int? _ecLimitSeen;

    // Profile auto-switching (monitor thread).
    private bool? _lastOnAc;
    private bool? _pendingOnAc;
    private bool _lowBatteryApplied;

    // Cached firmware GPU mode: reading the UEFI variable can trap into firmware, so not on a timer.
    private FirmwareGpuInfo? _gpu;
    private string? _gpuError;

    /// <param name="controlsHardware">
    /// False for a copy that doesn't own the running instance (screenshots, measurements): it reads the
    /// EC for display but never writes it, handles no keys and never releases what the owner set.
    /// </param>
    public HardwareService(SettingsStore store, SystemMonitor monitor, bool controlsHardware = true)
    {
        _store = store;
        _monitor = monitor;
        _controls = controlsHardware;
        State = HardwareState.Initial(Elevation.IsElevated ? "Connecting to the embedded controller…" : "Hardware controls need administrator rights.");
    }

    public HardwareState State { get; private set; }

    /// <summary>False in a copy that runs alongside the owner (screenshots, measurements): it only reads.</summary>
    public bool ControlsHardware => _controls;

    /// <summary>The EC for changes: null unless this copy owns the hardware.</summary>
    private EmbeddedController? Writer => _controls ? _ec : null;

    private const string NotOwner = "Another copy of Wattkin is in charge of the hardware.";
    public bool IsCalibrating => _store.Current.Calibration is not null;

    public event Action? StateChanged;
    public event Action<string>? Osd;
    public event Action? OpenRequested;

    public void Initialize()
    {
        _msiRunning = MsiProcesses.Any(n => Process.GetProcessesByName(n).Length > 0);
        if (_controls && WindowsPower.HasLegacyKeys(_store.Current))
        {
            _store.Update(WindowsPower.MigrateLegacyKeys);
        }

        if (!Elevation.IsElevated)
        {
            SafeRefreshState();
            return;
        }

        InitializeKeyboardLight(); // a USB light, where the keys aren't lit by the EC
        _acpi = MsiAcpi.TryOpen(out var error, readOnly: !_controls);
        if (_acpi is null)
        {
            State = HardwareState.Initial(error ?? "MSI WMI interface not available.");
            StateChanged?.Invoke();
            return;
        }

        var ec = new EmbeddedController(_acpi, _store.Current.SportModeSeen);
        Log.Info($"EC firmware {ec.FirmwareVersion}: {ec.Support} register map ({ec.MapSource ?? "none"}), writes {(ec.CanWrite ? "enabled" : "disabled")}.");

        if (_controls)
        {
            // The laptop in MSI's Sport mode by itself: its firmware has one, so profiles may use it.
            if (ec.CanWrite && ec.ShiftAtStart == EmbeddedController.MsiSport && !_store.Current.SportModeSeen)
            {
                _store.Update(s => s.SportModeSeen = true);
                Log.Info("The laptop was in MSI's Sport mode (0xC0): Sport is offered in profiles from now on.");
            }

            if (!_store.Current.HardwareAdopted)
            {
                AdoptCurrentHardwareState(ec);
            }

            // Killed during a forced fan mode: the first profile applied puts Cooler Boost back as it was.
            if (_store.Current.CoolerBoostBeforeForce is { } boostBefore)
            {
                _fansBeforeForce = new FansBeforeForce(null, boostBefore, null, null);
            }

            CaptureFactoryFanCurve(ec); // before any profile can write a curve of its own
            ec.MarkVendorSoftwarePresent();
            _charge = new ChargeController(_store);
            _calibration = new CalibrationController(_store, () => _monitor.BatteryInfo);
            _calibration.Finished += p => Osd?.Invoke(p.Message);
        }

        _monitor.ChargeTarget = () => _store.Current.FullChargeOnce || IsCalibrating ? 100 : _store.Current.ChargeLimit;
        _monitor.Sensors = ec;
        _gpu = FirmwareGpuSwitch.Read(out _gpuError);

        // Publish last: from here on monitor samples drive the charge logic.
        _ec = ec;
        if (_controls)
        {
            StartControlling();
            ScheduleFirstFanMeasurement(ec);
        }

        _monitor.FastChanged += OnUiVisibilityChanged;
        OnUiVisibilityChanged(_monitor.IsFast);
        _monitor.Poke();
    }

    /// <summary>Keyboard settings, special keys and mute LEDs: only the copy that owns the hardware.</summary>
    private void StartControlling()
    {
        ApplyKeyboardSettings(_monitor.Latest?.Power.OnAc ?? true);
        RestoreKeyboardLight();

        if (_store.Current.HandleHotkeys)
        {
            _hotkeys.Pressed += OnHotkey;
            if (_hotkeys.Start() is { } hotkeyError)
            {
                Log.Warn(hotkeyError);
            }
        }

        if (_store.Current.SyncMuteLeds)
        {
            _audio.Changed += SyncMuteLeds;
            _audio.Start();
            SyncMuteLeds();
        }
    }

    /// <summary>
    /// First time the hardware is reachable: adopt what the laptop is set to now (charge limit, keyboard
    /// auto-off, performance and fan mode), so starting the app changes nothing until the user does.
    /// Only marked done when every read succeeded.
    /// </summary>
    private void AdoptCurrentHardwareState(EmbeddedController ec)
    {
        var limit = ec.GetChargeLimit();
        var autoOff = ec.GetKeyboardAutoOff();
        var shift = ec.GetShiftMode();
        var fan = ec.GetFanMode();
        var boost = ec.GetCoolerBoost();

        // A register the map doesn't have reads as null too: only a failed read of one it has counts
        // (the Stealth 15M has no keyboard auto-off register; some maps no Cooler Boost). A performance
        // mode never chosen (0x80, MSI's software never ran) is no failure: that laptop is adopted as
        // Balanced, or every start would find it unreadable and push the defaults anyway.
        if (limit is null || (shift is null && !ec.ShiftModeUnset) || fan is null || (autoOff is null && ec.HasKeyboardTimeout) || (boost is null && ec.HasCoolerBoost))
        {
            Log.Warn("Couldn't read the current hardware state; will try again next start. Nothing was changed.");
            return;
        }

        _store.Update(s =>
        {
            if (limit is >= ChargePolicy.Min and <= ChargePolicy.Max)
            {
                s.ChargeLimit = limit.Value;
                s.ChargeResumeBelow = null;
            }

            if (autoOff is { } off)
            {
                s.KeyboardTimeout = off ? KeyboardTimeout.AutoOff : KeyboardTimeout.AlwaysOn;
            }

            var wanted = shift switch
            {
                ShiftMode.Eco => "Battery saver",
                ShiftMode.Turbo => "Performance",
                _ => fan == FanMode.Silent ? "Quiet" : "Balanced",
            };

            // The default profile of that name, else (renamed earlier) the first one.
            var profile = s.FindProfile(wanted) ?? s.Profiles[0];
            profile.CoolerBoost = boost ?? false;
            if (shift == ShiftMode.Sport)
            {
                profile.Performance = ShiftMode.Sport; // as the laptop is now: adopting changes nothing
            }

            if (fan == FanMode.Custom)
            {
                profile.Fan = null; // keep MSI's custom fan curve
            }

            s.ActiveProfile = profile.Name;
            s.AcProfile = profile.Name;
            s.BatteryProfile = profile.Name;
            s.HardwareAdopted = true;
        });
        Log.Info($"Adopted current hardware state: limit {limit}%, keyboard auto-off {autoOff}, mode {shift}/{fan}.");
    }

    // ---- Monitor thread ---------------------------------------------------------------------------

    public void OnSnapshot(Snapshot snapshot)
    {
        if (!_controls || _ec is null || _disposed || _suspended)
        {
            return;
        }

        ExpireFanForce();
        (string Name, ProfileScope Scope, bool Announce)? apply;
        lock (_gate)
        {
            if (_disposed || _suspended)
            {
                return;
            }

            ReconcileCharge(snapshot);
            apply = AutoSwitchProfiles(snapshot);
        }

        // Applied after the lock: a full profile changes Windows settings and maybe the display mode,
        // which take seconds, and the UI's own hardware calls mustn't wait for that.
        if (apply is { } a)
        {
            ApplyProfile(a.Name, a.Scope, a.Announce);
        }
    }

    private bool ChargeIsActive() =>
        IsCalibrating || _simulatingSeen || _store.Current is { DrainToLimit: true } or { FullChargeOnce: true } || _store.Current.ChargeResumeBelow is not null
        || DateTime.UtcNow < _activeUntilUtc || RunningFromBattery;

    private void ReconcileCharge(Snapshot snapshot)
    {
        var ec = _ec;
        if (ec is not { CanWrite: true } || _charge is null || _calibration is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var interval = ChargeIsActive() ? ActiveReconcile : IdleReconcile;
        if (!_reconcileNow && now - _lastReconcileUtc < interval)
        {
            return;
        }

        _reconcileNow = false;
        _lastReconcileUtc = now;

        // Each register is handled on its own: a failed read of one never blocks fixing the other.
        var limitRaw = ec.GetChargeLimitRaw();
        var simulating = ec.GetSimulatedUnplug();
        if (simulating is { } s)
        {
            if (_simulatingSeen && !s)
            {
                _simulateClearedUtc = now;
            }

            _simulatingSeen = s;
        }

        if (limitRaw is { } raw)
        {
            _ecLimitSeen = raw & 0x7F;
        }

        var percent = snapshot.Power.Percent;
        if (percent is null)
        {
            _percentUnknownSinceUtc = _percentUnknownSinceUtc == DateTime.MaxValue ? now : _percentUnknownSinceUtc;
        }
        else
        {
            _percentUnknownSinceUtc = DateTime.MaxValue;
        }

        var target = _calibration.IsActive ? _calibration.Tick(snapshot, simulating == true) : _charge.Decide(snapshot, simulating == true);
        var wantSimulate = target?.SimulateUnplug ?? false;
        if (!_calibration.IsActive && RunningFromBattery)
        {
            wantSimulate = true; // Debug page: a few minutes on the battery with the charger connected
        }

        // Independent safety guard: whatever a controller says, never discharge blind or near empty.
        if (wantSimulate && ((percent is null && now - _percentUnknownSinceUtc > UnknownPercentLimit) || percent is <= AbsoluteDischargeFloor))
        {
            Log.Warn($"Safety stop: not discharging while plugged in (battery {(percent is { } p ? $"{p:0}%" : "unknown")}).");
            wantSimulate = false;
        }

        if (target is { } t && limitRaw is { } current && current != EmbeddedController.EncodeChargeLimit(t.Limit) && ec.SetChargeLimit(t.Limit))
        {
            _ecLimitSeen = t.Limit;
            _activeUntilUtc = now + ActiveWindow;
        }

        if (simulating is null)
        {
            return; // couldn't read 0xBF this pass; the next pass (in 3 s while active) handles it
        }

        if (wantSimulate)
        {
            var heartbeat = simulating.Value && now - _lastSimulateAssertUtc > SimulateReassert;
            if (!simulating.Value || heartbeat)
            {
                ec.SetSimulatedUnplug(true, heartbeat);
                _lastSimulateAssertUtc = now;
                _simulatingSeen = true;
            }
        }
        else if (simulating.Value && ec.SetSimulatedUnplug(false))
        {
            _simulatingSeen = false;
            _simulateClearedUtc = now;
            _activeUntilUtc = now + ActiveWindow;
        }
    }

    /// <summary>Under the lock: which profile the power state calls for now, if any (the caller applies it after the lock).</summary>
    private (string Name, ProfileScope Scope, bool Announce)? AutoSwitchProfiles(Snapshot snapshot)
    {
        var settings = _store.Current;
        (string Name, ProfileScope Scope, bool Announce)? apply = null;

        // The charger counts as connected while (or just after) the EC simulates an unplug: that's the
        // EC's own state, not a settings flag, so a finished drain or calibration can't cause flapping.
        var settling = DateTime.UtcNow - _simulateClearedUtc < SimulateSettle;
        if (snapshot.Power.Source == PowerSource.Unknown && !_simulatingSeen && !settling)
        {
            return null; // no trustworthy reading this sample
        }

        var onAc = snapshot.Power.OnAc || _simulatingSeen || settling;

        var percent = snapshot.Power.Percent;
        var low = !onAc && settings.IsLow(percent);
        if (_lastOnAc is null)
        {
            _lastOnAc = onAc;
            // Already low at a start: the low-battery rule is dealt with when the start-up pick came from the
            // rules (the power source changed while Wattkin was off) or the profile was applied while this
            // low (by the rule, or by hand). Otherwise it still applies, on the next sample.
            _lowBatteryApplied = low && (settings.ActiveProfileOnAc != onAc || (settings.ActiveProfileOnAc == false && settings.IsLow(settings.ActiveProfilePercent)));
            ApplyKeyboardSettings(onAc);

            // The profile picked last keeps Windows' own settings (they survive a restart); one the rules
            // pick now, or the same one on the other power source (Windows' power mode is set per source),
            // is applied in full: the charger changed while Wattkin was off.
            var startup = StartupProfile(settings, onAc, percent);
            var same = string.Equals(startup, settings.ActiveProfile, StringComparison.OrdinalIgnoreCase)
                && (settings.ActiveProfileOnAc is not { } was || was == onAc);
            return (startup, same ? ProfileScope.Hardware : ProfileScope.Full, false);
        }

        // Debounce: a change must be seen on two samples in a row.
        if (onAc != _lastOnAc)
        {
            if (_pendingOnAc != onAc)
            {
                _pendingOnAc = onAc;
                return null;
            }

            _pendingOnAc = null;
            _lastOnAc = onAc;
            _lowBatteryApplied = low; // unplugged already low: the low-battery rule decides now, not again later
            _activeUntilUtc = DateTime.UtcNow + ActiveWindow;
            _reconcileNow = true;
            Log.Info(onAc ? "Charger connected." : "Charger disconnected.");
            ApplyKeyboardSettings(onAc);
            if (settings.AutoProfile(onAc, percent) is { } rule)
            {
                apply = (rule, ProfileScope.Full, true);
            }
        }
        else
        {
            _pendingOnAc = null;
        }

        // Crossing the threshold on battery: once, until the charger comes back. A manual pick stays until
        // then. With the low-battery rule on "Leave as is" there's nothing to switch to.
        if (low && !_lowBatteryApplied)
        {
            _lowBatteryApplied = true;
            if (settings.LowBatteryProfile is not null && settings.AutoProfile(onAc, percent) is { } rule)
            {
                apply = (rule, ProfileScope.Full, true);
            }
        }

        return apply;
    }

    // ---- Profiles ------------------------------------------------------------------------------

    /// <summary>
    /// After a restart or an update: the profile picked last, unless the charger was plugged in or out
    /// in the meantime. Then what the automatic rules pick, as when that happens while running (still the
    /// last one when the matching rule leaves the profile alone).
    /// </summary>
    public static string StartupProfile(AppSettings settings, bool onAc, double? percent = null) =>
        settings.ActiveProfileOnAc != onAc && settings.AutoProfile(onAc, percent) is { } rule ? rule : settings.ActiveProfile;

    private enum ProfileScope
    {
        /// <summary>EC settings only (they can reset across boots and sleep).</summary>
        Hardware,
        /// <summary>EC plus Windows power mode, CPU boost/max state, refresh rate and dGPU.</summary>
        Full,
    }

    public Task ApplyProfileAsync(string name) => Task.Run(() => ApplyProfile(name, ProfileScope.Full, announce: false));

    /// <summary>
    /// EC writes happen under the hardware lock; slow Windows changes (dGPU, display) outside it.
    /// <paramref name="name"/> null: the active profile, looked up under the lock (after sleep, so a
    /// profile the rules applied meanwhile isn't overwritten by the one from before).
    /// </summary>
    private void ApplyProfile(string? name, ProfileScope scope, bool announce)
    {
        if (!_controls)
        {
            return; // changes EC and Windows settings
        }

        Profile? profile;
        var appliedOnAc = _lastOnAc ?? _monitor.Latest?.Power.OnAc ?? true; // the charger as the rules see it (a drain counts as plugged in)
        lock (_gate)
        {
            profile = _store.Current.FindProfile(name ?? _store.Current.ActiveProfile);
            if (profile is null)
            {
                return;
            }

            if (_ec is { } ec && !_disposed)
            {
                if (profile.Performance is { } shift)
                {
                    ec.SetShiftMode(shift);
                }

                ApplyFans(ec, profile);
            }

            if (!_disposed && profile.KeyboardLevel is { } level)
            {
                WriteKeyboardLevel(level); // the EC's register, or the USB light
            }

            // Remembered with the power source and battery level it was chosen at, so a restart keeps it
            // (see AutoSwitchProfiles). Under the lock, so the active profile is never a step behind.
            var appliedPercent = _monitor.Latest?.Power.Percent;
            var current = _store.Current;
            if (current.ActiveProfile != profile.Name || current.ActiveProfileOnAc != appliedOnAc || current.IsLow(current.ActiveProfilePercent) != current.IsLow(appliedPercent))
            {
                var picked = profile.Name;
                _store.Update(s =>
                {
                    s.ActiveProfile = picked;
                    s.ActiveProfileOnAc = appliedOnAc;
                    s.ActiveProfilePercent = appliedPercent;
                });
            }
        }

        if (scope == ProfileScope.Full)
        {
            try
            {
                var onAc = appliedOnAc;
                // Inside Update so the saved originals are never changed while being serialized.
                _store.Update(s =>
                {
                    WindowsPower.SetMode(profile.WindowsMode, onAc, s);
                    if (profile.CpuBoost is { } boost)
                    {
                        WindowsPower.SetCpuBoost(boost, s);
                    }

                    if (profile.MaxCpuPercent is { } max)
                    {
                        WindowsPower.SetMaxCpuPercent(max, s);
                    }

                    if (profile.EnergyPreference is { } epp)
                    {
                        WindowsPower.SetEnergyPreference(epp, s);
                    }

                    if (profile.MaxFrequencyMHz is { } mhz)
                    {
                        WindowsPower.SetMaxFrequency(mhz, s);
                    }
                });

                if (profile.RefreshRate is { } hz)
                {
                    DisplayModes.SetRefreshRate(hz);
                }
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException or UnauthorizedAccessException)
            {
                Log.Error($"Applying Windows settings for profile {profile.Name} failed", ex);
            }
        }

        Log.Info($"Profile applied ({scope}): {profile.Name}");
        if (announce)
        {
            Osd?.Invoke($"Profile: {profile.Name}");
        }

        SafeRefreshState();
    }

    public void CycleProfile()
    {
        var profiles = _store.Current.Profiles;
        if (profiles.Count == 0)
        {
            return;
        }

        var index = profiles.FindIndex(p => p.Name == _store.Current.ActiveProfile);
        var next = profiles[(index + 1) % profiles.Count].Name;
        Task.Run(() => ApplyProfile(next, ProfileScope.Full, announce: true));
    }

    // ---- Battery (settings only; ReconcileCharge applies them) -----------------------------------

    public Task SetChargeLimitAsync(int limit, int? resumeBelow) => Mutate("charge limit", s =>
    {
        s.ChargeLimit = Math.Clamp(limit, ChargePolicy.Min, ChargePolicy.Max);
        var maxResume = ChargePolicy.MaxResumeFor(s.ChargeLimit);
        s.ChargeResumeBelow = resumeBelow is { } r && r < maxResume ? Math.Max(0, r) : null;
        s.ChargeBandHolding = false;
    });

    public Task SetFullChargeOnceAsync(bool enabled) => Mutate(enabled ? "charge to 100% once: on" : "charge to 100% once: off", s =>
    {
        s.FullChargeOnce = enabled;
        if (enabled)
        {
            s.DrainToLimit = false;
        }
    });

    public Task SetDrainToLimitAsync(bool enabled) => enabled && _ec is { CanSimulateUnplug: false }
        ? Task.CompletedTask // needs the verified "run from battery while plugged in" register
        : Mutate(enabled ? "drain to limit: on" : "drain to limit: off", s =>
    {
        s.DrainToLimit = enabled;
        if (enabled)
        {
            s.FullChargeOnce = false;
        }
    });

    private Task Mutate(string what, Action<AppSettings> change)
    {
        if (IsCalibrating)
        {
            Log.Info($"Ignored '{what}': calibration owns charging until it finishes.");
            return Task.CompletedTask;
        }

        _store.Update(change);
        var s = _store.Current;
        Log.Info($"Charge settings ({what}): limit {s.ChargeLimit}%, resume {(s.ChargeResumeBelow is { } r ? $"{r}%" : "default")}, full once {s.FullChargeOnce}, drain {s.DrainToLimit}.");
        _activeUntilUtc = DateTime.UtcNow + ActiveWindow;
        _reconcileNow = true;
        _monitor.Poke();
        return Task.CompletedTask;
    }

    public string? StartCalibration(bool fast)
    {
        if (_calibration is null || _ec is not { CanWrite: true } || _monitor.Latest is not { } latest)
        {
            return State.Problem ?? "Hardware controls are not available.";
        }

        if (!_ec.CanSimulateUnplug)
        {
            return "Calibration needs a register that is only verified on supported firmware.";
        }

        string? error;
        lock (_gate)
        {
            error = _calibration.Start(latest, fast);
        }

        _reconcileNow = true;
        _monitor.Poke();
        return error;
    }

    public void CancelCalibration()
    {
        lock (_gate)
        {
            if (_calibration is not null)
            {
                _calibration.Cancel();
            }
            else if (_store.Current.Calibration is { } record)
            {
                // No hardware connection (e.g. started without admin): close the record so charging isn't locked.
                _store.Update(s =>
                {
                    record.FinishedUtc = DateTime.UtcNow;
                    record.Phase = nameof(CalibrationPhase.Cancelled);
                    record.Outcome = "Cancelled.";
                    s.CalibrationHistory = [record, .. s.CalibrationHistory.Take(19)];
                    s.Calibration = null;
                });
            }
        }

        _reconcileNow = true;
        _monitor.Poke();
    }

    public void SetCalibrationFast(bool fast)
    {
        lock (_gate)
        {
            _calibration?.SetFastDischarge(fast);
        }
    }

    // ---- Keyboard, devices ----------------------------------------------------------------------

    public Task SetKeyboardTimeoutAsync(KeyboardTimeout timeout) => Task.Run(() =>
    {
        _store.Update(s => s.KeyboardTimeout = timeout);
        ApplyKeyboardSettings(_monitor.Latest?.Power.OnAc ?? true);
        SafeRefreshState();
    });

    private void ApplyKeyboardSettings(bool onAc)
    {
        var autoOff = _store.Current.KeyboardTimeout switch
        {
            KeyboardTimeout.AutoOff => true,
            KeyboardTimeout.AutoOffOnBattery => !onAc,
            _ => false,
        };
        Writer?.SetKeyboardAutoOff(autoOff);
        SetSoftwareAutoOff(autoOff); // where the EC's auto-off doesn't reach the keys (USB light)
    }

    public Task SetWebcamAsync(bool enabled) => Task.Run(() =>
    {
        Writer?.SetWebcam(enabled);
        SafeRefreshState();
    });

    /// <summary>
    /// Turns a disabled RTX back on (left disabled by an older Wattkin or in Device Manager). Wattkin never
    /// disables it: a disabled RTX can't use NVIDIA's deep power-off, so it draws more than a sleeping one.
    /// </summary>
    public Task<string?> ReEnableDgpuAsync() => Task.Run(() =>
    {
        if (!_controls)
        {
            return NotOwner;
        }

        var error = DiscreteGpu.Enable();
        _monitor.Poke();
        return error;
    });

    public Task<string?> RequestGpuModeAsync(FirmwareGpuMode mode) => Task.Run(() =>
    {
        if (!_controls)
        {
            return NotOwner;
        }

        if (_acpi is null || _ec is not { CanSwitchGpuMode: true })
        {
            return "The firmware GPU switch is only enabled on verified firmware.";
        }

        var error = FirmwareGpuSwitch.RequestMode(mode, _acpi);
        if (error is null && mode == FirmwareGpuMode.Integrated && DiscreteGpu.Query().State == DgpuState.DisabledInWindows)
        {
            // Windows remembers a disabled device: turn it back on now, or it returns disabled in Hybrid.
            DiscreteGpu.Enable();
        }

        _gpu = FirmwareGpuSwitch.Read(out _gpuError);
        SafeRefreshState();
        return error;
    });

    // ---- Power transitions ------------------------------------------------------------------------

    /// <summary>
    /// Before sleep: stop discharging and let the PC sleep. Doesn't wait for the lock (the firmware calls
    /// serialize themselves), so it finishes inside Windows' short suspend window. Settings are kept, so
    /// a drain or calibration resumes afterwards.
    /// </summary>
    public void OnSuspend()
    {
        Interlocked.Increment(ref _suspendGeneration);
        _suspended = true;
        ReleaseHardware(restoreRestingLimit: false);
    }

    /// <summary>Sign-out or shutdown: as suspend, and also put the normal charge limit back.</summary>
    public void OnShutdown()
    {
        Interlocked.Increment(ref _suspendGeneration);
        _suspended = true;
        ReleaseHardware(restoreRestingLimit: true);
    }

    /// <summary>After sleep the EC may have reset some settings: re-apply the hardware part.</summary>
    public void OnResume()
    {
        var generation = Volatile.Read(ref _suspendGeneration);
        Task.Run(() =>
        {
            Thread.Sleep(3000);
            lock (_gate)
            {
                // Checked under the lock: a suspend between the check and clearing the flag would
                // otherwise leave it clear for the next sleep.
                if (Volatile.Read(ref _suspendGeneration) != generation || _disposed)
                {
                    return; // slept again in the meantime
                }

                if (!_controls || _ec is not { } ec)
                {
                    _suspended = false;
                    return;
                }

                ec.MarkVendorSoftwarePresent();
                _calibration?.OnResume();
                _lastMicLed = null;
                _lastSpeakerLed = null;
                _reconcileNow = true;
                _activeUntilUtc = DateTime.UtcNow + ActiveWindow;
                _suspended = false;
            }

            ApplyKeyboardSettings(_monitor.Latest?.Power.OnAc ?? true);
            RestoreKeyboardLight();
            ApplyProfile(null, ProfileScope.Hardware, announce: false); // the active one, as it is by then
            SyncMuteLeds();
            _monitor.Poke();
        });
    }

    /// <summary>
    /// Clears "run from battery while plugged in" (retrying) and stops calibration load. On exit it also
    /// writes the user's normal limit if a calibration had raised it to 100%.
    /// </summary>
    private void ReleaseHardware(bool restoreRestingLimit)
    {
        if (!_controls)
        {
            return; // the owning copy is still running and keeps what it set
        }

        _calibration?.Pause();

        // A USB light auto-off turned off: on again for the sign-in screen and after Wattkin. Not tied to
        // the controller (the light and its auto-off work where the controller stays read-only).
        WakeKeyboardLight();
        if (_ec is not { CanWrite: true } ec)
        {
            return;
        }

        // A charge pass already past its suspended/disposed check could still switch run-from-battery
        // on after it's cleared below: wait (briefly, inside Windows' suspend window) for it to finish.
        var locked = Monitor.TryEnter(_gate, TimeSpan.FromMilliseconds(750));
        try
        {
            ClearSimulatedUnplug(ec);
            Volatile.Write(ref _runFromBatteryUntilTicks, 0);
            ReleaseFanForce(ec); // under the lock when it came free: not alongside an expiring force
        }
        finally
        {
            if (locked)
            {
                Monitor.Exit(_gate);
            }
        }

        if (restoreRestingLimit && IsCalibrating)
        {
            ec.SetChargeLimit(_store.Current.ChargeLimit);
        }
    }

    private static void ClearSimulatedUnplug(EmbeddedController ec)
    {
        var cleared = false;
        for (var attempt = 0; attempt < 3 && !cleared; attempt++)
        {
            var simulating = ec.GetSimulatedUnplug();
            cleared = simulating == false || (simulating == true && ec.SetSimulatedUnplug(false) && ec.GetSimulatedUnplug() == false);
            if (!cleared)
            {
                Thread.Sleep(150);
            }
        }

        if (!cleared)
        {
            Log.Error("Could not confirm that discharge-while-plugged-in was switched off.");
        }
    }

    // ---- Hotkeys & LEDs ---------------------------------------------------------------------------

    private void OnHotkey(HotkeyEvent evt)
    {
        try
        {
            HandleHotkey(evt);
        }
        catch (Exception ex)
        {
            Log.Error($"Hotkey {evt.Code} failed", ex);
        }
    }

    private void HandleHotkey(HotkeyEvent evt)
    {
        var ec = _ec;
        switch (evt.Known)
        {
            case MsiHotkey.MicMute:
                if (_audio.ToggleMicrophone() is { } muted)
                {
                    if (ec?.SetMicMuteLed(muted) == true)
                    {
                        _lastMicLed = muted;
                    }

                    Osd?.Invoke(muted ? "Microphone off" : "Microphone on");
                }

                break;
            case MsiHotkey.PerformanceMode:
                CycleProfile();
                break;
            case MsiHotkey.CoolerBoost:
                if (ec?.GetCoolerBoost() is { } boost)
                {
                    ec.SetCoolerBoost(!boost);
                    Osd?.Invoke(!boost ? "Cooler Boost on" : "Cooler Boost off");
                }

                break;
            case MsiHotkey.LaunchControlCenter or MsiHotkey.LaunchControlCenter2:
                OpenRequested?.Invoke();
                break;
            case MsiHotkey.Webcam or MsiHotkey.WebcamLock:
                Thread.Sleep(150);
                if (ec?.GetWebcam() is { } cam)
                {
                    Osd?.Invoke(cam ? "Webcam on" : "Webcam off");
                }

                break;
            case MsiHotkey.FnLock:
                Thread.Sleep(150);
                if (ec?.GetFnLock() is { } locked)
                {
                    Osd?.Invoke(locked ? "Fn lock on" : "Fn lock off");
                }

                break;
            case MsiHotkey.TouchpadOff:
                Osd?.Invoke("Touchpad off");
                break;
            case MsiHotkey.TouchpadOn:
                Osd?.Invoke("Touchpad on");
                break;
            case MsiHotkey.MonitorOff:
                MonitorPower.TurnOff();
                break;
            case MsiHotkey.CameraApp:
                Launch("microsoft.windows.camera:");
                break;
            case MsiHotkey.BluetoothSettings:
                Launch("ms-settings:bluetooth");
                break;
        }
    }

    private static void Launch(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Error($"Could not open {uri}", ex);
        }
    }

    /// <summary>Called when Windows reports a mute change (no polling) and after resume.</summary>
    private void SyncMuteLeds()
    {
        try
        {
            if (_ec is not { CanWrite: true } ec || !_store.Current.SyncMuteLeds || _disposed || _suspended)
            {
                return;
            }

            if (_audio.SpeakerMuted is { } s && s != _lastSpeakerLed && ec.SetMuteLed(s))
            {
                _lastSpeakerLed = s;
            }

            if (_audio.MicrophoneMuted is { } m && m != _lastMicLed && ec.SetMicMuteLed(m))
            {
                _lastMicLed = m;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Mute LED sync failed", ex);
        }
    }

    // ---- State (only refreshed while a window is open) ------------------------------------------------

    private void OnUiVisibilityChanged(bool visible)
    {
        if (visible)
        {
            _msiRunning = MsiProcesses.Any(n => Process.GetProcessesByName(n).Length > 0);
            _stateTimer ??= new Timer(_ => SafeRefreshState(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }
        else
        {
            _stateTimer?.Dispose();
            _stateTimer = null;
        }
    }

    /// <summary>Refresh off the UI thread (EC reads take tens of milliseconds).</summary>
    public void SafeRefreshStateAsync() => Task.Run(SafeRefreshState);

    public void SafeRefreshState()
    {
        if (Interlocked.Exchange(ref _stateBusy, 1) == 1)
        {
            return;
        }

        try
        {
            RefreshState();
        }
        catch (Exception ex)
        {
            Log.Error("Reading hardware state failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _stateBusy, 0);
        }
    }

    private void RefreshState()
    {
        var settings = _store.Current;
        if (_ec is not { } ec || _disposed)
        {
            State = State with { ActiveProfile = settings.ActiveProfile, MsiSoftwareRunning = _msiRunning };
            StateChanged?.Invoke();
            return;
        }

        State = new HardwareState(
            true,
            ec.CanWrite ? null : ec.Reason ?? "Hardware controls are off for this firmware.",
            ec.FirmwareFull,
            ec.WmiVersion,
            ec.CanWrite,
            ec.GetChargeLimit(),
            ec.GetShiftMode(),
            ec.GetFanMode(),
            ec.GetCoolerBoost(),
            KeyboardLevelNow(ec),
            ec.GetKeyboardAutoOff(),
            ec.GetWebcam(),
            _simulatingSeen,
            _gpu,
            _gpuError,
            ChargeStatusText(),
            CalibrationView(),
            settings.ActiveProfile,
            _msiRunning,
            new EcCapabilities(ec.Support, ec.MapSource, ec.CanSimulateUnplug, ec.CanSwitchGpuMode, ec.HasCoolerBoost, ec.HasKeyboardLight || HasUsbKeyboardLight, ec.HasKeyboardTimeout || HasUsbKeyboardLight,
                HasUsbKeyboardLight ? MysticLight.MaxLevel : ec.KeyboardMaxLevel, ec.HasFanCurves, ec.HasFullFanTable, ec.HasSportMode, HasKeyboardColor: HasUsbKeyboardLight));
        StateChanged?.Invoke();
    }

    private string ChargeStatusText() => _calibration?.IsActive == true ? "Calibrating" : _charge?.Status ?? "";

    /// <summary>Progress while running; the final result stays visible afterwards.</summary>
    private CalibrationProgress? CalibrationView() => _calibration?.Progress;

    /// <summary>Called on every snapshot so the charge/calibration text stays live without extra EC reads.</summary>
    public void RefreshStatusOnly()
    {
        if (_ec is null)
        {
            return;
        }

        State = State with
        {
            ChargeStatus = ChargeStatusText(),
            Calibration = CalibrationView(),
            EcChargeLimit = _ecLimitSeen ?? State.EcChargeLimit,
            SimulatingUnplug = _simulatingSeen,
            ActiveProfile = _store.Current.ActiveProfile,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Hardware first, each step isolated, so nothing can skip releasing the EC.
        _disposed = true;
        try
        {
            ReleaseHardware(restoreRestingLimit: true);
        }
        catch (Exception ex)
        {
            Log.Error("Releasing hardware failed", ex);
        }

        foreach (var step in new Action[] { () => _stateTimer?.Dispose(), _hotkeys.Dispose, _audio.Dispose, () => _calibration?.Dispose(), () => _acpi?.Dispose(), DisposeKeyboardLight })
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                Log.Error("Shutdown step failed", ex);
            }
        }
    }
}

internal static class MonitorPower
{
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MONITORPOWER = 0xF170;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    public static void TurnOff() => PostMessage(new IntPtr(0xFFFF), WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(2));
}
