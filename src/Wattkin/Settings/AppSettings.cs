using System.Text.Json;
using System.Text.Json.Serialization;
using Wattkin.Hardware;

namespace Wattkin.Settings;

public enum KeyboardTimeout
{
    AlwaysOn,
    AutoOff,
    AutoOffOnBattery,
}

public enum WindowsPowerMode
{
    Unchanged,
    BestEfficiency,
    Balanced,
    BestPerformance,
}

/// <summary>A bundle of independent knobs. Null means "leave this alone".</summary>
public sealed class Profile
{
    public string Name { get; set; } = "Profile";

    /// <summary>
    /// The firmware's CPU power limits (MSI's shift mode). Null = leave the register alone, for power
    /// limits set elsewhere (BIOS, ThrottleStop). Written even when null, so that survives a restart.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ShiftMode? Performance { get; set; } = ShiftMode.Balanced;

    /// <summary>
    /// Null = leave the fan mode alone (e.g. keep a custom curve set by MSI Center). Written even when
    /// null: until 1.1.1 a null was left out of the file and came back as Auto after a restart.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public FanMode? Fan { get; set; } = FanMode.Auto;
    /// <summary>
    /// With <see cref="FanMode.Custom"/>: the <see cref="FanProfile"/> to use, by name, or
    /// <see cref="FanProfile.Off"/>; null = <see cref="FanProfile.LaptopDefault"/>.
    /// </summary>
    public string? FanProfileName { get; set; }
    /// <summary>Fans at full speed (MSI's Cooler Boost), whatever <see cref="Fan"/> says.</summary>
    public bool CoolerBoost { get; set; }
    public WindowsPowerMode WindowsMode { get; set; } = WindowsPowerMode.Balanced;
    public bool? CpuBoost { get; set; }
    public int? MaxCpuPercent { get; set; }

    /// <summary>The power plan's energy performance preference, 0 (performance) to 100 (efficiency).</summary>
    public int? EnergyPreference { get; set; }

    /// <summary>The power plan's maximum processor frequency in MHz; 0 = no cap.</summary>
    public int? MaxFrequencyMHz { get; set; }

    public int? RefreshRate { get; set; }
    public int? KeyboardLevel { get; set; }

    public Profile Clone() => (Profile)MemberwiseClone();

    public static List<Profile> Defaults() =>
    [
        new()
        {
            Name = "Battery saver",
            Performance = ShiftMode.Eco,
            Fan = FanMode.Auto,
            WindowsMode = WindowsPowerMode.BestEfficiency,
            CpuBoost = false,
        },
        new()
        {
            Name = "Balanced",
            Performance = ShiftMode.Balanced,
            Fan = FanMode.Auto,
            WindowsMode = WindowsPowerMode.Balanced,
            CpuBoost = true,
        },
        new()
        {
            // The controller's own fan table: on the Prestige 16 Studio it keeps the fans off up to 60°C
            // (Auto ran them at ~3,000–3,600 rpm at 45°C, off at 42°C). Laptops without a usable
            // table get Silent instead (see HardwareService.ApplyFans).
            Name = "Quiet",
            Performance = ShiftMode.Balanced,
            Fan = FanMode.Custom,
            WindowsMode = WindowsPowerMode.Balanced,
            CpuBoost = true,
        },
        new()
        {
            Name = "Performance",
            Performance = ShiftMode.Turbo,
            Fan = FanMode.Auto,
            WindowsMode = WindowsPowerMode.BestPerformance,
            CpuBoost = true,
        },
    ];
}

/// <summary>
/// A named fan curve, made in Settings › Fan profiles and picked by name in a profile's Fans setting.
/// Two names are built in and can't be used: <see cref="LaptopDefault"/> (the curve the laptop came
/// with, read from its controller) and <see cref="Off"/> (fans off until the firmware's fixed hottest
/// step, experimental).
/// </summary>
public sealed class FanProfile
{
    public const string LaptopDefault = "Laptop default";
    public const string Off = "Off";

    public string Name { get; set; } = "Fan profile";
    public FanSpeeds Speeds { get; set; } = new();

    /// <summary>
    /// On a laptop where Wattkin writes whole fan tables (<see cref="FanTable"/>): this fan profile's
    /// table, temperatures included; <see cref="Speeds"/> is kept in step with its first six speeds.
    /// Null: the speeds run at the laptop's own temperature steps.
    /// </summary>
    public FanTable? Table { get; set; }

    public FanProfile Clone() => new() { Name = Name, Speeds = Speeds.Clone(), Table = Table?.Clone() };

    public static bool IsBuiltIn(string? name) =>
        string.Equals(name, LaptopDefault, StringComparison.OrdinalIgnoreCase) || string.Equals(name, Off, StringComparison.OrdinalIgnoreCase);

    public static FanSpeeds OffSpeeds() => new() { Cpu = new int[FanSpeeds.Steps], Gpu = new int[FanSpeeds.Steps] };

    public static List<FanProfile> Defaults() =>
    [
        new() { Name = "Quiet", Speeds = FanSpeeds.Quiet() },
        new() { Name = "Cool", Speeds = FanSpeeds.Cool() },
    ];
}

public sealed class CalibrationRecord
{
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public string Phase { get; set; } = "Charging";
    public uint FullChargeBeforeMWh { get; set; }
    public uint? FullChargeAfterMWh { get; set; }
    public int LimitBefore { get; set; } = 80;
    public bool FastDischarge { get; set; }
    public string? Outcome { get; set; }
}

public sealed class AppSettings
{
    // Battery
    public int ChargeLimit { get; set; } = 80;
    /// <summary>Resume charging below this %. Null = the EC default (limit − 10).</summary>
    public int? ChargeResumeBelow { get; set; }
    public bool FullChargeOnce { get; set; }
    public bool DrainToLimit { get; set; }
    public bool ChargeBandHolding { get; set; }
    public CalibrationRecord? Calibration { get; set; }
    public List<CalibrationRecord> CalibrationHistory { get; set; } = [];
    public bool BatteryLogging { get; set; } = true;

    // Profiles
    public List<Profile> Profiles { get; set; } = Profile.Defaults();
    /// <summary>Which changes to the built-in profiles this file has had (see <see cref="Normalize"/>).</summary>
    public int ProfilesVersion { get; set; }
    public string ActiveProfile { get; set; } = "Balanced";
    /// <summary>Whether the charger was connected when <see cref="ActiveProfile"/> was applied (null: unknown).</summary>
    public bool? ActiveProfileOnAc { get; set; }

    /// <summary>
    /// The battery level when <see cref="ActiveProfile"/> was applied. After a restart on a low battery it
    /// says whether the low-battery rule was already dealt with (picked by the rule, or by hand while low).
    /// </summary>
    public double? ActiveProfilePercent { get; set; }

    // Automatic switching: each rule names a profile, or null to leave the profile alone. Nulls are
    // written to the file, so "leave alone" survives a restart instead of coming back as the default.
    public bool AutoSwitchProfiles { get; set; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AcProfile { get; set; } = "Balanced";
    /// <summary>On battery, above <see cref="LowBatteryThreshold"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? BatteryProfile { get; set; } = "Balanced";
    /// <summary>On battery, switch to <see cref="LowBatteryProfile"/> at or below this %. 0 = off.</summary>
    public int LowBatteryThreshold { get; set; } = 30;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? LowBatteryProfile { get; set; } = "Battery saver";

    // Keyboard, LEDs, hotkeys. Where the keys are lit by a USB controller that can't be read back (the
    // Stealth 15M's Mystic Light), KeyboardLevel is also what Wattkin last set, sent again at startup.
    public int? KeyboardLevel { get; set; }
    /// <summary>"#RRGGBB": the keyboard's colour where the light is RGB (see Hardware.MysticLight).</summary>
    public string KeyboardColor { get; set; } = LightColor.White.Hex;
    public KeyboardTimeout KeyboardTimeout { get; set; } = KeyboardTimeout.AlwaysOn;

    /// <summary>
    /// Seconds without a key press before auto-off, where Wattkin does it itself (the USB light); the
    /// EC's own auto-off is fixed at about 10 s.
    /// </summary>
    public int KeyboardAutoOffSeconds { get; set; } = 10;
    public const int KeyboardAutoOffMinSeconds = 5;
    public const int KeyboardAutoOffMaxSeconds = 300;

    // Screen brightness (see Features.ScreenBrightness): Alt+Page Down / Page Up as global keys, and the
    // "extra dim" level below the backlight's lowest (100 = off). A copy restarting into an update hands
    // its dim over (ScreenDimHandoffUtc) so the new one keeps it without a flash; otherwise a start
    // begins at normal brightness.
    public bool ScreenHotkeys { get; set; }
    public int ScreenExtraDimPercent { get; set; } = 100;
    public DateTime? ScreenDimHandoffUtc { get; set; }
    /// <summary>Wattkin allowed Windows' full gamma range (GdiICMGammaRange); uninstalling takes it back.</summary>
    public bool ScreenGammaRangeSet { get; set; }

    /// <summary>
    /// While dimmed: each display's own gamma ramp from before (a colour calibration lives there), by GDI
    /// name, base64. Un-dimming puts them back, also after a restart into an update or a crash.
    /// </summary>
    public Dictionary<string, string>? ScreenBaseRamps { get; set; }
    public bool SyncMuteLeds { get; set; } = true;
    public bool HandleHotkeys { get; set; } = true;
    public bool ShowOsd { get; set; } = true;

    // Settings window: the simple view hides the fine-grained controls (they keep their values).
    public bool ExpertView { get; set; }

    // Updates from GitHub releases (signed); see Updater.
    public bool AutoUpdate { get; set; } = true;
    public DateTime? LastUpdateCheckUtc { get; set; }

    // Tray / widget
    public bool TrayShowsWatts { get; set; } = true;
    public bool WidgetEnabled { get; set; }
    public double? WidgetLeft { get; set; }
    public double? WidgetTop { get; set; }
    public bool WidgetTopmost { get; set; }
    public bool WidgetCompact { get; set; }
    public double WidgetOpacity { get; set; } = 0.92;

    // Each fan's top speed, measured with Cooler Boost (once, automatically) or the fastest seen since:
    // the flyout's fan bar is drawn against it.
    public int? Fan1MaxRpm { get; set; }
    public int? Fan2MaxRpm { get; set; }
    public DateTime? FanTopSpeedMeasuredUtc { get; set; }
    public int FanTopSpeedAttempts { get; set; }

    // Fan curves: the user's named fan profiles; the laptop's own speeds, kept the first time they're
    // read (fan profiles overwrite the controller's copy); and whether the user accepted the
    // experimental mode (curves that let the laptop run hot, and "Off").
    public List<FanProfile> FanProfiles { get; set; } = FanProfile.Defaults();
    public FanSpeeds? FactoryFanCurve { get; set; }
    public bool UnrestrictedFanCurves { get; set; }

    /// <summary>
    /// The controller's whole fan table the first time Wattkin read it, on a laptop where it writes
    /// whole tables: "Laptop default", and what uninstalling puts back. If another tool (YAMDCC) had
    /// written its own table before, that's the one kept.
    /// </summary>
    public FanTable? FactoryFanTable { get; set; }

    /// <summary>This laptop has been seen in MSI's Sport mode (0xC0) by itself, so Wattkin offers Sport too.</summary>
    public bool SportModeSeen { get; set; }

    /// <summary>
    /// Set while a fan mode is forced (Debug page, fan tests): Cooler Boost as it was before. Still set at
    /// a start, Wattkin was killed mid-force, and it's put back then (a profile leaving the fans alone
    /// wouldn't otherwise turn a forced full speed off).
    /// </summary>
    public bool? CoolerBoostBeforeForce { get; set; }

    // The battery in the laptop (Windows' unique ID for it, and what it last held when full) and when a
    // different one was first seen: capacity history and calibration advice start again from then.
    public string? BatteryIdentity { get; set; }
    public uint? BatteryFullMWh { get; set; }
    public DateTime? BatteryChangedUtc { get; set; }

    // Learned power model
    public double? LearnedRestOfSystemWatts { get; set; }
    public int LearnedRestOfSystemSamples { get; set; }

    // Windows settings captured before Wattkin first changed them: "{plan}:{setting}:{ac|dc}" and "mode:{ac|dc}".
    public Dictionary<string, uint> OriginalPowerSettings { get; set; } = [];
    public Dictionary<string, Guid> OriginalPowerModes { get; set; } = [];

    /// <summary>
    /// False until the app has once read the laptop's own settings (needs admin). Until then nothing
    /// is pushed to the hardware, so a first non-admin launch can't impose defaults later.
    /// </summary>
    public bool HardwareAdopted { get; set; }

    /// <summary>
    /// Profiles is only ever replaced, never mutated in place, so readers on other threads can
    /// enumerate the list they got without locking.
    /// </summary>
    public Profile? FindProfile(string? name) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>FanProfiles, like Profiles, is only ever replaced, never mutated in place.</summary>
    public FanProfile? FindFanProfile(string? name) =>
        FanProfiles.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The fan speeds a profile's fan choice stands for (null: hand the controller back the table it
    /// holds), and whether they may break the safety rules (only after the user accepted the
    /// experimental mode). "Off" without that acceptance comes back restricted, so it's refused.
    /// </summary>
    public (FanSpeeds? Speeds, bool Unrestricted) ResolveFanCurve(Profile profile) => profile.FanProfileName switch
    {
        null => (FactoryFanCurve, false),
        var name when string.Equals(name, FanProfile.LaptopDefault, StringComparison.OrdinalIgnoreCase) => (FactoryFanCurve, false),
        var name when string.Equals(name, FanProfile.Off, StringComparison.OrdinalIgnoreCase) => (FanProfile.OffSpeeds(), UnrestrictedFanCurves),
        var name => FindFanProfile(name) is { } fan ? (fan.Speeds, UnrestrictedFanCurves) : (FactoryFanCurve, false),
    };

    /// <summary>
    /// Where the laptop takes whole fan tables: the table a profile's fan choice stands for (the same
    /// choices as <see cref="ResolveFanCurve"/>). Speeds without a table of their own run at the laptop's
    /// temperatures. Null when no table of the laptop's is known yet (the speeds-only path is used then).
    /// </summary>
    public FanTable? ResolveFanTable(Profile profile) => profile.FanProfileName switch
    {
        null => FactoryFanTable,
        var name when string.Equals(name, FanProfile.LaptopDefault, StringComparison.OrdinalIgnoreCase) => FactoryFanTable,
        var name when string.Equals(name, FanProfile.Off, StringComparison.OrdinalIgnoreCase) => FactoryFanTable is { } factory ? FanTable.Off(factory) : null,
        var name => FindFanProfile(name) is { } fan ? fan.Table ?? FactoryFanTable?.WithSpeeds(fan.Speeds) : FactoryFanTable,
    };

    /// <summary>
    /// The profile the automatic rules pick for this power state (on the charger; on battery above the
    /// threshold; at or below it), or null when the matching rule leaves the profile alone.
    /// </summary>
    public string? AutoProfile(bool onAc, double? percent) =>
        !AutoSwitchProfiles ? null
        : onAc ? AcProfile
        : IsLow(percent) && LowBatteryProfile is { } low ? low // "Leave as is" here: no low-battery rule, the battery one applies
        : BatteryProfile;

    /// <summary>At or below the low-battery threshold (on battery), when there is one.</summary>
    public bool IsLow(double? percent) => LowBatteryThreshold > 0 && percent is { } p && p <= LowBatteryThreshold;

    /// <summary>
    /// Quiet and Cool as shipped suit the Prestige 16 Studio's fan table. On a laptop whose table can't
    /// take them (the Katana GF66 12UG's tops out at 75%), copies nobody has edited are rebuilt from the
    /// laptop's own curve. Returns the names rebuilt.
    /// </summary>
    public List<string> FitDefaultFanProfiles(FanCurves laptop)
    {
        var rebuilt = new List<string>();
        FanProfiles = [.. FanProfiles.Select(f =>
        {
            (FanSpeeds? shipped, FanSpeeds? fitted) = f.Name switch
            {
                "Quiet" => (FanSpeeds.Quiet(), FanSpeeds.Quiet(laptop)),
                "Cool" => (FanSpeeds.Cool(), FanSpeeds.Cool(laptop)),
                _ => (null, null),
            };
            if (shipped is null || fitted is null || !f.Speeds.Cpu.SequenceEqual(shipped.Cpu) || !f.Speeds.Gpu.SequenceEqual(shipped.Gpu) || shipped.Fits(laptop))
            {
                return f;
            }

            rebuilt.Add(f.Name);
            return new FanProfile { Name = f.Name, Speeds = fitted };
        })];
        return rebuilt;
    }

    /// <summary>A fan profile was renamed (or deleted, <paramref name="newName"/> null): profiles that used it follow.</summary>
    public void RenameFanProfile(string oldName, string? newName)
    {
        foreach (var p in Profiles.Where(p => string.Equals(p.FanProfileName, oldName, StringComparison.OrdinalIgnoreCase)))
        {
            p.FanProfileName = newName;
        }
    }

    /// <summary>
    /// Repairs anything a hand-edited or damaged file could contain: nulls, out-of-range numbers and
    /// enum values the hardware layer must never see. The elevated process reads this file.
    /// </summary>
    public void Normalize()
    {
        // A repeated name (a hand-edited file) can't be picked or edited apart: only the first is kept.
        var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Profiles = Profiles is { Count: > 0 } ? [.. Profiles.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name) && profileNames.Add(p.Name))] : Profile.Defaults();
        if (Profiles.Count == 0)
        {
            Profiles = Profile.Defaults();
        }

        foreach (var p in Profiles)
        {
            if (p.Performance is { } shift && !Enum.IsDefined(shift))
            {
                p.Performance = ShiftMode.Balanced;
            }

            if (p.Fan is { } f && !Enum.IsDefined(f))
            {
                p.Fan = FanMode.Auto;
            }

            if (!Enum.IsDefined(p.WindowsMode))
            {
                p.WindowsMode = WindowsPowerMode.Unchanged;
            }

            p.MaxCpuPercent = p.MaxCpuPercent is { } m ? Math.Clamp(m, 5, 100) : null;
            p.EnergyPreference = p.EnergyPreference is { } epp ? Math.Clamp(epp, 0, 100) : null;
            p.MaxFrequencyMHz = p.MaxFrequencyMHz is { } mhz ? (mhz <= 0 ? 0 : Math.Clamp(mhz, 400, 8000)) : null;
            p.KeyboardLevel = p.KeyboardLevel is { } k ? Math.Clamp(k, 0, 3) : null;
            p.RefreshRate = p.RefreshRate is > 0 and < 1000 ? p.RefreshRate : null;
        }

        // A whole table that breaks the hard rules is dropped (its speeds stay, at the laptop's
        // temperatures); a good one sets the speeds, so the speeds-only path agrees with it.
        foreach (var f in FanProfiles ?? [])
        {
            if (f?.Table is { } table)
            {
                if (table.Problem(unrestricted: true) is null)
                {
                    f.Speeds = table.ToSpeeds();
                }
                else
                {
                    f.Table = null;
                }
            }
        }

        // A malformed fan profile never reaches the controller: it's dropped, and profiles that used it
        // fall back to the laptop's default curve. Built-in names and repeats can't be used.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FanProfiles = [.. (FanProfiles ?? []).Where(f => f is { Speeds: not null } && !string.IsNullOrWhiteSpace(f.Name)
            && !FanProfile.IsBuiltIn(f.Name) && f.Speeds.Problem(unrestricted: true) is null && seen.Add(f.Name))];
        foreach (var p in Profiles)
        {
            if (p.FanProfileName is { } name && !FanProfile.IsBuiltIn(name) && FindFanProfile(name) is null)
            {
                p.FanProfileName = null;
            }
        }

        if (FactoryFanCurve?.Problem(unrestricted: true) is not null)
        {
            FactoryFanCurve = null;
        }

        if (FactoryFanTable?.Problem(unrestricted: true) is not null)
        {
            FactoryFanTable = null;
        }

        // 1: Quiet moved from Silent to the fan curve. Only a Quiet still on the old built-in fan
        // setting is moved; one the user changed is left alone.
        if (ProfilesVersion < 1)
        {
            if (FindProfile("Quiet") is { Fan: FanMode.Silent, CoolerBoost: false } quiet)
            {
                quiet.Fan = FanMode.Custom;
            }

            ProfilesVersion = 1;
        }

        ChargeLimit = Math.Clamp(ChargeLimit, 10, 100);
        ChargeResumeBelow = ChargeResumeBelow is { } r ? Math.Clamp(r, 0, 99) : null;
        LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, 0, 90);
        KeyboardLevel = KeyboardLevel is { } kb ? Math.Clamp(kb, 0, 3) : null;
        KeyboardColor = (LightColor.Parse(KeyboardColor) ?? LightColor.White).Hex;
        KeyboardAutoOffSeconds = Math.Clamp(KeyboardAutoOffSeconds, KeyboardAutoOffMinSeconds, KeyboardAutoOffMaxSeconds);
        ScreenExtraDimPercent = Math.Clamp(ScreenExtraDimPercent, Features.ScreenBrightness.MinExtraDim, 100);
        ScreenBaseRamps = ScreenBaseRamps?.Where(kv => !string.IsNullOrEmpty(kv.Key) && Platform.GammaRamp.Decode(kv.Value) is not null).ToDictionary() is { Count: > 0 } ramps ? ramps : null;
        if (!Enum.IsDefined(KeyboardTimeout))
        {
            KeyboardTimeout = KeyboardTimeout.AlwaysOn;
        }

        WidgetOpacity = double.IsFinite(WidgetOpacity) ? Math.Clamp(WidgetOpacity, 0.3, 1) : 0.92;
        CalibrationHistory ??= [];
        OriginalPowerSettings ??= [];
        OriginalPowerModes ??= [];
        ActiveProfile = FindProfile(ActiveProfile)?.Name ?? Profiles[0].Name;

        // A rule naming a profile that's gone leaves the profile alone (null), as "Leave as is" does.
        AcProfile = FindProfile(AcProfile)?.Name;
        BatteryProfile = FindProfile(BatteryProfile)?.Name;
        LowBatteryProfile = FindProfile(LowBatteryProfile)?.Name;
        if (LearnedRestOfSystemWatts is { } w && (!double.IsFinite(w) || w is < 0 or > 60))
        {
            LearnedRestOfSystemWatts = null;
            LearnedRestOfSystemSamples = 0;
        }
    }
}

/// <summary>
/// Loads and saves settings.json in %APPDATA%\Wattkin. Saves are flushed to disk and the previous
/// file is kept as settings.json.bak, so a power cut mid-save can't lose everything.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();

    /// <param name="readOnly">Changes stay in memory: for a copy that doesn't own the settings (screenshots, measurements).</param>
    public SettingsStore(string? directory = null, bool readOnly = false)
    {
        Directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wattkin");
        FilePath = Path.Combine(Directory, "settings.json");
        ReadOnly = readOnly;
        Current = Load();
    }

    public string Directory { get; }
    public string FilePath { get; }
    public bool ReadOnly { get; }
    public AppSettings Current { get; private set; }

    public event Action? Changed;

    public void Update(Action<AppSettings> change)
    {
        lock (_gate)
        {
            change(Current);
            Save();
        }

        Changed?.Invoke();
    }

    public void Save()
    {
        if (ReadOnly)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var temp = FilePath + ".tmp";
                var bytes = JsonSerializer.SerializeToUtf8Bytes(Current, Options);
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(FilePath))
                {
                    File.Replace(temp, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, FilePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                Log.Error("Could not save settings", ex);
            }
        }
    }

    private AppSettings Load()
    {
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    continue;
                }

                if (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) is { } settings)
                {
                    settings.Normalize();
                    if (path != FilePath)
                    {
                        Log.Warn("settings.json was unreadable; restored the previous copy.");
                    }

                    return settings;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
                Log.Error($"Settings file {Path.GetFileName(path)} unreadable", ex);
                try
                {
                    if (!ReadOnly)
                    {
                        File.Copy(path, path + ".broken", overwrite: true);
                    }
                }
                catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        var fresh = new AppSettings();
        fresh.Normalize();
        return fresh;
    }
}
