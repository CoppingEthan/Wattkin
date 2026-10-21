using System.Diagnostics;
using System.Management;
using Microsoft.Win32;
using Wattkin.Native;
using Wattkin.Platform;
using Wattkin.Settings;

namespace Wattkin.Features;

/// <summary>
/// The screen's brightness as one range, like f.lux's Alt+Page Down / Page Up but reaching both ways:
/// the laptop panel's backlight (0–100%, what Windows' slider and the Fn keys set, which also saves
/// power), and below its lowest an "extra dim" that darkens the picture itself (<see cref="GammaRamp"/>),
/// down to <see cref="MinExtraDim"/>%.
///
/// One number covers both (<see cref="ToPosition"/>): 0 to 100 is the backlight; −90 to −1 is extra dim
/// (the picture at 10–99%) with the backlight at its lowest. The keys and the flyout's slider move along
/// it. Only the copy that owns the hardware changes anything. A display's gamma ramp is only touched
/// once the user dims, never while f.lux runs (both would write the same ramps), and its own ramp (a
/// colour calibration lives there) is what dimming scales and what un-dimming puts back.
///
/// A dim is kept across a restart into an update (the old copy hands it over, so the screen doesn't
/// flash bright at night); an exit, sign-out, shutdown, crash or power cut starts again at normal.
/// </summary>
public sealed class ScreenBrightness : IDisposable
{
    public const int MinExtraDim = 10;
    public const int Step = 10;
    public const int MinPosition = MinExtraDim - 100;

    /// <summary>How long a slider the user just moved is left alone by refreshes (its value is still being applied).</summary>
    public static readonly TimeSpan UserMoveSettles = TimeSpan.FromSeconds(1.5);

    private const char MonitorSeparator = '|'; // in neither a GDI name nor a monitor's interface path
    private const int DimKey = 0x5701;
    private const int BrightKey = 0x5702;
    private static readonly TimeSpan HandoffWithin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan KeepCheck = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OwnEchoes = TimeSpan.FromSeconds(5);

    private readonly SettingsStore _store;
    private readonly bool _controls;

    // _gate: the screen state below (held through slow WMI and GDI calls). _requestGate: only the
    // slider's pending position, so the UI thread never waits for the screen while dragging.
    private readonly object _gate = new();
    private readonly object _requestGate = new();

    // Under _gate: each display's own ramp (from before the first dim; a straight one where it couldn't
    // be read), the level each dimmed display shows, and displays whose own ramp couldn't be put back
    // yet. A display that refused every ramp (HDR) isn't in _shown, so nothing keeps retrying it.
    private readonly Dictionary<string, ushort[]> _bases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _shown = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _restorePending = new(StringComparer.OrdinalIgnoreCase);

    // Under _gate: which monitor each saved ramp came from. A GDI name is reused (undocked, \\.\DISPLAY2
    // can be another monitor), and one monitor's calibration must never end up on another.
    private readonly Dictionary<string, string> _monitors = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(int Value, long AtMs)> _ownSets = [];
    private int _backlightVersion;

    private GlobalHotkeys? _hotkeys;
    private bool _hotkeysWanted;
    private int _hotkeyRetries;
    private System.Windows.Threading.DispatcherTimer? _retryHotkeys;
    private Timer? _keep;
    private ManagementEventWatcher? _brightnessEvents;
    private int? _pending;
    private bool _working;
    private volatile bool _disposed;
    private string? _hotkeyProblem;
    private string? _dimProblem;

    /// <param name="controls">False for screenshot and measurement copies: they never change the screen.</param>
    public ScreenBrightness(SettingsStore store, bool controls)
    {
        _store = store;
        _controls = controls;
    }

    /// <summary>Something changed (from any thread).</summary>
    public event Action? Changed;

    /// <summary>A short message for the on-screen display (the keys).</summary>
    public event Action<string>? Osd;

    /// <summary>The panel's backlight as last read or set; null when it has none Windows controls (or not read yet).</summary>
    public int? Backlight { get; private set; }

    public bool HasBacklight => Backlight is not null;

    /// <summary>Where the screen is on the combined range (<see cref="ToPosition"/>).</summary>
    public int Position => ToPosition(Backlight, _store.Current.ScreenExtraDimPercent);

    public int MaxPosition => HasBacklight ? 100 : 0;

    public bool HotkeysActive => _hotkeys?.IsRegistered(DimKey) == true;

    /// <summary>Why the keys or extra dim don't fully work, in a sentence; null when all's well.</summary>
    public string? Problem => _hotkeyProblem ?? _dimProblem;

    // ---- the range (pure) -------------------------------------------------------------------------

    /// <summary>One number for both: the backlight (0–100), or below 0 the extra dim (picture at 100 + position %).</summary>
    public static int ToPosition(int? backlight, int extraDim) => extraDim < 100 ? Math.Max(MinPosition, extraDim - 100) : backlight ?? 0;

    /// <summary>The backlight (null: leave it, there's none) and the extra dim for a position.</summary>
    public static (int? Backlight, int ExtraDim) FromPosition(int position, bool hasBacklight) =>
        position < 0
            ? (hasBacklight ? 0 : null, Math.Max(MinExtraDim, 100 + position))
            : (hasBacklight ? Math.Min(100, position) : null, 100);

    /// <summary>One press of the keys: to the next multiple of <see cref="Step"/> down or up, within the range.</summary>
    public static int StepFrom(int position, bool down, bool hasBacklight)
    {
        // Floored, so it lands on tens below 0 too (−25 → −30 or −20; −5 up → 0).
        var next = down
            ? (int)Math.Floor((position - 1) / (double)Step) * Step
            : ((int)Math.Floor(position / (double)Step) + 1) * Step;
        return Math.Clamp(next, MinPosition, hasBacklight ? 100 : 0);
    }

    /// <summary>"Brightness 40%", or "Extra dim 70%" below the backlight's lowest.</summary>
    public static string Describe(int position, bool hasBacklight = true) =>
        position < 0 ? $"Extra dim {100 + position}%" : hasBacklight ? $"Brightness {position}%" : "Extra dim off";

    /// <summary>The slider's label: "40%", "Extra dim 70%", or "Normal" on a screen with no backlight Windows controls.</summary>
    public static string SliderText(int position, bool hasBacklight) =>
        position < 0 ? $"Extra dim {100 + position}%" : hasBacklight ? $"{position}%" : "Normal";

    /// <summary>
    /// At startup: a dim is kept only when the previous copy handed it over moments ago, restarting into
    /// an update. Otherwise (an exit, a crash, a power cut, the next day) the screen starts at normal.
    /// </summary>
    public static bool KeepDimmedAtStart(int extraDim, DateTime? handoffUtc, DateTime nowUtc) =>
        extraDim < 100 && handoffUtc is { } handoff && nowUtc >= handoff && nowUtc - handoff < HandoffWithin;

    // ---- running ----------------------------------------------------------------------------------

    /// <summary>
    /// On the UI thread, once. What the previous copy left is settled before the keys can be pressed:
    /// an inherited dim is known as such, so its dimmed ramps are never mistaken for the displays' own.
    /// </summary>
    public void Start()
    {
        if (!_controls)
        {
            return;
        }

        bool inherited;
        bool restore;
        lock (_gate)
        {
            LoadBases();
            var s = _store.Current;
            inherited = KeepDimmedAtStart(s.ScreenExtraDimPercent, s.ScreenDimHandoffUtc, DateTime.UtcNow);
            if (inherited)
            {
                foreach (var display in _bases.Keys)
                {
                    _shown[display] = s.ScreenExtraDimPercent; // dimmed by the previous copy
                }
            }

            restore = !inherited && (s.ScreenExtraDimPercent < 100 || _bases.Count > 0);
            if (restore)
            {
                // Owed their own ramps from the start: a key pressed before the restore below runs
                // un-dims these too, rather than find nothing dimmed and drop the saved ramps.
                _restorePending.UnionWith(_bases.Keys);
            }

            if (s.ScreenDimHandoffUtc is not null)
            {
                _store.Update(x => x.ScreenDimHandoffUtc = null);
            }
        }

        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        _keep = new Timer(_ => Keep(), null, Timeout.Infinite, Timeout.Infinite);
        SetHotkeys(_store.Current.ScreenHotkeys);
        Task.Run(() =>
        {
            try
            {
                var backlight = Platform.Backlight.Get();
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    Backlight = backlight;
                    _backlightVersion++;
                    if (inherited)
                    {
                        ShowExtraDim(_store.Current.ScreenExtraDimPercent); // the same ramps: no flash
                    }
                    else if (restore)
                    {
                        // Dimmed when Wattkin stopped without handing over (a crash, a power cut): the
                        // displays' own ramps back, if known.
                        RestoreDisplays(assumeDimmed: true);
                    }
                }

                if (backlight is not null)
                {
                    WatchBrightness();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Screen brightness: starting failed", ex);
            }

            Changed?.Invoke();
        });
    }

    /// <summary>
    /// Windows tells when the panel's brightness changes (WmiMonitorBrightnessEvent): the Fn keys,
    /// Windows' own slider, Quick Settings, battery saver, or Wattkin itself. Event-driven, so the
    /// flyout's slider follows at once without polling. Re-subscribed if WMI restarts.
    /// </summary>
    private void WatchBrightness()
    {
        try
        {
            var watcher = new ManagementEventWatcher(new ManagementScope(@"root\WMI"), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            watcher.EventArrived += OnBrightnessEvent;
            watcher.Stopped += OnBrightnessWatchStopped;
            watcher.Start();
            ManagementEventWatcher? old;
            lock (_gate)
            {
                if (_disposed)
                {
                    watcher.Dispose();
                    return;
                }

                old = _brightnessEvents;
                _brightnessEvents = watcher;
            }

            old?.Dispose();
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Log.Warn($"Screen brightness changes from the Fn keys won't show until the flyout is reopened: {ex.Message}");
        }
    }

    /// <summary>The WMI service restarted (servicing, a repository repair): subscribe again shortly.</summary>
    private void OnBrightnessWatchStopped(object sender, StoppedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (!_disposed)
            {
                WatchBrightness();
            }
        });
    }

    /// <summary>
    /// WMI's thread. An echo of a value Wattkin set itself (they can arrive late, after a newer one) is
    /// ignored. Otherwise brighter while extra dim is on (the Fn key's "up", or the charger's
    /// brightness) means it's meant to be brighter, so extra dim ends.
    /// </summary>
    private void OnBrightnessEvent(object sender, EventArrivedEventArgs e)
    {
        try
        {
            using var reading = e.NewEvent;
            if (reading["Brightness"] is not byte value)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                var now = Environment.TickCount64;
                _ownSets.RemoveAll(s => now - s.AtMs > OwnEchoes.TotalMilliseconds);
                var echo = _ownSets.FindIndex(s => s.Value == value);
                if (echo >= 0)
                {
                    _ownSets.RemoveAt(echo);
                    return;
                }

                if (value == Backlight)
                {
                    return;
                }

                var brighter = value > (Backlight ?? 0);
                Backlight = value;
                _backlightVersion++;
                if (brighter && _store.Current.ScreenExtraDimPercent < 100)
                {
                    ShowExtraDim(100);
                }
            }

            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Reading a screen brightness change failed", ex);
        }
    }

    /// <summary>
    /// Reads the backlight again, in case a change was missed. A set or event meanwhile wins. Only reads,
    /// so copies that don't control the screen (screenshots) use it too, to show the real brightness.
    /// </summary>
    public Task RefreshAsync() => Task.Run(() =>
    {
        int version;
        lock (_gate)
        {
            version = _backlightVersion;
        }

        var now = Platform.Backlight.Get();
        lock (_gate)
        {
            if (_disposed || version != _backlightVersion || now == Backlight)
            {
                return;
            }

            Backlight = now;
            _backlightVersion++;
        }

        Changed?.Invoke();
    });

    /// <summary>The flyout's slider: applied in the background; while dragging only the latest position counts.</summary>
    public void RequestPosition(int position)
    {
        if (!_controls)
        {
            return;
        }

        lock (_requestGate)
        {
            _pending = Math.Clamp(position, MinPosition, MaxPosition);
            if (_working)
            {
                return;
            }

            _working = true;
        }

        Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    int next;
                    lock (_requestGate)
                    {
                        if (_pending is not { } p || _disposed)
                        {
                            _working = false; // in the same lock as the check, so a request can't slip between
                            return;
                        }

                        next = p;
                        _pending = null;
                    }

                    lock (_gate)
                    {
                        if (!_disposed)
                        {
                            Apply(next);
                        }
                    }

                    Changed?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Setting the screen brightness failed", ex);
                lock (_requestGate)
                {
                    _working = false; // or the slider would stop working
                }
            }
        });
    }

    /// <summary>Alt+Page Down / Page Up: one step, from where the screen really is.</summary>
    private void StepByKey(bool down) => Task.Run(() =>
    {
        try
        {
            int next;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_store.Current.ScreenExtraDimPercent >= 100)
                {
                    Backlight = Platform.Backlight.Get(); // in case a change was missed
                    _backlightVersion++;
                }

                next = StepFrom(Position, down, HasBacklight);
                Apply(next);
            }

            var text = Describe(next, HasBacklight);
            Osd?.Invoke(next <= MinPosition ? $"{text} (darkest)" : text);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Screen brightness key failed", ex);
        }
    });

    /// <summary>Under the lock. Moving within extra dim leaves the backlight alone: only going into it lowers the backlight.</summary>
    private void Apply(int position)
    {
        var (backlight, extraDim) = FromPosition(position, HasBacklight);
        var withinExtraDim = position < 0 && Position < 0;
        if (!withinExtraDim && backlight is { } b && b != Backlight && Platform.Backlight.Set(b))
        {
            Backlight = b;
            _backlightVersion++;
            _ownSets.Add((b, Environment.TickCount64)); // its event echoes back later
        }

        ShowExtraDim(extraDim);
    }

    /// <summary>Under the lock: the picture at <paramref name="percent"/> on every display (100 = their own ramps back), remembered in the settings.</summary>
    private void ShowExtraDim(int percent)
    {
        if (FluxRunning)
        {
            // f.lux owns the ramps: don't fight it, in either direction.
            _dimProblem = percent < 100
                ? "f.lux is running and controls the same thing, so Wattkin can't dim below the lowest brightness. Quit f.lux (and take it out of Windows startup) to use extra dim."
                : null;
            if (percent >= 100)
            {
                _shown.Clear();
                _restorePending.Clear();
                _bases.Clear();
                _monitors.Clear();
                SaveBases();
                Remember(100);
                ArmKeep();
            }

            return;
        }

        if (percent >= 100)
        {
            RestoreDisplays(assumeDimmed: false);
            return;
        }

        var wasDimmed = _shown.Count > 0;
        foreach (var display in GammaRamp.Displays())
        {
            if (_bases.ContainsKey(display) && !SameMonitor(display))
            {
                Forget(display); // another monitor has this name now: start afresh with its own ramp
            }

            var wasPending = _restorePending.Remove(display); // dimming again supersedes a restore still pending
            if (!_bases.ContainsKey(display))
            {
                // Its own ramp (a calibration, if it has one), read only while Wattkin hasn't dimmed it.
                // Unreadable: the straight ramp, so every dimmed display has a base to go back to.
                _bases[display] = !_shown.ContainsKey(display) && GammaRamp.Read(display) is { } own && GammaRamp.Plausible(own) ? own : GammaRamp.Linear();
                if (GammaRamp.MonitorId(display) is { } monitor)
                {
                    _monitors[display] = monitor;
                }
            }

            if (GammaRamp.Dim(display, _bases[display], percent) is { } level)
            {
                _shown[display] = level;
            }
            else if (wasPending)
            {
                // Still showing an old dim of Wattkin's, refused for now: wanted dimmed again, so Keep
                // dims it once it can, and un-dimming still owes it its own ramp.
                _shown[display] = percent;
            }
            else if (!_shown.ContainsKey(display))
            {
                // Refused even normal (HDR) and never dimmed by Wattkin: left alone from now on, with no
                // ramp to put back (else every exit and start would keep retrying it).
                Forget(display);
            }

            // Otherwise dimmed by Wattkin and refused only for now (the lock screen after a wake): it stays
            // dimmed in the books, so Keep puts the dim back, and un-dimming its own ramp, once it can.
        }

        SaveBases();
        _dimProblem = _shown.Count == 0
            ? "The screen didn't accept extra dim. It doesn't work while HDR is on."
            : _shown.Values.All(level => level > percent) ? $"Windows only lets the picture go down to {_shown.Values.Min()}% for now." : null;
        if (_dimProblem is not null && _shown.Count > 0 && GammaRamp.Limited() && Elevation.IsElevated && GammaRamp.AllowFullRange())
        {
            _dimProblem += " Wattkin has allowed the full range: it works after you next sign in.";
            _store.Update(s => s.ScreenGammaRangeSet = true);
        }

        if (!wasDimmed && _shown.Count > 0)
        {
            Log.Info($"Screen extra dim: on (picture at {_shown.Values.Min()}%).");
        }

        Remember(percent);
        ArmKeep();
    }

    /// <summary>
    /// Under the lock: each dimmed display gets its own ramp back. One that refuses (e.g. while the
    /// session isn't on screen) stays remembered and is tried again, also after a restart.
    /// <paramref name="assumeDimmed"/>: every display Wattkin remembers a ramp for (at start, at exit).
    /// </summary>
    private void RestoreDisplays(bool assumeDimmed)
    {
        var displays = (assumeDimmed ? _bases.Keys : (IEnumerable<string>)_shown.Keys).Concat(_restorePending).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var restored = 0;
        foreach (var display in displays)
        {
            if (!SameMonitor(display))
            {
                Forget(display); // another monitor has the name now: never Wattkin's dim, and not its ramp
            }
            else if (GammaRamp.Write(display, _bases.GetValueOrDefault(display) ?? GammaRamp.Linear()))
            {
                _restorePending.Remove(display);
                restored++;
            }
            else if (GammaRamp.Displays().Contains(display, StringComparer.OrdinalIgnoreCase))
            {
                _restorePending.Add(display);
            }
            else
            {
                _restorePending.Remove(display); // unplugged: nothing to put back
            }
        }

        if (restored > 0)
        {
            Log.Info("Screen extra dim: off.");
        }

        _shown.Clear();
        foreach (var display in _bases.Keys.Where(d => !_restorePending.Contains(d)).ToList())
        {
            _bases.Remove(display);
            _monitors.Remove(display);
        }

        _dimProblem = _restorePending.Count > 0 ? "Wattkin couldn't put the screen's normal picture back yet; it keeps trying." : null;
        SaveBases();
        Remember(100);
        ArmKeep();
    }

    /// <summary>The level in the settings: what the slider shows.</summary>
    private void Remember(int percent)
    {
        if (_store.Current.ScreenExtraDimPercent != percent)
        {
            _store.Update(s => s.ScreenExtraDimPercent = percent);
        }
    }

    /// <summary>Under the lock: the monitor at this GDI name is the one its saved ramp came from (yes when either is unknown).</summary>
    private bool SameMonitor(string display) =>
        !_monitors.TryGetValue(display, out var saved)
        || GammaRamp.MonitorId(display) is not { } now
        || string.Equals(now, saved, StringComparison.OrdinalIgnoreCase);

    /// <summary>Under the lock: nothing Wattkin knew about this display applies any more.</summary>
    private void Forget(string display)
    {
        _bases.Remove(display);
        _shown.Remove(display);
        _restorePending.Remove(display);
        _monitors.Remove(display);
    }

    /// <summary>
    /// The displays' own ramps survive a restart into an update (or a crash) in the settings, keyed
    /// "GDI name|monitor" (just the GDI name when the monitor couldn't be told).
    /// </summary>
    private void SaveBases()
    {
        Dictionary<string, string>? saved = _bases.Count == 0 ? null : _bases.ToDictionary(
            kv => _monitors.TryGetValue(kv.Key, out var monitor) ? $"{kv.Key}{MonitorSeparator}{monitor}" : kv.Key,
            kv => GammaRamp.Encode(kv.Value));
        var current = _store.Current.ScreenBaseRamps;
        if ((saved is null && current is null) || (saved is not null && current is not null && saved.Count == current.Count && saved.All(kv => current.TryGetValue(kv.Key, out var v) && v == kv.Value)))
        {
            return;
        }

        _store.Update(s => s.ScreenBaseRamps = saved);
    }

    private void LoadBases()
    {
        _bases.Clear();
        _monitors.Clear();
        foreach (var (key, text) in _store.Current.ScreenBaseRamps ?? [])
        {
            if (GammaRamp.Decode(text) is { } ramp)
            {
                var split = key.IndexOf(MonitorSeparator);
                var display = split < 0 ? key : key[..split];
                _bases[display] = ramp;
                if (split >= 0 && split + 1 < key.Length)
                {
                    _monitors[display] = key[(split + 1)..];
                }
            }
        }
    }

    /// <summary>Under the lock: the keep-alive check runs only while there's a dim to keep or a restore to finish (no idle wake-ups).</summary>
    private void ArmKeep()
    {
        var active = _shown.Count > 0 || _restorePending.Count > 0;
        _keep?.Change(active ? KeepCheck : Timeout.InfiniteTimeSpan, active ? KeepCheck : Timeout.InfiniteTimeSpan);
    }

    /// <summary>Every few seconds while dimmed: a display change, a game or sleep can put a normal ramp back; a pending restore is retried.</summary>
    private void Keep()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || (_shown.Count == 0 && _restorePending.Count == 0))
                {
                    return;
                }

                if (FluxRunning)
                {
                    return;
                }

                if (_restorePending.Count > 0)
                {
                    RestoreDisplays(assumeDimmed: false);
                    return;
                }

                foreach (var (display, level) in _shown.ToList())
                {
                    if (!SameMonitor(display))
                    {
                        Forget(display); // swapped while dimmed: the new one gets its own ramp at the next dim
                        SaveBases();
                        continue;
                    }

                    var expected = GammaRamp.Scale(_bases.GetValueOrDefault(display), level);
                    if (GammaRamp.Read(display) is { } now && !GammaRamp.Close(now, expected))
                    {
                        GammaRamp.Write(display, expected);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Keeping the screen dimmed failed", ex);
        }
    }

    /// <summary>A display came, went or changed mode: dim the new one too, and read the backlight again.</summary>
    private void OnDisplaysChanged(object? sender, EventArgs e) => Task.Run(async () =>
    {
        await Task.Delay(1500).ConfigureAwait(false); // let the new mode settle
        try
        {
            lock (_gate)
            {
                if (!_disposed && _store.Current.ScreenExtraDimPercent < 100 && _shown.Count > 0)
                {
                    ShowExtraDim(_store.Current.ScreenExtraDimPercent);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Screen brightness: display change failed", ex);
        }

        await RefreshAsync().ConfigureAwait(false);
    });

    /// <summary>After sleep the ramps may be back to normal and the backlight changed.</summary>
    public void OnResume() => OnDisplaysChanged(null, EventArgs.Empty);

    /// <summary>Sign-out or shutdown: the displays' own ramps back, and the next start begins at normal brightness.</summary>
    public void OnSessionEnding()
    {
        if (!_controls)
        {
            return;
        }

        lock (_gate)
        {
            if (!_disposed && (_shown.Count > 0 || _bases.Count > 0 || _store.Current.ScreenExtraDimPercent < 100))
            {
                RestoreDisplays(assumeDimmed: true);
            }
        }
    }

    /// <summary>
    /// From the crash handler: the displays' own ramps back, so a crash doesn't leave the screen dark
    /// with nothing running to brighten it. Best effort: gives up if the screen is busy, never throws.
    /// </summary>
    public void RestoreAfterCrash()
    {
        if (!_controls || !Monitor.TryEnter(_gate, TimeSpan.FromMilliseconds(500)))
        {
            return;
        }

        try
        {
            if (!_disposed && (_shown.Count > 0 || _restorePending.Count > 0))
            {
                RestoreDisplays(assumeDimmed: true);
            }
        }
        catch
        {
            // crashing already: nothing more to do
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <summary>
    /// UI thread: hold Alt+Page Down / Page Up, or let them go. If another app has them (f.lux), tries
    /// again after 30 s, then less and less often (up to every 5 min).
    /// </summary>
    public void SetHotkeys(bool enabled)
    {
        if (!_controls || _disposed)
        {
            return;
        }

        var toggled = enabled != _hotkeysWanted;
        _hotkeysWanted = enabled;
        _hotkeys ??= new GlobalHotkeys();
        _hotkeys.UnregisterAll();
        var problem = (string?)null;
        if (enabled)
        {
            var error = _hotkeys.Register(DimKey, User32.MOD_ALT, User32.VK_NEXT, () => StepByKey(down: true))
                ?? _hotkeys.Register(BrightKey, User32.MOD_ALT, User32.VK_PRIOR, () => StepByKey(down: false));
            if (error is not null)
            {
                _hotkeys.UnregisterAll();
                problem = $"Alt+Page Down / Page Up aren't working: {error}{(FluxRunning ? " (f.lux uses them)" : "")}. Wattkin keeps trying.";
            }
        }

        var changed = toggled || problem != _hotkeyProblem;
        if (problem != _hotkeyProblem)
        {
            // Logged when it changes, not at every retry.
            if (problem is not null)
            {
                Log.Warn(problem);
            }
            else if (_hotkeyProblem is not null)
            {
                Log.Info("Alt+Page Down / Page Up: working now.");
            }

            _hotkeyProblem = problem;
        }

        if (enabled && problem is not null)
        {
            _retryHotkeys ??= new System.Windows.Threading.DispatcherTimer();
            _retryHotkeys.Stop();
            _retryHotkeys.Interval = TimeSpan.FromSeconds(Math.Min(300, 30 << Math.Min(_hotkeyRetries, 4)));
            _retryHotkeys.Tick -= OnRetryHotkeys;
            _retryHotkeys.Tick += OnRetryHotkeys;
            _retryHotkeys.Start();
            _hotkeyRetries++;
        }
        else
        {
            _retryHotkeys?.Stop();
            _hotkeyRetries = 0;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private void OnRetryHotkeys(object? sender, EventArgs e)
    {
        _retryHotkeys?.Stop();
        SetHotkeys(_store.Current.ScreenHotkeys);
    }

    private static bool FluxRunning => Process.GetProcessesByName("flux").Length > 0;

    /// <summary>
    /// UI thread. <paramref name="restoreScreen"/>: the displays' own ramps back and the dim forgotten
    /// (a normal exit). Otherwise (restarting into an update) the dim is handed over, and the new copy
    /// puts the same ramps back at once.
    /// </summary>
    public void Dispose(bool restoreScreen)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        _retryHotkeys?.Stop();
        _hotkeys?.Dispose();
        ManagementEventWatcher? watcher;
        lock (_gate)
        {
            if (_controls && !_disposed)
            {
                var dimmed = _shown.Count > 0 || _bases.Count > 0 || _store.Current.ScreenExtraDimPercent < 100;
                if (restoreScreen && dimmed)
                {
                    RestoreDisplays(assumeDimmed: true);
                }
                else if (!restoreScreen && _store.Current.ScreenExtraDimPercent < 100)
                {
                    _store.Update(s => s.ScreenDimHandoffUtc = DateTime.UtcNow);
                }
            }

            _disposed = true;
            watcher = _brightnessEvents;
            _brightnessEvents = null;
        }

        _keep?.Dispose(); // after the restore above, which re-arms it

        try
        {
            watcher?.Stop();
            watcher?.Dispose();
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
        }
    }

    public void Dispose() => Dispose(restoreScreen: true);
}
