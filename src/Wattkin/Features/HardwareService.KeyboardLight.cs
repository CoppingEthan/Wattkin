using System.Diagnostics;
using Microsoft.Win32;
using Wattkin.Hardware;
using Wattkin.Platform;
using Wattkin.Settings;

namespace Wattkin.Features;

/// <summary>
/// The keyboard light: the EC's register on most MSI laptops, a USB Mystic Light controller where the
/// keys are lit by one (the Stealth 15M A11UEK; see <see cref="MysticLight"/>). That controller can't be
/// read back, so its level and colour live in settings.json, and Wattkin sends them again at startup and
/// after sleep. Where it is found, the EC's keyboard register is left alone, and auto-off is Wattkin's
/// own (<see cref="KeyboardAutoOff"/>): the EC's doesn't reach these keys.
/// </summary>
public sealed partial class HardwareService
{
    private readonly object _lightGate = new();
    private readonly KeyboardAutoOff _autoOff = new();
    private MysticLightInfo? _lightInfo;
    private MysticLight? _light;
    private KeyboardActivity? _keys;
    private Timer? _autoOffTimer;

    /// <summary>The keys are lit by a USB controller found at startup (<see cref="MysticLightInfo.LightsTheKeys"/>).</summary>
    private bool HasUsbKeyboardLight => _lightInfo?.LightsTheKeys(_ec is { HasKeyboardLight: true }) == true;

    private static LightColor KeyboardColorOf(AppSettings s) => LightColor.Parse(s.KeyboardColor) ?? LightColor.White;

    private static long NowMs => Environment.TickCount64;

    /// <summary>The user's light is on (auto-off has something to switch off).</summary>
    private bool KeyboardLit => _store.Current.KeyboardLevel is > 0;

    /// <summary>Looks for the USB light (any copy, for display); only the copy that owns the hardware opens it.</summary>
    private void InitializeKeyboardLight()
    {
        _lightInfo = MysticLight.Find();
        if (_lightInfo is null || !_controls)
        {
            return;
        }

        _light = MysticLight.Open(out var error);
        if (_light is not null)
        {
            Log.Info($"Keyboard light: {_lightInfo.Product} over USB; the controller's keyboard register isn't used.");
        }
        else if (error is not null)
        {
            Log.Warn($"Keyboard light: {error}");
        }

        _autoOff.Delay = TimeSpan.FromSeconds(_store.Current.KeyboardAutoOffSeconds);
        _autoOffTimer = new Timer(_ => OnAutoOffTimer());
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    /// <summary>Sets the keyboard level on whichever light this laptop has, and remembers it (the USB light can't be read back).</summary>
    private void WriteKeyboardLevel(int level)
    {
        if (!_controls)
        {
            return;
        }

        if (_store.Current.KeyboardLevel != level)
        {
            _store.Update(s => s.KeyboardLevel = level);
        }

        if (HasUsbKeyboardLight)
        {
            ShowKeyboardLight(level, KeyboardColorOf(_store.Current));
        }
        else
        {
            _ec?.SetKeyboardLevel(level);
        }
    }

    /// <summary>At startup and after sleep: the USB light back to the level and colour last set, if one was.</summary>
    private void RestoreKeyboardLight()
    {
        if (_controls && HasUsbKeyboardLight && _store.Current.KeyboardLevel is { } level)
        {
            ShowKeyboardLight(level, KeyboardColorOf(_store.Current));
        }
    }

    /// <summary>The user, a profile or a restore sets the light: it shows that now, and auto-off's wait starts again.</summary>
    private void ShowKeyboardLight(int level, LightColor color)
    {
        lock (_lightGate)
        {
            SendLocked(level, color, log: true);
            _autoOff.Shown(NowMs);
            ArmAutoOff();
        }
    }

    /// <summary>Under the light's lock. Retries once with a fresh handle: after sleep or a USB reset Windows may have enumerated the controller again.</summary>
    private bool SendLocked(int level, LightColor color, bool log)
    {
        if (_disposed)
        {
            return false;
        }

        if (_light?.Set(level, color, log) == true)
        {
            return true;
        }

        _light?.Dispose();
        _light = MysticLight.Open(out var error);
        if (_light is null)
        {
            Log.Warn($"Keyboard light: not reachable{(error is null ? "" : $" ({error})")}.");
            return false;
        }

        return _light.Set(level, color, log);
    }

    // ---- Auto-off (Wattkin's own, for the USB light) -----------------------------------------------

    /// <summary>From <see cref="ApplyKeyboardSettings"/>: the key listener runs only while auto-off is wanted.</summary>
    private void SetSoftwareAutoOff(bool enabled)
    {
        if (!_controls || !HasUsbKeyboardLight)
        {
            return;
        }

        lock (_lightGate)
        {
            if (_disposed || enabled == _autoOff.Enabled)
            {
                return;
            }

            if (enabled)
            {
                _keys = KeyboardActivity.Start(OnKeyActivity, out var error);
                if (_keys is null)
                {
                    Log.Warn($"Keyboard auto-off unavailable: {error}");
                    return;
                }

                // Started while Windows is locked (e.g. restarting into an update at the lock screen): the
                // light stays on for the password until the session is unlocked.
                Do(_autoOff.SetLocked(SessionLocked(), NowMs));
            }
            else
            {
                _keys?.Dispose();
                _keys = null;
            }

            Log.Info(enabled ? $"Keyboard auto-off: on, after {_autoOff.Delay.TotalSeconds:0} s without a key press on the built-in keyboard (Wattkin's own; the controller's doesn't reach these keys)." : "Keyboard auto-off: off.");
            Do(_autoOff.SetEnabled(enabled, NowMs));
            ArmAutoOff();
        }
    }

    /// <summary>The key listener's thread, at every key press.</summary>
    private void OnKeyActivity()
    {
        lock (_lightGate)
        {
            if (!_disposed)
            {
                Do(_autoOff.KeyPressed(NowMs));
            }
        }
    }

    private void OnAutoOffTimer()
    {
        lock (_lightGate)
        {
            if (_disposed)
            {
                return;
            }

            var step = _autoOff.Check(NowMs, KeyboardLit);
            if (step == LightStep.None)
            {
                ArmAutoOff(); // woken a little early, or a key was pressed since
            }
            else
            {
                Do(step);
            }
        }
    }

    /// <summary>Keys typed on the lock screen don't reach the listener: the light stays on while Windows is locked.</summary>
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not (SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock))
        {
            return;
        }

        lock (_lightGate)
        {
            if (!_disposed)
            {
                Do(_autoOff.SetLocked(e.Reason == SessionSwitchReason.SessionLock, NowMs));
                ArmAutoOff();
            }
        }
    }

    /// <summary>Under the light's lock: carries out auto-off's step (switching doesn't change the user's level).</summary>
    private void Do(LightStep step)
    {
        var settings = _store.Current;
        if (step == LightStep.Off)
        {
            SendLocked(0, KeyboardColorOf(settings), log: false);
        }
        else if (step == LightStep.On && settings.KeyboardLevel is { } level)
        {
            SendLocked(level, KeyboardColorOf(settings), log: false);
        }

        if (step != LightStep.None)
        {
            ArmAutoOff();
        }
    }

    private void ArmAutoOff() => _autoOffTimer?.Change(_autoOff.NextCheck(NowMs, KeyboardLit) ?? Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    // ---- UI, Debug page, exit -------------------------------------------------------------------------

    public Task SetKeyboardLevelAsync(int level) => Task.Run(() =>
    {
        WriteKeyboardLevel(level);
        SafeRefreshState();
    });

    /// <summary>A colour for the RGB keyboard. Picking one lights the keys: at full brightness if they were off.</summary>
    public Task SetKeyboardColorAsync(LightColor color) => Task.Run(() =>
    {
        _store.Update(s => s.KeyboardColor = color.Hex);
        if (HasUsbKeyboardLight)
        {
            WriteKeyboardLevel(_store.Current.KeyboardLevel is { } current and > 0 ? current : MysticLight.MaxLevel);
        }

        SafeRefreshState();
    });

    /// <summary>How long without typing before Wattkin's own auto-off (USB light), within the settings' bounds.</summary>
    public Task SetKeyboardAutoOffSecondsAsync(int seconds) => Task.Run(() =>
    {
        seconds = Math.Clamp(seconds, AppSettings.KeyboardAutoOffMinSeconds, AppSettings.KeyboardAutoOffMaxSeconds);
        _store.Update(s => s.KeyboardAutoOffSeconds = seconds);
        lock (_lightGate)
        {
            _autoOff.Delay = TimeSpan.FromSeconds(seconds);
            if (!_disposed)
            {
                ArmAutoOff();
            }
        }
    });

    /// <summary>For the UI: the level the EC reports, or for the USB light the one Wattkin last set (auto-off doesn't change it).</summary>
    private int? KeyboardLevelNow(EmbeddedController ec) => HasUsbKeyboardLight ? _store.Current.KeyboardLevel : ec.GetKeyboardLevel();

    /// <summary>Debug page: one line about the USB light, or null when the keys are lit by the EC.</summary>
    private string? KeyboardLightLine() => _lightInfo is { } info
        ? $"USB   {info.Product}{(info.Usable ? "" : " (unknown report layout, not used)")}  keyboard light: "
            + (_store.Current.KeyboardLevel is { } level ? $"level {level}, {_store.Current.KeyboardColor}" : "not set by Wattkin yet")
            + (_autoOff.Enabled ? $", auto-off after {_autoOff.Delay.TotalSeconds:0} s{(_autoOff.Dimmed ? " (off now, until a key is pressed)" : "")}" : ", auto-off off")
        : null;

    /// <summary>
    /// This session's lock screen is up (the sign-in screen's process runs in it). Other sessions' sign-in
    /// screens don't count; one still closing just after sign-in can, until the next lock or unlock.
    /// </summary>
    private static bool SessionLocked()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var processes = Process.GetProcessesByName("LogonUI");
        try
        {
            return processes.Any(p => p.SessionId == session);
        }
        catch (InvalidOperationException)
        {
            return false; // gone while being looked at
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }

    /// <summary>Sleep, sign-out, shutdown: a light auto-off switched off comes back on (nothing would wake it on the sign-in screen).</summary>
    private void WakeKeyboardLight()
    {
        lock (_lightGate)
        {
            if (!_disposed && _autoOff.Dimmed && _store.Current.KeyboardLevel is { } level)
            {
                _light?.Set(level, KeyboardColorOf(_store.Current), log: false);
                _autoOff.Shown(NowMs);
            }
        }
    }

    /// <summary>On exit: a light auto-off switched off comes back on, as nothing would wake it any more.</summary>
    private void DisposeKeyboardLight()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        lock (_lightGate)
        {
            _keys?.Dispose();
            _keys = null;
            _autoOffTimer?.Dispose();
            _autoOffTimer = null;
            if (_autoOff.Dimmed && _store.Current.KeyboardLevel is { } level)
            {
                _light?.Set(level, KeyboardColorOf(_store.Current));
            }

            _light?.Dispose();
            _light = null;
        }
    }
}
