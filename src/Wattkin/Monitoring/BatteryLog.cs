using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;

namespace Wattkin.Monitoring;

public readonly record struct CapacityPoint(DateTime Date, double FullWh, double DesignWh);

/// <summary>
/// Battery history in %LOCALAPPDATA%\Wattkin\battery (one CSV per month) plus a daily capacity file.
/// On battery: one line a minute. On the charger: only when something changes, plus a 10-minute
/// heartbeat. Lines are buffered and written every 15 minutes (and on plug/unplug, sleep and exit),
/// so the SSD isn't woken every minute.
/// </summary>
public sealed class BatteryLog
{
    private static readonly TimeSpan BatteryInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan AcHeartbeat = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(15);
    private const string Header = "time,on_ac,charging,percent,remaining_mwh,full_mwh,rate_mw,system_w,cpu_pkg_w,cpu_usage,dgpu";

    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wattkin", "battery");
    private readonly object _gate = new();
    private readonly List<(string File, string Line)> _pending = [];
    private DateTime _lastLineUtc = DateTime.MinValue;
    private DateTime _lastFlushUtc = DateTime.UtcNow;
    private string _lastKey = "";
    private bool? _lastOnAc;
    private DateOnly _lastCapacityDay;

    public string Directory => _directory;
    private string CapacityFile => Path.Combine(_directory, "capacity.csv");

    public void Record(Snapshot s, BatteryInfo? info)
    {
        if (s.Power.Percent is not { } percent)
        {
            return;
        }

        var p = s.Power;
        var now = s.TimestampUtc;
        var key = $"{p.OnAc}|{p.Charging}|{Math.Round(percent)}|{s.Dgpu.State}";
        var plugChanged = _lastOnAc is { } wasOnAc && wasOnAc != p.OnAc;
        var due = p.OnAc
            ? key != _lastKey || now - _lastLineUtc >= AcHeartbeat
            : now - _lastLineUtc >= BatteryInterval;
        if (!due && !plugChanged)
        {
            return;
        }

        _lastKey = key;
        _lastOnAc = p.OnAc;
        _lastLineUtc = now;
        var local = now.ToLocalTime();
        var line = string.Join(',',
            local.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            p.OnAc ? 1 : 0,
            p.Charging ? 1 : 0,
            F(percent),
            F(p.RemainingWh * 1000, "0"),
            info?.FullChargedCapacityMWh.ToString(CultureInfo.InvariantCulture) ?? "",
            F(p.BatteryRateWatts * 1000, "0"),
            F(p.SystemWatts),
            F(p.CpuPackageWatts),
            F(s.Cpu.UsagePercent),
            s.Dgpu.State);

        lock (_gate)
        {
            _pending.Add((Path.Combine(_directory, $"battery-{local:yyyy-MM}.csv"), line));
        }

        var today = DateOnly.FromDateTime(local);
        if (info is not null && today != _lastCapacityDay)
        {
            _lastCapacityDay = today;
            Safe(() => RecordCapacity(today, info));
        }

        if (plugChanged || now - _lastFlushUtc >= FlushInterval)
        {
            Flush();
        }
    }

    /// <summary>Writes buffered lines (also called before sleep and on exit).</summary>
    public void Flush()
    {
        List<(string File, string Line)> lines;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            lines = [.. _pending];
            _pending.Clear();
            _lastFlushUtc = DateTime.UtcNow;
        }

        Safe(() =>
        {
            System.IO.Directory.CreateDirectory(_directory);
            foreach (var group in lines.GroupBy(l => l.File))
            {
                var text = new StringBuilder();
                if (!File.Exists(group.Key))
                {
                    text.AppendLine(Header);
                }

                foreach (var (_, line) in group)
                {
                    text.AppendLine(line);
                }

                File.AppendAllText(group.Key, text.ToString());
            }
        });
    }

    private void RecordCapacity(DateOnly day, BatteryInfo info)
    {
        System.IO.Directory.CreateDirectory(_directory);
        var dayText = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (File.Exists(CapacityFile) && File.ReadLines(CapacityFile).Any(l => l.StartsWith(dayText, StringComparison.Ordinal)))
        {
            return;
        }

        if (!File.Exists(CapacityFile))
        {
            File.WriteAllText(CapacityFile, "date,full_mwh,design_mwh,cycles" + Environment.NewLine);
        }

        File.AppendAllText(CapacityFile, $"{dayText},{info.FullChargedCapacityMWh},{info.DesignedCapacityMWh},{info.CycleCount}{Environment.NewLine}");
    }

    /// <summary>
    /// Capacity history: Windows' battery report (goes back to Windows install) merged with Wattkin's own
    /// daily records. <paramref name="since"/>: the day a new battery went in; older days were another battery's.
    /// </summary>
    public async Task<IReadOnlyList<CapacityPoint>> LoadCapacityHistoryAsync(DateTime? since = null)
    {
        var points = new SortedDictionary<DateTime, CapacityPoint>();
        foreach (var point in await Task.Run(ReadWindowsBatteryReport).ConfigureAwait(false))
        {
            points[point.Date.Date] = point;
        }

        try
        {
            if (File.Exists(CapacityFile))
            {
                foreach (var line in File.ReadLines(CapacityFile).Skip(1))
                {
                    var parts = line.Split(',');
                    if (parts.Length >= 3
                        && DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                        && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var full)
                        && double.TryParse(parts[2], CultureInfo.InvariantCulture, out var design))
                    {
                        points[date.Date] = new CapacityPoint(date, full / 1000, design / 1000);
                    }
                }
            }
        }
        catch (IOException)
        {
        }

        return [.. points.Values.Where(p => since is not { } day || p.Date.Date >= day.Date)];
    }

    /// <summary>
    /// Runs powercfg into a fresh, randomly named folder and parses the XML with DTDs disabled. The
    /// worst a tampered file can do is produce a wrong chart.
    /// </summary>
    private static List<CapacityPoint> ReadWindowsBatteryReport()
    {
        var result = new List<CapacityPoint>();
        DirectoryInfo? folder = null;
        try
        {
            folder = System.IO.Directory.CreateTempSubdirectory("wattkin-");
            var file = Path.Combine(folder.FullName, "battery.xml");
            // By full path: Wattkin runs elevated, and a bare name also searches the current folder.
            var powercfg = Path.Combine(Environment.SystemDirectory, "powercfg.exe");
            using (var p = Process.Start(new ProcessStartInfo(powercfg, $"/batteryreport /xml /output \"{file}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, WorkingDirectory = Environment.SystemDirectory }))
            {
                p?.StandardOutput.ReadToEnd();
                p?.WaitForExit(15000);
            }

            if (!File.Exists(file))
            {
                return result;
            }

            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(file, settings);
            var doc = new XmlDocument { XmlResolver = null };
            doc.Load(reader);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("b", doc.DocumentElement?.NamespaceURI ?? string.Empty);
            if (doc.SelectNodes("//b:History/b:HistoryEntry", ns) is not { } entries)
            {
                return result;
            }

            foreach (XmlElement entry in entries)
            {
                if (DateTime.TryParse(entry.GetAttribute("StartDate"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var date)
                    && double.TryParse(entry.GetAttribute("FullChargeCapacity"), CultureInfo.InvariantCulture, out var full)
                    && double.TryParse(entry.GetAttribute("DesignCapacity"), CultureInfo.InvariantCulture, out var design)
                    && full > 0 && design > 0)
                {
                    result.Add(new CapacityPoint(date.ToLocalTime(), full / 1000, design / 1000));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or XmlException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Log.Error("Reading the Windows battery report failed", ex);
        }
        finally
        {
            try
            {
                folder?.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }

        return result;
    }

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Battery log write failed", ex);
        }
    }

    private static string F(double? value, string format = "0.###") =>
        value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) : string.Empty;
}
