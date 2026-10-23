using Wattkin.Hardware;
using Wattkin.Native;

namespace Wattkin.Monitoring;

public interface IEcSensorSource
{
    EcSensors? ReadSensors();
}

/// <summary>
/// Samples on one background thread and publishes immutable snapshots.
///
/// Two tiers, to keep an always-on tray app cheap:
///  - tray only (no window open): every 5 s, just the battery and the CPU energy meter, which is all
///    the tray number needs;
///  - a window is open: every 1–2 s, plus per-core clocks, RAM, disks, thermal zone, EC sensors and
///    (for the flyout) per-process CPU.
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    private static readonly TimeSpan TrayInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BatteryInfoInterval = TimeSpan.FromMinutes(2);

    private readonly CpuTopology _topology = new();
    private readonly BatteryDevice _battery = new();
    private readonly ProcessCpuTracker _processes = new();
    private readonly Dictionary<object, TimeSpan> _fastDemand = [];
    private readonly HashSet<object> _processDemand = [];
    private readonly object _demandLock = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();

    private PdhQuery? _trayQuery;
    private PdhCounter? _cpuUtility;
    private PdhCounter? _energy;

    private PdhQuery? _detailQuery;
    private PdhCounter? _gpuEngine;
    private IReadOnlyList<GpuAdapter> _adapters = [];
    private DateTime _adaptersReadUtc = DateTime.MinValue;
    private static readonly System.Text.RegularExpressions.Regex GpuEngineInstance =
        new(@"luid_0x(?<hi>[0-9a-f]{8})_0x(?<lo>[0-9a-f]{8})_phys_\d+_eng_(?<eng>\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private PdhCounter? _cpuPerformance;
    private PdhCounter? _cpuBaseFrequency;
    private PdhCounter? _diskRead;
    private PdhCounter? _diskWrite;
    private PdhCounter? _diskIdle;
    private PdhCounter? _thermal;
    private bool _detailPrimed;

    private readonly NvidiaPower _nvidia = new();
    private DgpuStatus _dgpu = new(DgpuState.Unknown, null, []);
    private DateTime _dgpuReadUtc = DateTime.MinValue;
    private DateTime _batteryInfoReadUtc = DateTime.MinValue;
    private DateTime _lastSampleUtc = DateTime.MinValue;
    private bool _batteryTempSupported = true;
    private long _lastCollectTicks;
    private bool _wasFast;
    private Task? _loop;

    public SystemMonitor(double? learnedRestOfSystemWatts, int learnedSamples)
    {
        Power = new PowerModel(learnedRestOfSystemWatts, learnedSamples);
    }

    public event Action<Snapshot>? Updated;

    /// <summary>Raised (on the monitor thread) when a window starts or stops wanting live data.</summary>
    public event Action<bool>? FastChanged;

    public Snapshot? Latest { get; private set; }
    public StaticSystemInfo? Info { get; private set; }
    public BatteryInfo? BatteryInfo { get; private set; }
    public PowerModel Power { get; }

    /// <summary>True while any window wants live data.</summary>
    public bool IsFast
    {
        get
        {
            lock (_demandLock)
            {
                return _fastDemand.Count > 0;
            }
        }
    }

    /// <summary>Charge limit in percent; used for "time until charged".</summary>
    public Func<int> ChargeTarget { get; set; } = () => 100;

    public IEcSensorSource? Sensors { get; set; }

    public void Start()
    {
        _loop = Task.Factory.StartNew(Run, TaskCreationOptions.LongRunning).Unwrap();
    }

    /// <summary>
    /// Ask for live updates every <paramref name="interval"/> (default 1 s), and optionally per-process CPU,
    /// until released with <paramref name="fast"/> = false.
    /// </summary>
    public void Demand(object owner, bool fast, bool processes = false, TimeSpan? interval = null)
    {
        lock (_demandLock)
        {
            if (fast)
            {
                _fastDemand[owner] = interval ?? TimeSpan.FromSeconds(1);
            }
            else
            {
                _fastDemand.Remove(owner);
            }

            if (processes && fast)
            {
                _processDemand.Add(owner);
            }
            else
            {
                _processDemand.Remove(owner);
            }
        }

        Poke();
    }

    /// <summary>Sample now (e.g. charger plugged in).</summary>
    public void Poke()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    private async Task Run()
    {
        Info = StaticSystemInfo.Read(_topology);
        CreateCounters();
        _trayQuery?.Collect(); // rate counters need a baseline
        _lastCollectTicks = Environment.TickCount64;

        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            bool fast, processes;
            TimeSpan interval;
            lock (_demandLock)
            {
                fast = _fastDemand.Count > 0;
                processes = _processDemand.Count > 0;
                interval = fast ? _fastDemand.Values.Min() : TrayInterval;
            }

            if (fast != _wasFast)
            {
                _wasFast = fast;
                _detailPrimed = false;
                try
                {
                    FastChanged?.Invoke(fast);
                }
                catch (Exception ex)
                {
                    Log.Error("FastChanged handler failed", ex);
                }
            }

            try
            {
                var snapshot = Sample(fast, processes, interval);
                Latest = snapshot;
                Updated?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                Log.Error("Monitor sample failed", ex);
            }

            try
            {
                await _wake.WaitAsync(interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void CreateCounters()
    {
        _trayQuery = new PdhQuery();
        _cpuUtility = _trayQuery.Add(@"\Processor Information(_Total)\% Processor Utility");
        _energy = _trayQuery.Add(@"\Energy Meter(*)\Power");

        _detailQuery = new PdhQuery();
        _cpuPerformance = _detailQuery.Add(@"\Processor Information(*)\% Processor Performance");
        _cpuBaseFrequency = _detailQuery.Add(@"\Processor Information(*)\Processor Frequency");
        _diskRead = _detailQuery.Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
        _diskWrite = _detailQuery.Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
        _diskIdle = _detailQuery.Add(@"\PhysicalDisk(*)\% Idle Time");
        _thermal = _detailQuery.Add(@"\Thermal Zone Information(*)\High Precision Temperature");

        // Windows' GPU scheduler counters (what Task Manager shows). Reading them doesn't wake a sleeping
        // RTX: measured 60 of 60 samples still asleep while reading every second.
        _gpuEngine = _detailQuery.Add(@"\GPU Engine(*)\Utilization Percentage");
    }

    /// <summary>Per GPU, the busiest engine: each engine's use summed over processes. Only while a window is open.</summary>
    private IReadOnlyList<GpuLoad> ReadGpus(DateTime now)
    {
        if (now - _adaptersReadUtc > TimeSpan.FromSeconds(30) || (_adapters.Count < 2 && _dgpu.State is DgpuState.Active or DgpuState.Sleeping && now - _adaptersReadUtc > TimeSpan.FromSeconds(5)))
        {
            _adapters = GpuAdapters.List();
            _adaptersReadUtc = now;
        }

        if (_adapters.Count == 0 || _gpuEngine?.Read() is not { } values)
        {
            return [];
        }

        var engines = new Dictionary<(long Luid, int Engine), double>();
        foreach (var (instance, value) in values)
        {
            var m = GpuEngineInstance.Match(instance);
            if (m.Success && double.IsFinite(value))
            {
                var luid = (Convert.ToInt64(m.Groups["hi"].Value, 16) << 32) | Convert.ToInt64(m.Groups["lo"].Value, 16);
                var key = (luid, int.Parse(m.Groups["eng"].Value, System.Globalization.CultureInfo.InvariantCulture));
                engines[key] = engines.GetValueOrDefault(key) + value;
            }
        }

        return [.. _adapters.OrderBy(a => a.Discrete).Select(a => new GpuLoad(a, Math.Clamp(engines.Where(e => e.Key.Luid == a.Luid).Select(e => e.Value).DefaultIfEmpty(0).Max(), 0, 100)))];
    }

    /// <summary>
    /// The RTX is only asked for its power while the scheduler counters show it working and Windows
    /// reports it powered: asking a sleeping RTX anything wakes it (and then it draws ~8 W idling).
    /// </summary>
    private IReadOnlyList<GpuLoad> WithWatts(IReadOnlyList<GpuLoad> gpus, double? integratedWatts, DateTime now)
    {
        var busy = gpus.Any(g => g.Adapter.Discrete && g.Percent >= 1);
        if (busy)
        {
            _dgpu = DiscreteGpu.Query(); // cheap, and doesn't touch the GPU
            _dgpuReadUtc = now;
        }

        var discreteWatts = busy && _dgpu.State == DgpuState.Active ? _nvidia.Read(now) : null;
        if (!busy)
        {
            _nvidia.ReleaseIfIdle(now);
        }

        return [.. gpus.Select(g => g with { Watts = g.Adapter.Discrete ? discreteWatts : integratedWatts })];
    }

    private Snapshot Sample(bool fast, bool processes, TimeSpan interval)
    {
        // Rate counters are deltas between collections; over a few ms they're mostly noise.
        var sinceLast = Environment.TickCount64 - _lastCollectTicks;
        if (sinceLast < 800)
        {
            Thread.Sleep((int)(800 - sinceLast));
        }

        var now = DateTime.UtcNow;

        // After sleep/hibernate the first delta covers the whole gap; re-baseline instead of reporting it.
        if (_lastSampleUtc != DateTime.MinValue && now - _lastSampleUtc > (interval * 3) + TimeSpan.FromSeconds(5))
        {
            _trayQuery?.Collect();
            _detailPrimed = false;
            Thread.Sleep(1000);
            now = DateTime.UtcNow;
        }

        _lastSampleUtc = now;
        _trayQuery?.Collect();
        var detail = false;
        if (fast)
        {
            _detailQuery?.Collect();
            detail = _detailPrimed; // the first detail collection is only a baseline
            _detailPrimed = true;
        }

        _lastCollectTicks = Environment.TickCount64;

        if (now - _batteryInfoReadUtc > BatteryInfoInterval || BatteryInfo is null)
        {
            BatteryInfo = _battery.ReadInfo() ?? BatteryInfo;
            _batteryInfoReadUtc = now;
        }

        // The dGPU only matters while it can draw power; when it's off or absent, look rarely.
        var dgpuInterval = fast ? TimeSpan.FromSeconds(5)
            : _dgpu.State is DgpuState.Active or DgpuState.Sleeping or DgpuState.Unknown ? TimeSpan.FromSeconds(15)
            : TimeSpan.FromSeconds(60);
        if (now - _dgpuReadUtc > dgpuInterval)
        {
            _dgpu = DiscreteGpu.Query();
            _dgpuReadUtc = now;
        }

        var cpu = ReadCpu(detail, out var packageWatts);

        // Before the power model: on the charger, the charger number includes the RTX's own reading.
        IReadOnlyList<GpuLoad> gpus = detail ? WithWatts(ReadGpus(now), cpu.GraphicsWatts, now) : [];
        var reading = _battery.ReadStatus();
        var power = Power.Update(now, reading, BatteryInfo?.FullChargedCapacityMWh, packageWatts, ChargeTarget(), _dgpu.State == DgpuState.Active,
            gpus.FirstOrDefault(g => g.Adapter.Discrete)?.Watts);

        EcSensors? sensors = null;
        double? thermal = null;
        double? batteryTemp = null;
        IReadOnlyList<AppUsage> top = [];
        var memory = ReadMemory(); // one cheap syscall: always, so a window never opens on an empty RAM row
        IReadOnlyList<DiskStat> disks = [];
        if (fast)
        {
            try
            {
                sensors = Sensors?.ReadSensors();
            }
            catch (Exception ex)
            {
                Log.Error("EC sensor read failed", ex);
            }

            if (detail)
            {
                disks = ReadDisks();
                if (_thermal?.Read() is { Count: > 0 } zones)
                {
                    thermal = (zones.Values.Max() / 10.0) - 273.15;
                }
            }

            if (_batteryTempSupported)
            {
                batteryTemp = _battery.ReadTemperatureC();
                _batteryTempSupported = batteryTemp is not null;
            }

            top = processes ? _processes.Sample(5) : [];
        }

        if (!processes)
        {
            _processes.Reset();
        }

        if (!detail)
        {
            _nvidia.ReleaseIfIdle(now);
        }

        return new Snapshot(now, power, cpu, memory, disks, sensors, thermal, batteryTemp, _dgpu, top, gpus);
    }

    private static readonly MemoryStat Empty = new(0, 0, 0, 0, 0);

    private CpuStat ReadCpu(bool detail, out double? packageWatts)
    {
        var usage = Math.Clamp(_cpuUtility?.ReadInstance("_Total") ?? 0, 0, 100);

        double sum = 0, max = 0, pSum = 0, eSum = 0;
        int count = 0, pCount = 0, eCount = 0;
        if (detail && _cpuPerformance?.Read() is { } perCore)
        {
            // Per-instance nominal frequency: P- and E-cores can have different base clocks.
            var bases = _cpuBaseFrequency?.Read();
            var fallbackBase = bases is not null && bases.TryGetValue("_Total", out var t) ? t : 0;
            foreach (var (instance, performance) in perCore)
            {
                if (instance.Contains("_Total", StringComparison.Ordinal))
                {
                    continue;
                }

                var baseMHz = bases is not null && bases.TryGetValue(instance, out var b) && b > 0 ? b : fallbackBase;
                if (baseMHz <= 0 || !double.IsFinite(performance))
                {
                    continue;
                }

                var mhz = baseMHz * performance / 100.0;
                sum += mhz;
                count++;
                max = Math.Max(max, mhz);
                switch (_topology.IsPerformance(instance))
                {
                    case true:
                        pSum += mhz;
                        pCount++;
                        break;
                    case false:
                        eSum += mhz;
                        eCount++;
                        break;
                }
            }
        }

        double? cores = null, graphics = null;
        packageWatts = null;
        if (_energy?.Read() is { Count: > 0 } meters)
        {
            foreach (var (name, milliwatts) in meters)
            {
                if (name.EndsWith("_pkg", StringComparison.OrdinalIgnoreCase))
                {
                    packageWatts = milliwatts / 1000.0;
                }
                else if (name.EndsWith("_pp0", StringComparison.OrdinalIgnoreCase))
                {
                    cores = milliwatts / 1000.0;
                }
                else if (name.EndsWith("_pp1", StringComparison.OrdinalIgnoreCase))
                {
                    graphics = milliwatts / 1000.0;
                }
            }
        }

        // Reject counter glitches: non-finite, negative, absurd, or sub-domains above the package.
        if (packageWatts is { } package)
        {
            if (!double.IsFinite(package) || package is < 0 or > 250)
            {
                packageWatts = null;
                cores = graphics = null;
            }
            else
            {
                cores = cores is { } c && double.IsFinite(c) && c >= 0 ? Math.Min(c, package) : null;
                graphics = graphics is { } gr && double.IsFinite(gr) && gr >= 0 ? Math.Min(gr, package) : null;
            }
        }

        return new CpuStat(
            usage,
            count > 0 ? sum / count : 0,
            max,
            pCount > 0 ? pSum / pCount : null,
            eCount > 0 ? eSum / eCount : null,
            cores,
            graphics);
    }

    private static MemoryStat ReadMemory()
    {
        var status = new Kernel32.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Kernel32.MEMORYSTATUSEX>() };
        if (!Kernel32.GlobalMemoryStatusEx(ref status))
        {
            return Empty;
        }

        return new MemoryStat(
            status.ullTotalPhys,
            status.ullTotalPhys - status.ullAvailPhys,
            status.dwMemoryLoad,
            status.ullTotalPageFile - status.ullAvailPageFile,
            status.ullTotalPageFile);
    }

    private List<DiskStat> ReadDisks()
    {
        var disks = new List<DiskStat>();
        if (_diskRead?.Read() is not { } reads)
        {
            return disks;
        }

        var writes = _diskWrite?.Read();
        var idle = _diskIdle?.Read();
        foreach (var (instance, read) in reads)
        {
            if (instance == "_Total")
            {
                continue;
            }

            var write = writes is not null && writes.TryGetValue(instance, out var w) ? w : 0;
            var idlePercent = idle is not null && idle.TryGetValue(instance, out var i) ? i : 100;
            disks.Add(new DiskStat(FormatDiskName(instance), read, write, Math.Clamp(100 - idlePercent, 0, 100)));
        }

        disks.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return disks;
    }

    private static string FormatDiskName(string instance)
    {
        // "0 C: D:" -> "Disk 0 (C: D:)"
        var space = instance.IndexOf(' ');
        return space < 0 ? $"Disk {instance}" : $"Disk {instance[..space]} ({instance[(space + 1)..]})";
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }

        _trayQuery?.Dispose();
        _detailQuery?.Dispose();
        _battery.Dispose();
        _processes.Dispose();
        if (_loop is null || _loop.IsCompleted)
        {
            _nvidia.Dispose(); // NVML is only ever called on the sampling thread
        }

        _stop.Dispose();
    }
}
