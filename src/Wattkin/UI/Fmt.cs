using System.Globalization;
using Wattkin.Hardware;
using Wattkin.Monitoring;

namespace Wattkin.UI;

internal static class Fmt
{
    private static readonly CultureInfo C = CultureInfo.CurrentCulture;

    public static string Watts(double? watts) => watts is null ? "—" : WattsNumber(watts) + " W";

    /// <summary>One decimal below 10 W, whole watts above. Rounded first, so 9.97 shows as "10", not "10.0".</summary>
    public static string WattsNumber(double? watts)
    {
        if (watts is not { } w || !double.IsFinite(w))
        {
            return "—";
        }

        var tenth = Math.Round(w, 1, MidpointRounding.AwayFromZero);
        return tenth < 10 ? tenth.ToString("0.0", C) : Math.Round(w, MidpointRounding.AwayFromZero).ToString("0", C);
    }

    /// <summary>For display only: the per-user folders as %APPDATA% / %LOCALAPPDATA%, so screenshots don't show the account name.</summary>
    public static string FriendlyPath(string path)
    {
        foreach (var (folder, name) in new[] { (Environment.SpecialFolder.LocalApplicationData, "%LOCALAPPDATA%"), (Environment.SpecialFolder.ApplicationData, "%APPDATA%") })
        {
            var root = Environment.GetFolderPath(folder);
            if (root.Length > 0 && path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return name + path[root.Length..];
            }
        }

        return path;
    }

    /// <summary>Time estimates in steps that don't flicker: 5 minutes above an hour (above 10 minutes when coarse).</summary>
    public static TimeSpan? Steady(TimeSpan? span, bool coarse = false) =>
        span is { } v && v.TotalMinutes >= (coarse ? 10 : 60) ? TimeSpan.FromMinutes(Math.Round(v.TotalMinutes / 5) * 5) : span;

    public static string Duration(TimeSpan? span)
    {
        if (span is not { } t)
        {
            return "—";
        }

        if (t.TotalMinutes < 1)
        {
            return "<1 min";
        }

        if (t.TotalHours < 1)
        {
            return $"{(int)t.TotalMinutes} min";
        }

        return t.Minutes == 0 ? $"{(int)t.TotalHours}h" : $"{(int)t.TotalHours}h {t.Minutes:00}m";
    }

    /// <summary>A short wait in seconds: "45 s", "1 min", "1 min 30 s".</summary>
    public static string Delay(int seconds) => (seconds / 60, seconds % 60) switch
    {
        (0, var s) => $"{s} s",
        (var m, 0) => $"{m} min",
        (var m, var s) => $"{m} min {s} s",
    };

    public static string Percent(double? value, string format = "0") => value is { } v ? v.ToString(format, C) + "%" : "—";

    public static string GHz(double mhz) => mhz <= 0 ? "—" : (mhz / 1000).ToString("0.00", C) + " GHz";

    public static string Bytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        while (bytes >= 1024 && i < units.Length - 1)
        {
            bytes /= 1024;
            i++;
        }

        return bytes.ToString(bytes >= 100 || i == 0 ? "0" : "0.0", C) + " " + units[i];
    }

    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Temp(int? celsius) => celsius is { } c ? $"{c}°C" : "—";

    public static string Temp(double? celsius) => celsius is { } c ? $"{c:0}°C" : "—";

    public static string Rpm(int? rpm) => rpm switch
    {
        null => "—",
        0 => "off",
        _ => rpm.Value.ToString("N0", C),
    };

    /// <summary>Both fans: "off" when both are stopped, else "2,400 / off rpm".</summary>
    public static string Fans(int? fan1, int? fan2) => (fan1, fan2) switch
    {
        (null, null) => "—",
        (0, 0) => "off",
        _ => $"{Rpm(fan1)} / {Rpm(fan2)} rpm",
    };

    public static string PowerHeadline(PowerState p) => p.Source switch
    {
        PowerSource.BatteryMeasured => "whole laptop, measured from the battery",
        PowerSource.AcEstimated => "whole laptop, estimated (on charger)",
        PowerSource.AcCpuOnly => "CPU only on the charger. Unplug for a few minutes once so Wattkin learns the rest",
        _ => "",
    };

    /// <param name="coarse">Round times to 5 minutes (the tray tooltip: fewer pointless updates).</param>
    public static string BatteryLine(PowerState p, string chargeStatus, bool coarse = false)
    {
        if (p.Percent is null)
        {
            return "No battery data";
        }

        if (p.Source == PowerSource.BatteryMeasured)
        {
            return p.TimeToEmpty is { } t ? $"{Duration(Steady(t, coarse))} left" : "Estimating…";
        }

        if (p.Charging)
        {
            return p.TimeToTarget is { } t ? $"Charging · {Duration(Steady(t, coarse))} to limit" : "Charging";
        }

        return string.IsNullOrEmpty(chargeStatus) ? "Plugged in" : chargeStatus;
    }

    public static string Dgpu(DgpuStatus status) => status.State switch
    {
        DgpuState.None => "No discrete GPU",
        DgpuState.RemovedByFirmware => "Off (integrated-only mode)",
        DgpuState.DisabledInWindows => "Disabled in Windows",
        DgpuState.Sleeping => "Asleep",
        DgpuState.Active => "Awake",
        _ => "Unknown",
    };

    /// <summary>The parts that exist, as a meter bar's right-hand text: "4.5 W  ·  1.20 GHz  ·  46°C".</summary>
    public static string Join(params string?[] parts) =>
        string.Join("  ·  ", parts.Where(p => !string.IsNullOrEmpty(p) && p != "—"));

    /// <summary>"whole laptop, …" → "Whole laptop, …".</summary>
    public static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Everything a profile sets, one item each: what the flyout shows under the profile buttons.</summary>
    public static string ProfileDetails(Settings.Profile p)
    {
        var parts = new List<string>();
        if (p.Performance is { } shift)
        {
            parts.Add("CPU " + Shift(shift));
        }

        if (FanWords(p) is { } fans)
        {
            parts.Add(fans);
        }

        if (p.WindowsMode != Settings.WindowsPowerMode.Unchanged)
        {
            parts.Add("Windows " + (p.WindowsMode switch
            {
                Settings.WindowsPowerMode.BestEfficiency => "best efficiency",
                Settings.WindowsPowerMode.BestPerformance => "best performance",
                _ => "balanced",
            }));
        }

        if (p.CpuBoost is { } boost)
        {
            parts.Add(boost ? "turbo on" : "turbo off");
        }

        if (p.MaxCpuPercent is { } max)
        {
            parts.Add($"CPU up to {max}%");
        }

        if (p.MaxFrequencyMHz is { } mhz)
        {
            parts.Add(mhz == 0 ? "no clock cap" : $"up to {mhz / 1000.0:0.0#} GHz");
        }

        if (p.EnergyPreference is { } epp)
        {
            parts.Add($"EPP {epp}");
        }

        if (p.RefreshRate is { } hz)
        {
            parts.Add($"{hz} Hz screen");
        }

        if (p.KeyboardLevel is { } level)
        {
            parts.Add("keyboard " + (level switch { 0 => "off", 1 => "low", 2 => "mid", _ => "high" }));
        }

        return parts.Count == 0 ? "leaves everything as it is" : string.Join("  ·  ", parts);
    }

    /// <summary>A profile's fan setting in a few words; null when it leaves the fans alone.</summary>
    private static string? FanWords(Settings.Profile p) => p switch
    {
        { CoolerBoost: true } => "fans at full speed",
        { Fan: FanMode.Auto } => "auto fans",
        { Fan: FanMode.Silent } => "silent fans",
        { Fan: FanMode.Custom, FanProfileName: null } => "laptop's fan curve",
        { Fan: FanMode.Custom, FanProfileName: var name } when string.Equals(name, Settings.FanProfile.LaptopDefault, StringComparison.OrdinalIgnoreCase) => "laptop's fan curve",
        { Fan: FanMode.Custom, FanProfileName: var name } when string.Equals(name, Settings.FanProfile.Off, StringComparison.OrdinalIgnoreCase) => "fans off",
        { Fan: FanMode.Custom, FanProfileName: var name } => $"{name} fans",
        _ => null,
    };

    /// <summary>What a profile changes, in plain words, built from its settings so it stays true after editing.</summary>
    public static string ProfileSummary(Settings.Profile p)
    {
        var parts = new List<string>();
        if (p.Performance is { } shift)
        {
            parts.Add(shift switch
            {
                ShiftMode.Eco => "lowest power",
                ShiftMode.Sport => "higher power limits",
                ShiftMode.Turbo => "highest power limits",
                _ => "normal power",
            });
        }

        if (p.Fan != FanMode.Auto || p.CoolerBoost)
        {
            if (FanWords(p) is { } fans)
            {
                parts.Add(fans);
            }
        }

        if (p.CpuBoost == false)
        {
            parts.Add("no turbo boost");
        }

        if (p.MaxCpuPercent is { } max and < 100)
        {
            parts.Add($"CPU capped at {max}%");
        }

        if (p.MaxFrequencyMHz is { } mhz and > 0)
        {
            parts.Add($"clock capped at {mhz / 1000.0:0.0#} GHz");
        }

        if (p.EnergyPreference is { } epp and >= 60)
        {
            parts.Add("favours efficiency");
        }

        if (p.RefreshRate is { } hz)
        {
            parts.Add($"{hz} Hz screen");
        }

        if (p.KeyboardLevel is { } level)
        {
            parts.Add($"keyboard light {(level == 0 ? "off" : "on")}");
        }

        if (parts.Count > 0)
        {
            return string.Join(", ", parts);
        }

        // Only the usual values set (auto fans, turbo on…) isn't the same as leaving things alone.
        var setsNothing = p is { Performance: null, Fan: null, CoolerBoost: false, CpuBoost: null, MaxCpuPercent: null, MaxFrequencyMHz: null, EnergyPreference: null }
            && p.WindowsMode == Settings.WindowsPowerMode.Unchanged;
        return setsNothing ? "leaves everything as it is" : "normal settings";
    }

    public static string Shift(ShiftMode? mode) => mode switch
    {
        ShiftMode.Eco => "Eco",
        ShiftMode.Balanced => "Balanced",
        ShiftMode.Sport => "Sport",
        ShiftMode.Turbo => "Extreme",
        _ => "—",
    };
}
