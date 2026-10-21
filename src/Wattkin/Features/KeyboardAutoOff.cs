namespace Wattkin.Features;

/// <summary>What <see cref="KeyboardAutoOff"/> asks of the light.</summary>
public enum LightStep
{
    None,
    /// <summary>Switch the light off (the user's level stays as it is in the settings).</summary>
    Off,
    /// <summary>Put the user's level back.</summary>
    On,
}

/// <summary>
/// The rules of Wattkin's own keyboard auto-off, for a light the EC's auto-off doesn't reach: on the
/// Stealth 15M, 0x2C bit 3 takes and reads back the setting but the Mystic Light keys stay lit (owner,
/// 2026-10-21). Off after <see cref="Delay"/> without a key press on the laptop's own keyboard, back on
/// at the next one. While Windows is locked the keys aren't seen (the lock screen's input doesn't reach
/// applications), so the light stays on then, for typing the password. Pure: times are milliseconds
/// (Environment.TickCount64), the service does the writing and the timer.
/// </summary>
public sealed class KeyboardAutoOff
{
    private bool _locked;
    private long _lastActivityMs;

    /// <summary>How long without a key press (settings' KeyboardAutoOffSeconds). A change counts from the last key.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(10);

    public bool Enabled { get; private set; }

    /// <summary>Off because of auto-off (not because the user chose Off).</summary>
    public bool Dimmed { get; private set; }

    /// <summary>Auto-off switched on or off (setting, power source). Turning it off puts a dimmed light back.</summary>
    public LightStep SetEnabled(bool enabled, long nowMs)
    {
        if (enabled == Enabled)
        {
            return LightStep.None;
        }

        Enabled = enabled;
        _lastActivityMs = nowMs;
        return Wake();
    }

    public LightStep KeyPressed(long nowMs)
    {
        _lastActivityMs = nowMs;
        return Wake();
    }

    /// <summary>Locked: the light comes back and stays. Unlocked: the wait starts again.</summary>
    public LightStep SetLocked(bool locked, long nowMs)
    {
        _locked = locked;
        _lastActivityMs = nowMs;
        return Wake();
    }

    /// <summary>The user, a profile or a restore just set the light: it shows that now, and the wait starts again.</summary>
    public void Shown(long nowMs)
    {
        _lastActivityMs = nowMs;
        Dimmed = false;
    }

    /// <summary>Timer: off once the keys have been idle for <see cref="Delay"/>, if the user's light is on at all.</summary>
    public LightStep Check(long nowMs, bool lit)
    {
        if (!Waiting(lit) || nowMs - _lastActivityMs < (long)Delay.TotalMilliseconds)
        {
            return LightStep.None;
        }

        Dimmed = true;
        return LightStep.Off;
    }

    /// <summary>When the timer should call <see cref="Check"/> next; null when nothing is pending (a key or a change re-arms it).</summary>
    public TimeSpan? NextCheck(long nowMs, bool lit) =>
        Waiting(lit) ? TimeSpan.FromMilliseconds(Math.Max(0, (long)Delay.TotalMilliseconds - (nowMs - _lastActivityMs))) : null;

    private bool Waiting(bool lit) => Enabled && !_locked && !Dimmed && lit;

    private LightStep Wake()
    {
        if (!Dimmed)
        {
            return LightStep.None;
        }

        Dimmed = false;
        return LightStep.On;
    }
}
