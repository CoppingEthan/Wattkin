namespace Wattkin.Monitoring;

/// <summary>What the power numbers mean right now.</summary>
public enum PowerSource
{
    /// <summary>Running on battery: battery discharge rate is the whole laptop's draw.</summary>
    BatteryMeasured,
    /// <summary>On the charger: CPU package power + learned rest-of-system draw.</summary>
    AcEstimated,
    /// <summary>On the charger and not enough data yet to estimate the rest of the system.</summary>
    AcCpuOnly,
    Unknown,
}

/// <param name="SystemWatts">This sample's reading; null when there was none.</param>
/// <param name="WattsNow">What to display: the latest reading, held for a few seconds across a missed one.</param>
/// <param name="AverageWatts">The draw behind the time estimates (<see cref="PowerModel"/> explains the averaging).</param>
/// <param name="ChargerWatts">
/// On the charger, with the rest of the system learned: what the charger is giving the laptop (its
/// estimated draw, a working dGPU's own reading and what goes into the battery). Into the laptop, not
/// from the wall. Null otherwise.
/// </param>
public sealed record PowerState(
    PowerSource Source,
    double? SystemWatts,
    double? WattsNow,
    double? AverageWatts,
    double? CpuPackageWatts,
    double? BatteryRateWatts,
    bool OnAc,
    bool Charging,
    double? Percent,
    double? RemainingWh,
    double? FullWh,
    TimeSpan? TimeToEmpty,
    TimeSpan? TimeToTarget,
    TimeSpan? UnpluggedRuntimeEstimate,
    BatterySession? Session,
    double? ChargerWatts = null);

/// <summary>Since the charger was last unplugged.</summary>
public sealed record BatterySession(DateTime StartedUtc, TimeSpan Duration, double UsedWh, double? AverageWatts, TimeSpan? ProjectedRemaining);

/// <summary>
/// Turns raw battery and RAPL readings into what the user cares about: how many watts the laptop is
/// using and how long the battery will last. Pure logic — no I/O — so it is unit tested.
/// </summary>
public sealed class PowerModel
{
    private const double OffsetTauSeconds = 300;
    private const double PackageTauSeconds = 10;
    private const double MaxPlausibleBatteryWatts = 200;
    private double? _packageEma;
    private static readonly TimeSpan MinimumSessionForAverage = TimeSpan.FromMinutes(3);

    // Time estimates average the draw over the last 10 minutes in 30-second slices, so a burst of work or
    // a quiet moment barely moves them. If the last 2 minutes differ a lot from that, the workload has
    // really changed, and the estimate follows them instead.
    private static readonly TimeSpan Slice = TimeSpan.FromSeconds(30);
    private const int LongSlices = 20;
    private const int RecentSlices = 4;
    private const double SustainedChange = 1.4;
    private static readonly TimeSpan MinimumHistory = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HoldReading = TimeSpan.FromSeconds(15);
    private readonly Queue<(double Wh, double Hours)> _slices = new();
    private double _sliceWh;
    private double _sliceHours;
    private double? _wattsNow;
    private DateTime _wattsNowUtc;

    private DateTime? _lastSampleUtc;
    private bool? _wasOnAc;
    private DateTime? _sessionStartUtc;
    private double? _sessionStartWh;

    /// <summary>Learned laptop draw excluding the CPU package (display, SSD, RAM, Wi-Fi, ...).</summary>
    public double? RestOfSystemWatts { get; private set; }

    /// <summary>Number of on-battery samples behind <see cref="RestOfSystemWatts"/>.</summary>
    public int RestOfSystemSamples { get; private set; }

    public PowerModel(double? learnedRestOfSystemWatts = null, int learnedSamples = 0)
    {
        RestOfSystemWatts = learnedRestOfSystemWatts;
        RestOfSystemSamples = learnedSamples;
    }

    /// <param name="chargeTargetPercent">Where charging will stop (the charge limit).</param>
    /// <param name="dgpuActive">A powered dGPU draws power RAPL does not see; don't learn from those samples.</param>
    /// <param name="dgpuWatts">
    /// The dGPU's own reading while it's working and a window is open, else null. Only the charger number
    /// adds it: in the system estimate it would come and go with the windows.
    /// </param>
    public PowerState Update(DateTime nowUtc, BatteryReading? battery, double? fullMWh, double? cpuPackageWatts, int chargeTargetPercent, bool dgpuActive, double? dgpuWatts = null)
    {
        var dt = _lastSampleUtc is { } last ? Math.Clamp((nowUtc - last).TotalSeconds, 0.05, 60) : 1.0;
        _lastSampleUtc = nowUtc;
        if (cpuPackageWatts is { } p0 && !double.IsFinite(p0))
        {
            cpuPackageWatts = null;
        }

        if (cpuPackageWatts is { } pkgNow)
        {
            _packageEma = _packageEma is { } e ? Ema(e, pkgNow, dt, PackageTauSeconds) : pkgNow;
        }

        if (battery is not { } b)
        {
            // A failed read is not a power-source change: keep the last known charger state.
            return new PowerState(PowerSource.Unknown, null, HeldWatts(nowUtc), Average(), cpuPackageWatts, null, _wasOnAc ?? true, false, null, null, null, null, null, null, null);
        }

        var onAc = b.State.HasFlag(BatteryPowerState.OnLine);
        var charging = b.State.HasFlag(BatteryPowerState.Charging);
        var discharging = b.State.HasFlag(BatteryPowerState.Discharging);
        var rateW = b.RateMW / 1000.0;

        // The gauge reports nonsense for a moment when the power source changes (seen on this laptop:
        // ~1,060 W right after unplugging). No laptop battery moves more than ~200 W: treat as unknown.
        if (rateW is { } reported && Math.Abs(reported) > MaxPlausibleBatteryWatts)
        {
            rateW = null;
        }

        var remainingWh = b.CapacityMWh / 1000.0;
        var fullWh = fullMWh / 1000.0;
        var percent = remainingWh is { } r && fullWh is > 0 ? Math.Clamp(r / fullWh.Value * 100.0, 0, 100) : (double?)null;

        if (_wasOnAc != onAc)
        {
            // Plug/unplug: readings and averages from the other power source are meaningless.
            _wattsNow = null;
            _slices.Clear();
            _sliceWh = _sliceHours = 0;
            if (!onAc)
            {
                _sessionStartUtc = nowUtc;
                _sessionStartWh = remainingWh;
            }

            _wasOnAc = onAc;
        }

        if (!onAc && _sessionStartUtc is not null && _sessionStartWh is null)
        {
            _sessionStartWh = remainingWh; // capacity wasn't known at the moment of unplugging
        }

        PowerSource source;
        double? systemWatts;
        if (!onAc && discharging && rateW is < 0)
        {
            source = PowerSource.BatteryMeasured;
            systemWatts = -rateW.Value;

            // The battery gauge averages over several seconds; RAPL is near-instant. Learn only while
            // the package is steady (compared with its own smoothed value) so bursts don't bias it,
            // and keep low readings (clamped at 0) instead of dropping them.
            if (cpuPackageWatts is { } pkg && _packageEma is { } avg && !dgpuActive && Math.Abs(pkg - avg) < 1.5)
            {
                var rest = Math.Clamp(systemWatts.Value - avg, 0, 30);
                RestOfSystemWatts = RestOfSystemWatts is { } prior ? Ema(prior, rest, dt, OffsetTauSeconds) : rest;
                RestOfSystemSamples++;
            }
        }
        else if (!onAc)
        {
            // On battery but the gauge hasn't reported a rate yet (right after unplugging).
            source = PowerSource.Unknown;
            systemWatts = null;
        }
        else if (cpuPackageWatts is { } pkg)
        {
            var learned = RestOfSystemSamples >= 60 ? RestOfSystemWatts : null;
            source = learned is null ? PowerSource.AcCpuOnly : PowerSource.AcEstimated;
            systemWatts = pkg + (learned ?? 0);
        }
        else
        {
            source = PowerSource.Unknown;
            systemWatts = null;
        }

        // The charger gives the laptop's draw plus what goes into the battery. At the limit that's just the
        // draw; running from the battery while plugged in, the battery covers it. CPU-only would read far too low.
        // The rest is learned with the dGPU asleep, so a working dGPU's own reading comes on top.
        double? chargerWatts = null;
        if (source == PowerSource.AcEstimated && systemWatts is { } estimate)
        {
            var laptop = estimate + (dgpuWatts is { } g && double.IsFinite(g) && g > 0 ? g : 0);
            chargerWatts = !charging && !discharging ? laptop : rateW is { } batteryW ? Math.Max(0, laptop + batteryW) : null;
        }

        if (systemWatts is { } w && double.IsFinite(w))
        {
            _wattsNow = w;
            _wattsNowUtc = nowUtc;
            AddEnergy(w, dt);
        }

        var averageWatts = Average();
        TimeSpan? timeToEmpty = null;
        TimeSpan? timeToTarget = null;
        TimeSpan? unpluggedEstimate = null;
        if (source == PowerSource.BatteryMeasured && remainingWh is { } rem && averageWatts is > 0.3)
        {
            timeToEmpty = Hours(rem / averageWatts.Value);
        }
        else if (onAc && remainingWh is { } rem2)
        {
            if (charging && rateW is > 0.3 && fullWh is { } full)
            {
                var targetWh = full * Math.Clamp(chargeTargetPercent, 0, 100) / 100.0;
                if (targetWh > rem2)
                {
                    timeToTarget = Hours((targetWh - rem2) / rateW.Value);
                }
            }

            if (source == PowerSource.AcEstimated && averageWatts is > 0.3)
            {
                unpluggedEstimate = Hours(rem2 / averageWatts.Value);
            }
        }

        BatterySession? session = null;
        if (!onAc && _sessionStartUtc is { } start && _sessionStartWh is { } startWh && remainingWh is { } nowWh)
        {
            var duration = nowUtc - start;
            var used = Math.Max(0, startWh - nowWh);
            double? average = duration >= MinimumSessionForAverage && used > 0.2 ? used / duration.TotalHours : null;
            session = new BatterySession(start, duration, used, average, average is > 0.3 ? Hours(nowWh / average.Value) : null);
        }

        return new PowerState(source, systemWatts, HeldWatts(nowUtc), averageWatts, cpuPackageWatts, rateW, onAc, charging, percent, remainingWh, fullWh, timeToEmpty, timeToTarget, unpluggedEstimate, session, chargerWatts);
    }

    /// <summary>The latest reading, kept for a few seconds so one missed sample doesn't blank the display.</summary>
    private double? HeldWatts(DateTime nowUtc) => _wattsNow is { } w && nowUtc - _wattsNowUtc <= HoldReading ? w : null;

    private void AddEnergy(double watts, double seconds)
    {
        _sliceWh += watts * seconds / 3600;
        _sliceHours += seconds / 3600;
        if (_sliceHours * 3600 >= Slice.TotalSeconds)
        {
            _slices.Enqueue((_sliceWh, _sliceHours));
            _sliceWh = _sliceHours = 0;
            while (_slices.Count > LongSlices)
            {
                _slices.Dequeue();
            }
        }
    }

    /// <summary>Energy over time for the last 10 minutes, or the last 2 when the workload has clearly changed.</summary>
    private double? Average()
    {
        var all = _slices.Append((Wh: _sliceWh, Hours: _sliceHours)).ToArray();
        var hours = all.Sum(s => s.Hours);
        if (hours * 3600 < MinimumHistory.TotalSeconds)
        {
            return _wattsNow; // too little history yet: the reading itself
        }

        var longAverage = all.Sum(s => s.Wh) / hours;

        // Sustained only if every one of the last four slices moved the same way: a burst can't trigger it.
        var complete = _slices.ToArray();
        if (complete.Length > RecentSlices)
        {
            var recent = complete[^RecentSlices..];
            var levels = recent.Select(s => s.Wh / s.Hours).ToArray();
            if (levels.Min() > longAverage * SustainedChange || levels.Max() * SustainedChange < longAverage)
            {
                return (recent.Sum(s => s.Wh) + _sliceWh) / (recent.Sum(s => s.Hours) + _sliceHours);
            }
        }

        return longAverage;
    }

    private static double Ema(double previous, double sample, double dtSeconds, double tauSeconds)
    {
        var alpha = 1 - Math.Exp(-dtSeconds / tauSeconds);
        return previous + (alpha * (sample - previous));
    }

    private static TimeSpan? Hours(double hours) =>
        double.IsFinite(hours) && hours is > 0 and < 100 ? TimeSpan.FromHours(hours) : null;
}
