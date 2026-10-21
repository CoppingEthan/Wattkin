using System.Runtime.InteropServices;
using Wattkin.Native;
using Wattkin.Settings;

namespace Wattkin.Platform;

/// <summary>
/// The active power plan's processor settings the power self-test lifts, on the charger and on battery:
/// boost mode and maximum state, and maximum frequency and energy preference when the plan has them.
/// </summary>
public readonly record struct ProcessorPlan(
    Guid Scheme, uint BoostAc, uint BoostDc, uint MaxAc, uint MaxDc,
    (uint Ac, uint Dc)? Freq, (uint Ac, uint Dc)? Epp, (uint Ac, uint Dc)? Freq1 = null, (uint Ac, uint Dc)? Epp1 = null);

/// <summary>
/// Windows power mode (Settings > Power) and processor power-plan knobs. Everything Wattkin changes is
/// remembered first, keyed by power plan, so "Restore" and uninstall can put the user's values back.
/// </summary>
public static class WindowsPower
{
    public static readonly Guid BestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid Balanced = Guid.Empty;
    public static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    private static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid BoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    private static readonly Guid MaxProcessorState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid BatterySubgroup = new("e73a048d-bf27-4f12-9731-8b2076e8891f");
    private static readonly Guid CriticalBatteryLevel = new("9a66d8d7-4ff7-4ef9-b5a2-5a326ca2a469");

    // PERFEPP / PERFEPP1 (0–100 %, higher saves energy) and PROCFREQMAX / PROCFREQMAX1 (MHz, 0 = no cap):
    // the "1" settings are for processor power-efficiency class 1, a hybrid CPU's faster cores (winnt.h);
    // both are set alike so a profile holds on any CPU.
    private static readonly Guid EnergyPreference = new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
    private static readonly Guid EnergyPreference1 = new("36687f9e-e3a5-4dbf-b1dc-15eb381c6864");
    private static readonly Guid MaxFrequency = new("75b0ae3f-bce0-45a7-8c89-c9611c25e100");
    private static readonly Guid MaxFrequency1 = new("75b0ae3f-bce0-45a7-8c89-c9611c25e101");

    private const uint BoostDisabled = 0;
    private const uint BoostAggressive = 2;

    // Only these settings are ever written back from saved originals (the file is user-writable), each
    // with the largest value it may take.
    private static readonly Dictionary<string, (Guid Setting, uint Max)> RestorableSettings = new()
    {
        ["boost"] = (BoostMode, 100),
        ["maxstate"] = (MaxProcessorState, 100),
        ["epp"] = (EnergyPreference, 100),
        ["epp1"] = (EnergyPreference1, 100),
        ["freqmax"] = (MaxFrequency, 10000),
        ["freqmax1"] = (MaxFrequency1, 10000),
    };

    private static readonly bool HasPerSourceModes =
        NativeLibrary.TryLoad("powrprof.dll", out var lib) && NativeLibrary.TryGetExport(lib, "PowerSetUserConfiguredACPowerMode", out _);

    /// <summary>
    /// Sets Windows' power mode for the current power source only (Windows 11 24H2 keeps separate
    /// plugged-in and on-battery modes; the profile for the other source sets that one when it applies).
    /// </summary>
    public static void SetMode(WindowsPowerMode mode, bool onAc, AppSettings settings)
    {
        if (mode == WindowsPowerMode.Unchanged)
        {
            return;
        }

        var guid = mode switch
        {
            WindowsPowerMode.BestEfficiency => BestEfficiency,
            WindowsPowerMode.BestPerformance => BestPerformance,
            _ => Balanced,
        };

        var key = $"mode:{(onAc ? "ac" : "dc")}";
        if (!settings.OriginalPowerModes.ContainsKey(key) && GetModeGuid(onAc) is { } original)
        {
            settings.OriginalPowerModes[key] = original;
        }

        uint status;
        if (HasPerSourceModes)
        {
            status = onAc ? PowrProf.PowerSetUserConfiguredACPowerMode(guid) : PowrProf.PowerSetUserConfiguredDCPowerMode(guid);
        }
        else
        {
            status = PowrProf.PowerSetActiveOverlayScheme(guid);
        }

        if (status != 0)
        {
            Log.Warn($"Setting the Windows power mode failed (0x{status:X}).");
        }
    }

    public static WindowsPowerMode GetMode(bool onAc) => GetModeGuid(onAc) switch
    {
        null => WindowsPowerMode.Unchanged,
        var g when g == BestEfficiency => WindowsPowerMode.BestEfficiency,
        var g when g == BestPerformance => WindowsPowerMode.BestPerformance,
        _ => WindowsPowerMode.Balanced,
    };

    private static Guid? GetModeGuid(bool onAc)
    {
        Guid guid;
        uint status;
        if (HasPerSourceModes)
        {
            status = onAc ? PowrProf.PowerGetUserConfiguredACPowerMode(out guid) : PowrProf.PowerGetUserConfiguredDCPowerMode(out guid);
        }
        else
        {
            status = PowrProf.PowerGetEffectiveOverlayScheme(out guid);
        }

        return status == 0 ? guid : null;
    }

    /// <summary>
    /// Turbo boost on/off in the active power plan (both AC and DC). Off caps the CPU at its base
    /// clock, the single biggest battery saver for light work.
    /// </summary>
    public static void SetCpuBoost(bool enabled, AppSettings settings)
    {
        if (!TryActiveScheme(out var scheme))
        {
            return;
        }

        CaptureOriginal(scheme, BoostMode, settings, "boost");
        var on = settings.OriginalPowerSettings.TryGetValue(Key(scheme, "boost", "ac"), out var original) && original != BoostDisabled ? original : BoostAggressive;
        WriteProcessor(scheme, BoostMode, enabled ? on : BoostDisabled, settings, "boost");
    }

    public static void SetMaxCpuPercent(int percent, AppSettings settings)
    {
        if (TryActiveScheme(out var scheme))
        {
            WriteProcessor(scheme, MaxProcessorState, (uint)Math.Clamp(percent, 5, 100), settings, "maxstate");
        }
    }

    /// <summary>
    /// Energy performance preference in the active plan (AC and DC), 0 = performance, 100 = efficiency:
    /// how eagerly the processor (Intel Speed Shift) raises its clock for light and medium work.
    /// Windows' "Best power efficiency" mode can override it.
    /// </summary>
    public static void SetEnergyPreference(int percent, AppSettings settings)
    {
        if (TryActiveScheme(out var scheme))
        {
            WriteProcessor(scheme, EnergyPreference, (uint)Math.Clamp(percent, 0, 100), settings, "epp");
            WriteProcessor(scheme, EnergyPreference1, (uint)Math.Clamp(percent, 0, 100), settings, "epp1");
        }
    }

    /// <summary>Maximum processor frequency in the active plan (AC and DC), in MHz; 0 lifts the cap.</summary>
    public static void SetMaxFrequency(int mhz, AppSettings settings)
    {
        if (TryActiveScheme(out var scheme))
        {
            WriteProcessor(scheme, MaxFrequency, (uint)Math.Clamp(mhz, 0, 10000), settings, "freqmax");
            WriteProcessor(scheme, MaxFrequency1, (uint)Math.Clamp(mhz, 0, 10000), settings, "freqmax1");
        }
    }

    /// <summary>The active plan's processor settings as they are, for the self-test to put back; null if unreadable.</summary>
    public static ProcessorPlan? ReadProcessorPlan() =>
        TryActiveScheme(out var scheme) && Read(scheme, BoostMode) is { } boost && Read(scheme, MaxProcessorState) is { } max
            ? new ProcessorPlan(scheme, boost.Ac, boost.Dc, max.Ac, max.Dc, Read(scheme, MaxFrequency), Read(scheme, EnergyPreference), Read(scheme, MaxFrequency1), Read(scheme, EnergyPreference1))
            : null;

    /// <summary>Writes a plan's processor settings as given (no originals recorded: the self-test puts its own snapshot back) and applies them.</summary>
    public static void WriteProcessorPlan(ProcessorPlan plan)
    {
        PowrProf.PowerWriteACValueIndex(IntPtr.Zero, plan.Scheme, ProcessorSubgroup, BoostMode, plan.BoostAc);
        PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, plan.Scheme, ProcessorSubgroup, BoostMode, plan.BoostDc);
        PowrProf.PowerWriteACValueIndex(IntPtr.Zero, plan.Scheme, ProcessorSubgroup, MaxProcessorState, plan.MaxAc);
        PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, plan.Scheme, ProcessorSubgroup, MaxProcessorState, plan.MaxDc);
        foreach (var (setting, values) in new[] { (MaxFrequency, plan.Freq), (EnergyPreference, plan.Epp), (MaxFrequency1, plan.Freq1), (EnergyPreference1, plan.Epp1) })
        {
            if (values is { } v)
            {
                PowrProf.PowerWriteACValueIndex(IntPtr.Zero, plan.Scheme, ProcessorSubgroup, setting, v.Ac);
                PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, plan.Scheme, ProcessorSubgroup, setting, v.Dc);
            }
        }

        PowrProf.PowerSetActiveScheme(IntPtr.Zero, plan.Scheme);
    }

    /// <summary>
    /// Full speed on the charger (where the self-test measures): turbo on (aggressive), no state or
    /// frequency cap, and energy preference 0. The on-battery values stay as they are, so a self-test
    /// that dies midway can't leave the battery plan flat out.
    /// </summary>
    public static ProcessorPlan Unlimited(ProcessorPlan plan) => plan with
    {
        BoostAc = BoostAggressive,
        MaxAc = 100,
        Freq = plan.Freq is { } f ? (0, f.Dc) : null,
        Epp = plan.Epp is { } e ? (0, e.Dc) : null,
        Freq1 = plan.Freq1 is { } f1 ? (0, f1.Dc) : null, // a hybrid CPU's faster cores: a profile's cap there too
        Epp1 = plan.Epp1 is { } e1 ? (0, e1.Dc) : null,
    };

    public static bool? GetCpuBoost(bool onAc)
    {
        if (!TryActiveScheme(out var scheme))
        {
            return null;
        }

        var status = onAc
            ? PowrProf.PowerReadACValueIndex(IntPtr.Zero, scheme, ProcessorSubgroup, BoostMode, out var value)
            : PowrProf.PowerReadDCValueIndex(IntPtr.Zero, scheme, ProcessorSubgroup, BoostMode, out value);
        return status == 0 ? value != BoostDisabled : null;
    }

    /// <summary>Windows' "critical battery level" on battery, in percent.</summary>
    public static int CriticalBatteryPercent()
    {
        if (TryActiveScheme(out var scheme) && PowrProf.PowerReadDCValueIndex(IntPtr.Zero, scheme, BatterySubgroup, CriticalBatteryLevel, out var value) == 0 && value <= 50)
        {
            return (int)value;
        }

        return 5;
    }

    /// <summary>Puts every processor setting and power mode back to what it was before Wattkin changed it.</summary>
    public static void RestoreOriginals(AppSettings settings)
    {
        MigrateLegacyKeys(settings); // an uninstall can run before a newer version ever started
        var touched = new HashSet<Guid>();
        foreach (var (key, value) in settings.OriginalPowerSettings)
        {
            if (!TryParseOriginal(key, value, out var scheme, out var setting, out var onAc))
            {
                continue;
            }

            var status = onAc
                ? PowrProf.PowerWriteACValueIndex(IntPtr.Zero, scheme, ProcessorSubgroup, setting, value)
                : PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, scheme, ProcessorSubgroup, setting, value);
            if (status == 0)
            {
                touched.Add(scheme);
            }
        }

        if (TryActiveScheme(out var active) && touched.Contains(active))
        {
            PowrProf.PowerSetActiveScheme(IntPtr.Zero, active);
        }

        foreach (var (key, guid) in settings.OriginalPowerModes)
        {
            if (guid != BestEfficiency && guid != Balanced && guid != BestPerformance)
            {
                continue;
            }

            if (key == "mode:ac")
            {
                if (HasPerSourceModes)
                {
                    PowrProf.PowerSetUserConfiguredACPowerMode(guid);
                }
                else
                {
                    PowrProf.PowerSetActiveOverlayScheme(guid);
                }
            }
            else if (key == "mode:dc" && HasPerSourceModes)
            {
                PowrProf.PowerSetUserConfiguredDCPowerMode(guid);
            }
        }

        settings.OriginalPowerSettings.Clear();
        settings.OriginalPowerModes.Clear();
    }

    /// <summary>
    /// A saved original from settings.json, key "{scheme}:{setting}:{ac|dc}": accepted only for a setting
    /// Wattkin writes and a value that setting can take, as the file is user-writable and the restore isn't.
    /// </summary>
    internal static bool TryParseOriginal(string key, uint value, out Guid scheme, out Guid setting, out bool onAc)
    {
        scheme = Guid.Empty;
        setting = Guid.Empty;
        onAc = false;
        var parts = key.Split(':');
        if (parts.Length != 3 || parts[2] is not ("ac" or "dc") || !Guid.TryParse(parts[0], out scheme)
            || !RestorableSettings.TryGetValue(parts[1], out var restorable) || value > restorable.Max)
        {
            return false;
        }

        setting = restorable.Setting;
        onAc = parts[2] == "ac";
        return true;
    }

    private static string Key(Guid scheme, string setting, string source) => $"{scheme}:{setting}:{source}";

    private static bool IsLegacyKey(string key) => key.Count(c => c == ':') == 1;

    public static bool HasLegacyKeys(AppSettings settings) => settings.OriginalPowerSettings.Keys.Any(IsLegacyKey);

    /// <summary>Earlier builds saved "boost:ac" without the plan; they were taken from the plan active then.</summary>
    public static void MigrateLegacyKeys(AppSettings settings)
    {
        var legacy = settings.OriginalPowerSettings.Where(kv => IsLegacyKey(kv.Key)).ToList();
        if (legacy.Count == 0 || !TryActiveScheme(out var scheme))
        {
            return;
        }

        foreach (var (key, value) in legacy)
        {
            settings.OriginalPowerSettings.Remove(key);
            settings.OriginalPowerSettings.TryAdd($"{scheme}:{key}", value);
        }
    }

    private static void WriteProcessor(Guid scheme, Guid setting, uint value, AppSettings settings, string key)
    {
        CaptureOriginal(scheme, setting, settings, key);
        if (Read(scheme, setting) == (value, value))
        {
            return; // already set; don't touch the power plan
        }

        PowrProf.PowerWriteACValueIndex(IntPtr.Zero, scheme, ProcessorSubgroup, setting, value);
        PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, scheme, ProcessorSubgroup, setting, value);
        PowrProf.PowerSetActiveScheme(IntPtr.Zero, scheme);
    }

    /// <summary>Remembers the plan's own AC/DC values the first time Wattkin touches a setting in that plan.</summary>
    private static void CaptureOriginal(Guid scheme, Guid setting, AppSettings settings, string key)
    {
        if (!settings.OriginalPowerSettings.ContainsKey(Key(scheme, key, "ac")) && Read(scheme, setting) is { } values)
        {
            settings.OriginalPowerSettings[Key(scheme, key, "ac")] = values.Ac;
            settings.OriginalPowerSettings[Key(scheme, key, "dc")] = values.Dc;
        }
    }

    private static (uint Ac, uint Dc)? Read(Guid scheme, Guid setting) =>
        PowrProf.PowerReadACValueIndex(IntPtr.Zero, scheme, ProcessorSubgroup, setting, out var ac) == 0
        && PowrProf.PowerReadDCValueIndex(IntPtr.Zero, scheme, ProcessorSubgroup, setting, out var dc) == 0
            ? (ac, dc)
            : null;

    private static bool TryActiveScheme(out Guid scheme)
    {
        scheme = Guid.Empty;
        if (PowrProf.PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0 || pointer == IntPtr.Zero)
        {
            return false;
        }

        scheme = Marshal.PtrToStructure<Guid>(pointer);
        Marshal.FreeHGlobal(pointer); // LocalFree on Windows
        return true;
    }
}
