using System.Diagnostics;
using System.Globalization;
using System.Text;
using Wattkin.Hardware;
using Wattkin.Monitoring;
using Wattkin.Settings;

namespace Wattkin.Features;

/// <summary>A fan mode forced from the Debug page or by a fan test, over whatever the profile says.</summary>
public enum FanForce
{
    None,
    Auto,
    Silent,
    /// <summary>The controller's own fan table (see <see cref="FanCurve"/>).</summary>
    Curve,
    Full,
}

/// <summary>
/// The Debug page's tests: forced fan modes, the fans' top speed, Auto vs Silent, running from the
/// battery while plugged in and a light test. Each one ends by itself and puts the profile's
/// settings back, also when Wattkin exits; a crash is undone at the next start, when the profile is
/// applied again.
/// </summary>
public sealed partial class HardwareService
{
    private static readonly TimeSpan ForceDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CompareStep = TimeSpan.FromSeconds(45);

    private volatile FanForce _fanForce;
    private FansBeforeForce? _fansBeforeForce;
    private long _fanForceUntilTicks;
    private int _fanTestBusy;
    private long _runFromBatteryUntilTicks;

    /// <summary>How the fans were when a force began; a custom curve's speeds and table where readable.</summary>
    private sealed record FansBeforeForce(FanMode? Mode, bool? CoolerBoost, FanSpeeds? Speeds, FanTable? Table);

    public FanForce ForcedFans => _fanForce;
    public bool FanTestRunning => Volatile.Read(ref _fanTestBusy) == 1;

    /// <summary>What the running (or last) fan test is doing, for the Debug page.</summary>
    public string FanTestStatus { get; private set; } = "";

    public event Action? FanTestChanged;

    /// <summary>Debug: "run from battery while plugged in" until this time (ReconcileCharge applies it).</summary>
    public DateTime? RunningFromBatteryUntilUtc => RunningFromBattery ? new DateTime(Volatile.Read(ref _runFromBatteryUntilTicks), DateTimeKind.Utc) : null;

    private bool RunningFromBattery => DateTime.UtcNow.Ticks < Volatile.Read(ref _runFromBatteryUntilTicks);

    // ---- Fans ---------------------------------------------------------------------------------

    /// <summary>
    /// Debug: one fan mode for a few minutes. <see cref="FanForce.None"/> goes back to the profile, and
    /// also stops a running fan test.
    /// </summary>
    public Task ForceFansAsync(FanForce force) => Task.Run(() =>
    {
        if (!FanTestRunning || force == FanForce.None)
        {
            SetFanForce(force, ForceDuration);
        }
    });

    private void SetFanForce(FanForce force, TimeSpan duration)
    {
        lock (_gate)
        {
            // The fans before the first force, for a profile that leaves them alone ("Unchanged"): ending
            // the force puts them back as they were (a custom table too: the Curve force overwrites it)
            // rather than leaving the forced setting.
            if (_fanForce == FanForce.None && force != FanForce.None && Writer is { } before)
            {
                var mode = before.GetFanMode();
                var custom = mode == FanMode.Custom;
                _fansBeforeForce = new FansBeforeForce(
                    mode,
                    before.GetCoolerBoost(),
                    custom && before.HasFanCurves && before.ReadFanCurves() is { } curves ? FanSpeeds.From(curves) : null,
                    custom && before.HasFullFanTable ? before.ReadFullFanTable() : null);
                var boost = _fansBeforeForce.CoolerBoost ?? false;
                _store.Update(s => s.CoolerBoostBeforeForce = boost); // in case Wattkin is killed before the force ends
            }

            _fanForce = force;
            Volatile.Write(ref _fanForceUntilTicks, (DateTime.UtcNow + duration).Ticks);
            if (Writer is { } ec && !_disposed)
            {
                ApplyFans(ec, _store.Current.FindProfile(_store.Current.ActiveProfile));
            }
        }

        Log.Info(force == FanForce.None ? "Fans: back to the profile's setting." : $"Fans forced to {force} for {duration.TotalMinutes:0.#} min.");
        SafeRefreshState();
    }

    /// <summary>The fan part of a profile, or the forced mode while one is on. Called under the lock.</summary>
    private void ApplyFans(EmbeddedController ec, Profile? profile)
    {
        switch (_fanForce)
        {
            case FanForce.Full:
                ec.SetCoolerBoost(true);
                break;
            case FanForce.Auto or FanForce.Silent:
                ec.SetCoolerBoost(false);
                ec.SetFanMode(_fanForce == FanForce.Silent ? FanMode.Silent : FanMode.Auto);
                break;
            case FanForce.Curve:
                ec.SetCoolerBoost(false);
                // The laptop's own table is always allowed, even where the safety rules for edited tables are stricter.
                ec.SetFanMode(FanMode.Custom, _store.Current.FactoryFanCurve, unrestricted: ec.HasFullFanTable && _store.Current.FactoryFanTable?.LooksLikeFactory() == true,
                    table: ec.HasFullFanTable ? _store.Current.FactoryFanTable : null);
                break;
            default:
                if (profile is null)
                {
                    return;
                }

                // The profile's fan profile, else the laptop's factory curve (a whole table, temperatures
                // included, where the laptop takes one). No usable fan table (or a curve the safety rules
                // refuse): the quietest built-in curve instead.
                var (curve, unrestricted) = _store.Current.ResolveFanCurve(profile);
                var table = ec.HasFullFanTable ? _store.Current.ResolveFanTable(profile) : null;

                // The laptop's own whole table (as it came) is always allowed: the safety rules for
                // edited tables are stricter than some factory tables.
                if (table is not null && _store.Current.FactoryFanTable is { } factory && factory.LooksLikeFactory() && table.SameAs(factory))
                {
                    unrestricted = true;
                }

                var leftAlone = profile.Fan is null && !profile.CoolerBoost;
                var afterForce = _fansBeforeForce;
                _fansBeforeForce = null;
                if (_store.Current.CoolerBoostBeforeForce is not null)
                {
                    _store.Update(s => s.CoolerBoostBeforeForce = null); // the force is over
                }
                if (profile.Fan is { } fan && !ec.SetFanMode(fan, curve, unrestricted, table) && fan == FanMode.Custom)
                {
                    ec.SetFanMode(FanMode.Silent);
                }
                else if (profile.Fan is null && afterForce is { Mode: { } before })
                {
                    // Leaving the fans alone after a force: back as they were. A custom table is written
                    // back as it was read (it was in the controller already, so any rules it broke are
                    // the laptop's or the user's own).
                    ec.SetFanMode(before, afterForce.Speeds, unrestricted: true, afterForce.Table);
                }

                // Fans left alone: Cooler Boost too (the Fn key may have turned it on), except that a force
                // ending puts it back as it was before (off if that's unknown, never left on by the force).
                if (!leftAlone)
                {
                    ec.SetCoolerBoost(profile.CoolerBoost);
                }
                else if (afterForce is not null)
                {
                    ec.SetCoolerBoost(afterForce.CoolerBoost ?? false);
                }

                break;
        }
    }

    /// <summary>
    /// First time the laptop's fan table is readable: keep its factory speeds, so "Factory" can always
    /// be restored (custom curves overwrite the controller's copy; uninstalling writes these back), and
    /// fit the shipped fan profiles to it.
    /// </summary>
    private void CaptureFactoryFanCurve(EmbeddedController ec)
    {
        // The whole table too, where the laptop takes one: the temperatures are the user's to change there.
        if (_store.Current.FactoryFanTable is null && ec.HasFullFanTable && ec.ReadFullFanTable() is { } held && held.Problem(unrestricted: true) is null)
        {
            // Fan profiles (1.1.1's, or this version's after a failed first read) wrote their speeds into
            // these same registers, leaving the temperatures alone. With the laptop's own speeds already
            // known from its first start, those go back in, so a profile's curve (even "Off") is never
            // kept as the laptop's own.
            var table = _store.Current.FactoryFanCurve is { } own ? held.WithSpeeds(own) : held;

            // The factory table is written with the safety rules off: one that doesn't look like a
            // laptop's own (another fan tool's quiet table) isn't kept. Tried again at the next start.
            if (table.LooksLikeFactory())
            {
                _store.Update(s => s.FactoryFanTable = table);
                Log.Info($"Factory fan table saved: CPU {table.Cpu.Describe()}; GPU {table.Gpu.Describe()}.");
            }
            else
            {
                Log.Warn($"The fan table in the controller doesn't look like the laptop's own, so it wasn't saved as the factory table: CPU {table.Cpu.Describe()}; GPU {table.Gpu.Describe()}.");
            }
        }

        if (_store.Current.FactoryFanCurve is not null || !ec.HasFanCurves || ec.ReadFanCurves() is not { Sane: true } curves)
        {
            return;
        }

        var factory = FanSpeeds.From(curves);
        List<string> rebuilt = [];
        _store.Update(s =>
        {
            s.FactoryFanCurve = factory;
            rebuilt = s.FitDefaultFanProfiles(curves);
        });
        Log.Info($"Factory fan curve saved (fan mode now {ec.GetFanMode()}): CPU {string.Join("/", factory.Cpu)}, GPU {(curves.HasGpuFan ? string.Join("/", factory.Gpu) : "none (one fan)")}, top {curves.Cpu.Speeds[^1]}/{curves.Gpu.Speeds[^1]}%.");
        if (rebuilt.Count > 0)
        {
            Log.Info($"Fan profiles fitted to this laptop's table: {string.Join(", ", rebuilt)}.");
        }
    }

    /// <summary>The laptop's fan tables now, for the profile editor; null without hardware access.</summary>
    public FanCurves? ReadFanCurves() => _ec?.ReadFanCurves();

    /// <summary>The laptop's whole fan table now, temperatures included; null unless the laptop takes whole tables.</summary>
    public FanTable? ReadFullFanTable() => _ec is { HasFullFanTable: true } ec ? ec.ReadFullFanTable() : null;

    /// <summary>Monitor thread: a forced mode ends by itself.</summary>
    private void ExpireFanForce()
    {
        if (_fanForce != FanForce.None && DateTime.UtcNow.Ticks > Volatile.Read(ref _fanForceUntilTicks) && !FanTestRunning)
        {
            SetFanForce(FanForce.None, TimeSpan.Zero);
            Osd?.Invoke("Fans back to normal");
        }
    }

    /// <summary>On exit or sleep: no forced mode outlives Wattkin. Doesn't wait for the lock, like the rest of <see cref="ReleaseHardware"/>.</summary>
    private void ReleaseFanForce(EmbeddedController ec)
    {
        if (_fanForce == FanForce.None)
        {
            return;
        }

        _fanForce = FanForce.None;
        ApplyFans(ec, _store.Current.FindProfile(_store.Current.ActiveProfile));
    }

    /// <summary>
    /// Cooler Boost until both fans stop speeding up: their top speeds, which the flyout's fan bar is
    /// drawn against. Takes 10–40 s.
    /// </summary>
    public Task<string> MeasureFanTopSpeedAsync() => Task.Run(() => RunFanTest(MeasureTopSpeed));

    /// <summary>Auto, Silent and (where the firmware has one) the fan curve, 45 s each: the speed each settles at, at today's temperature.</summary>
    public Task<string> CompareFanModesAsync() => Task.Run(() => RunFanTest(CompareModes));

    private string RunFanTest(Func<EmbeddedController, string> test)
    {
        if (Writer is not { HasCoolerBoost: true } ec)
        {
            return _controls ? "Fan control isn't available on this firmware." : NotOwner;
        }

        if (Interlocked.Exchange(ref _fanTestBusy, 1) == 1)
        {
            return "A fan test is already running.";
        }

        string result;
        try
        {
            result = test(ec);
        }
        catch (Exception ex)
        {
            Log.Error("Fan test failed", ex);
            result = "The fan test failed. The log has the details.";
        }
        finally
        {
            Interlocked.Exchange(ref _fanTestBusy, 0);
            SetFanForce(FanForce.None, TimeSpan.Zero);
        }

        ReportFanTest(result);
        return result;
    }

    private string MeasureTopSpeed(EmbeddedController ec)
    {
        _store.Update(s => s.FanTopSpeedAttempts++);
        SetFanForce(FanForce.Full, TimeSpan.FromMinutes(1));
        var readings = new List<FanReading>();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(40) && StillForced(FanForce.Full))
        {
            Thread.Sleep(1000);
            if (ec.ReadSensors() is { } sensors)
            {
                readings.Add(new FanReading(FanTest.Plausible(sensors.Fan1Rpm), FanTest.Plausible(sensors.Fan2Rpm)));
                ReportFanTest($"Fans at full speed: {Rpm(readings[^1].Fan1)} / {Rpm(readings[^1].Fan2)} rpm…");
            }

            if (clock.Elapsed >= TimeSpan.FromSeconds(8) && FanTest.Settled(readings))
            {
                break;
            }
        }

        if (!StillForced(FanForce.Full))
        {
            return "Stopped before the fans reached full speed.";
        }

        var top = FanTest.Top(readings);
        if (top.Fan1 + top.Fan2 < 1000)
        {
            return "The fans didn't report a speed.";
        }

        _store.Update(s =>
        {
            s.Fan1MaxRpm = top.Fan1 > 0 ? top.Fan1 : null;
            s.Fan2MaxRpm = top.Fan2 > 0 ? top.Fan2 : null;
            s.FanTopSpeedMeasuredUtc = DateTime.UtcNow;
        });
        Log.Info($"Fan top speed measured: {top.Fan1} / {top.Fan2} rpm after {clock.Elapsed.TotalSeconds:0} s.");
        return $"Top speed: {Rpm(top.Fan1)} / {Rpm(top.Fan2)} rpm.";
    }

    private string CompareModes(EmbeddedController ec)
    {
        var results = new List<string>();
        var stopped = new List<bool>();
        FanForce[] modes = ec.HasFanCurves && ec.ReadFanCurves() is { Sane: true } ? [FanForce.Auto, FanForce.Silent, FanForce.Curve] : [FanForce.Auto, FanForce.Silent];
        foreach (var force in modes)
        {
            var name = force.ToString();
            SetFanForce(force, TimeSpan.FromMinutes(2));
            var readings = new List<(FanReading Fans, int? CpuC)>();
            for (var second = 1; second <= CompareStep.TotalSeconds && StillForced(force); second++)
            {
                Thread.Sleep(1000);
                if (ec.ReadSensors() is { } sensors)
                {
                    readings.Add((new FanReading(FanTest.Plausible(sensors.Fan1Rpm), FanTest.Plausible(sensors.Fan2Rpm)), sensors.CpuTempC));
                    ReportFanTest($"{name}: {second} of {CompareStep.TotalSeconds:0} s · {Rpm(readings[^1].Fans.Fan1)} / {Rpm(readings[^1].Fans.Fan2)} rpm");
                }
            }

            if (!StillForced(force))
            {
                return "Stopped.";
            }

            // The last 15 s: by then the fans have adjusted to the mode.
            var settled = readings.TakeLast(15).ToList();
            var fans = FanTest.Average(settled.Select(r => r.Fans).ToList());
            var temps = settled.Where(r => r.CpuC is not null).Select(r => r.CpuC!.Value).ToList();
            results.Add($"{name}: {Rpm(fans.Fan1)} / {Rpm(fans.Fan2)} rpm" + (temps.Count > 0 ? $" at CPU {temps.Average():0}°C" : ""));
            stopped.Add(fans.Fan1 + fans.Fan2 == 0);
        }

        Log.Info("Fan comparison: " + string.Join("; ", results));
        if (stopped.All(s => s))
        {
            results.Add("The fans were off in every mode: the laptop is cool enough for all of them. The modes differ once it's warmer, so try again while charging or after some work.");
        }

        return string.Join("\n", results);
    }

    private bool StillForced(FanForce force) => _fanForce == force && !_disposed && !_suspended;

    private void ReportFanTest(string status)
    {
        FanTestStatus = status;
        FanTestChanged?.Invoke();
    }

    private static string Rpm(int rpm) => rpm.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Once per install (three tries at most): measure the top speed, so the fan bar has a real scale.</summary>
    private void ScheduleFirstFanMeasurement(EmbeddedController ec)
    {
        var settings = _store.Current;
        if (!ec.HasCoolerBoost || settings.FanTopSpeedMeasuredUtc is not null || settings.FanTopSpeedAttempts >= 3)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (_disposed || _suspended || IsCalibrating || _fanForce != FanForce.None || FanTestRunning)
            {
                return;
            }

            Osd?.Invoke("Measuring fan speed: full fans for ~20 s");
            Osd?.Invoke(await MeasureFanTopSpeedAsync().ConfigureAwait(false));
        });
    }

    // ---- Battery, lights, registers --------------------------------------------------------------

    /// <summary>Debug: run from the battery with the charger connected (zero stops it). Returns an error, or null.</summary>
    public string? RunFromBattery(TimeSpan duration)
    {
        if (!_controls)
        {
            return NotOwner;
        }

        if (_ec is not { CanSimulateUnplug: true })
        {
            return "This needs a register that is only verified on supported firmware.";
        }

        if (IsCalibrating)
        {
            return "Not while a calibration is running.";
        }

        Volatile.Write(ref _runFromBatteryUntilTicks, duration > TimeSpan.Zero ? (DateTime.UtcNow + duration).Ticks : 0);
        Log.Info(duration > TimeSpan.Zero ? $"Debug: running from the battery for {duration.TotalMinutes:0} min." : "Debug: back on the charger.");
        _activeUntilUtc = DateTime.UtcNow + ActiveWindow;
        _reconcileNow = true;
        _monitor.Poke();
        return null;
    }

    /// <summary>Debug: flashes the mute and mic-mute key lights and the keyboard backlight three times, then puts them back.</summary>
    public Task TestLightsAsync() => Task.Run(() =>
    {
        if (Writer is not { } ec)
        {
            return;
        }

        // The USB light can't be read back: only flashed when Wattkin knows the level to put back.
        var usb = HasUsbKeyboardLight;
        var level = usb ? _store.Current.KeyboardLevel : ec.HasKeyboardLight ? ec.GetKeyboardLevel() : null;
        var color = KeyboardColorOf(_store.Current);
        void Keyboard(int value)
        {
            if (usb)
            {
                ShowKeyboardLight(value, color);
            }
            else
            {
                ec.SetKeyboardLevel(value);
            }
        }

        for (var i = 0; i < 3 && !_disposed; i++)
        {
            ec.SetMuteLed(true);
            ec.SetMicMuteLed(true);
            if (level is not null)
            {
                Keyboard(usb ? MysticLight.MaxLevel : ec.KeyboardMaxLevel);
            }

            Thread.Sleep(400);
            ec.SetMuteLed(false);
            ec.SetMicMuteLed(false);
            if (level is not null)
            {
                Keyboard(0);
            }

            Thread.Sleep(400);
        }

        if (level is { } original)
        {
            Keyboard(original);
        }

        _lastMicLed = _lastSpeakerLed = null;
        SyncMuteLeds();
        SafeRefreshState();
    });

    /// <summary>Debug page: the registers Wattkin uses, raw and decoded, and the firmware's fan tables. Reads only.</summary>
    public string ReadRegisters()
    {
        if (_ec is not { } ec)
        {
            return State.Problem ?? "Not connected to the embedded controller.";
        }

        var sb = new StringBuilder();
        void Row(byte address, string meaning)
        {
            var value = ec.ReadRegister(address);
            sb.AppendLine($"0x{address:X2}  {(value is { } v ? $"0x{v:X2}  {Convert.ToString(v, 2).PadLeft(8, '0')}" : "read failed"),-16}  {meaning}");
        }

        Row(0xD2, $"performance mode: {ec.GetShiftMode()?.ToString() ?? "unknown value"}");
        Row(0xD4, $"fan mode: {ec.GetFanMode()?.ToString() ?? "?"}");
        Row(0x71, $"CPU fan target: {ec.ReadRegister(0x71)?.ToString(CultureInfo.InvariantCulture) ?? "?"}%");
        Row(0x89, $"GPU fan target: {ec.ReadRegister(0x89)?.ToString(CultureInfo.InvariantCulture) ?? "?"}%");
        Row(0x98, $"Cooler Boost: {OnOff(ec.GetCoolerBoost())}");
        Row(0xD7, $"charge limit: {ec.GetChargeLimit()?.ToString(CultureInfo.InvariantCulture) ?? "?"}%");
        Row(0xBF, $"run from battery while plugged in: {OnOff(ec.GetSimulatedUnplug())}");
        Row(0xD3, $"keyboard backlight: level {ec.GetKeyboardLevel()?.ToString(CultureInfo.InvariantCulture) ?? "?"}{(HasUsbKeyboardLight ? " (not used: the keys are lit over USB)" : "")}");
        Row(0x2C, $"keyboard auto-off: {OnOff(ec.GetKeyboardAutoOff())} (also the mic-mute light)");
        Row(0x2D, "mute light");
        Row(0xD9, $"Fn lock: {OnOff(ec.GetFnLock())}");
        if (KeyboardLightLine() is { } light)
        {
            sb.AppendLine(light);
        }

        if (ec.HasFullFanTable && ec.ReadFullFanTable() is { } table)
        {
            sb.AppendLine();
            sb.AppendLine($"Fan table (used only in curve mode){(table.Problem(unrestricted: true) is { } problem ? $": {problem}" : "")}");
            sb.AppendLine($"  CPU fan  {table.Cpu.Describe()}");
            sb.AppendLine($"  GPU fan  {table.Gpu.Describe()}");
        }
        else if (ec.ReadFanCurves() is { } curves)
        {
            sb.AppendLine();
            sb.AppendLine($"Fan curve (used only in curve mode){(curves.Sane ? "" : ": doesn't look safe, so Wattkin won't use it")}");
            sb.AppendLine($"  CPU fan  {curves.Cpu.Describe()}");
            sb.AppendLine($"  GPU fan  {(curves.HasGpuFan ? curves.Gpu.Describe() : "none (this laptop has one fan)")}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string OnOff(bool? value) => value switch { true => "on", false => "off", _ => "?" };
}

/// <summary>Two fans' speeds at one moment, in rpm (0 = stopped or not reported).</summary>
public readonly record struct FanReading(int Fan1, int Fan2);

/// <summary>The arithmetic of the fan tests, kept apart so it can be tested without hardware.</summary>
public static class FanTest
{
    /// <summary>Tach glitches read as absurd speeds: those count as no reading.</summary>
    public static int Plausible(int? rpm) => rpm is > 0 and < 12000 ? rpm.Value : 0;

    /// <summary>The last four readings of each fan within 2% (or 60 rpm) of each other: done speeding up.</summary>
    public static bool Settled(IReadOnlyList<FanReading> readings)
    {
        if (readings.Count < 4)
        {
            return false;
        }

        var last = readings.Skip(readings.Count - 4).ToList();
        static bool Flat(IReadOnlyList<int> values) => values.Max() - values.Min() <= Math.Max(60, values.Max() * 0.02);
        return Flat([.. last.Select(r => r.Fan1)]) && Flat([.. last.Select(r => r.Fan2)]);
    }

    public static FanReading Top(IReadOnlyList<FanReading> readings) =>
        readings.Count == 0 ? default : new(readings.Max(r => r.Fan1), readings.Max(r => r.Fan2));

    public static FanReading Average(IReadOnlyList<FanReading> readings) =>
        readings.Count == 0 ? default : new((int)Math.Round(readings.Average(r => r.Fan1)), (int)Math.Round(readings.Average(r => r.Fan2)));
}
